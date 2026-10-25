using System.Collections;
using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity.Game
{
    /// <summary>
    /// Offline game flow over the original UI dialogs:
    /// Login -> (new driver: operator intro -> car creation) -> loading -> parking lot (stage 9) -> exit + gate -> city,
    /// and from a city back to the parking lot (the RQ house's "parking lot" button; here the travel list, key C).
    /// Server/channel selection is skipped; everything the server did is replaced by local state.
    /// </summary>
    public sealed partial class GameFlow : MonoBehaviour
    {
        [Tooltip("Folder containing DriftCity.exe and the Data folder")]
        public string GameRoot = @"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha";
        [Tooltip("Open city archive under Data\\World: cras, moonpalace, koinonia, oros")]
        public string City = "cras";
        [Tooltip("NIF whose first mesh marks the spawn point (in city coordinates)")]
        public string StartMarker = @"cras\track23\point.nif";
        [Tooltip("Go straight from login to the driver's last city instead of the parking lot (tests and tools; the client always starts in the parking lot)")]
        public bool SkipParkingLot;
        [Tooltip("Leave out the start-up story (game/intro/newintro.nif) before a first driver's licence registration (tests)")]
        public bool SkipIntro;

        UiManager ui;
        ParkingLotStage lot;
        Hud hud;
        GameObject world;
        DriftCar car;
        NuiView quitMenu;
        string pendingName;
        bool newDriver;
        bool inCity;
        CitySession session;

        public static GameFlow Instance { get; private set; }
        public DriftCar PlayerCar => car;
        public Bounds CityBounds { get; private set; }
        public bool InCity => inCity;
        /// <summary>True while driving in the parking lot (not during its loading screen).</summary>
        public bool InParkingLot => lot && lot.Active;

        /// <summary>True while a race or chase owns the player car (swapping or rebuilding it would break the mission).</summary>
        public static bool MissionActive => RaceMission.Active || OnlineRace.Active || ChaseMission.Active || DeliveryService.Active || PatrolMission.Active || UndercityStage.Active || LapTimeBattle.Active || PvpSpectate.Watching
                                             || RushTime.Joined || (VirtualChase.Active && VirtualChase.Active.State == VirtualChase.Phase.Running) || AutoDrive.Active
                                             || StepQuests.Running(PlayerProfile.Current) != null || ApprenticeCourse.Active;

        /// <summary>Shows why the car can't be changed right now; returns true when it is blocked.</summary>
        public static bool BlockedByMission()
        {
            if (!MissionActive) return false;
            UiPopup.Message("Finish your race or chase first.");
            return true;
        }

        /// <summary>True while a menu, popup, dialog or the custom shop has the player's attention (hotkeys and offers wait).</summary>
        public bool MenuOpen => ListWindow.IsOpen || UiPopup.IsOpen || CustomShop.IsActive || Menus.DealerOpen || OriginalMenus.IsOpen || ParkingLotStage.GateOpen || PvpLobby.AnyOpen || TradeWindow.IsOpen || CountPopup.IsOpen || Social.Community.IsOpen || Social.Teams.CenterOpen || (ui && ui.Find(4040000));

        /// <summary>Rebuilds the player's current car (after buying or switching) where the old one stood.</summary>
        public void RespawnCar()
        {
            if ((!inCity && !InParkingLot) || PlayerProfile.Current?.Car == null) return;
            Vector3 pos = car ? car.transform.position + Vector3.up * 0.5f : Vector3.zero;
            Quaternion rot = car ? Quaternion.Euler(0, car.transform.eulerAngles.y, 0) : Quaternion.identity;
            if (car) Destroy(car.gameObject);
            SpawnPlayerCar(pos, rot);
            if (Net.OnlineSession.Instance) Net.OnlineSession.Instance.MarkTeleport();
        }

        public void RefreshHud() { if (hud) hud.UpdateProfile(); }

        Text notice;
        float noticeUntil;

        /// <summary>Every 10th speed combo the client asks the server for an item roll (offline: Progress rolls it).</summary>
        void OnComboMilestone(int count)
        {
            Progress.Reward("combo", new Dictionary<string, object> { ["count"] = count }, () => new Progress.Result { Ok = true }, r =>
            {
                if (r.Ok && r.Drop != null) Notice($"{count} combo!  Found: {Crafting.Name(r.Drop)}");
            });
        }

        /// <summary>A short line over the HUD that fades by itself.</summary>
        void Notice(string text)
        {
            if (!notice)
            {
                notice = HudText.Create("Notice", new Vector2(0.5f, 0.72f), Vector2.zero, new Vector2(700, 40), 22, TextAnchor.MiddleCenter);
                notice.color = new Color(1f, 0.9f, 0.35f);
            }
            notice.text = text;
            notice.enabled = true;
            noticeUntil = Time.time + 3f;
            StartCoroutine(HideNotice());
        }

        IEnumerator HideNotice()
        {
            while (Time.time < noticeUntil) yield return null;
            if (notice) notice.enabled = false;
        }

        void SpawnPlayerCar(Vector3 pos, Quaternion rot)
        {
            var p = PlayerProfile.Current;
            var info = GameTables.Car(p.Car.VehicleIndex);
            car = CarFactory.BuildDrivable(info, p.Car, pos, rot);
            // an engine-sound item gives the car another model's engine
            var engine = PartsCatalog.Effective(info, p.Car, PartSlot.Engine);
            car.gameObject.AddComponent<CarSounds>();
            car.gameObject.AddComponent<CarEffects>();
            car.gameObject.AddComponent<EngineAudio>().EngineNumber = EngineAudio.LookupEngine(engine != null ? engine.Variant : info.UniqueId);
            NamePlate.Attach(car.transform, p.Name, own: true);   // the client's NAME plate over the player's car
            RepaintCar();
            ApplyCarStats();
            var cam = Camera.main;
            if (cam && cam.TryGetComponent<ChaseCamera>(out var chase)) chase.Target = car.transform;
            if (hud) hud.Car = car;
            if (session) { session.Car = car; if (session.Traffic) session.Traffic.Player = car.transform; }
            if (lot) lot.Car = car;
        }

        /// <summary>Applies the current car's grade ratings to its handling.</summary>
        public void ApplyCarStats()
        {
            var p = PlayerProfile.Current;
            if (!car || p?.Car == null) return;
            var info = GameTables.Car(p.Car.VehicleIndex);
            var s = ShopData.Stats(info, p.Car);
            car.ApplyStats(s.accel, s.speed, s.crash, s.boost);
        }

        /// <summary>Re-tints the car: a bought paint item wins over the creation colour.</summary>
        public void RepaintCar()
        {
            var p = PlayerProfile.Current;
            if (!car || p?.Car == null) return;
            CarPaint.Apply(car.gameObject, GameTables.Car(p.Car.VehicleIndex), p.Car);
        }

        /// <summary>Plays the fitted horn (or the stock one).</summary>
        public void Horn()
        {
            var p = PlayerProfile.Current;
            if (!car || p?.Car == null) return;
            var horn = PartsCatalog.Effective(GameTables.Car(p.Car.VehicleIndex), p.Car, PartSlot.Horn);
            AudioLibrary.PlaySfx(horn?.Sound ?? "sfx/SID_TRAFFIC_HONK", 0.9f);
        }

        /// <summary>Loads another open city directly (the travel list's gates; the client goes through the parking lot).</summary>
        public void TravelTo(string city)
        {
            if (string.IsNullOrEmpty(city) || (inCity && city == City) || entering) return;
            inCity = false;
            EnterGate(city);
        }

        /// <summary>Leaves the parking lot (or a city) through a gate into <paramref name="city"/> (SELGATE -> zone move 0x30f -> stage 13).</summary>
        public void EnterGate(string city)
        {
            if (string.IsNullOrEmpty(city) || entering) return;
            City = city;
            var p = PlayerProfile.Current;
            if (p != null) { p.City = city; p.Save(); }
            StartCoroutine(EnterCity());
        }

        /// <summary>Back to the parking lot (the RQ house's "parking lot" button sets stage 9, 0x561d92).</summary>
        public void ReturnToParkingLot()
        {
            if (entering || BlockedByMission()) return;
            inCity = false;
            StartCoroutine(EnterParkingLot());
        }

        /// <summary>
        /// Hook: the driver is logged in and has a car (offline login, new driver created, online login or online driver
        /// created). The client then enters the parking lot (packet 0x56 handler 0x4ef1a0 sets stage 9).
        /// </summary>
        public void OnLoginComplete()
        {
            // a new driver is an apprentice: the client (US ApprenticeFlowType 1) puts them in the parking lot, where Erina
            // sends them to the Battle Zone's Tutorial (Tutorial/ApprenticeCourse.cs); the remake's old start in Cras stays
            // behind ApprenticeCourse.SendNewDriversToLot
            if (newDriver)
            {
                newDriver = false;
                if (ApprenticeCourse.SendNewDriversToLot) { StartCoroutine(EnterParkingLot()); return; }
                City = "cras"; StartCoroutine(EnterCity()); return;
            }
            if (SkipParkingLot) StartCoroutine(EnterCity());
            else StartCoroutine(EnterParkingLot());
        }

        void Awake() => Instance = this;

        IEnumerator Start()
        {
            var root = DriftCityData.ResolveGameRoot(GameRoot);
            ui = UiManager.Ensure();
            if (root == null)
            {
                Debug.LogError("Drift City game data not found");
                var msg = new GameObject("Missing Install").AddComponent<MissingInstallMessage>();
                msg.Text = "Drift City game data not found.\n\nImport it in the editor (Drift City > Import Game Data Into Project) and rebuild,\n" +
                           "or start with  -gameRoot \"C:\\path\\to\\Drift City\"  or put that folder (with Data) into gameroot.txt next to this program.";
                yield break;
            }
            GameRoot = root;
            DriftCityData.Mount(GameRoot);
            Debug.Log($"Drift City: game data from {GameRoot} ({(DriftCityData.Files.Imported ? "imported copy, no installation needed" : "installed client")}, {DriftCityData.Files.AllFiles.Count} files)");
            GameTables.Load();
            PlayerProfile.Load();
            AudioLibrary.LoadVolumes();
            GameOptions.LoadAndApply();   // goption.ini (resolution, AA, shadows, volumes, keys)
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_title.mp3");
            yield return null;
            ShowLogin();
        }

        // ---------------- new driver ----------------
        List<VehicleInfo> starters;
        int starterIndex, paint;
        GameObject preview;
        Camera previewCam;
        RenderTexture previewRt;
        RawImage previewLayer;

        void ShowCreateCar()
        {
            ui.CloseAll();
            var v = ui.Open(150000); // CREATECAR (new character)
            starters = GameTables.StarterCars;
            starterIndex = 0; paint = 0;
            SetupPreview();
            RefreshCreateCar(v);
            ui.On("ID_CREATECAR_NEW_SELCARPREV", view => { starterIndex = (starterIndex + starters.Count - 1) % starters.Count; RefreshCreateCar(view); });
            ui.On("ID_CREATECAR_NEW_SELCARNEXT", view => { starterIndex = (starterIndex + 1) % starters.Count; RefreshCreateCar(view); });
            for (int i = 1; i <= 4; i++)
            {
                int slot = i - 1;
                ui.On("ID_CREATECAR_NEW_COLORCHECK" + i, view => { paint = slot; RefreshCreateCar(view); });
            }
            ui.On("ID_CREATECAR_NEW_PREV", _ => { DestroyPreview(); ShowCreateCharacter(); });
            ui.On("ID_CREATECAR_NEW_EXIT", _ => Quit());
            ui.On("ID_CREATECAR_NEW_NEXT", _ =>
            {
                if (online)
                {
                    if (busy) return;
                    busy = true;
                    Net.OnlineSession.Instance.CreateDriver(pendingName, starters[starterIndex].Index, paint, pendingAvatar, (ok, error) =>
                    {
                        busy = false;
                        if (!ok) { UiPopup.Message("Could not create the driver: " + error); return; }
                        DestroyPreview();
                        newDriver = true;
                        OnLoginComplete();
                    });
                    return;
                }
                CreateOfflineDriver(starters[starterIndex].Index, paint);
                DestroyPreview();
                newDriver = true;
                OnLoginComplete();
            });
        }

        void RefreshCreateCar(NuiView v)
        {
            var car = starters[starterIndex];
            // the original name plates, grade badges and maker emblem (the client's CMN image catalogues)
            var prev = starters[(starterIndex + starters.Count - 1) % starters.Count];
            var next = starters[(starterIndex + 1) % starters.Count];
            OriginalMenus.Plate(v, "ID_CREATECAR_NEW_NAMEPOS", car.UiName, car);
            OriginalMenus.Plate(v, "ID_CREATECAR_NEW_NAMEPOSPREV", prev.UiName, prev);
            OriginalMenus.Plate(v, "ID_CREATECAR_NEW_NAMEPOSNEXT", next.UiName, next);
            OriginalMenus.Emblem(v, "ID_CREATECAR_NEW_GRADEPOS", ShopData.Grade(car, 0));
            OriginalMenus.Emblem(v, "ID_CREATECAR_NEW_GRADEPOSPREV", ShopData.Grade(prev, 0));
            OriginalMenus.Emblem(v, "ID_CREATECAR_NEW_GRADEPOSNEXT", ShopData.Grade(next, 0));
            OriginalMenus.MakerEmblem(v, "ID_CREATECAR_NEW_EMBLEMPOS", car.Maker);
            v.SetText("ID_CREATECAR_NEW_CHARNAME", pendingName ?? "");
            SetGauge(v, "SPEED", car.Speed); SetGauge(v, "ACCEL", car.Accel); SetGauge(v, "CRASH", car.Crash); SetGauge(v, "BOOST", car.Boost);
            for (int i = 1; i <= 4; i++)
            {
                var swatch = v.Control("ID_CREATECAR_NEW_COLOR" + i);
                if (swatch) foreach (var ri in swatch.GetComponentsInChildren<RawImage>()) ri.color = CarFactory.FactoryColor(car, i - 1);
                var check = v.Control("ID_CREATECAR_NEW_COLORCHECK" + i);
                if (check) foreach (var ri in check.GetComponentsInChildren<RawImage>()) ri.color = new Color(1, 1, 1, i - 1 == paint ? 1f : 0.15f);
            }
            if (preview) Destroy(preview);
            preview = CarFactory.BuildVisual(car, paint, new Vector3(0, -1000, 0), Quaternion.Euler(0, 210, 0));
        }

        static void SetGauge(NuiView v, string stat, int value)
        {
            v.SetText("ID_CREATECAR_NEW_TEXT_" + stat, $"({value:000})");
            var g = v.Control("ID_CREATECAR_NEW_GAUGE_" + stat);
            if (!g) return;
            float fill = Mathf.Clamp01(value / 100f);
            foreach (Transform el in g.transform)
            {
                var rt = (RectTransform)el;
                if (!el.TryGetComponent<RawImage>(out var img)) continue;
                if (!el.gameObject.TryGetComponent<GaugeWidth>(out var gw)) { gw = el.gameObject.AddComponent<GaugeWidth>(); gw.Full = rt.sizeDelta.x; gw.FullUv = img.uvRect; }
                rt.sizeDelta = new Vector2(gw.Full * fill, rt.sizeDelta.y);
                var uv = gw.FullUv; uv.width *= fill; img.uvRect = uv;
            }
        }

        sealed class GaugeWidth : MonoBehaviour { public float Full; public Rect FullUv; }

        void SetupPreview()
        {
            previewRt = new RenderTexture(1024, 768, 24);
            var camGo = new GameObject("Preview Camera");
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
            previewLayer = ui.Layer("Car Preview");
            previewLayer.texture = previewRt;
        }

        void DestroyPreview()
        {
            if (preview) Destroy(preview);
            if (previewCam) Destroy(previewCam.gameObject);
            if (previewLayer) Destroy(previewLayer.gameObject);
            if (previewRt) { previewRt.Release(); Destroy(previewRt); }
        }

        // ---------------- loading + city ----------------
        IEnumerator EnterCity()
        {
            if (entering) yield break;   // a second click / reply while a city is already loading
            entering = true;
            try { yield return LoadCity(); }
            finally { entering = false; }
        }

        IEnumerator EnterParkingLot()
        {
            if (entering) yield break;
            entering = true;
            try { yield return LoadParkingLot(); }
            finally { entering = false; }
        }

        /// <summary>GAMELOADING + GAMELOADINGBAR with a random tip.</summary>
        NuiView OpenLoading()
        {
            ui.CloseAll();
            var loading = ui.Open(160000);   // GAMELOADING
            var bar = ui.Open(180000);       // GAMELOADINGBAR
            int tip = Random.Range(0, 9);
            for (int i = 0; i < 9; i++) loading.SetVisible("ID_GAMELOADING_TIP0" + i, i == tip);
            loading.SetVisible("ID_GAMELOADING_MISSIONBATTLE", false);
            loading.SetVisible("ID_GAMELOADING_UNDERCITY", false);
            SetLoadingProgress(bar, 0f);
            return bar;
        }

        IEnumerator LoadParkingLot()
        {
            var bar = OpenLoading();
            yield return null;

            ClearCity();
            world = new GameObject("World");
            lot = gameObject.AddComponent<ParkingLotStage>();
            yield return lot.Build(world.transform, t => SetLoadingProgress(bar, 0.05f + 0.85f * t));
            CityBounds = lot.Bounds;
            var (start, startRot) = lot.ArrivalPose();
            SetLoadingProgress(bar, 0.9f);
            yield return null;

            yield return SetupDriving(start, startRot, ParkingLotStage.Music, bar);
            lot.Begin(this, car);
            if (!TryGetComponent<Minimap>(out var minimap)) minimap = gameObject.AddComponent<Minimap>();
            minimap.Hide();
            minimap.Show(this);
        }

        IEnumerator LoadCity()
        {
            var bar = OpenLoading();
            yield return null;

            ClearCity();
            world = new GameObject("World");
            var loader = new CityLoader(City);
            yield return loader.Load(world.transform, (t, _) => SetLoadingProgress(bar, 0.05f + 0.85f * t));
            CityBounds = loader.WorldBounds;
            Vector3 start = FindStart(world, out Quaternion startRot);
            SetLoadingProgress(bar, 0.9f);
            yield return null;

            yield return SetupDriving(start, startRot, CityMusic.For(City), bar);
            session = gameObject.AddComponent<CitySession>();
            session.Begin(City, car, hud, world.transform);
            StationPanel.Attach(session, City);
            DeliveryService.Attach(session, City);   // the stations' Delivery Service (ArbeitClient.tdf)
            InstantMissions.Attach(session, City);   // Call Mission offers while driving (Instant_Client.tdf)
            PatrolMission.Attach(session, City);     // the officers' patrols beside each station
            UndercityEntrance.Attach(session, City); // Undercity entrance (uc_gate, UC_Enter) and its runs
            // the self-starting hosts live on DontDestroyOnLoad objects: make sure none was lost (a scene reload, a teardown)
            EventObjects.Ensure(); PickEm.Ensure(); GuideTextDriver.Ensure(); Social.SocialHost.Ensure(); Social.ChannelBattle.EnsureRunner(); BonusBadges.Ensure(); ShopPeriods.Ensure(); MittronFuelMeter.Ensure(); ApprenticeHost.Ensure();
            MissionExtras.Attach(session, City);       // Mita 500 spots, Rush Time, QUESTPLAYINFO, mission push router
            OrosAmbience.Attach(session.gameObject, City);   // Oros: dust devils and tornadoes in free roam
            if (!TryGetComponent<Minimap>(out var minimap)) minimap = gameObject.AddComponent<Minimap>();
            minimap.Hide();
            minimap.Show(this);
            inCity = true;
            if (online && Net.OnlineSession.Active) Net.OnlineSession.Instance.EnterCity(City);
        }

        /// <summary>Player car, lights, sky, chase camera, music and the driving HUD (city and parking lot alike).</summary>
        IEnumerator SetupDriving(Vector3 start, Quaternion startRot, string music, NuiView bar)
        {
            SpawnPlayerCar(start, startRot);
            SetupLighting();
            AudioLibrary.PlayMusic(this, music);
            var cam = Camera.main ?? new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" }.GetComponent<Camera>();
            // chase camera of init/camera.ini: 0.4 .. 512, the fog ends at the far plane
            cam.nearClipPlane = 0.4f;
            cam.farClipPlane = OriginalLook.ChaseFar;
            var sky = OriginalLook.BuildSky();
            if (sky) sky.transform.SetParent(world.transform, true);
            WeatherClock.Attach(world, sky, indoors: lot);   // the four weathers of init/weather.ini on the clock
            // (TryGetComponent: in the editor GetComponent returns a fake-null object that ?? treats as present)
            if (!cam.TryGetComponent<ChaseCamera>(out var chase)) chase = cam.gameObject.AddComponent<ChaseCamera>();
            ArcadeLook.Attach(cam);
            LensFlare.Attach(cam);
            ArcadeLook.ApplyEnvironment();
            chase.Target = car.transform;
            cam.transform.position = car.transform.position - car.transform.forward * 7 + Vector3.up * 3;
            SetLoadingProgress(bar, 1f);
            yield return new WaitForSeconds(0.3f);

            ui.CloseAll();
            if (!TryGetComponent(out hud)) hud = gameObject.AddComponent<Hud>();
            hud.Car = car;
            hud.Show();
            var combo = DriftCombo.Attach(hud);   // SPEEDCOMBO; closes itself when the speedometer goes
            if (combo) { combo.Milestone -= OnComboMilestone; combo.Milestone += OnComboMilestone; }
            Net.ChatBox.Ensure();   // the chat window with its system log, as the client keeps it on screen
            ui.On("ID_MAINBUTTON_QUIT", _ => ToggleQuitMenu()); ui.On("ID_MAINBUTTON_ADV_QUIT", _ => ToggleQuitMenu());
        }

        /// <summary>Back to the original lighting (when the arcade look is switched off).</summary>
        public void ReapplyLighting() { SetupLighting(); ArcadeLook.ApplyEnvironment(); }

        /// <summary>The weather's ground and car lights, fog and shadows (init/weather.ini).</summary>
        static void SetupLighting() => OriginalLook.Apply();

        static void SetLoadingProgress(NuiView bar, float t)
        {
            if (!bar) return;
            int lit = Mathf.RoundToInt(t * 10);
            for (int i = 0; i < 10; i++) bar.SetVisible("ID_GAMELOADINGBAR_GAUGE0" + i, i < lit);
        }

        Vector3 FindStart(GameObject world, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            Vector3 p = CityBounds.center;
            bool found = false;
            if (!string.IsNullOrEmpty(StartMarker) && StartMarker.StartsWith(City + "\\") && DriftCityData.Files.Exists(StartMarker))
            {
                var marker = new NifSceneBuilder().Load(StartMarker, null);
                var r = marker.GetComponentInChildren<Renderer>();
                if (r) { p = r.bounds.center; found = true; }
                Destroy(marker);
            }
            if (!found)
            {
                // otherwise start on the traffic lane point closest to the city centre, facing along the lane
                var fs = DriftCityData.Files;
                var path = $@"{City}\{City}.tcs";
                if (fs.Exists(path))
                {
                    var net = Core.TrafficNetwork.Parse(fs.Read(path));
                    float best = float.MaxValue;
                    foreach (var lane in net.Lanes)
                        for (int i = 0; i + 1 < lane.Points.Length; i++)
                        {
                            var w = DriftCityData.ToUnity(lane.Points[i]);
                            float d = (new Vector2(w.x, w.z) - new Vector2(CityBounds.center.x, CityBounds.center.z)).sqrMagnitude;
                            if (d >= best) continue;
                            best = d; p = w;
                            var dir = DriftCityData.ToUnity(lane.Points[i + 1]) - w; dir.y = 0;
                            if (dir.sqrMagnitude > 0.01f) rotation = Quaternion.LookRotation(dir.normalized);
                        }
                }
            }
            Physics.SyncTransforms();
            if (Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out var hit, 500f)) p = hit.point;
            return p + Vector3.up;
        }

        void ClearCity()
        {
            CustomShop.Abort();
            // leaves the channel and any race lobby, stops state uploads, clears remote cars
            if (Net.OnlineSession.Instance) Net.OnlineSession.Instance.LeaveCity();
            bool hadCity = world || car;
            inCity = false;
            if (RaceMission.Active) RaceMission.Active.Abort();
            if (OnlineRace.Active) OnlineRace.Active.Abort();
            PvpLobby.CloseAll();
            if (VirtualChase.Active) VirtualChase.Active.Cancel();
            if (AutoDrive.Active) AutoDrive.Active.Stop(false);
            if (UndercityStage.Active) Destroy(UndercityStage.Active);
            if (ChaseMission.Active) Destroy(ChaseMission.Active.gameObject);
            ListWindow.CloseOpen();
            OriginalMenus.Close();
            if (session) Destroy(session);
            if (lot) Destroy(lot);
            lot = null;
            if (TryGetComponent<Minimap>(out var oldMap)) oldMap.Hide();
            if (hud) hud.Hide();
            if (world) Destroy(world);
            if (car) Destroy(car.gameObject);
            if (hadCity || TextureLibrary.PendingReload)
            {
                // free the previous city's materials and textures (meshes go with their objects)
                TextureLibrary.PendingReload = false;
                NifSceneBuilder.ClearCaches();
                TextureLibrary.Clear();
                Resources.UnloadUnusedAssets();
            }
            if (Net.ChatBox.Instance) Destroy(Net.ChatBox.Instance.gameObject);
            if (Net.OnlineSession.Instance && Net.OnlineSession.Instance.Remotes) Net.OnlineSession.Instance.Remotes.Clear();
        }

        // ---------------- in-city menus ----------------
        void Update()
        {
            if (!inCity && !InParkingLot) return;
#if ENABLE_INPUT_SYSTEM
            bool esc = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
            bool esc = Input.GetKeyDown(KeyCode.Escape);
#endif
            // Esc closes the top list window or popup first; only a free Esc opens the quit menu (in the Undercity: "Esc Exit Undercity")
            // the Battle Zone screens close with Esc (the wait room is left through its own exit button)
            if (esc && !Net.ChatBox.Typing && !UiPopup.IsOpen && (Ladder.IsOpen || Tournament.IsOpen || PvpLobby.IsOpen) && !PvpWaitRoom.IsOpen)
            {
                if (Ladder.IsOpen) Ladder.Close(); else if (Tournament.IsOpen) Tournament.Close(true); else PvpLobby.Close();
                return;
            }
            // the lap-time battle: Esc asks before leaving (10724)
            if (esc && LapTimeBattle.Active && !UiPopup.IsOpen && !Net.ChatBox.Typing) { MissionUi.Confirm(Pvp.Text("10724"), () => LapTimeBattle.Active?.Leave()); return; }
            if (esc && PvpLobby.AnyOpen) return;
            if (esc && !Net.ChatBox.Typing && !UiPopup.IsOpen && Social.Teams.CenterOpen) { Social.Teams.CloseTop(); return; }
            if (esc && !Net.ChatBox.Typing && !UiPopup.IsOpen && Social.Community.IsOpen) { Social.Community.Close(); return; }
            if (esc && UndercityStage.Active && !Net.ChatBox.Typing && !ListWindow.IsOpen && !UiPopup.IsOpen && !OriginalMenus.IsOpen) { UndercityStage.Active.AskQuit(); return; }
            if (esc && !Net.ChatBox.Typing && !ListWindow.IsOpen && ListWindow.ClosedFrame != Time.frameCount && !UiPopup.IsOpen && !OriginalMenus.IsOpen && !ParkingLotStage.GateOpen && ParkingLotStage.GateClosedFrame != Time.frameCount) ToggleQuitMenu();
        }

        void ToggleQuitMenu()
        {
            if (ui.IsOpen(quitMenu)) { ui.Close(quitMenu); return; }
            quitMenu = ui.Open(4040000); // QUIT
            ui.On("ID_QUIT_GAME", _ => Quit());
            ui.On("ID_QUIT_CHARSEL", _ => ShowCharacterSelect());
            ui.On("ID_QUIT_SERVERSEL", _ => ShowServerSelect());
            ui.On("ID_QUIT_CLOSE", v => ui.Close(v));
            ui.On("ID_QUIT_OPTION", v => { ui.Close(v); Menus.Options(); });
            ui.On("ID_QUIT_HELP", v => { ui.Close(v); HelpWindow.Open(); });
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
