using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The original speed combo (client 0x4804d0, HUD class 0x563990): drifts, jumps, near misses with traffic and
    /// wrong-way driving each add to a combo that ends 3 s after the last trick or on any hit, shown on SPEEDCOMBO.
    /// Rules and addresses: DriftCity_RE/COMBO.md.
    /// </summary>
    public sealed class DriftCombo : MonoBehaviour
    {
        public enum Trick { Reverse = 0, Drift = 1, Jump = 2, Graze = 3, None = 4 }

        [Tooltip("Server content flag 0x4000000: long drifts, jumps and close near misses count 2-3 at once")]
        public bool MultiCount;

        public Hud Hud;
        /// <summary>The driver's own chain (what the server, the instant missions and the party total count).</summary>
        public int Count { get; private set; }
        /// <summary>
        /// Party members' tricks added to this chain (0x4ed43c: a member's combo packet calls AddCombo(type, 1, ..., fromParty
        /// = 1) on the local car: +1, the timer reset, the party labels). Kept apart from <see cref="Count"/> so the count the
        /// party sends to the server (PartyWindow) never echoes the members' own tricks back.
        /// </summary>
        public int PartyAdded { get; private set; }
        /// <summary>The number on SPEEDCOMBO: the chain including the party's tricks.</summary>
        public int DisplayCount => Count + PartyAdded;
        /// <summary>The last combo shown came from a party member (PARTYCOMBO / PARTYIMG and the PARTYNUM digit art).</summary>
        public bool PartyStyle { get; private set; }
        /// <summary>The speed combo of the HUD (set by <see cref="Show"/>).</summary>
        public static DriftCombo Current { get; private set; }
        /// <summary>Every 10th combo (20th with MultiCount) the client asks the server to roll an item drop.</summary>
        public event System.Action<int> Milestone;

        const int DialogId = 300000;        // SPEEDCOMBO basic; 310000 goes with the advanced SPEEDMETER, Hud opens the basic 320000
        const float Timeout = 3f;           // in[0x44]
        const float ShowTime = 6f, LabelTime = 0.7f, PopTime = 0.3f;
        const float TrickSpeedKmh = 100f;   // near miss and wrong way only count above this
        static readonly string[] Icons = { "ID_SPEEDCOMBO_REVERSE", "ID_SPEEDCOMBO_DRIFT", "ID_SPEEDCOMBO_JUMP", "ID_SPEEDCOMBO_STUNT" };
        static readonly string[] Sounds = { "sfx/SID_COMBO_REVERSE", "sfx/SID_COMBO_DRIFT", "sfx/SID_COMBO_JUMP", "sfx/SID_COMBO_GRAZE" };

        NuiView view;
        DriftCar car;
        DriftComboContacts contacts;
        CarRoadState road;
        readonly RawImage[] slots = new RawImage[4];
        readonly Vector2[] slotPos = new Vector2[4];
        readonly RawImage[] digitArt = new RawImage[10];
        readonly RawImage[] partyArt = new RawImage[10];
        readonly int[] digits = new int[4];
        bool shown;
        float hideAt, labelUntil, popUntil;
        Trick labelTrick = Trick.None;

        // trick state (client fields in COMBO.md)
        float comboTimer, driftTime, airTime, jumpCooldown, reverseTime;
        bool driftVoid, hadPos;
        Vector3 lastJumpPos, lastPos;
        sealed class Pass { public bool Armed, Hit; public float MinSq; }
        readonly Dictionary<TrafficCar, Pass> passes = new Dictionary<TrafficCar, Pass>();
        readonly List<TrafficCar> stale = new List<TrafficCar>();

        /// <summary>Opens the combo display next to the HUD (call after <see cref="Hud.Show"/>); it closes itself with the speedometer.</summary>
        public static DriftCombo Attach(Hud hud)
        {
            if (!hud) return null;
            if (!hud.TryGetComponent(out DriftCombo combo)) combo = hud.gameObject.AddComponent<DriftCombo>();
            combo.Hud = hud;
            combo.Show();
            return combo;
        }

        public void Show()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (view) ui.Close(view);
            view = ui.Open(DialogId);
            if (!view) return;
            Current = this;
            BuildDigits();
            ResetCombo();
            SetCar(Hud ? Hud.Car : null);
        }

        public void Hide()
        {
            if (view) UiManager.Instance?.Close(view);
            view = null;
            SetCar(null);
            Count = 0;
            PartyAdded = 0;
        }

        void OnDestroy() { Hide(); if (Current == this) Current = null; }

        // ---------------- display ----------------

        void BuildDigits()
        {
            // NUM0..9 are only the digit art: the client hides them and draws copies at the four NUMPOS slots
            for (int d = 0; d < 10; d++)
            {
                var c = view.Control("ID_SPEEDCOMBO_NUM" + d);
                digitArt[d] = c ? c.GetComponentInChildren<RawImage>(true) : null;
                var pc = view.Control("ID_SPEEDCOMBO_PARTYNUM" + d);
                partyArt[d] = pc ? pc.GetComponentInChildren<RawImage>(true) : null;
                view.SetVisible("ID_SPEEDCOMBO_NUM" + d, false);
                view.SetVisible("ID_SPEEDCOMBO_PARTYNUM" + d, false);
            }
            for (int k = 0; k < 4; k++)
            {
                slots[k] = null;
                var pos = view.Control("ID_SPEEDCOMBO_NUMPOS" + k);
                if (!pos) continue;
                var rt = (RectTransform)pos.transform;
                slotPos[k] = rt.anchoredPosition;
                var img = NuiView.NewRect("Combo Digit " + k, rt.parent).gameObject.AddComponent<RawImage>();
                img.raycastTarget = false;
                img.enabled = false;
                slots[k] = img;
                pos.gameObject.SetActive(false);
            }
            foreach (var id in Icons) view.SetVisible(id, false);
            foreach (var id in new[] { "ID_SPEEDCOMBO_COMBO", "ID_SPEEDCOMBO_PARTYCOMBO", "ID_SPEEDCOMBO_PARTYIMG", "ID_SPEEDCOMBO_AUTOREFILL" })
                view.SetVisible(id, false);
        }

        void SetShown(bool on)
        {
            shown = on;
            if (view) view.gameObject.SetActive(on);
        }

        void ShowCombo(int previous, Trick trick, bool party = false)
        {
            float now = Time.time;
            hideAt = now + ShowTime;
            labelUntil = now + LabelTime;
            if (previous % 10 != DisplayCount % 10) popUntil = now + PopTime;
            for (int i = 0; i < Icons.Length; i++) view.SetVisible(Icons[i], (int)trick < Icons.Length && i == (int)trick);
            // 0x563990: COMBO for the driver's own trick, PARTYCOMBO + PARTYIMG for a member's
            view.SetVisible("ID_SPEEDCOMBO_COMBO", !party);
            view.SetVisible("ID_SPEEDCOMBO_PARTYCOMBO", party);
            view.SetVisible("ID_SPEEDCOMBO_PARTYIMG", party);
            PartyStyle = party;
            labelTrick = trick;
            SetShown(true);
        }

        void Update()
        {
            if (!view) return;
            // Hud.Hide / CloseAll took the speedometer away: go with it
            var ui = UiManager.Instance;
            if (!ui || !ui.Find(320000)) { Hide(); return; }
            if (Hud && Hud.Car != car) SetCar(Hud.Car);   // the car was rebuilt (shop, switch)
            if (!shown) return;
            float now = Time.time;
            if (now > hideAt) { SetShown(false); return; }
            if (labelTrick != Trick.None && now > labelUntil)
            {
                // the trick icon and "COMBO" (or the party labels) flash for 0.7 s, the number stays
                if ((int)labelTrick < Icons.Length) view.SetVisible(Icons[(int)labelTrick], false);
                view.SetVisible("ID_SPEEDCOMBO_COMBO", false);
                view.SetVisible("ID_SPEEDCOMBO_PARTYCOMBO", false);
                view.SetVisible("ID_SPEEDCOMBO_PARTYIMG", false);
                labelTrick = Trick.None;
            }
            else if (labelTrick == Trick.None && PartyStyle && now > labelUntil)
            {
                view.SetVisible("ID_SPEEDCOMBO_PARTYCOMBO", false);
                view.SetVisible("ID_SPEEDCOMBO_PARTYIMG", false);
            }
            DrawDigits(now);
        }

        void DrawDigits(float now)
        {
            int c = DisplayCount;
            digits[0] = c / 1000 % 10; digits[1] = c / 100 % 10; digits[2] = c / 10 % 10; digits[3] = c % 10;
            // after a change every digit starts 1.5x tall (bottom edge fixed) and transparent, settling in 0.3 s
            float r = popUntil - now > 0f ? (popUntil - now) / PopTime : 0f;
            bool leading = true;
            for (int k = 0; k < 4; k++)
            {
                var img = slots[k];
                if (!img) continue;
                int d = digits[k];
                if (d != 0) leading = false;
                // PARTYNUM0-9: the same digits in the party colours after a member's trick (0x5646c0 with party = 1)
                var art = PartyStyle && partyArt[d] ? partyArt[d] : digitArt[d];
                if (leading || !art) { img.enabled = false; continue; }
                img.enabled = true;
                img.texture = art.texture;
                img.uvRect = art.uvRect;
                var size = art.rectTransform.sizeDelta;
                int grow = (int)(size.y * r * 0.5f);
                var rt = img.rectTransform;
                rt.sizeDelta = new Vector2(size.x, size.y + grow);
                rt.anchoredPosition = slotPos[k] + new Vector2(d == 1 ? 5f : 0f, grow);
                img.color = new Color(1f, 1f, 1f, r > 0f ? (int)((1f - r) * 255f) / 255f : 1f);
            }
        }

        // ---------------- combo ----------------

        void Add(Trick trick, int n)
        {
            if (!view || !car || car.ExternalControl || RaceMission.Active || OnlineRace.Active || MittronFuel.Empty) return;   // no combos on an empty tank (0x4804d0)
            int previous = Count;
            comboTimer = 0f;
            int period = MultiCount ? 20 : 10;
            for (int i = 0; i < n; i++)
            {
                Count++;
                if (Count % period == 0) Milestone?.Invoke(Count);
            }
            ShowCombo(previous, trick);
            AudioLibrary.PlaySfx(Sounds[(int)trick]);
        }

        /// <summary>
        /// A party member chained a trick (online: the server's party.combo push with the member's rise): +1 per trick to the
        /// shown chain, the chain's timer reset, and the party labels and digit art (0x4ed43c -> 0x4804d0 fromParty = 1).
        /// </summary>
        public void AddFromParty(int tricks = 1, Trick trick = Trick.None)
        {
            if (!view || !car || tricks <= 0) return;
            int previous = DisplayCount;
            comboTimer = 0f;
            PartyAdded += Mathf.Min(tricks, 10);
            ShowCombo(previous, trick, party: true);
            AudioLibrary.PlaySfx(Sounds[trick == Trick.None ? (int)Trick.Drift : (int)trick]);
        }

        void ResetCombo()
        {
            Count = 0;
            PartyAdded = 0;
            PartyStyle = false;
            comboTimer = 0f;
            labelTrick = Trick.None;
            popUntil = 0f;
            SetShown(false);
        }

        void SetCar(DriftCar next)
        {
            if (next != car || (next && !contacts))
            {
                if (contacts) contacts.Contact -= OnContact;
                contacts = null;
                car = next;
                road = null;
                if (car)
                {
                    if (!car.TryGetComponent(out contacts)) contacts = car.gameObject.AddComponent<DriftComboContacts>();
                    contacts.Contact += OnContact;
                    if (!car.TryGetComponent(out road)) road = car.gameObject.AddComponent<CarRoadState>();
                }
            }
            ResetTricks();
        }

        void ResetTricks()
        {
            driftTime = airTime = jumpCooldown = reverseTime = 0f;
            driftVoid = hadPos = false;
            passes.Clear();
            if (view) ResetCombo();
        }

        void AddGauge(float clientUnits) => car.BoostGauge = Mathf.Clamp01(car.BoostGauge + clientUnits / 100f);

        void FixedUpdate()
        {
            if (!view || !car) return;
            float dt = Time.fixedDeltaTime;
            var pos = car.transform.position;
            // a car reset (R / respawn) clears the combo like the client's body re-creation
            if (hadPos && (pos - lastPos).sqrMagnitude > 25f * 25f) ResetTricks();
            lastPos = pos; hadPos = true;

            comboTimer += dt;
            if (comboTimer > Timeout && (DisplayCount > 0 || shown)) ResetCombo();
            // off the road (sidewalk, no road strip) the client resets the combo every step (0x481910)
            if (road && road.OffRoad && (Count > 0 || shown)) ResetCombo();

            // drift: more than 0.4 s of FRSLIP/MTSLIP that was not ended by a hit
            if (car.DriftState != 0) { if (!driftVoid) driftTime += dt; }
            else
            {
                if (driftTime > 0.4f) Add(Trick.Drift, MultiCount ? (driftTime > 0.7f ? 3 : driftTime > 0.5f ? 2 : 1) : 1);
                driftTime = 0f;
                driftVoid = false;
            }

            // jump: more than 0.4 s off the ground, not within 2 s and 10 m of the previous landing
            if (jumpCooldown > 0f) jumpCooldown = Mathf.Max(0f, jumpCooldown - dt);
            GameEffects.TrackJump(car);   // jump_up / jump_down NIFs (0x4851ae / 0x485299)
            if (car.Airborne) airTime += dt;
            else if (airTime > 0f)
            {
                if (airTime > 0.4f && (jumpCooldown <= 0f || (pos - lastJumpPos).magnitude > 10f)) Add(Trick.Jump, MultiCount ? 3 : 1);
                jumpCooldown = 2f;
                lastJumpPos = pos;
                airTime = 0f;
            }

            float kmh = car.SpeedKmh;
            var traffic = CitySession.Current ? CitySession.Current.Traffic : null;
            UpdateWrongWay(traffic, pos, kmh, dt);
            UpdatePasses(traffic, pos, kmh);
        }

        /// <summary>Wall and vehicle hits end the combo (0x47e510); the road under the car does not.</summary>
        void OnContact(Collision c)
        {
            bool side = false;
            for (int i = 0; i < c.contactCount && !side; i++) side = c.GetContact(i).normal.y <= 0.6f;
            if (!side) return;
            if (Count > 0 || shown) ResetCombo();
            // DriftCar ends the drift with no reward; don't count it when the state drops
            if (car && car.DriftState != 0) { driftTime = 0f; driftVoid = true; }
            var hit = c.collider ? c.collider.GetComponentInParent<TrafficCar>() : null;
            if (hit)
            {
                if (!passes.TryGetValue(hit, out var p)) passes[hit] = p = new Pass();
                p.Hit = true;
            }
        }

        // ---------------- near miss (0x4a6460 / 0x489030) ----------------

        void UpdatePasses(TrafficSystem traffic, Vector3 pos, float kmh)
        {
            if (!traffic) return;
            foreach (var tc in traffic.Cars)
            {
                if (!tc) continue;
                float sq = (pos - tc.transform.position).sqrMagnitude;
                passes.TryGetValue(tc, out var p);
                if (sq < 100f)
                {
                    if (kmh < TrickSpeedKmh) continue;
                    float d2 = CornerDistanceSq(tc, pos);
                    if (d2 >= 4f) continue;
                    if (p == null) passes[tc] = p = new Pass();
                    if (!p.Armed) { p.Armed = true; p.MinSq = d2; GameEffects.Graze(car, GameEffects.PassesOnLeft(car, tc.transform)); }
                    else if (d2 < p.MinSq) p.MinSq = d2;
                    if (tc.Wrecked) p.Hit = true;
                }
                else if (sq > 225f && p != null)
                {
                    passes.Remove(tc);
                    if (p.Armed && !p.Hit) Graze(Mathf.Sqrt(p.MinSq));
                }
            }
            stale.Clear();
            foreach (var k in passes.Keys) if (!k) stale.Add(k);
            foreach (var k in stale) passes.Remove(k);
        }

        void Graze(float d)
        {
            AddGauge((2f - d) * 6f + 1f);
            Add(Trick.Graze, MultiCount ? (d < 0.35f ? 3 : d < 1f ? 2 : 1) : 1);
            CouponBook.Count(CouponBook.Cat.SkillPass);
        }

        /// <summary>
        /// Squared distance from the player to the traffic car's nearest front/rear corner, or the middle of that end
        /// when the player is closer to the centre line than to the side (the client's reference point).
        /// </summary>
        static float CornerDistanceSq(TrafficCar tc, Vector3 pos)
        {
            var local = tc.transform.InverseTransformPoint(pos);
            if (Mathf.Abs(local.y) > 3f) return float.MaxValue;   // a bridge above or below
            var box = tc.GetComponent<BoxCollider>();
            Vector3 center = box ? box.center : Vector3.zero, size = box ? box.size : new Vector3(2f, 1.6f, 4.6f);
            bool zLong = size.z >= size.x;
            float halfLen = Mathf.Max(size.x, size.z) * 0.5f, halfWid = Mathf.Min(size.x, size.z) * 0.5f;
            var p = local - center;
            float along = zLong ? p.z : p.x, side = zLong ? p.x : p.z;
            float end = along > 0f ? halfLen : -halfLen;
            float face = side > 0f ? halfWid : -halfWid;
            float sideRef = Mathf.Abs(face - side) <= Mathf.Abs(side) ? face : 0f;
            float da = end - along, ds = sideRef - side;
            return da * da + ds * ds;
        }

        // ---------------- wrong way (0x485ee0 / 0x48637e) ----------------

        void UpdateWrongWay(TrafficSystem traffic, Vector3 pos, float kmh, float dt)
        {
            // the client's road map (CarRoadState: .tcs roads or AREACHECK) when the city has one, else the lane stand-in
            bool wrong = road && road.HasRoads ? road.WrongWay
                : traffic && traffic.Network != null && Lanes.WrongWay(traffic.Network, pos, car.transform.forward);
            if (!wrong) { reverseTime = Mathf.Max(0f, reverseTime - dt * 4f); return; }
            if (kmh <= TrickSpeedKmh) return;
            reverseTime += dt;
            if (reverseTime <= 2f) return;
            reverseTime -= 2f;
            AddGauge(3f);
            // the client really calls AddCombo twice here: +2 and the sound twice
            Add(Trick.Reverse, 1);
            Add(Trick.Reverse, 1);
        }

        /// <summary>
        /// Fallback when the city has no road map (CarRoadState.HasRoads false): the nearest traffic lane segment
        /// within 4 m pointing more than 120 deg away from the car's heading.
        /// </summary>
        static class Lanes
        {
            const float Cell = 16f, Reach = 4f;
            static Core.TrafficNetwork built;
            static readonly List<Vector3> from = new List<Vector3>(), to = new List<Vector3>();
            static readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();

            static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

            static void Build(Core.TrafficNetwork net)
            {
                built = net;
                from.Clear(); to.Clear(); grid.Clear();
                foreach (var lane in net.Lanes)
                    for (int i = 1; i < lane.Points.Length; i++)
                    {
                        var a = DriftCityData.ToUnity(lane.Points[i - 1]);
                        var b = DriftCityData.ToUnity(lane.Points[i]);
                        int s = from.Count;
                        from.Add(a); to.Add(b);
                        int x0 = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - Reach) / Cell), x1 = Mathf.FloorToInt((Mathf.Max(a.x, b.x) + Reach) / Cell);
                        int z0 = Mathf.FloorToInt((Mathf.Min(a.z, b.z) - Reach) / Cell), z1 = Mathf.FloorToInt((Mathf.Max(a.z, b.z) + Reach) / Cell);
                        for (int x = x0; x <= x1; x++)
                            for (int z = z0; z <= z1; z++)
                            {
                                if (!grid.TryGetValue(Key(x, z), out var list)) grid[Key(x, z)] = list = new List<int>();
                                list.Add(s);
                            }
                    }
            }

            public static bool WrongWay(Core.TrafficNetwork net, Vector3 pos, Vector3 forward)
            {
                if (net != built) Build(net);
                if (!grid.TryGetValue(Key(Mathf.FloorToInt(pos.x / Cell), Mathf.FloorToInt(pos.z / Cell)), out var list)) return false;
                float best = Reach * Reach;
                Vector2 dir = Vector2.zero;
                var p = new Vector2(pos.x, pos.z);
                foreach (int s in list)
                {
                    Vector3 a = from[s], b = to[s];
                    var a2 = new Vector2(a.x, a.z);
                    var ab = new Vector2(b.x, b.z) - a2;
                    float len2 = ab.sqrMagnitude;
                    if (len2 < 1e-4f) continue;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a2, ab) / len2);
                    if (Mathf.Abs(Mathf.Lerp(a.y, b.y, t) - pos.y) > 4f) continue;   // a road above or below
                    float d = (a2 + ab * t - p).sqrMagnitude;
                    if (d < best) { best = d; dir = ab; }
                }
                if (dir == Vector2.zero) return false;
                var f = new Vector2(forward.x, forward.z);
                return f.sqrMagnitude > 1e-4f && Vector2.Dot(f.normalized, dir.normalized) < -0.5f;
            }
        }
    }
}
