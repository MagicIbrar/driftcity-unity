using System;
using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The HUV chase screens of DriftCity.exe (DriftCity_RE/CHASES.md), shared by the chases, Rush Time and the Virtual HUV:
    /// <list type="bullet">
    /// <item>CHASING 870000 / EVENTCHASING 880000: the "HUV spotted" call. Handler 0x74c2f0: TABBUTTON (Tab) sends the chase
    /// request with the car's position (0x74c280 -> 0x783a20), CANCEL (Esc) refuses (0x74be20). While it is up the four
    /// NOTIFY art variants cycle with fmod(t, 0.8): up to 0.08 s NOTIFY2, 0.16 NOTIFY3, 0.24 NOTIFY4, else NOTIFY1
    /// (0x6f7d80 -> 0x6f5a40); EVENTCHASING (Christmas art) replaces it while the server's event flag net+0x288 bit 7 is set.</item>
    /// <item>CHASEINFO 610000-640000 (one per city, 0x6f7c10): the city's chase map; its draw message (0x5546d0 -> 0x6fd500)
    /// places the player (icon 1, with heading), the target (icon 4) and the party members (icon 3) on the map control.</item>
    /// <item>CHASEPLAYINFO 890000 (draw 0x556780 on message 0x2001 while the game mode is 3): the time left as digit art
    /// (TIME_6..TIME_0 = m m s s . t t t, TIME_6 only when the minutes reach ten) and the HUV count 0..99 (HUVCOUNT /
    /// HUVCOUNT_TEN, the tens only when not zero); CANCEL (Esc, 0x557480) asks 10727 "Do you really want to quit the Chase?".</item>
    /// <item>CHASERESULT 970000 / PARTYCHASERESULT 980000 (packet 0x4f9dcd): TIME "%d\"%d\"%d" (minutes, seconds,
    /// thousandths x 60 / 1000), per row the name, EXP "%d" and "%d Mito" (base + bonus, 0x5539f0), LVUP icons. The exe's
    /// one-driver filler is an empty stub (0x62ef70 `ret 8`), so a solo chase shows only chat 10617 / 10618 and the rising
    /// reward lines; the remake shows CHASERESULT for it as well.</item>
    /// <item>QUEST 1780000 (0x5612a0 / 0x561350 / 0x5613a0): "%d EXP" (10172) and "%d Mito" (10173) rising from LOW to HI
    /// over 1.4 s after a reward.</item>
    /// <item>QUEST 1770000 (0x5531e0, 0x563710): a new best time as large digit art (POS0..6 = m m s s t t t).</item>
    /// </list>
    /// </summary>
    public static class ChaseScreens
    {
        public const int OfferId = 870000, EventOfferId = 880000, PlayInfoId = 890000, ResultId = 970000, PartyResultId = 980000;
        public const int RewardId = 1780000, NewRecordId = 1770000;
        public static readonly Dictionary<string, int> InfoIds = new Dictionary<string, int>
        {
            ["moonpalace"] = 610000, ["koinonia"] = 620000, ["cras"] = 630000, ["oros"] = 640000,
        };

        // ---------------- digit art ----------------

        /// <summary>Copies a digit image (texture and uv) onto a slot control's image.</summary>
        public static void CopyArt(NuiView v, string slot, string art, bool show = true)
        {
            var dst = MissionUi.Image(v, slot);
            var src = MissionUi.Image(v, art);
            if (!dst) return;
            var c = v.Control(slot);
            if (c) c.gameObject.SetActive(show);
            if (!show || !src) return;
            dst.texture = src.texture; dst.uvRect = src.uvRect; dst.enabled = src.texture;
        }

        static void HideAll(NuiView v, string prefix, int from, int to)
        {
            for (int i = from; i <= to; i++) v.SetVisible(prefix + i, false);
        }

        // ---------------- CHASING / EVENTCHASING ----------------

        /// <summary>The event art (EVENTCHASING) instead of the plain call: Christmas season (net+0x288 bit 7).</summary>
        public static bool EventStyle => SeasonEvents.Is(EventSeason.Christmas);

        /// <summary>Shows the chase call (870000 / 880000). Tab or the TAB button accepts, Esc or CANCEL refuses.</summary>
        public static NuiView Offer(Action accept, Action refuse, bool? eventStyle = null)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            CloseOffer();
            bool ev = eventStyle ?? EventStyle;
            int id = ev ? EventOfferId : OfferId;
            var v = ui.Open(id);
            if (!v) return null;
            string p = ev ? "ID_EVENTCHASING_" : "ID_CHASING_";
            var blink = v.gameObject.AddComponent<ChaseOfferBlink>();
            blink.Prefix = p;
            blink.Accept = accept; blink.Refuse = refuse;
            blink.Apply(0);
            AudioLibrary.PlaySfx("sfx/SID_CALLSIGN_RING");   // 0x4e98 when the call comes up (0x6f814e)
            ui.On(p + "TABBUTTON", view => { ui.Close(view); AudioLibrary.PlaySfx("sfx/SID_CALLSIGN_ACCEPT"); accept?.Invoke(); });
            ui.On(p + "CANCEL", view => { ui.Close(view); AudioLibrary.PlaySfx("sfx/SID_CALLSIGN_DENY"); refuse?.Invoke(); });
            return v;
        }

        public static NuiView OfferView
        {
            get
            {
                var ui = UiManager.Instance;
                return ui ? (ui.Find(OfferId) ?? ui.Find(EventOfferId)) : null;
            }
        }

        public static void CloseOffer()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            foreach (int id in new[] { OfferId, EventOfferId }) { var o = ui.Find(id); if (o) ui.Close(o); }
        }

        /// <summary>0x6f5a40 state from the call's age: fmod(t, 0.8) &lt;= 0.08 -> 1, &lt;= 0.16 -> 2, &lt;= 0.24 -> 3, else 0.</summary>
        public static int BlinkState(float t)
        {
            float r = t % 0.8f;
            return r <= 0.08f ? 1 : r <= 0.16f ? 2 : r <= 0.24f ? 3 : 0;
        }

        // ---------------- CHASEINFO ----------------

        /// <summary>The city's chase map with the player, the target and the party members on it (0x6fd500).</summary>
        public static NuiView Info(string city, Func<Vector3?> target)
        {
            var ui = UiManager.Instance;
            if (!ui || city == null || !InfoIds.TryGetValue(city, out int id)) return null;
            var old = ui.Find(id);
            if (old) ui.Close(old);
            var v = ui.Open(id);
            if (!v) return null;
            string img = city switch { "moonpalace" => "ID_CHASEINFO_MOONPALACE_IMG", "koinonia" => "ID_CHASEINFO_KOINONIA_IMG", "cras" => "ID_CHASEINFO_CRAS_IMG", _ => "ID_CHASEINFO_OROS_IMG" };
            var region = v.Control(img);
            var map = CityMapInfo.ForCity(city);
            if (region && map != null)
            {
                // KO / CR / Oros: the IMG control is only the icon region over the popup's own map art
                var own = MissionUi.Image(v, img);
                if (own && !own.texture) own.enabled = false;
                // the icons go on an overlay over the IMG rectangle, above the popup's map art (drawn after IMG)
                var src = (RectTransform)region.transform;
                var overlay = NuiView.NewRect("Chase Info Icons", src.parent);
                overlay.anchorMin = src.anchorMin; overlay.anchorMax = src.anchorMax; overlay.pivot = src.pivot;
                overlay.anchoredPosition = src.anchoredPosition; overlay.sizeDelta = src.sizeDelta;
                overlay.SetAsLastSibling();
                var drawer = overlay.gameObject.AddComponent<ChaseInfoMap>();
                drawer.Region = overlay;
                drawer.Map = map;
                drawer.Target = target;
            }
            return v;
        }

        // ---------------- CHASEPLAYINFO ----------------

        /// <summary>Opens 890000 with the digit art hidden (the client only copies NUM images onto the slots).</summary>
        public static NuiView OpenPlayInfo(Action cancel)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            var old = ui.Find(PlayInfoId);
            if (old) ui.Close(old);
            var v = ui.Open(PlayInfoId);
            if (!v) return null;
            HideAll(v, "ID_CHASEPLAYINFO_TIME_NUM_", 0, 9);
            HideAll(v, "ID_CHASEPLAYINFO_HUVCOUNT", 0, 9);
            HideAll(v, "ID_CHASEPLAYINFO_HUVCOUNT_TEN", 0, 9);
            ui.On("ID_CHASEPLAYINFO_CANCEL", _ => MissionUi.Confirm(MissionText.Get("10727"), () => cancel?.Invoke()));
            return v;
        }

        /// <summary>0x556780: time left (minutes of the hour, seconds, thousandths) and the HUV count.</summary>
        public static void PlayInfo(NuiView v, float secondsLeft, int huvCount)
        {
            if (!v) return;
            int whole = Mathf.Max(0, Mathf.FloorToInt(secondsLeft));
            int ms = Mathf.Clamp(Mathf.FloorToInt((secondsLeft - whole) * 1000f), 0, 999);
            int t = whole % 3600, m = t / 60, s = t % 60;
            const string N = "ID_CHASEPLAYINFO_TIME_NUM_", T = "ID_CHASEPLAYINFO_TIME_";
            CopyArt(v, T + "6", N + (m / 10 % 10), m / 10 != 0);
            CopyArt(v, T + "5", N + (m % 10));
            CopyArt(v, T + "4", N + (s / 10));
            CopyArt(v, T + "3", N + (s % 10));
            CopyArt(v, T + "2", N + (ms / 100));
            CopyArt(v, T + "1", N + (ms % 100 / 10));
            CopyArt(v, T + "0", N + (ms % 10));
            int n = Mathf.Clamp(huvCount, 0, 99);
            for (int d = 0; d < 10; d++)
            {
                v.SetVisible("ID_CHASEPLAYINFO_HUVCOUNT" + d, d == n % 10);
                v.SetVisible("ID_CHASEPLAYINFO_HUVCOUNT_TEN" + d, n / 10 != 0 && d == n / 10);
            }
        }

        // ---------------- results ----------------

        public sealed class ResultRow
        {
            public string Name;
            public int Level;
            public long Exp, Mito;
            public int LevelUps;
        }

        /// <summary>'%d"%d"%d' of 0x5539f0: minutes, seconds, thousandths x 60 / 1000.</summary>
        public static string ResultTime(float seconds)
        {
            int ms = Mathf.Max(0, Mathf.FloorToInt(seconds * 1000f));
            return MissionText.Printf("%d\"%d\"%d", ms / 60000, ms % 60000 / 1000, ms % 1000 * 60 / 1000);
        }

        /// <summary>CHASERESULT 970000: one driver's time, EXP and Mito.</summary>
        public static NuiView Result(float seconds, long exp, long mito)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            CloseResults();
            var v = ui.Open(ResultId);
            if (!v) return null;
            v.SetText("ID_CHASERESULT_TIME", ResultTime(seconds));
            v.SetText("ID_CHASERESULT_EXP", MissionText.Printf("%d", exp));
            v.SetText("ID_CHASERESULT_MITO", MissionText.Printf("%d Mito", mito));
            ui.On("ID_CHASERESULT_OK", view => ui.Close(view));
            return v;
        }

        /// <summary>PARTYCHASERESULT 980000: up to four rows (0x5539f0); empty rows print "0.0" like the client.</summary>
        public static NuiView PartyResult(float seconds, IList<ResultRow> rows)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            CloseResults();
            var v = ui.Open(PartyResultId);
            if (!v) return null;
            v.SetText("ID_PARTYCHASERESULT_TIME", ResultTime(seconds));
            for (int i = 0; i < 4; i++)
            {
                string k = (i + 1).ToString();
                bool on = rows != null && i < rows.Count && rows[i] != null;
                var r = on ? rows[i] : null;
                v.SetText("ID_PARTYCHASERESULT_USER" + k, on ? r.Name : "0.0");
                v.SetText("ID_PARTYCHASERESULT_EXP" + k, on ? MissionText.Printf("%d", r.Exp) : "0.0");
                v.SetText("ID_PARTYCHASERESULT_MITO" + k, on ? MissionText.Printf("%d Mito", r.Mito) : "0.0");
                v.SetVisible("ID_PARTYCHASERESULT_LVUP" + k, on && r.LevelUps > 0);
            }
            ui.On("ID_PARTYCHASERESULT_OK", view => ui.Close(view));
            return v;
        }

        public static void CloseResults()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            foreach (int id in new[] { ResultId, PartyResultId }) { var o = ui.Find(id); if (o) ui.Close(o); }
        }

        // ---------------- QUEST 1780000: rising reward lines ----------------

        /// <summary>0x5612a0 / 0x561350 / 0x560ac0: "%d EXP" and "%d Mito" rise from LOW to HI over 1.4 s.</summary>
        public static NuiView RewardPopup(long exp, long mito)
        {
            var ui = UiManager.Instance;
            if (!ui || (exp <= 0 && mito <= 0)) return null;
            var old = ui.Find(RewardId);
            if (old) ui.Close(old);
            var v = ui.Open(RewardId);
            if (!v) return null;
            for (int d = 0; d <= 9; d++) v.SetVisible("ID_QUEST_REWARD_DAMAGENUM" + d, false);
            foreach (var s in new[] { "1ST", "2ND", "3RD", "4TH", "5TH" }) v.SetVisible("ID_QUEST_REWARD_DAMAGENUMPOS" + s, false);
            v.SetVisible("ID_QUEST_REWARD_DAMAGE", false);
            v.SetVisible("ID_QUEST_REWARD_LOW", false);
            v.SetVisible("ID_QUEST_REWARD_HI", false);
            v.SetVisible("ID_QUEST_REWARD_EXP", exp > 0);
            v.SetVisible("ID_QUEST_REWARD_MITO", mito > 0);
            if (exp > 0) v.SetText("ID_QUEST_REWARD_EXP", MissionText.Get("10172", exp));
            if (mito > 0) v.SetText("ID_QUEST_REWARD_MITO", MissionText.Get("10173", mito));
            v.gameObject.AddComponent<RewardRise>().Init(v);
            return v;
        }

        // ---------------- QUEST 1770000: new best time ----------------

        /// <summary>0x5531e0: qs_newrecord and the time in the large digit art (m m ' s s " t t t); hides after 5 s.</summary>
        public static NuiView NewRecord(float seconds, float hideAfter = 5f)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            var old = ui.Find(NewRecordId);
            if (old) ui.Close(old);
            var v = ui.Open(NewRecordId);
            if (!v) return null;
            GameEffects.Screen("qs_newrecord");
            const string N = "ID_QUEST_BESTTIME_RENEWAL_NUM", P = "ID_QUEST_BESTTIME_RENEWAL_POS";
            for (int d = 0; d < 10; d++) v.SetVisible(N + d, false);
            int ms = Mathf.Clamp(Mathf.FloorToInt(seconds * 1000f), 0, 99 * 60000 + 59999);
            int m = ms / 60000, s = ms / 1000 % 60, f = ms % 1000;
            int[] digits = { m / 10, m % 10, s / 10, s % 10, f / 100, f / 10 % 10, f % 10 };
            for (int k = 0; k < 7; k++) CopyArt(v, P + k, N + digits[k]);
            v.gameObject.AddComponent<AutoClose>().Seconds = hideAfter;
            return v;
        }
    }

    /// <summary>The CHASING call's NOTIFY cycle (0x6f5a40), Tab accepts (J = 9).</summary>
    public sealed class ChaseOfferBlink : MonoBehaviour
    {
        public string Prefix;
        public Action Accept, Refuse;
        float age;
        NuiView view;

        public void Apply(int state)
        {
            if (!view) view = GetComponent<NuiView>();
            if (!view) return;
            for (int i = 0; i < 4; i++) view.SetVisible(Prefix + "NOTIFY" + (i + 1), i == state);
        }

        void Update()
        {
            age += Time.deltaTime;
            Apply(ChaseScreens.BlinkState(age));
            if (MissionUi.TabPressed() && view) { UiSounds.Click(); view.Click(Prefix + "TABBUTTON"); }
        }
    }

    /// <summary>CHASEINFO's map icons (0x6fd500 -> 0x5f3fe0): the player (1), the target (4), party members (3).</summary>
    public sealed class ChaseInfoMap : MonoBehaviour
    {
        public RectTransform Region;
        public CityMapInfo Map;
        public Func<Vector3?> Target;
        RectTransform me, target;
        readonly List<RectTransform> party = new List<RectTransform>();

        RectTransform Icon(string name, string minimapIcon, Color fallback, float size)
        {
            var rt = NuiView.NewRect(name, Region);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            var mini = UiManager.Instance ? UiManager.Instance.Find(190000) : null;
            var src = mini ? MissionUi.Image(mini, minimapIcon) : null;
            if (src && src.texture)
            {
                var img = rt.gameObject.AddComponent<RawImage>();
                img.texture = src.texture; img.uvRect = src.uvRect; img.raycastTarget = false;
                rt.sizeDelta = src.rectTransform.sizeDelta;
            }
            else { var img = rt.gameObject.AddComponent<Image>(); img.color = fallback; img.raycastTarget = false; }
            return rt;
        }

        void Place(RectTransform rt, Vector3 world, float? headingDeg)
        {
            var f = Map.Fraction(world);
            var size = Region.rect.size;
            rt.anchoredPosition = new Vector2(Mathf.Clamp01(f.x) * size.x, -Mathf.Clamp01(f.y) * size.y);
            rt.localEulerAngles = headingDeg.HasValue ? new Vector3(0, 0, -headingDeg.Value) : Vector3.zero;
            rt.gameObject.SetActive(true);
        }

        void LateUpdate()
        {
            if (!Region || Map == null) return;
            var flow = GameFlow.Instance;
            var car = flow ? flow.PlayerCar : null;
            if (car)
            {
                if (!me) me = Icon("Me", "ID_MINIMAP_ICON_MINE", new Color(1f, 0.85f, 0.1f), 10);
                var fwd = car.transform.forward;
                Place(me, car.transform.position, Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg);
            }
            var t = Target?.Invoke();
            if (t.HasValue)
            {
                if (!target) target = Icon("Target", "ID_MINIMAP_ICON_HUV", new Color(1f, 0.2f, 0.1f), 12);
                Place(target, t.Value, null);
            }
            else if (target) target.gameObject.SetActive(false);
            int n = 0;
            var session = Net.OnlineSession.Instance;
            if (session && session.Remotes && Social.PartyWindow.InParty)
                foreach (var rc in session.Remotes.All)
                {
                    if (!rc || rc.DriverName == null || !Social.PartyWindow.Members.Exists(m => m.Name == rc.DriverName)) continue;
                    if (party.Count <= n) party.Add(Icon("Party", "ID_MINIMAP_ICON_PARTY", new Color(0.2f, 0.8f, 1f), 10));
                    var f = rc.transform.forward;
                    Place(party[n++], rc.transform.position, Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg);
                }
            for (int i = n; i < party.Count; i++) if (party[i]) party[i].gameObject.SetActive(false);
        }
    }

    /// <summary>0x5613a0: the reward lines rise 220 px over 1.4 s and fade out over the last third.</summary>
    public sealed class RewardRise : MonoBehaviour
    {
        public const float Duration = 1.4f, Rise = 220f;
        readonly List<(RectTransform rt, Vector2 start, Text[] texts)> lines = new List<(RectTransform, Vector2, Text[])>();
        float t;

        public void Init(NuiView v)
        {
            float offset = 0;
            foreach (var id in new[] { "ID_QUEST_REWARD_EXP", "ID_QUEST_REWARD_MITO" })
            {
                var c = v.Control(id);
                if (!c || !c.gameObject.activeSelf) continue;
                var rt = (RectTransform)c.transform;
                rt.anchoredPosition += new Vector2(0, -offset);
                offset += 34f;
                lines.Add((rt, rt.anchoredPosition, c.GetComponentsInChildren<Text>(true)));
            }
        }

        void Update()
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / Duration);
            float a = k < 0.66f ? 1f : Mathf.Clamp01((1f - k) / 0.34f);
            foreach (var (rt, start, texts) in lines)
            {
                if (!rt) continue;
                rt.anchoredPosition = start + new Vector2(0, Rise * k);
                foreach (var tx in texts) { var c = tx.color; c.a = a; tx.color = c; }
            }
            if (t >= Duration) { var v = GetComponent<NuiView>(); if (v && UiManager.Instance) UiManager.Instance.Close(v); else Destroy(gameObject); }
        }
    }

    /// <summary>Closes the dialog it sits on after some seconds.</summary>
    public sealed class AutoClose : MonoBehaviour
    {
        public float Seconds = 5f;
        void Update()
        {
            Seconds -= Time.deltaTime;
            if (Seconds > 0) return;
            var v = GetComponent<NuiView>();
            if (v && UiManager.Instance) UiManager.Instance.Close(v); else Destroy(gameObject);
        }
    }
}
