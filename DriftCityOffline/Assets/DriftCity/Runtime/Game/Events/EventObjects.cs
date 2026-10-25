using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The events in the open city, run by a self-starting component (no hook needed in the city code):
    ///  - the event posts of World/Data/Icon_&lt;city&gt;_Client.tdf while their season runs: the Christmas trees
    ///    (post_N_xmas_01, game\x_tree001\x_tree001_0N.nif by decoration count), the harvest restaurants and distribution
    ///    centres (post_N_harvest_01 / _02), and all year the coupon posts ("Instructor Duffy Coupon": COUPONPOPUP 1900000,
    ///    Space opens the coupon book). Like the client (0x6f73b0 / 0x6fc0f0: 7 m, car stopped) they open when the car is
    ///    within <see cref="Radius"/> below <see cref="MaxKmh"/>;
    ///  - the coupon counters (driving and reckless time, drifts, jumps, combos, chases, races, Undercity runs);
    ///  - the play drops of the running seasons (<see cref="EventDrops"/>) and the daily attendance gift.
    /// </summary>
    public sealed class EventObjects : MonoBehaviour
    {
        public static EventObjects Instance { get; private set; }
        /// <summary>Tests and tools can switch the city side off.</summary>
        public static bool Enabled = true;
        public const float Radius = 10f, MaxKmh = 10f, LeaveRadius = 25f;

        public enum Kind { Tree, Restaurant, Distribution, Coupon, PropNpc, Prop }
        public sealed class Spot
        {
            public Kind Kind;
            public string Post, Name, City;
            public Vector3 Position;
            public GameObject Visual;
            public bool Latched;
            public int Index;    // Prop: 0 fedora, 1 whip, 2 crystal skull (PropEvent)
        }

        readonly List<Spot> spots = new List<Spot>();
        public IReadOnlyList<Spot> Spots => spots;
        CitySession session;
        EventSeason builtFor;
        string builtCity;
        int lastHuv = -1, lastRaces = -1;
        bool wasDrifting, wasAirborne, wasUndercity;
        float airTime, nextTick;
        DriftCombo combo;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static EventObjects Ensure()
        {
            if (Instance) return Instance;
            var go = new GameObject("Events");
            DontDestroyOnLoad(go);
            return Instance = go.AddComponent<EventObjects>();
        }

        void OnDestroy() { if (Instance == this) Instance = null; Clear(); }

        void Update()
        {
            SeasonEvents.Tick();
            var s = CitySession.Current;
            var p = PlayerProfile.Current;
            if (!Enabled || !s || p == null || !s.Car) { if (session && !s) Clear(); session = s; return; }
            var seasons = SeasonEvents.Active;
            if (s != session || seasons != builtFor || builtCity != s.City) Build(s, seasons);
            Track(p, s);
            Proximity(s);
            if (Time.unscaledTime >= nextTick) { nextTick = Time.unscaledTime + 2f; Drops(p, s.City); CouponBook.Sync(); }
        }

        // ---------------- posts ----------------

        void Clear()
        {
            foreach (var sp in spots) if (sp.Visual) Destroy(sp.Visual);
            spots.Clear();
            if (CouponBook.NpcView) CouponBook.HideNpc();
        }

        void Build(CitySession s, EventSeason seasons)
        {
            Clear();
            session = s; builtFor = seasons; builtCity = s.City;
            foreach (var icon in CityMapInfo.Icons(s.City))
            {
                Kind? kind = null;
                if (icon.Id.EndsWith("_xmas_01") && (seasons & EventSeason.Christmas) != 0) kind = Kind.Tree;
                else if (icon.Id.EndsWith("_harvest_01") && (seasons & EventSeason.Harvest) != 0) kind = Kind.Restaurant;
                else if (icon.Id.EndsWith("_harvest_02") && (seasons & EventSeason.Harvest) != 0) kind = Kind.Distribution;
                else if ((icon.Name ?? "").IndexOf("Coupon", StringComparison.OrdinalIgnoreCase) >= 0 && (icon.Name ?? "").IndexOf("Exit", StringComparison.OrdinalIgnoreCase) < 0) kind = Kind.Coupon;
                if (kind == null) continue;
                spots.Add(new Spot { Kind = kind.Value, Post = icon.Id, Name = icon.Name, City = s.City, Position = icon.Position });
            }
            if ((seasons & EventSeason.Indy4) != 0) AddProps(s);
            RefreshTrees();
            foreach (var sp in spots.Where(x => x.Kind == Kind.Restaurant || x.Kind == Kind.Distribution))
            {
                var b = MissionBeacon.Create("Event " + sp.Name, null, sp.Kind == Kind.Restaurant ? new Color(1f, 0.6f, 0.1f, 0.35f) : new Color(0.3f, 0.9f, 0.3f, 0.35f));
                b.MoveTo(sp.Position);
                Parent(b.transform, s);
                sp.Visual = b.gameObject;
            }
        }

        /// <summary>The Indy4 NPC and today's three props of the city (PropEvent, 0x6eca00).</summary>
        void AddProps(CitySession s)
        {
            var icons = CityMapInfo.Icons(s.City);
            var npc = icons.FirstOrDefault(i => i.Id == PropEvent.NpcPost(s.City));
            if (npc == null) return;
            spots.Add(new Spot { Kind = Kind.PropNpc, Post = npc.Id, Name = "Indiana Jones", City = s.City, Position = npc.Position });
            var picked = PropEvent.Pick(s.City, EventNet.Rng);
            for (int k = 0; k < picked.Length; k++)
            {
                var ic = icons.FirstOrDefault(i => i.Id == picked[k]);
                if (ic != null) spots.Add(new Spot { Kind = Kind.Prop, Post = ic.Id, Name = EventItems.Name(PropEvent.Props[k]), City = s.City, Position = ic.Position, Index = k });
            }
            foreach (var sp in spots.Where(x => x.Kind == Kind.PropNpc || x.Kind == Kind.Prop))
            {
                var b = MissionBeacon.Create("Event " + sp.Name, null, sp.Kind == Kind.PropNpc ? new Color(0.3f, 0.5f, 1f, 0.35f) : new Color(1f, 0.9f, 0.2f, 0.35f));
                b.MoveTo(sp.Position);
                Parent(b.transform, s);
                sp.Visual = b.gameObject;
            }
        }

        /// <summary>0x495: the NPC took the props; three new posts.</summary>
        public void RepickProps()
        {
            if (!session) return;
            foreach (var sp in spots.Where(x => x.Kind == Kind.PropNpc || x.Kind == Kind.Prop)) if (sp.Visual) Destroy(sp.Visual);
            spots.RemoveAll(x => x.Kind == Kind.PropNpc || x.Kind == Kind.Prop);
            if (SeasonEvents.Is(EventSeason.Indy4)) AddProps(session);
        }

        static void Parent(Transform t, CitySession s)
        {
            if (s && s.Traffic && s.Traffic.transform.parent) t.SetParent(s.Traffic.transform.parent, true);
        }

        /// <summary>Re-dresses the trees for their decoration count (X_tree001_01..06).</summary>
        public void RefreshTrees()
        {
            foreach (var sp in spots.Where(x => x.Kind == Kind.Tree))
            {
                int stage = XmasTree.Stage(XmasTree.Count(XmasTree.Mask(sp.City)));
                string nif = $@"game\x_tree001\x_tree001_{stage:00}.nif";
                if (sp.Visual && sp.Visual.name == nif) continue;
                if (sp.Visual) Destroy(sp.Visual);
                sp.Visual = null;
                var fs = DriftCityData.Files;
                if (fs == null || !fs.Exists(nif)) continue;
                try
                {
                    var go = new NifSceneBuilder { LogErrors = false }.Load(nif);
                    if (!go) continue;
                    go.name = nif;
                    foreach (var c in go.GetComponentsInChildren<Collider>()) Destroy(c);
                    var p = sp.Position;
                    Physics.SyncTransforms();
                    if (Physics.Raycast(p + Vector3.up * 40f, Vector3.down, out var hit, 120f, ~0, QueryTriggerInteraction.Ignore)) p = hit.point;
                    go.transform.position = p;
                    Parent(go.transform, session);
                    sp.Visual = go;
                }
                catch (Exception ex) { Debug.LogWarning("xmas tree: " + ex.Message); }
            }
        }

        void Proximity(CitySession s)
        {
            var car = s.Car;
            Vector3 cp = car.transform.position;
            float kmh = car.SpeedKmh;
            bool busy = UiPopup.IsOpen || MissionUi.ScriptOpen || XmasTree.View || CouponBook.View || TokenBook.View || EventPopup.View || ObEvent.View || CouponStamp.View || HarvestScreens.ThanksView;
            foreach (var sp in spots)
            {
                float d = MissionPosts.Flat(cp, sp.Position);
                if (sp.Latched) { if (d > LeaveRadius) { sp.Latched = false; if (sp.Kind == Kind.Coupon) CouponBook.HideNpc(); } continue; }
                if (d > Radius || busy) continue;
                if (kmh > MaxKmh) continue;
                if (sp.Kind == Kind.Coupon) { sp.Latched = true; CouponBook.ShowNpc(); continue; }
                sp.Latched = true;
                Interact(sp);
            }
            // Space also drifts: the prompt only answers while the car stands
            if (CouponBook.NpcView && kmh <= MaxKmh && SpacePressed()) { CouponBook.HideNpc(); CouponBook.OpenFromNpc(); }
        }

        /// <summary>Opens a post's screen (also used by tests).</summary>
        public static void Interact(Spot sp)
        {
            switch (sp.Kind)
            {
                case Kind.Tree: XmasTree.Open(sp.City); break;
                case Kind.Restaurant: Harvest.Restaurant(sp.City); break;
                case Kind.Distribution: Harvest.Distribution(sp.City); break;
                case Kind.Coupon: CouponBook.ShowNpc(); break;
                case Kind.PropNpc: PropEvent.AtNpc(); break;
                case Kind.Prop: PropEvent.Found(sp.Index); break;
            }
        }

        static bool SpacePressed()
        {
            if (ChatBox.Typing) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }

        // ---------------- coupon counters ----------------

        void Track(PlayerProfile p, CitySession s)
        {
            var car = s.Car;
            float dt = Time.deltaTime;
            float kmh = car.SpeedKmh;
            if (kmh > 10f) CouponBook.Count(CouponBook.Cat.DrivingTime, dt);
            if (car.TryGetComponent<CarRoadState>(out var road) && road.WrongWay && kmh > 10f) CouponBook.Count(CouponBook.Cat.Reckless, dt);
            bool drifting = car.DriftState != 0;
            if (drifting && !wasDrifting) CouponBook.Count(CouponBook.Cat.Drift);
            wasDrifting = drifting;
            if (car.Airborne) airTime += dt;
            else { if (wasAirborne && airTime > 0.4f) CouponBook.Count(CouponBook.Cat.Jump); airTime = 0; }
            wasAirborne = car.Airborne;
            if (!combo) { combo = FindAnyObjectByType<DriftCombo>(); if (combo) combo.Milestone += _ => CouponBook.Count(CouponBook.Cat.Combo, 10); }
            int races = p.RacesEntered + p.PvpRaces;
            if (lastHuv >= 0 && p.HuvCaught > lastHuv) CouponBook.Count(CouponBook.Cat.Chase, p.HuvCaught - lastHuv);
            if (lastRaces >= 0 && races > lastRaces) CouponBook.Count(CouponBook.Cat.Battle, races - lastRaces);
            lastHuv = p.HuvCaught; lastRaces = races;
            bool uc = UndercityStage.Active;
            if (wasUndercity && !uc) CouponBook.Count(CouponBook.Cat.GameMode);
            wasUndercity = uc;
        }

        // ---------------- play drops and attendance ----------------

        // a request whose answer never comes (the connection dropped) must not stop the drops for the session
        float tickPendingUntil;

        void Drops(PlayerProfile p, string city)
        {
            var st = EventNet.State(p);
            ObEvent.CheckConfig(st);
            int races = p.RacesEntered + p.PvpRaces;
            bool newDay = st.Day != EventNet.Today || st.AttendDay != EventNet.Today;
            bool played = st.SeenHuv >= 0 && (p.HuvCaught > st.SeenHuv || races > st.SeenRaces);
            if (!newDay && !played && st.SeenHuv >= 0) return;
            if (Time.unscaledTime < tickPendingUntil) return;
            tickPendingUntil = Time.unscaledTime + 10f;
            EventNet.Request("event.tick", new Dictionary<string, object> { ["city"] = city }, () => EventDrops.TickOffline(p, city, EventNet.Rng), r =>
            {
                tickPendingUntil = 0f;
                if (!r.Ok || r.Grants.Count == 0) return;
                EventNet.Log(r.Grants);
                string greet = r.Data.S("greeting");
                // UI effect 13 3rd_anniversary_fin (0x6b3b00; no caller left in this exe): the remake plays it with the day's
                // anniversary attendance gift (Etc.Event3Year), the other drops get the item pickup
                if (!string.IsNullOrEmpty(greet) && greet.StartsWith("3rdanni")) GameEffects.Screen("3rd_anniversary_fin");
                else GameEffects.GetItem();
                // the daily greetings go to the chat (non-modal, so nothing pops up while driving)
                if (!string.IsNullOrEmpty(greet))
                    foreach (var page in Core.NpcScript.Parse(EventUi.Ment(greet)))
                        foreach (var line in page.Lines) ChatBox.System(line.Replace("$name", PlayerProfile.Current?.Name ?? "Driver"));
                if (r.Data.B("halloween")) ChatBox.System($"{EventUi.T("11427", "Halloween Event")}: {EventUi.T("11428", "Halloween Event Reward")} - {EventItems.Name("i_n_halloween")}");
            });
        }

        /// <summary>Starts the day's counters (daily mission base, drop cap).</summary>
        public static void RollDay(PlayerProfile p)
        {
            var st = EventNet.State(p);
            int races = p.RacesEntered + p.PvpRaces;
            if (st.SeenHuv < 0) { st.SeenHuv = p.HuvCaught; st.SeenRaces = races; }
            if (st.Day == EventNet.Today) return;
            st.Day = EventNet.Today; st.DayDrops = 0; st.DayTokens = 0; st.DayMission = false;
            st.DayHuvBase = p.HuvCaught; st.DayRaceBase = races;
        }
    }

    /// <summary>
    /// What playing gives while a season runs (this rebuild's rule; the official server's event drops were its own):
    /// every HUV caught and every race finished gives one drop per running season, at most <see cref="MaxPerDay"/> a day:
    /// Christmas a Santa Gift Box (chases and races), harvest the current city's ingredient box (chases), Valentine/Easter
    /// eggs (rw_valentain_bronze from chases, _silver from races; the three basket recipes with the first, a Colorful
    /// Paint every 3rd), the summer festival's letters (rw_2010_sumfest_droptable_cs / _bz). Once a day: the anniversary
    /// greeting with a Small Gift Box, the Halloween Drink. Mirrored by src/events.js tick().
    /// </summary>
    public static class EventDrops
    {
        public const int MaxPerDay = 10;
        static readonly Dictionary<string, string> Short = new Dictionary<string, string> { ["cras"] = "cras", ["moonpalace"] = "moon", ["koinonia"] = "koi", ["oros"] = "oros" };

        public static EventReply TickOffline(PlayerProfile p, string city, System.Random rng)
        {
            var st = EventNet.State(p);
            EventObjects.RollDay(p);
            var grants = new List<RewardGrant>();
            var seasons = SeasonEvents.Active;
            int races = p.RacesEntered + p.PvpRaces;
            int chases = Math.Max(0, p.HuvCaught - st.SeenHuv), raced = Math.Max(0, races - st.SeenRaces);
            st.SeenHuv = p.HuvCaught; st.SeenRaces = races;
            var r = new EventReply { Ok = true, Grants = grants };
            for (int i = 0; i < chases; i++) Drop(p, st, seasons, city, true, rng, grants);
            for (int i = 0; i < raced; i++) Drop(p, st, seasons, city, false, rng, grants);
            if (st.AttendDay != EventNet.Today)
            {
                st.AttendDay = EventNet.Today;
                if ((seasons & EventSeason.Anniversary) != 0)
                {
                    grants.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = "i_n_00100", Count = 1 });
                    r.Data["greeting"] = "3rdanni_attendevent_01_omd";
                }
                if ((seasons & EventSeason.Halloween) != 0) { grants.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = "i_n_halloween", Count = 1 }); r.Data["halloween"] = true; }
            }
            EventNet.Apply(p, grants);
            return r;
        }

        static void Drop(PlayerProfile p, EventState st, EventSeason seasons, string city, bool chase, System.Random rng, List<RewardGrant> into)
        {
            void Add(IEnumerable<RewardGrant> g) { foreach (var x in g) into.Add(x); }
            if ((seasons & EventSeason.Christmas) != 0 && st.DayDrops < MaxPerDay) { st.DayDrops++; into.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = XmasTree.GiftBox, Count = 1 }); }
            if (chase && (seasons & EventSeason.Harvest) != 0 && st.DayDrops < MaxPerDay && Short.TryGetValue(city ?? "", out var sh))
            {
                st.DayDrops++; Add(EventItems.Pools.Draw("rw_harvest_" + sh, p.Level, rng));
            }
            if ((seasons & EventSeason.Valentine) != 0 && st.DayDrops < MaxPerDay)
            {
                st.DayDrops++;
                Add(EventItems.Pools.Draw(chase ? "rw_valentain_bronze" : "rw_valentain_silver", p.Level, rng));
                foreach (var rc in Harvest.BasketNeeds.Keys) if (p.Count(rc) <= 0 && !into.Exists(g => g.ItemId == rc)) into.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = rc, Count = 1 });
                if (st.DayDrops % 3 == 0) into.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = "careful_wrapping", Count = 1 });
            }
            if ((seasons & EventSeason.Summer) != 0 && st.DayDrops < MaxPerDay)
            {
                st.DayDrops++; Add(EventItems.Pools.Draw(chase ? "rw_2010_sumfest_droptable_cs" : "rw_2010_sumfest_droptable_bz", p.Level, rng));
            }
        }
    }
}
