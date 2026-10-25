using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The apprentice course's long-lived side (a DontDestroyOnLoad host, self-starting): the lines in QUESTDIALOG 600000, the
    /// parking-lot arrival (0x762f70: an apprentice hears the walkie-talkie and Erina's welcome, line 0; after the reward
    /// Erina's "move to a city", line 12) with a beacon over the Battle Zone building, and the trip to Moon Palace and back.
    /// </summary>
    public sealed class ApprenticeHost : MonoBehaviour
    {
        static ApprenticeHost instance;
        bool wasInLot, introShown;
        MissionBeacon beacon;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static ApprenticeHost Ensure()
        {
            if (instance) return instance;
            var go = new GameObject("Apprentice");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ApprenticeHost>();
            return instance;
        }

        /// <summary>Tests: a fresh session (the welcome line shows again).</summary>
        public static void ResetSession()
        {
            if (instance) { instance.introShown = false; instance.wasInLot = false; }
            ApprenticeCourse.ErinaPending = false;
        }

        /// <summary>A script row of Apprentice_Client.tdf in QUESTDIALOG; <paramref name="then"/> runs when it is closed.</summary>
        public void Talk(int row, Action then)
        {
            var text = Apprentice.Script(row);
            if (string.IsNullOrEmpty(text)) { then?.Invoke(); return; }
            MissionUi.Script(text, "Instructor Duffy", false, null, then);
        }

        // ---------------- the parking lot ----------------

        void Update()
        {
            var flow = GameFlow.Instance;
            bool inLot = flow && flow.InParkingLot && ParkingLotStage.Current;
            if (inLot && !wasInLot) StartCoroutine(Arrived());
            if (!inLot && beacon) { Destroy(beacon.gameObject); beacon = null; }
            wasInLot = inLot;
        }

        /// <summary>0x762f70 for an apprentice: line 0 once a session (step 0), line 12 after the reward (step &gt; 8).</summary>
        IEnumerator Arrived()
        {
            yield return new WaitForSeconds(0.5f);
            var flow = GameFlow.Instance;
            if (!flow || !flow.InParkingLot) yield break;
            var p = PlayerProfile.Current;
            if (ApprenticeCourse.ErinaPending)
            {
                ApprenticeCourse.ErinaPending = false;
                Talk(Apprentice.Erina, null);
            }
            else if (Apprentice.IsApprentice(p) && !introShown)
            {
                introShown = true;
                Talk(Apprentice.Intro, null);
            }
            // the client draws a guide line from the dome's centre to the Battle Zone (0x6bb660); the remake a beacon
            if (Apprentice.IsApprentice(p) && !beacon && ParkingLotStage.Current)
            {
                var at = ParkingLotStage.Current.PointIn(ParkingLotStage.Area.Battle);
                if (at.HasValue)
                {
                    beacon = MissionBeacon.Create("Apprentice Beacon", null, new Color(1f, 0.8f, 0.2f, 0.35f));
                    beacon.MoveTo(at.Value);
                }
            }
        }

        // ---------------- the trip ----------------

        public void StartCourse(bool lesson) => StartCoroutine(TravelAndStart(lesson));

        IEnumerator TravelAndStart(bool lesson)
        {
            var flow = GameFlow.Instance;
            if (!flow) yield break;
            var p = PlayerProfile.Current;
            string prev = p?.City;
            var s = CitySession.Current;
            if (!(flow.InCity && s && s.City == Apprentice.City))
            {
                flow.TravelTo(Apprentice.City);
                float t = 0f;
                while (t < 900f && !(flow.InCity && CitySession.Current && CitySession.Current.City == Apprentice.City && flow.PlayerCar))
                { t += Time.unscaledDeltaTime; yield return null; }
                s = CitySession.Current;
                if (!s || s.City != Apprentice.City) yield break;
                yield return new WaitForSeconds(1f);
            }
            else prev = null;
            ApprenticeCourse.Create(s, lesson, prev);
        }

        /// <summary>After the reward: back to the parking lot, where Erina's line 12 waits (now if the driver is already there).</summary>
        public void AfterReward(string prevCity = null)
        {
            var flow = GameFlow.Instance;
            if (flow && flow.InParkingLot) { Talk(Apprentice.Erina, null); return; }
            ApprenticeCourse.ErinaPending = true;
            BackToLot(prevCity);
        }

        /// <summary>The course ends in the parking lot (0x725330: stage 9); the driver's saved city goes back to where it was.</summary>
        public void BackToLot(string prevCity)
        {
            var p = PlayerProfile.Current;
            if (p != null && !string.IsNullOrEmpty(prevCity) && p.City != prevCity) { p.City = prevCity; if (!p.OnlineDriver) p.Save(); }
            var flow = GameFlow.Instance;
            if (flow) flow.ReturnToParkingLot();
        }
    }

    /// <summary>
    /// The reward pick: CREATECAR 4160000 in the client's apprentice mode (stage 6, enter 0x749530, refresh 0x747bb0, confirm
    /// 0x747060): the cars with Sellable 10001..40001 (Advance, Ironia, Drifter, Kicker), the record "My Record m:ss", a car
    /// the grade doesn't reach shows "Tutorial Record: Less than ... Please try again." with NEXT disabled; NEXT claims it.
    /// </summary>
    public static class ApprenticeRewardPick
    {
        const int Dialog = 4160000;
        static NuiView view;
        static List<(VehicleInfo car, int sellable)> cars;
        static int index, paint, grade;
        static float seconds;
        static Action<bool> done;
        static bool busy;
        static Text record;
        static GameObject preview;
        static Camera previewCam;
        static RenderTexture previewRt;
        static RawImage previewLayer;

        public static bool IsOpen => view;
        public static NuiView View => view;
        public static int Index => index;
        public static VehicleInfo Selected => cars != null && cars.Count > 0 ? cars[index].car : null;

        public static void Open(int forGrade, float time, Action<bool> onDone)
        {
            var ui = UiManager.Instance;
            if (!ui || view) return;
            cars = Apprentice.RewardCars;
            if (cars.Count == 0) { onDone?.Invoke(false); return; }
            grade = Mathf.Clamp(forGrade, 0, 3); seconds = time; done = onDone;
            index = 0; paint = 0; busy = false;
            view = ui.Open(Dialog);
            if (!view) { onDone?.Invoke(false); return; }
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (car) car.ExternalControl = true;
            view.SetVisible("ID_CREATECAR_SELECT_PREV", false);   // no way back from the reward
            view.SetVisible("ID_CREATECAR_SELECT_VERSION", false);
            record = MissionUi.Label(view.transform, new Vector2(300, -620), new Vector2(424, 44), 16, new Color(1f, 0.9f, 0.6f), TextAnchor.MiddleCenter);
            record.gameObject.AddComponent<Outline>().effectColor = Color.black;
            SetupPreview(ui);
            ui.On("ID_CREATECAR_SELECT_SELCARPREV", _ => { index = (index + cars.Count - 1) % cars.Count; Refresh(); });
            ui.On("ID_CREATECAR_SELECT_SELCARNEXT", _ => { index = (index + 1) % cars.Count; Refresh(); });
            for (int i = 1; i <= 4; i++)
            {
                int slot = i - 1;
                ui.On("ID_CREATECAR_SELECT_COLORCHK" + i, _ => { paint = slot; Refresh(); });
            }
            ui.On("ID_CREATECAR_SELECT_NEXT", _ => Confirm());
            AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE");
            Refresh();
        }

        /// <summary>Tests: picks the row of a car (by vehicle index).</summary>
        public static bool Pick(int vehicleIndex)
        {
            if (!view) return false;
            int i = cars.FindIndex(c => c.car.Index == vehicleIndex);
            if (i < 0) return false;
            index = i; Refresh();
            return true;
        }

        public static bool Locked => cars != null && cars.Count > 0 && !Apprentice.Allowed(grade, cars[index].sellable);

        static void Refresh()
        {
            if (!view) return;
            var (car, sell) = cars[index];
            var prev = cars[(index + cars.Count - 1) % cars.Count].car;
            var next = cars[(index + 1) % cars.Count].car;
            OriginalMenus.Plate(view, "ID_CREATECAR_SELECT_NAMEPOS", car.UiName, car);
            OriginalMenus.Plate(view, "ID_CREATECAR_SELECT_NAMEPOSPREV", prev.UiName, prev);
            OriginalMenus.Plate(view, "ID_CREATECAR_SELECT_NAMEPOSNEXT", next.UiName, next);
            OriginalMenus.Emblem(view, "ID_CREATECAR_SELECT_GRADEPOS", ShopData.Grade(car, 0));
            OriginalMenus.Emblem(view, "ID_CREATECAR_SELECT_GRADEPOSPREV", ShopData.Grade(prev, 0));
            OriginalMenus.Emblem(view, "ID_CREATECAR_SELECT_GRADEPOSNEXT", ShopData.Grade(next, 0));
            OriginalMenus.MakerEmblem(view, "ID_CREATECAR_SELECT_EMBLEMPOS", car.Maker);
            var g = ShopData.Grade(car, 0);
            Gauge("SPEED", g?.Speed ?? car.Speed); Gauge("ACCEL", g?.Accel ?? car.Accel); Gauge("CRASH", g?.Crash ?? car.Crash); Gauge("BOOST", g?.Boost ?? car.Boost);
            for (int i = 1; i <= 4; i++)
            {
                var swatch = view.Control("ID_CREATECAR_SELECT_COLOR" + i);
                if (swatch) foreach (var ri in swatch.GetComponentsInChildren<RawImage>()) ri.color = CarFactory.FactoryColor(car, i - 1);
                var check = view.Control("ID_CREATECAR_SELECT_COLORCHK" + i);
                if (check) check.SetChecked(i - 1 == paint);
            }
            // 0x747bb0: "My Record m:ss" and the grade's lock text; NEXT (control flag 4) only for a car the grade reaches
            string lockText = Apprentice.LockText(grade, sell);
            string rec = Apprentice.Record(seconds);
            if (record) record.text = string.Join("\n", new[] { rec, lockText }.Where(x => !string.IsNullOrEmpty(x)));
            view.SetEnabled("ID_CREATECAR_SELECT_NEXT", lockText == null && !busy);
            if (preview) UnityEngine.Object.Destroy(preview);
            preview = CarFactory.BuildVisual(car, paint, new Vector3(0, -1000, 0), Quaternion.Euler(0, 210, 0));
        }

        static void Gauge(string stat, int value)
        {
            view.SetText("ID_CREATECAR_SELECT_TEXT_" + stat, $"({value:000})");
            var c = view.Control("ID_CREATECAR_SELECT_GAUGE_" + stat);
            if (!c) return;
            float fill = Mathf.Clamp01(value / 100f);
            foreach (Transform el in c.transform)
            {
                if (!el.TryGetComponent<RawImage>(out var img)) continue;
                var rt = (RectTransform)el;
                if (!el.TryGetComponent<FullWidth>(out var fw)) { fw = el.gameObject.AddComponent<FullWidth>(); fw.Width = rt.sizeDelta.x; fw.Uv = img.uvRect; }
                rt.sizeDelta = new Vector2(fw.Width * fill, rt.sizeDelta.y);
                var uv = fw.Uv; uv.width *= fill; img.uvRect = uv;
            }
        }

        sealed class FullWidth : MonoBehaviour { public float Width; public Rect Uv; }

        /// <summary>NEXT (0x747060 in apprentice mode: packet 0x79f0a0 with the time, the car and the colour).</summary>
        public static void Confirm()
        {
            if (!view || busy || Locked) return;
            var (car, sell) = cars[index];
            busy = true;
            view.SetEnabled("ID_CREATECAR_SELECT_NEXT", false);
            AudioLibrary.PlaySfx("sfx/SID_ENTER_SUCCESS");
            Apprentice.Claim(seconds, car, sell, paint, r =>
            {
                busy = false;
                if (!r.Ok)
                {
                    if (!string.IsNullOrEmpty(r.Error)) UiPopup.Message(r.Error);
                    if (view) view.SetEnabled("ID_CREATECAR_SELECT_NEXT", !Locked);
                    return;
                }
                ChatBox.System($"{car.UiName} has been added to your garage.");
                var cb = done;
                Close();
                // the new car is the current one (the server's 0x56 reply adds it): rebuild the player's car
                if (GameFlow.Instance) GameFlow.Instance.RespawnCar();
                cb?.Invoke(true);
            });
        }

        public static void Close()
        {
            var ui = UiManager.Instance;
            if (view) { if (ui) ui.Close(view); else UnityEngine.Object.Destroy(view.gameObject); }
            view = null; done = null; record = null;
            if (preview) UnityEngine.Object.Destroy(preview);
            if (previewCam) UnityEngine.Object.Destroy(previewCam.gameObject);
            if (previewLayer) UnityEngine.Object.Destroy(previewLayer.gameObject);
            if (previewRt) { previewRt.Release(); UnityEngine.Object.Destroy(previewRt); }
            preview = null; previewCam = null; previewLayer = null; previewRt = null;
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (car) car.ExternalControl = false;
        }

        /// <summary>The turning car behind the screen, as the creation screen draws it (GameFlow.SetupPreview).</summary>
        static void SetupPreview(UiManager ui)
        {
            previewRt = new RenderTexture(1024, 768, 24);
            var camGo = new GameObject("Apprentice Preview Camera");
            previewCam = camGo.AddComponent<Camera>();
            previewCam.clearFlags = CameraClearFlags.SolidColor;
            previewCam.backgroundColor = new Color(0, 0, 0, 0);
            previewCam.targetTexture = previewRt;
            previewCam.transform.position = new Vector3(0, -1000 + 1.6f, -6.2f);
            previewCam.transform.LookAt(new Vector3(0, -1000 + 0.6f, 0));
            previewCam.fieldOfView = 40;
            camGo.AddComponent<GarageLighting>();
            var light = camGo.AddComponent<Light>();
            light.type = LightType.Directional;
            previewLayer = ui.Layer("Apprentice Car Preview");
            previewLayer.texture = previewRt;
        }
    }
}
