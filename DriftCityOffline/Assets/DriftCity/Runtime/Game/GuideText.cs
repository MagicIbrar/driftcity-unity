using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>The 18 hint strips of GUIDETEXT 920000 / 930000 (ADV), in control order.</summary>
    public enum GuideKind
    {
        QuestGet, RequestHouseGo, RequestHouseSelect, QuestIng, ArbeitIng, TimeAttackIng, HuvStart, HuvFind, HuvCrash,
        FuelWarning, MoonPalace, Koinonia, Cras, Oros, GpsLoading, FuelEmpty, BeginnerQuest, BossHp,
    }

    /// <summary>
    /// The context hints over the top of the screen (DriftCity_RE/OPTIONS.md): GUIDETEXT 930000 for the ADV UI
    /// (goption MainUIType 1, the remaster's) or 920000 for the basic one; each hint is a pre-drawn text image. The
    /// client's guide object (0x5c2450, vtable 0x870ae0, held at UI manager +0x194) keeps one visibility flag per strip
    /// that the game code sets; its update (0x5c26b0) blinks GPSLOADING while a GPS load runs (0x5c2950) and hides
    /// BOSSHP when its timer runs out; 0x5c28c0 hides them all (stage changes). The remake shows one strip at a time:
    /// the newest flash, else the highest standing state (the strips share one place on screen). States are read from
    /// the game each quarter second (quests, delivery, race, HUV chase, booster gauge); other code can flash or hold a
    /// strip with <see cref="Flash"/> / <see cref="Hold"/>.
    /// </summary>
    public static class GuideText
    {
        public const int Basic = 920000, Adv = 930000;
        static readonly string[] Ids =
        {
            "QUEST_GET", "REQUESTHOUSE_GO", "REQUESTHOUSE_SELECT", "QUEST_ING", "ARBEIT_ING", "TIMEATTACK_ING", "HUV_START", "HUV_FIND", "HUV_CRASH",
            "FUELWARNING", "MOONPALACE", "KOINONIA", "CRAS", "OROS", "GPSLOADING", "FUELMEMPTY", "BEGINNERQUEST", "BOSSHP",
        };

        public static string ControlId(GuideKind k, bool adv) => (adv ? "ID_GUIDETEXT_ADV_" : "ID_GUIDETEXT_") + Ids[(int)k];

        static readonly Dictionary<GuideKind, float> flashes = new Dictionary<GuideKind, float>();
        static readonly HashSet<GuideKind> held = new HashSet<GuideKind>();
        static GuideKind? lastFlash;

        /// <summary>Shows a strip for some seconds (QUEST_GET, HUV_START, a city name ...).</summary>
        public static void Flash(GuideKind k, float seconds = 4f)
        {
            flashes[k] = Time.unscaledTime + seconds;
            lastFlash = k;
            GuideTextDriver.Ensure();
        }

        /// <summary>Keeps a strip up until released (states the poller can't see).</summary>
        public static void Hold(GuideKind k, bool on)
        {
            if (on) held.Add(k); else held.Remove(k);
            GuideTextDriver.Ensure();
        }

        /// <summary>0x5c2950: GPSLOADING blinks while on.</summary>
        public static void SetGpsLoading(bool on) => Hold(GuideKind.GpsLoading, on);

        /// <summary>0x5c28c0: everything off (a stage change).</summary>
        public static void HideAll() { flashes.Clear(); held.Clear(); lastFlash = null; GuideTextDriver.Clear(); }

        /// <summary>The strip on screen now, or null.</summary>
        public static GuideKind? Shown => GuideTextDriver.Shown;

        /// <summary>Standing states read from the game, highest priority first.</summary>
        internal static readonly List<GuideKind> polled = new List<GuideKind>();

        /// <summary>The strip to show: the newest live flash, else held / polled states by priority.</summary>
        internal static GuideKind? Pick()
        {
            float now = Time.unscaledTime;
            foreach (var k in flashes.Where(f => f.Value <= now).Select(f => f.Key).ToList()) flashes.Remove(k);
            if (lastFlash.HasValue && flashes.ContainsKey(lastFlash.Value)) return lastFlash;
            if (flashes.Count > 0) return flashes.OrderByDescending(f => f.Value).First().Key;
            foreach (var k in Priority) if (held.Contains(k) || polled.Contains(k)) return k;
            return null;
        }

        static readonly GuideKind[] Priority =
        {
            GuideKind.GpsLoading, GuideKind.BossHp, GuideKind.FuelEmpty, GuideKind.FuelWarning, GuideKind.HuvCrash, GuideKind.HuvFind, GuideKind.HuvStart,
            GuideKind.TimeAttackIng, GuideKind.ArbeitIng, GuideKind.RequestHouseSelect, GuideKind.RequestHouseGo, GuideKind.QuestIng, GuideKind.BeginnerQuest,
            GuideKind.QuestGet, GuideKind.MoonPalace, GuideKind.Koinonia, GuideKind.Cras, GuideKind.Oros,
        };

        public static GuideKind? CityKind(string city) => (city ?? "").ToLowerInvariant() switch
        {
            "moonpalace" => GuideKind.MoonPalace, "koinonia" => GuideKind.Koinonia, "cras" => GuideKind.Cras, "oros" => GuideKind.Oros, _ => (GuideKind?)null,
        };
    }

    /// <summary>Owns the GUIDETEXT dialog and polls the game state for the hints.</summary>
    public sealed class GuideTextDriver : MonoBehaviour
    {
        static GuideTextDriver instance;
        NuiView view;
        bool adv;
        float nextPoll;
        GuideKind? shown;

        // what the poller saw last time (to flash on changes)
        int questCount = -1;
        string city;
        ChaseMission chase;
        bool huvSeen, huvHit;
        TrafficCar huvCar;
        float emptySince = -1f;

        public static GuideKind? Shown => instance ? instance.shown : null;
        public static NuiView View => instance ? instance.view : null;

        public static void Ensure()
        {
            if (instance || !Application.isPlaying) return;
            var go = new GameObject("Drift City Guide Text");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<GuideTextDriver>();
        }

        public static void Clear() { if (instance) instance.Show(null); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        /// <summary>The hints show while driving in a city (HUD up, no full-screen menu over it).</summary>
        static bool InCity(out GameFlow flow)
        {
            flow = GameFlow.Instance;
            return flow && flow.PlayerCar && CitySession.Current && UiManager.Instance;
        }

        void Update()
        {
            if (!InCity(out var flow))
            {
                if (view) { UiManager.Instance?.Close(view); view = null; }
                shown = null;
                questCount = -1; city = null; chase = null; lastGauge = -1f;
                return;
            }
            if (Time.unscaledTime >= nextPoll) { nextPoll = Time.unscaledTime + 0.25f; Poll(flow); }
            var pick = GuideText.Pick();
            if (pick != shown) Show(pick);
            // 0x5c26b0: the GPS strip blinks (on half the time)
            if (shown == GuideKind.GpsLoading && view)
            {
                var c = view.Control(GuideText.ControlId(GuideKind.GpsLoading, adv));
                if (c) c.gameObject.SetActive(Mathf.Repeat(Time.unscaledTime, 1f) < 0.5f);
            }
        }

        void Show(GuideKind? k)
        {
            shown = k;
            if (!k.HasValue) { if (view) foreach (var c in view.Controls) c.gameObject.SetActive(false); return; }
            bool wantAdv = GameOptions.MainUIType != 0;
            if (view && wantAdv != adv) { UiManager.Instance.Close(view); view = null; }
            if (!view)
            {
                adv = wantAdv;
                view = UiManager.Instance.Open(adv ? GuideText.Adv : GuideText.Basic);
                if (!view) return;
                foreach (var g in view.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) g.raycastTarget = false;   // never takes clicks
            }
            foreach (var c in view.Controls) c.gameObject.SetActive(false);
            var on = view.Control(GuideText.ControlId(k.Value, adv));
            if (on) on.gameObject.SetActive(true);
        }

        float gpsUntil;
        float lastGauge = -1f;   // -1 until the first look (a car starts with an empty gauge)

        /// <summary>
        /// The strips' own texts (ui_contents52.dds, read off Preview/guidetext_all.png) decide the triggers [inferred]:
        /// QUEST_GET "Go to the Station to receive quests." (no quest taken), BEGINNERQUEST "Follow the arrow to begin
        /// a rookie mission." (a new driver), QUEST_ING "Mission In Progress.", REQUESTHOUSE_GO "Follow the arrow to the
        /// Station." (a quest done, to report), REQUESTHOUSE_SELECT "Go to the Station menu." (at a station: ENTRANCE
        /// 400000 or the delivery board up), ARBEIT_ING "Delivery Service In Progress.", TIMEATTACK_ING "Time Attack In
        /// Progress." (lap time battle or a race), HUV_START "Press Tab to start chasing." (the chase offer 860000 up),
        /// HUV_FIND "HUV found! Arrest the enemy car by hitting it." / HUV_CRASH "Enemy car found! ... colliding with
        /// it." (a HUV / boss HUV within 80 m), FUELWARNING / FUELMEMPTY "You are low on Mittron fuel. Fill up soon."
        /// (the gauge dropping under 10 % / boosting on empty), GPSLOADING "Receiving city's GPS data..." (blinks on
        /// arrival until the minimap has its map). The four city strips "Go to X to receive Missions." and BOSSHP "HUVs
        /// have restored the Boss HUV's HP." are left to their callers (Flash).
        /// </summary>
        void Poll(GameFlow flow)
        {
            var list = GuideText.polled;
            list.Clear();
            var p = PlayerProfile.Current;
            var ui = UiManager.Instance;

            // a city entered: the GPS strip blinks until the minimap has the city's map (at least 1.5 s, at most 4 s)
            if (!string.Equals(city, flow.City, StringComparison.OrdinalIgnoreCase))
            {
                city = flow.City;
                gpsUntil = Time.unscaledTime + 4f;
                gpsFrom = Time.unscaledTime;
            }
            if (Time.unscaledTime < gpsUntil)
            {
                var mm = flow.GetComponent<Minimap>();
                bool ready = mm && mm.Map != null && mm.Disc;
                if (ready && Time.unscaledTime > gpsFrom + 1.5f) gpsUntil = 0f;
                else list.Add(GuideKind.GpsLoading);
            }

            if (p != null)
            {
                int n = p.ActiveQuests.Count;
                questCount = n;
                bool done = false;
                foreach (var a in p.ActiveQuests)
                {
                    var q = QuestTracker.All.FirstOrDefault(x => x.Id == a.QuestId);
                    if (q != null && a.Counter >= q.GoalCount) done = true;
                }
                if (done) list.Add(GuideKind.RequestHouseGo);
                else if (n > 0) list.Add(GuideKind.QuestIng);
                else if (!QuestUnlocks.Of(p).Rookie) list.Add(GuideKind.BeginnerQuest);   // the beginner panel's strip until the rookie missions are done
                else list.Add(GuideKind.QuestGet);
            }

            // at a station: its menu (ENTRANCE 400000) or the request house's board
            if (DeliveryService.BoardOpen || (ui && ui.Find(400000))) list.Add(GuideKind.RequestHouseSelect);
            var d = DeliveryService.Active;
            if (d && d.Job != null) list.Add(GuideKind.ArbeitIng);
            if (LapTimeBattle.Active || RaceMission.Active) list.Add(GuideKind.TimeAttackIng);
            // the chase offer banner (INSTANTMISSION 860000, Tab accepts)
            if (ui && ui.Find(860000)) list.Add(GuideKind.HuvStart);

            // the HUV chase: the HUV comes within 80 m (a boss: the "enemy car" strip); the first crash into it
            var ch = ChaseMission.Active;
            if (ch != chase)
            {
                chase = ch;
                huvSeen = huvHit = false;
                huvCar = null;
            }
            if (ch)
            {
                if (!huvCar)
                {
                    foreach (var t in FindObjectsByType<TrafficCar>(FindObjectsSortMode.None))
                        if (t && t.name.StartsWith("HUV ")) { huvCar = t; t.Hit += OnHuvHit; break; }
                }
                if (huvCar && !huvSeen && flow.PlayerCar && Vector3.Distance(huvCar.transform.position, flow.PlayerCar.transform.position) < 80f)
                {
                    huvSeen = true;
                    string name = huvCar.name.Substring(4);
                    bool boss = GameTables.BossHuvs.Any(b => b.UiName == name);
                    GuideText.Flash(boss ? GuideKind.HuvCrash : GuideKind.HuvFind, 4f);
                }
            }

            // FUELWARNING / FUELMEMPTY follow the Mittron fuel tank (Fuel/MittronFuelMeter), not the booster gauge
        }

        float gpsFrom;

        void OnHuvHit(Collision c)
        {
            if (huvHit || !c.collider || !c.collider.GetComponentInParent<DriftCar>()) return;
            var flow = GameFlow.Instance;
            if (!flow || c.collider.GetComponentInParent<DriftCar>() != flow.PlayerCar) return;
            huvHit = true;   // the arrest has begun: the find strip makes way
            GuideText.Flash(GuideKind.HuvFind, 0.01f);
        }
    }
}
