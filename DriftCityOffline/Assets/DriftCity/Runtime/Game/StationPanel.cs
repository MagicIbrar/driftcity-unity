using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The city's mission stations (RQ houses "Station 1".."Station 4" of World/Data/Icon_&lt;city&gt;_Client.tdf): driving
    /// up to one opens the client's ENTRANCE panel (game.nui 400000) at the side of the screen without stopping the car,
    /// as the remaster footage shows: Mission, Delivery Service (<see cref="DeliveryService"/>) and Battle Zone, then
    /// Pick Up Mission and Enter Driver Dome (keys 1-5, the controls' NATION J). Driving away closes it. The trigger distance isn't stored in the client
    /// data (the stations come from the icon table only), so <see cref="Radius"/> is this rebuild's own.
    /// </summary>
    public sealed class StationPanel : MonoBehaviour
    {
        public float Radius = 30f;
        public CitySession Session;

        System.Collections.Generic.List<CityMapInfo.Icon> stations;
        NuiView view;
        CityMapInfo.Icon current;
        float nextCheck;

        public static StationPanel Attach(CitySession session, string city)
        {
            var panel = session.gameObject.AddComponent<StationPanel>();
            panel.Session = session;
            // every "Station N" row (Moon Palace's are flagged not shown on the map, column 3, but they are the main story's
            // first quest givers and the Delivery Service's a_00001.. starts)
            panel.stations = CityMapInfo.Stations(city);
            // Team ROO's stations (icon table column 4 "ROO": Roo Outpost, Team Roo HQ) open the same panel (0x562030 +0x210)
            panel.stations.AddRange(CityMapInfo.Icons(city).FindAll(i => Factions.IsRooPost(i.Id) && !panel.stations.Contains(i)));
            StepQuestRunner.Attach(session, city);
            return panel;
        }

        void Update()
        {
            var flow = GameFlow.Instance;
            var car = flow ? flow.PlayerCar : null;
            if (!car || stations == null) return;
            if (view) Keys();
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.25f;
            if (view && current != null) Badges(current);
            CityMapInfo.Icon near = null;
            float best = Radius * Radius;
            var p = car.transform.position;
            foreach (var s in stations)
            {
                float d = (new Vector2(s.Position.x, s.Position.z) - new Vector2(p.x, p.z)).sqrMagnitude;
                if (d < best) { best = d; near = s; }
            }
            // missions and races own the car: the stations wait. A delivery or patrol keeps them: the panel then shows the
            // delivery's "in progress" badge and its button opens the delivery log (MYARBEIT)
            // (a step quest keeps them too: ING_QUEST, and the end post may be a station)
            if (GameFlow.MissionActive && !DeliveryService.Active && !PatrolMission.Active &&
                !(StepQuests.Running(PlayerProfile.Current) != null && !ChaseMission.Active)) near = null;
            if (near == current && (near == null || view)) return;   // (a menu that closed every dialog reopens it)
            Close();
            current = near;
            if (near != null) Open(near);
        }

        /// <summary>A Team ROO station (icon table column 4 "ROO").</summary>
        public static bool IsRooStation(CityMapInfo.Icon station) => station != null && Factions.IsRooPost(station.Id);

        void Open(CityMapInfo.Icon station)
        {
            var ui = UiManager.Instance;
            view = ui.Open(400000);
            if (!view) return;
            bool roo = IsRooStation(station);
            // 0x5625e0: before the rookie missions are over (quest manager +0x108, QuestUnlocks.Rookie) the station shows the
            // beginner panel (0x561f80): only the big BEGINNERQUEST button (and ING_BEGINNERQUES while a quest runs) besides
            // the title, Mittron and the parking lot; afterwards the full panel (0x562030): an OMD station shows QUEST /
            // ARBEIT, a ROO station TEAMROO (its floor art), SECRETMISSION and TIMEATTACK, both with Battle Zone (CHASING).
            // The event Delivery Service pair (EVENTARBEIT / EVENTTIMEATTACK in place of ARBEIT / TIMEATTACK, net+0x290 &
            // 0x20000) and the station's instant mission (INSTANT in QUEST's place, net+0x24c & 0x100000) are server
            // switches (Services/ServerSwitches: eventDelivery / instant; offline the delivery event seasons / on).
            beginner = !QuestUnlocks.Current.Rookie;
            eventPair = Services.ServerSwitches.EventDelivery;
            instantOn = !roo && Services.ServerSwitches.Instant;
            string arbeitShown = roo ? (eventPair ? "ID_ENTRANCE_RQHOUSE_EVENTTIMEATTACK" : "ID_ENTRANCE_RQHOUSE_TIMEATTACK")
                                     : (eventPair ? "ID_ENTRANCE_RQHOUSE_EVENTARBEIT" : "ID_ENTRANCE_RQHOUSE_ARBEIT");
            foreach (var c in view.Controls)
            {
                string id = c.Data.IdName;
                bool keep = id == "ID_ENTRANCE_RQHOUSE_TITLE" || id == "ID_ENTRANCE_RQHOUSE_MITRON" || id == "ID_ENTRANCE_RQHOUSE_PARKINGLOT" ||
                            (roo && id == "ID_ENTRANCE_RQHOUSE_TEAMROO") ||
                            (beginner ? id == "ID_ENTRANCE_RQHOUSE_BEGINNERQUEST" || id == "ID_ENTRANCE_RQHOUSE_ING_BEGINNERQUES" :
                             id == "ID_ENTRANCE_RQHOUSE_CHASING" ||
                             id == arbeitShown || (instantOn && id == "ID_ENTRANCE_RQHOUSE_INSTANT") ||
                             (roo ? id == "ID_ENTRANCE_RQHOUSE_SECRETMISSION" : id == "ID_ENTRANCE_RQHOUSE_QUEST"));
                c.gameObject.SetActive(keep);
            }
            // TEAMROO (400002) is drawn over the OMD floor (400001) in the dialog's own order
            view.SetText("ID_ENTRANCE_RQHOUSE_TITLE", station.Name);
            Badges(station);
            // QUEST / SECRETMISSION: the station's step quest (QuestClient.tdf), its end, or 10191; the panel steps aside meanwhile
            System.Action<NuiView> quests = v =>
            {
                if (v) v.gameObject.SetActive(false);
                StepQuestRunner.OpenStation(station, () => { if (v) v.gameObject.SetActive(true); });
            };
            ui.On("ID_ENTRANCE_RQHOUSE_QUEST", quests);
            ui.On("ID_ENTRANCE_RQHOUSE_SECRETMISSION", quests);
            // BEGINNERQUEST (0x61a97) goes the same way as QUEST in the handler 0x561610: the station's quest dialog
            ui.On("ID_ENTRANCE_RQHOUSE_BEGINNERQUEST", quests);
            // "Fill up Mittron": the station's charger (CHARGER 940000, 0x561e9b); the panel steps aside while it is open
            ui.On("ID_ENTRANCE_RQHOUSE_MITRON", v => { if (v) v.gameObject.SetActive(false); MittronCharger.Open(() => { if (v) v.gameObject.SetActive(true); }); });
            ui.On("ID_ENTRANCE_RQHOUSE_CHASING", _ =>
            {
                // 10623: "You cannot open the Battle Zone Menu while doing a Delivery Service." (Team ROO: 10669, Time Attack)
                if (DeliveryService.Active || PatrolMission.Active) { UiPopup.Message(MissionText.Get(DeliveryService.Active ? Factions.DeliveryText("10623") : "10622")); return; }
                if (Session) RaceMission.Offer(Session);
            });
            // Delivery Service: the station's job list (DeliveryService); the panel steps aside while the list is open
            System.Action<NuiView> arbeit = v =>
            {
                // a ROO station's Time Attack serves Team ROO only (in Oros a station serves its own side)
                if (roo && !Factions.IsRoo && !DeliveryService.Active) { UiPopup.Message(MissionText.Get("10199")); return; }
                if (v) v.gameObject.SetActive(false);
                DeliveryService.OpenBoard(Session, station, () => { if (v) v.gameObject.SetActive(true); });
            };
            ui.On("ID_ENTRANCE_RQHOUSE_ARBEIT", arbeit);
            ui.On("ID_ENTRANCE_RQHOUSE_TIMEATTACK", arbeit);
            ui.On("ID_ENTRANCE_RQHOUSE_EVENTARBEIT", arbeit);
            ui.On("ID_ENTRANCE_RQHOUSE_EVENTTIMEATTACK", arbeit);
            ui.On("ID_ENTRANCE_RQHOUSE_PARKINGLOT", _ => { Close(); current = null; GameFlow.Instance?.ReturnToParkingLot(); });
            // INSTANT (0x562030): asks for an instant mission (packet 0x3de); the remake offers one of the driver's level
            ui.On("ID_ENTRANCE_RQHOUSE_INSTANT", _ =>
            {
                var im = InstantMissions.Instance;
                if (!im || im.Current != null) return;
                if (!im.Offer()) UiPopup.Message(MissionText.Get("10745"));
            });
        }

        bool blink, beginner, eventPair, instantOn;

        /// <summary>The open panel is the beginner one (BEGINNERQUEST only, 0x561f80).</summary>
        public bool Beginner => view && beginner;

        /// <summary>
        /// The badges, refreshed four times a second (0x562030 toggles its blink flag each call): ING_QUEST while a step quest
        /// runs, QUESTNEW1/2 blinking while the station has a quest to give, ING_QUICKSVC while a delivery runs, QSNEW1/2
        /// (QSEVENT1/2 in an event season) blinking while it has jobs; the Delivery Service button is shown disabled while a
        /// step quest runs, and in Oros the delivery badge only shows for the station's own side.
        /// </summary>
        void Badges(CityMapInfo.Icon station)
        {
            if (!view) return;
            blink = !blink;
            var p = PlayerProfile.Current;
            bool roo = IsRooStation(station);
            bool questRunning = StepQuests.Running(p) != null;
            // the panel switches when the rookie missions end (the reward of #5 while standing here)
            if (beginner == QuestUnlocks.Of(p).Rookie) { Close(); current = null; return; }
            bool questHere = !questRunning && p != null && StepQuests.AvailableAt(station.Id, p).Count > 0;
            if (beginner)
            {
                // 0x561f80: BEGINNERQUEST enabled while no quest runs and the station has one to give (0x702060
                // [inferred]); ING_BEGINNERQUES while one runs
                view.SetEnabled("ID_ENTRANCE_RQHOUSE_BEGINNERQUEST", !questRunning && questHere);
                view.SetVisible("ID_ENTRANCE_RQHOUSE_ING_BEGINNERQUES", questRunning);
                return;
            }
            // the side's finale done (+0x10f / +0x110): the quest button stays off, no NEW badge (0x562030)
            bool storyOver = StepQuests.Running(p) == null && QuestUnlocks.StoryOver(p);
            view.SetEnabled(roo ? "ID_ENTRANCE_RQHOUSE_SECRETMISSION" : "ID_ENTRANCE_RQHOUSE_QUEST", !storyOver);
            questHere &= !storyOver;
            // 0x562030: when the server offers no quest at all (0x703090; the remake: no step quest available to the driver
            // anywhere, or the side's story is over) INSTANT takes QUEST's place for OMD drivers: enabled while no instant
            // mission runs, else disabled with ING_QUEST
            bool instant = instantOn && !questRunning && (storyOver || !StepQuests.All.Any(q => StepQuests.Available(p, q)));
            bool instantRunning = InstantMissions.Instance && InstantMissions.Instance.Current != null;
            if (!roo) view.SetVisible("ID_ENTRANCE_RQHOUSE_QUEST", !instant);
            view.SetVisible("ID_ENTRANCE_RQHOUSE_INSTANT", instant);
            if (instant) view.SetEnabled("ID_ENTRANCE_RQHOUSE_INSTANT", !instantRunning);
            view.SetVisible("ID_ENTRANCE_RQHOUSE_ING_QUEST", questRunning || (instant && instantRunning));
            view.SetVisible("ID_ENTRANCE_RQHOUSE_QUESTNEW1", questHere && !DeliveryService.Active && blink);
            view.SetVisible("ID_ENTRANCE_RQHOUSE_QUESTNEW2", questHere && !DeliveryService.Active && !blink);
            view.SetVisible("ID_ENTRANCE_RQHOUSE_ING_QUICKSVC", DeliveryService.Active);
            string arbeitId = roo ? (eventPair ? "ID_ENTRANCE_RQHOUSE_EVENTTIMEATTACK" : "ID_ENTRANCE_RQHOUSE_TIMEATTACK")
                                  : (eventPair ? "ID_ENTRANCE_RQHOUSE_EVENTARBEIT" : "ID_ENTRANCE_RQHOUSE_ARBEIT");
            // disabled while a step quest runs, and before the Delivery Service is unlocked (+0x109)
            view.SetEnabled(arbeitId, (!questRunning || DeliveryService.Active) && (QuestUnlocks.Of(p).Arbeit || DeliveryService.Active));
            bool ownSide = Session == null || Session.City != "oros" || roo == Factions.IsRoo;
            bool jobs = !DeliveryService.Active && !questRunning && ownSide && p != null && DeliveryService.JobsAt(station.Id, p.Level).Count > 0;
            bool season = SeasonEvents.Is(EventSeason.Valentine) || SeasonEvents.Is(EventSeason.Harvest) || SeasonEvents.Is(EventSeason.Halloween);
            view.SetVisible("ID_ENTRANCE_RQHOUSE_QSNEW1", jobs && !season && blink);
            view.SetVisible("ID_ENTRANCE_RQHOUSE_QSNEW2", jobs && !season && !blink);
            view.SetVisible("ID_ENTRANCE_RQHOUSE_QSEVENT1", jobs && season && blink);
            view.SetVisible("ID_ENTRANCE_RQHOUSE_QSEVENT2", jobs && season && !blink);
        }

        /// <summary>1-5 press the panel's buttons, as in the client (NATION J 49..53).</summary>
        void Keys()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || Net.ChatBox.Typing || (GameFlow.Instance && GameFlow.Instance.MenuOpen)) return;
            UnityEngine.InputSystem.Controls.KeyControl[] digits = { kb.digit1Key, kb.digit2Key, kb.digit3Key, kb.digit4Key, kb.digit5Key };
            for (int i = 0; i < digits.Length; i++)
            {
                if (!digits[i].wasPressedThisFrame) continue;
                foreach (var c in view.Controls)
                    if (c.isActiveAndEnabled && Core.NuiControl.Int(c.Data.Nation, "J", 0) == 49 + i) { UiSounds.Click(); view.Click(c.Data.IdName); return; }
            }
#endif
        }

        void Close()
        {
            if (view) UiManager.Instance?.Close(view);
            view = null;
        }

        void OnDestroy() => Close();

        /// <summary>The station the player is at, or null.</summary>
        public CityMapInfo.Icon Current => current;
    }
}
