using System;
using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity.Game
{
    /// <summary>
    /// Everything that happens while driving around a city: traffic, mission offers (HUV chases,
    /// races, quests), the main-button menus and hotkeys. Created by GameFlow after the city loads.
    /// </summary>
    public sealed class CitySession : MonoBehaviour
    {
        public string City;
        public DriftCar Car;
        public Hud Hud;
        public TrafficSystem Traffic { get; private set; }
        public static CitySession Current { get; private set; }

        public float MissionOfferInterval = 75f;
        float offerTimer;
        NuiView offer;
        readonly Dictionary<KeyCode, Action> hotkeys = new Dictionary<KeyCode, Action>();
        Vector3 lastPos;

        public void Begin(string city, DriftCar car, Hud hud, Transform world)
        {
            Current = this;
            City = city; Car = car; Hud = hud;
            Traffic = new GameObject("Traffic").AddComponent<TrafficSystem>();
            Traffic.transform.SetParent(world, false);
            Traffic.Init(city, car.transform);
            Trains.Attach(this, city);     // Taipei's MRT line (trainpath.chpath / .signal)
            Passerby.Attach(this, city);   // pedestrians of <city>\passerby.pcs
            RoadMapQuery.Load(city);   // road map + sand/water zones for CarRoadState (wrong way, off road, splashes)
            offerTimer = 25f;
            lastPos = car.transform.position;
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            if (offer && UiManager.Instance) UiManager.Instance.Close(offer);
        }

        void Update()
        {
            if (!Car) return;
            // odometer for profile stats
            var p = PlayerProfile.Current;
            if (p != null)
            {
                float km = Vector3.Distance(lastPos, Car.transform.position) / 1000f;
                if (km < 0.2f) { p.DistanceKm += km; QuestTracker.TickDistance(km); } // ignore teleports
                lastPos = Car.transform.position;
            }

            // menu hotkeys wait while a window, popup, dialog or the custom shop is open
            bool busy = GameFlow.Instance && GameFlow.Instance.MenuOpen;
            if (!busy)
            {
                // a party leader's chase goes to the server (ChaseMission.StartFromCall)
                if (Pressed(KeyCode.H) && !GameFlow.MissionActive) ChaseMission.StartFromCall(this, false);
                if (Pressed(KeyCode.B) && !GameFlow.MissionActive) ChaseMission.StartFromCall(this, true);
                if (Pressed(KeyCode.I)) Menus.Inventory();
                if (Pressed(KeyCode.G)) Menus.Garage();
                if (Pressed(KeyCode.P)) Menus.Shop();
                if (Pressed(KeyCode.Q)) Menus.Quests();
                if (Pressed(KeyCode.T)) RaceMission.Offer(this);
                if (Pressed(KeyCode.C)) { if (!GameFlow.BlockedByMission()) Menus.CityTravel(); }
                if (Pressed(KeyCode.L)) WorkshopMenus.Licences();
                if (Pressed(KeyCode.U)) WorkshopMenus.PartsShop();
            }
            if (Pressed(KeyCode.M)) Menus.WorldMap();
            if (Pressed(KeyCode.N)) GameFlow.Instance?.Horn();

            if (!GameFlow.MissionActive && !busy && !offerOpen)
            {
                offerTimer -= Time.deltaTime;
                if (offerTimer <= 0) { offerTimer = MissionOfferInterval; OfferChase(); }
            }
        }

        public void StartChase(bool boss)
        {
            if (ChaseMission.Active || RaceMission.Active || OnlineRace.Active) return;
            if (offerOpen) UiManager.Instance.Close(offer);
            var m = ChaseMission.Begin(Traffic, Car, boss);
            if (m) m.Finished += won => QuestTracker.Notify(won ? QuestEvent.HuvCaught : QuestEvent.HuvEscaped);
            else UiPopup.Message("No HUV could be found nearby. Try again on a bigger road.");
        }

        /// <summary>Instant-mission offer using the original confirm dialog (accept / give up).</summary>
        bool offerOpen => offer && UiManager.Instance && UiManager.Instance.IsOpen(offer);

        // the HUV call (CHASING 870000, ChaseScreens): Tab starts the chase
        void OfferChase()
        {
            // the HUV call needs the CHASE unlock (level > 5 or a CHASE quest, 0x6ca110)
            if (offerOpen || !QuestUnlocks.ChaseOffer(PlayerProfile.Current)) return;
            offer = ChaseMission.Offer(this);
        }

        static bool Pressed(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null || Net.ChatBox.Typing) return false;
            // don't treat typing in an input field as hotkeys
            if (UnityEngine.EventSystems.EventSystem.current && UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject &&
                UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject.GetComponent<UnityEngine.UI.InputField>()) return false;
            Key ik;
            switch (key)
            {
                case KeyCode.H: ik = Key.H; break; case KeyCode.B: ik = Key.B; break; case KeyCode.I: ik = Key.I; break;
                case KeyCode.G: ik = Key.G; break; case KeyCode.P: ik = Key.P; break; case KeyCode.Q: ik = Key.Q; break;
                case KeyCode.M: ik = Key.M; break; case KeyCode.T: ik = Key.T; break; case KeyCode.C: ik = Key.C; break;
                case KeyCode.N: ik = Key.N; break; case KeyCode.L: ik = Key.L; break; case KeyCode.U: ik = Key.U; break;
                default: return false;
            }
            return k[ik].wasPressedThisFrame;
#else
            return Input.GetKeyDown(key);
#endif
        }
    }
}
