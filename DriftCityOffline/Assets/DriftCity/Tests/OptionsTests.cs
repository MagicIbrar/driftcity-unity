using System.Collections;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// Options (goption.ini), the controller / key page, the help book, the car audio and the guide texts
    /// (DriftCity_RE/OPTIONS.md). Writes Preview/options_*.png, help_*.png, caraudio.png, guidetext_*.png.
    /// </summary>
    public class OptionsTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_options_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;   // goption.ini goes here too
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Optioner", Level = 1, Mito = 1000, City = "cras" };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Save();
            PlayerProfile.Current = null;
            GameOptions.PathOverride = null;
            Directory.CreateDirectory("Preview");
        }

        [TearDown]
        public void TearDown()
        {
            GameOptions.PathOverride = null;
            PlayerProfile.SaveDirOverride = null;
            GameOptions.Load();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { Directory.Delete(tempSave, true); } catch { }
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex);
            Debug.Log("Drift City options: wrote Preview/" + name + ".png");
        }

        [Test]
        public void GoptionIniRoundTripsWithTheClientKeys()
        {
            GameOptions.PathOverride = Path.Combine(tempSave, "rt_goption.ini");
            GameOptions.Load();
            Assert.IsTrue(GameOptions.MainUIType == 1, "MainUIType default / shipped 1");
            Assert.AreEqual(2, GameOptions.DefaultPlayerCamera);
            Assert.AreEqual(1, KeyBindings.ButtonFor(BindAction.Accel), "0x4c9440: ButtonAccel defaults to 1 (shipped 0 stands for the default)");
            Assert.AreEqual(10, KeyBindings.ButtonFor(BindAction.Item2));
            GameOptions.RotateMinimap = true;
            GameOptions.DenyParty = true;
            GameOptions.MusicVol = 0.25f;
            GameOptions.MultiSampling = 8;
            KeyBindings.SetKey(BindAction.Booster, Key.B);
            KeyBindings.SetButton(BindAction.Drift, 6);
            GameOptions.Save();
            var text = File.ReadAllText(GameOptions.FilePath);
            StringAssert.Contains("[Graphic]", text);
            StringAssert.Contains("RotateMinimap=true", text);
            StringAssert.Contains("DenyParty=true", text);
            StringAssert.Contains("MusicVol=0.250", text);
            StringAssert.Contains("MultiSampling=8", text);
            StringAssert.Contains("ButtonDrift=6", text);
            StringAssert.Contains("KeyBooster=B", text);
            GameOptions.Load();
            Assert.IsTrue(GameOptions.RotateMinimap);
            Assert.IsTrue(GameOptions.DenyParty);
            Assert.AreEqual(0.25f, GameOptions.MusicVol, 1e-4f);
            Assert.AreEqual(Key.B, KeyBindings.KeyFor(BindAction.Booster));
            Assert.AreEqual(6, KeyBindings.ButtonFor(BindAction.Drift));
            // a snapshot puts values back
            var snap = GameOptions.Snapshot();
            GameOptions.RotateMinimap = false;
            GameOptions.Restore(snap);
            Assert.IsTrue(GameOptions.RotateMinimap);
        }

        [UnityTest]
        public IEnumerator OptionsHelpCarAudioAndGuideTextInCras()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            if (CitySession.Current.Traffic) CitySession.Current.Traffic.MaxCars = 0;
            yield return new WaitForSeconds(2f);
            GuideTextDriver.Ensure();   // earlier tests' teardown may have removed the persistent hosts
            var file = GameOptions.FilePath;
            StringAssert.StartsWith(tempSave, file, "the test's goption.ini");

            // ---------------- options: GAME tab ----------------
            var mm = flow.GetComponent<Minimap>();
            Assert.IsNotNull(mm);
            Assert.IsFalse(mm.Rotate);
            Menus.Options();
            yield return null;
            var ui = UiManager.Instance;
            var baseView = ui.Find(OriginalMenus.OptionBaseDialog);
            Assert.IsNotNull(baseView, "OPTIONBASE open");
            baseView.Click("ID_OPTIONBASE_TAB_GAME");
            yield return null;
            Assert.AreEqual(2, OriginalMenus.OptionTabShown);
            var tab = OriginalMenus.OptionTabView;
            tab.Click("ID_OPTION_GAME_ROTATE_MAP");
            tab.Click("ID_OPTION_GAME_CB_DENYPARTY_ON");
            tab.Click("ID_OPTION_GAME_CB_SHOWUSERINMAP_OFF");
            yield return null;
            Assert.IsTrue(mm.Rotate, "ROTATE_MAP turns the minimap at once");
            Assert.IsFalse(mm.ShowOtherUsers);
            Assert.IsTrue(GameOptions.DenyParty);
            yield return new WaitForSeconds(0.3f);
            Shot("options_game");

            // ---------------- AUDIO tab ----------------
            baseView.Click("ID_OPTIONBASE_TAB_AUDIO");
            yield return null;
            OriginalMenus.OptionTabView.Click("ID_OPTION_AUDIO_MUTEOFF");
            Assert.AreEqual(0f, AudioListener.volume, "sound off mutes");
            OriginalMenus.OptionTabView.Click("ID_OPTION_AUDIO_MUTEON");
            Assert.AreEqual(1f, AudioListener.volume);
            Assert.IsTrue(OriginalMenus.PickOption(OriginalMenus.OptionTabView, "ID_OPTION_AUDIO_CB_SPEAKER", 1), "speaker list");
            Assert.AreEqual("Mono", GameOptions.SpeakerMode);
            // the device is switched only where there is one (batch runs have none: GameOptions skips the reset)
            if (!Application.isBatchMode) Assert.AreEqual(AudioSpeakerMode.Mono, AudioSettings.GetConfiguration().speakerMode);
            Assert.IsTrue(OriginalMenus.PickOption(OriginalMenus.OptionTabView, "ID_OPTION_AUDIO_CB_SPEAKER", 0));

            // ---------------- VIDEO tab + detail ----------------
            baseView.Click("ID_OPTIONBASE_TAB_VIDEO");
            yield return null;
            var video = OriginalMenus.OptionTabView;
            video.Click("ID_OPTION_VIDEO_RESOLUTION");   // drops the list
            yield return new WaitForSeconds(0.2f);
            Shot("options_video_list");
            Assert.IsTrue(OriginalMenus.PickOption(video, "ID_OPTION_VIDEO_SCREENMODE", 1));
            Assert.IsFalse(GameOptions.Fullscreen, "Window picked");
            video.Click("ID_OPTION_VIDEO_ADVANCED");
            yield return null;
            var detail = OriginalMenus.OptionDetailView;
            Assert.IsNotNull(detail, "OPTIONDETAIL open");
            Assert.IsTrue(OriginalMenus.PickOption(detail, "ID_OPTIONDETAIL_CB_ANTIALIAS", 3));
            Assert.AreEqual(8, QualitySettings.antiAliasing, "8x");
            Assert.IsTrue(OriginalMenus.PickOption(detail, "ID_OPTIONDETAIL_CB_SHADOWCAR", 0));
            Assert.AreEqual(0, GameOptions.VehicleShadow);
            Assert.IsTrue(flow.PlayerCar.GetComponentsInChildren<Renderer>().All(r => r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off), "car shadows off");
            Assert.IsTrue(OriginalMenus.PickOption(detail, "ID_OPTIONDETAIL_CB_SHADOWBUILDING", 1));
            Assert.AreEqual(ShadowQuality.Disable, QualitySettings.shadows, "no object shadows, no car shadows: no shadow pass");
            yield return new WaitForSeconds(0.3f);
            Shot("options_detail");
            detail.Click("ID_OPTIONDETAIL_APPLY");
            yield return null;
            Assert.IsNull(OriginalMenus.OptionDetailView);

            // ---------------- APPLY writes goption.ini ----------------
            baseView.Click("ID_OPTIONBASE_APPLY");
            yield return null;
            Assert.IsFalse(OriginalMenus.IsOpen);
            Assert.IsTrue(File.Exists(file), "goption.ini written");
            var text = File.ReadAllText(file);
            StringAssert.Contains("RotateMinimap=true", text);
            StringAssert.Contains("DenyParty=true", text);
            StringAssert.Contains("ShowAnotherUserInMinimap=false", text);
            StringAssert.Contains("MultiSampling=8", text);
            StringAssert.Contains("VehicleShadow=0", text);
            GameOptions.Load();
            Assert.IsTrue(GameOptions.RotateMinimap, "persisted");
            Assert.AreEqual(8, GameOptions.MultiSampling);

            // CLOSE puts the values from the opening back
            Menus.Options();
            yield return null;
            baseView = ui.Find(OriginalMenus.OptionBaseDialog);
            baseView.Click("ID_OPTIONBASE_TAB_GAME");
            OriginalMenus.OptionTabView.Click("ID_OPTION_GAME_ROTATE_CAR");
            Assert.IsFalse(mm.Rotate);
            baseView.Click("ID_OPTIONBASE_CLOSE");
            yield return null;
            Assert.IsTrue(mm.Rotate, "CLOSE reverted");
            Assert.IsTrue(GameOptions.RotateMinimap);
            // back to the shipped look for the rest
            GameOptions.ShowObjectShadow = 1; GameOptions.VehicleShadow = 3; GameOptions.MultiSampling = 4; GameOptions.RotateMinimap = false;
            GameOptions.Apply();

            // ---------------- key rebind (OPTION 3000000) ----------------
            Menus.Options();
            yield return null;
            baseView = ui.Find(OriginalMenus.OptionBaseDialog);
            baseView.Click("ID_OPTIONBASE_TAB_INPUT");
            yield return null;
            Assert.IsTrue(OriginalMenus.PickOption(OriginalMenus.OptionTabView, "ID_OPTION_GAME_COMBO", 0), "Keyboard");
            yield return null;
            var pad = OriginalMenus.ControllerView;
            Assert.IsNotNull(pad, "controller page open");
            Assert.IsFalse(KeyBindings.PadUse);
            pad.Click("ID_OPTION_CONTROLLER_KEY_ACCEL");
            yield return null;
            Assert.IsTrue(OriginalMenus.RebindWaiting, "waits for a key");
            Assert.IsTrue(OriginalMenus.FeedRebind(Key.K));
            Assert.AreEqual(Key.K, KeyBindings.KeyFor(BindAction.Accel));
            yield return new WaitForSeconds(0.2f);
            Shot("options_controller");
            pad.Click("ID_OPTION_CONTROLLER_APPLY");
            yield return null;
            baseView.Click("ID_OPTIONBASE_APPLY");
            yield return null;
            StringAssert.Contains("KeyAccel=K", File.ReadAllText(file));

            // the new key drives the car; the old one doesn't
            // batch mode has no focused game view: let the test keyboard's input through regardless of focus
            var bg = InputSystem.settings.backgroundBehavior;
            var editorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            var kb = InputSystem.AddDevice<Keyboard>("OptionsTestKeyboard");
            kb.MakeCurrent();
            var car = flow.PlayerCar;
            InputSystem.QueueStateEvent(kb, new KeyboardState(Key.UpArrow));
            InputSystem.Update();
            KeyBindings.ReadDriving(out float t0, out _, out _, out _);
            Assert.AreEqual(0f, t0, "Up no longer accelerates");
            InputSystem.QueueStateEvent(kb, new KeyboardState(Key.K));
            InputSystem.Update();
            KeyBindings.ReadDriving(out float t1, out _, out _, out _);
            Assert.AreEqual(1f, t1, "K accelerates");
            float before = car.SpeedKmh;
            var body = car.GetComponent<Rigidbody>();
            for (float t = 0; t < 3f; t += Time.deltaTime)
            {
                InputSystem.QueueStateEvent(kb, new KeyboardState(Key.K));
                yield return null;
                if (Time.frameCount % 30 == 0)
                {
                    KeyBindings.ReadDriving(out float th, out _, out _, out _);
                    Debug.Log($"Drift City options: t={t:F2} throttle={th} current={(Keyboard.current != null ? Keyboard.current.name : "null")} ext={car.ExternalControl} kinematic={(body ? body.isKinematic : false)} timeScale={Time.timeScale} typing={Net.ChatBox.Typing} speed={car.SpeedKmh:F1}");
                }
            }
            Debug.Log($"Drift City options: speed {before:F1} -> {car.SpeedKmh:F1} km/h on K");
            Assert.Greater(car.SpeedKmh, before + 10f, "the car accelerates on the rebound key");
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            InputSystem.Update();
            InputSystem.RemoveDevice(kb);
            InputSystem.settings.backgroundBehavior = bg;
            InputSystem.settings.editorInputBehaviorInPlayMode = editorBehavior;
            KeyBindings.SetKey(BindAction.Accel, Key.UpArrow);

            // ---------------- help ----------------
            HelpWindow.Open(0);
            yield return null;
            Assert.IsTrue(HelpWindow.IsOpen);
            var help = HelpWindow.View;
            Assert.AreEqual(21, HelpWindow.ChapterCount);
            StringAssert.Contains("Controls", HelpWindow.Get(0).Title);
            Assert.IsTrue(help.Control("ID_HELPADV_IMG_KEYCTRL").gameObject.activeSelf, "chapter 1 shows the key chart");
            yield return new WaitForSeconds(0.2f);
            Shot("help_controls");
            help.Click("ID_HELPADV_NEXT");
            Assert.AreEqual(1, HelpWindow.Current);
            Assert.IsFalse(help.Control("ID_HELPADV_IMG_KEYCTRL").gameObject.activeSelf);
            Assert.Greater(HelpWindow.Get(1).Lines.Count, 3, "h2.xlt lines");
            help.Click("ID_HELPADV_COMM_CHAT");
            Assert.AreEqual(3, HelpWindow.Current);
            help.Click("ID_HELPADV_MINUS4");
            Assert.IsFalse(help.Control("ID_HELPADV_ITEM_REMODEL").gameObject.activeSelf, "Item collapsed");
            yield return new WaitForSeconds(0.2f);
            Shot("help_chat");
            help.Click("ID_HELPADV_GAME_COMBO");
            Assert.IsTrue(help.Control("ID_HELPADV_IMG_COMBO").gameObject.activeSelf, "combo chart");
            help.Click("ID_HELPADV_CLOSE");
            yield return null;
            Assert.IsFalse(HelpWindow.IsOpen);

            // ---------------- car audio ----------------
            CarAudio.Show();
            yield return null;
            Assert.IsTrue(CarAudio.Visible);
            var ca = CarAudio.Instance;
            ca.SetChannel(false, false);
            yield return WaitFor(() => AudioLibrary.CurrentMusic, 20, "game music");
            yield return new WaitForSeconds(0.5f);
            bool audioRuns = AudioLibrary.MusicPlaying;   // batch mode may run without an audio device
            Debug.Log("Drift City car audio: audio device running = " + audioRuns);
            var first = AudioLibrary.CurrentMusic ? AudioLibrary.CurrentMusic.name : "";
            CarAudio.View.Click("ID_CARAUDIO_NEXT");
            yield return WaitFor(() => AudioLibrary.CurrentMusic && AudioLibrary.CurrentMusic.name != first, 30, "next track");
            Debug.Log($"Drift City car audio: {first} -> {AudioLibrary.CurrentMusic.name} ({ca.Title})");
            Assert.AreEqual(CarAudio.SongTitle(ca.Playlist[ca.Track]), ca.Title, "music.txt title");
            Assert.AreEqual(ca.Title, CarAudio.Strip.Control("ID_CARAUDIO_TITLE_TEXT").GetComponentInChildren<UnityEngine.UI.Text>().text);
            CarAudio.View.Click("ID_CARAUDIO_PAUSE");
            Assert.IsTrue(ca.Paused, "paused");
            Assert.IsFalse(AudioLibrary.MusicPlaying, "paused");
            CarAudio.View.Click("ID_CARAUDIO_PLAY");
            Assert.IsFalse(ca.Paused);
            if (audioRuns) Assert.IsTrue(AudioLibrary.MusicPlaying, "playing again");
            CarAudio.View.Click("ID_CARAUDIO_AUDIOCHANNEL");
            yield return new WaitForSeconds(0.7f);
            Shot("caraudio");
            CarAudio.View.Click("ID_CARAUDIO_CH_MYMP3");
            Assert.IsTrue(GameOptions.UseUserMP3);
            if (CarAudio.UserTracks().Count == 0) Assert.AreEqual(MissionText.Get("10481"), ca.Title, "empty BGM folder");
            CarAudio.View.Click("ID_CARAUDIO_CH_SKIDRUSH");
            Assert.IsFalse(GameOptions.UseUserMP3);

            // ---------------- guide text ----------------
            var p = PlayerProfile.Current;
            var q = QuestTracker.All.FirstOrDefault(x => x.ReqLevel <= p.Level && !p.ActiveQuests.Any(a => a.QuestId == x.Id));
            Assert.IsNotNull(q, "a quest to take");
            // a new driver without quests: "Follow the arrow to begin a rookie mission."
            GuideText.HideAll();
            yield return WaitFor(() => GuideText.Shown == GuideKind.BeginnerQuest, 5, "BEGINNERQUEST");
            var gv = GuideTextDriver.View;
            Assert.IsNotNull(gv);
            Assert.AreEqual(GuideText.Adv, gv.Dialog.Id, "ADV guide texts with MainUIType 1");
            Assert.IsTrue(gv.Control("ID_GUIDETEXT_ADV_BEGINNERQUEST").gameObject.activeSelf);
            Shot("guidetext_beginner");
            // a quest taken: "Mission In Progress."
            p.ActiveQuests.Add(new QuestProgress { QuestId = q.Id });
            yield return WaitFor(() => GuideText.Shown == GuideKind.QuestIng || GuideText.Shown == GuideKind.RequestHouseGo, 3, "QUEST_ING while the quest runs");
            Assert.IsTrue(gv.Control(GuideText.ControlId(GuideText.Shown.Value, true)).gameObject.activeSelf);
            yield return new WaitForSeconds(0.2f);
            Shot("guidetext_quest_ing");
            GuideText.Flash(GuideKind.HuvFind, 2f);
            yield return null;
            yield return null;
            Assert.AreEqual(GuideKind.HuvFind, GuideText.Shown);
            Shot("guidetext_huv_find");
            p.ActiveQuests.Clear();

            // every strip once, stacked into one picture (Preview/guidetext_all.png) to read the pre-drawn texts
            {
                var kinds = (GuideKind[])System.Enum.GetValues(typeof(GuideKind));
                const int cw = 460, ch = 34;
                var sheet = new Texture2D(cw, ch * kinds.Length, TextureFormat.RGB24, false);
                for (int i = 0; i < kinds.Length; i++)
                {
                    GuideText.HideAll();
                    GuideText.Flash(kinds[i], 5f);
                    yield return null;
                    yield return null;
                    var tex = UiManager.Instance.Capture(Camera.main);
                    // the ADV strips sit right of centre above the speedometer: x 560..1020, y 610..644 of the 1024x768 capture
                    int x0 = tex.width - cw - 4, y0 = tex.height - 644;
                    for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++) sheet.SetPixel(x, ch * (kinds.Length - 1 - i) + y, tex.GetPixel(x0 + x, y0 + y));
                    Object.Destroy(tex);
                }
                sheet.Apply();
                File.WriteAllBytes(Path.Combine("Preview", "guidetext_all.png"), sheet.EncodeToPNG());
                GuideText.HideAll();
            }

            // ---------------- chat balloon ----------------
            GameOptions.ShowDialogBallon = true;
            var balloon = ChatBalloon.Say(flow.PlayerCar.transform, "Optioner", "Hello Cras! Anyone up for a race?");
            Assert.IsNotNull(balloon, "balloon shown");
            yield return new WaitForSeconds(0.3f);
            Assert.AreEqual(1, ChatBalloon.Count);
            Assert.IsTrue(balloon.View.gameObject.activeSelf, "over the car, on screen");
            Shot("chat_balloon");
            GameOptions.ShowDialogBallon = false;
            yield return null;
            yield return null;
            Assert.AreEqual(0, ChatBalloon.Count, "the option hides balloons");
            Assert.IsNull(ChatBalloon.Say(flow.PlayerCar.transform, "Optioner", "hidden"));
            GameOptions.ShowDialogBallon = true;
        }
    }
}
