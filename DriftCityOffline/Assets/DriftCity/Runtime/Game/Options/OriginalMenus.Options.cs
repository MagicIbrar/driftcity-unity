using System.Collections.Generic;
using System.Linq;
using DriftCity.Social;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity.Game
{
    /// <summary>
    /// The options screens (DriftCity_RE/OPTIONS.md): OPTIONBASE 4250000 with the VIDEO / AUDIO / GAME / INPUT tabs
    /// (4260000-4290000), the advanced video page OPTIONDETAIL 4300000 and the controller page OPTION 3000000.
    /// Every control edits goption.ini (GameOptions) and applies at once; Apply saves the file (the client's 0x4ca130),
    /// Close / Exit / Esc put back what was set when the window opened.
    /// </summary>
    public static partial class OriginalMenus
    {
        static readonly string[] OptionTabs = { "VIDEO", "AUDIO", "GAME", "INPUT" };
        static readonly int[] OptionTabDialogs = { 4260000, 4270000, 4280000, 4290000 };
        public const int OptionBaseDialog = 4250000, OptionDetailDialog = 4300000, ControllerDialog = 3000000;
        static NuiView optBase, optTab, optDetail, optPad;
        static string optionsAtOpen, detailAtOpen, padAtOpen;
        /// <summary>Remake settings outside goption.ini (HD textures, arcade look, traffic density).</summary>
        struct RemakeValues { public bool Arcade, Hd; public int Traffic; }
        static RemakeValues remakeAtOpen, remakeDetailAtOpen;

        /// <summary>The tab shown (0 video .. 3 input), for tests.</summary>
        public static int OptionTabShown { get; private set; } = -1;
        public static NuiView OptionTabView => optTab;
        public static NuiView OptionDetailView => optDetail;
        public static NuiView ControllerView => optPad;

        static TrafficSystem Traffic => CitySession.Current ? CitySession.Current.Traffic : null;

        static RemakeValues ReadRemake()
        {
            var t = Traffic;
            return new RemakeValues { Arcade = ArcadeLook.Enabled, Hd = TextureLibrary.UseEnhanced, Traffic = t ? t.MaxCars : -1 };
        }

        static void WriteRemake(RemakeValues o)
        {
            if (ArcadeLook.Enabled != o.Arcade) SetArcade(o.Arcade);
            if (TextureLibrary.UseEnhanced != o.Hd) TextureLibrary.UseEnhanced = o.Hd;
            var t = Traffic;
            if (t && o.Traffic >= 0) t.MaxCars = o.Traffic;
        }

        static void SetArcade(bool on)
        {
            ArcadeLook.Enabled = on;
            if (!on) { var f = GameFlow.Instance; if (f) f.ReapplyLighting(); }
        }

        /// <summary>Opens the options on the AUDIO tab (the tab the client opens with is the last one used; AUDIO the first time).</summary>
        public static void Options()
        {
            // the basic main bar (MainUIType 0) has the classic OPTION 2980000 window (OriginalMenus.ClassicOptions.cs)
            if (GameOptions.MainUIType == 0) { ClassicOptions(classicLastTab); return; }
            Options(lastTab);
        }

        static int lastTab = 1;

        public static void Options(int tab)
        {
            optionsAtOpen = GameOptions.Snapshot();
            remakeAtOpen = ReadRemake();
            optBase = OpenScreen(OptionBaseDialog, OptionsBack);
            if (!optBase) return;
            OnClose(() => { ComboList.CloseOpen(); KeyCapture.Stop(); optBase = optTab = optDetail = optPad = null; OptionTabShown = -1; GameOptions.Save(); });   // closed some other way (another menu, travel): keep what is set
            optTab = optDetail = optPad = null;
            foreach (var t in OptionTabs) optBase.SetVisible("ID_OPTIONBASE_TABKEY_" + t, false);   // invisible hotkey buttons lying over the tabs
            for (int i = 0; i < OptionTabs.Length; i++) { int n = i; Ui.On("ID_OPTIONBASE_TAB_" + OptionTabs[i], _ => OptionTab(n)); }
            Ui.On("ID_OPTIONBASE_APPLY", _ => ApplyOptions());
            Ui.On("ID_OPTIONBASE_CLOSE", _ => CancelOptions());
            Ui.On("ID_OPTIONBASE_EXIT", _ => CancelOptions());
            OptionTab(Mathf.Clamp(tab, 0, OptionTabs.Length - 1));
        }

        /// <summary>APPLY: keep everything and write goption.ini.</summary>
        public static void ApplyOptions()
        {
            GameOptions.Save();
            AudioLibrary.SaveVolumes();
            Close();
        }

        /// <summary>CLOSE / EXIT / Esc: put back the values from when the window opened.</summary>
        public static void CancelOptions()
        {
            GameOptions.Restore(optionsAtOpen);
            WriteRemake(remakeAtOpen);
            Close();
        }

        static bool OptionsBack()
        {
            if (KeyCapture.Active) { KeyCapture.Stop(); RefreshPad(); return true; }
            if (ComboList.IsOpen) { ComboList.CloseOpen(); return true; }
            if (optPad) { ClosePad(true); return true; }
            if (optDetail) { CloseOptionDetail(true); return true; }
            GameOptions.Restore(optionsAtOpen);
            WriteRemake(remakeAtOpen);
            return false;
        }

        static void OptionTab(int tab)
        {
            if (!optBase) return;
            ComboList.CloseOpen();
            if (optDetail) CloseOptionDetail(false);
            if (optPad) ClosePad(false);
            CloseView(optTab);
            for (int i = 0; i < OptionTabs.Length; i++) Pressed(optBase.Control("ID_OPTIONBASE_TAB_" + OptionTabs[i]), i == tab);
            optTab = Open(OptionTabDialogs[tab]);
            OptionTabShown = tab;
            lastTab = tab;
            if (!optTab) return;
            switch (tab)
            {
                case 0: VideoTab(optTab); break;
                case 1: AudioTab(optTab); break;
                case 2: GameTab(optTab); break;
                case 3: InputTab(optTab); break;
            }
        }

        // ---------------- VIDEO 4260000 ----------------
        static readonly string[] Ratios = { "4:3", "Wide" };

        static bool IsWide(int w, int h) => h > 0 && w * 3 > h * 4 + 8;

        /// <summary>The resolutions the display offers for a ratio (distinct sizes, small to large), with the current one.</summary>
        public static List<Vector2Int> Resolutions(bool wide)
        {
            var list = new List<Vector2Int>();
            foreach (var r in Screen.resolutions)
            {
                var v = new Vector2Int(r.width, r.height);
                if (v.x < 800 || v.y < 600 || IsWide(v.x, v.y) != wide || list.Contains(v)) continue;
                list.Add(v);
            }
            if (list.Count == 0)
                foreach (var v in wide ? new[] { new Vector2Int(1280, 720), new Vector2Int(1366, 768), new Vector2Int(1600, 900), new Vector2Int(1920, 1080) }
                                       : new[] { new Vector2Int(800, 600), new Vector2Int(1024, 768), new Vector2Int(1280, 960), new Vector2Int(1600, 1200) })
                    list.Add(v);
            var cur = new Vector2Int(GameOptions.ScreenWidth, GameOptions.ScreenHeight);
            if (IsWide(cur.x, cur.y) == wide && !list.Contains(cur)) list.Add(cur);
            return list.OrderBy(v => v.x * 10000 + v.y).ToList();
        }

        static string QualityLabel()
        {
            int g = GameOptions.VideoGradeSet;
            if (g < 0 || GameOptions.CustomVideoGrade || g >= QualitySettings.names.Length) return "Custom";
            return QualitySettings.names[g];
        }

        static void VideoTab(NuiView v)
        {
            bool wide = IsWide(GameOptions.ScreenWidth, GameOptions.ScreenHeight);
            Pick(v, "ID_OPTION_VIDEO_SCREENRATIO", Ratios, wide ? 1 : 0, i =>
            {
                var list = Resolutions(i == 1);
                var best = list.OrderBy(r => Mathf.Abs(r.y - GameOptions.ScreenHeight)).First();
                SetResolution(best.x, best.y);
                VideoTab(v);
            });
            var res = Resolutions(wide);
            int at = res.IndexOf(new Vector2Int(GameOptions.ScreenWidth, GameOptions.ScreenHeight));
            Pick(v, "ID_OPTION_VIDEO_RESOLUTION", res.Select(r => $"{r.x} x {r.y}").ToArray(), at, i => { SetResolution(res[i].x, res[i].y); VideoTab(v); });
            Pick(v, "ID_OPTION_VIDEO_SCREENMODE", new[] { "Full screen", "Window" }, GameOptions.Fullscreen ? 0 : 1, i =>
            {
                GameOptions.Fullscreen = i == 0;
                GameOptions.ApplyScreen();
            });
            Pick(v, "ID_OPTION_VIDEO_MAINUI", new[] { "Basic", "Advanced" }, GameOptions.MainUIType == 0 ? 0 : 1, i => GameOptions.MainUIType = i);
            var grades = QualitySettings.names.Concat(new[] { "Custom" }).ToArray();
            int grade = GameOptions.CustomVideoGrade || GameOptions.VideoGradeSet < 0 ? grades.Length - 1 : Mathf.Min(GameOptions.VideoGradeSet, grades.Length - 1);
            Pick(v, "ID_OPTION_VIDEO_CBGRAPHIC", grades, grade, i =>
            {
                bool custom = i == grades.Length - 1;
                GameOptions.CustomVideoGrade = custom;
                GameOptions.VideoGradeSet = custom ? -1 : i;
                if (!custom) QualitySettings.SetQualityLevel(i, true);
                GameOptions.ApplyGraphics();
            });
            Ui.On("ID_OPTION_VIDEO_ADVANCED", _ => OpenOptionDetail());
        }

        static void SetResolution(int w, int h)
        {
            GameOptions.ScreenWidth = w;
            GameOptions.ScreenHeight = h;
            GameOptions.ApplyScreen();
        }

        // ---------------- AUDIO 4270000 ----------------
        static void AudioTab(NuiView v)
        {
            // MUTEON = sound on (the client's "Enable")
            Radio(v, "ID_OPTION_AUDIO_MUTEON", "ID_OPTION_AUDIO_MUTEOFF", GameOptions.SoundEnable, on => { GameOptions.SoundEnable = on; GameOptions.ApplyAudio(); });
            Slider(v, "ID_OPTION_AUDIO_SLIDER_BGM", GameOptions.MusicVol, x => { GameOptions.MusicVol = x; GameOptions.ApplyAudio(); });
            Slider(v, "ID_OPTION_AUDIO_SLIDER_3D", GameOptions.FXSoundVol, x => { GameOptions.FXSoundVol = x; GameOptions.ApplyAudio(); });
            Slider(v, "ID_OPTION_AUDIO_SLIDER_UI", GameOptions.UISoundVol, x => { GameOptions.UISoundVol = x; GameOptions.ApplyAudio(); });
            Slider(v, "ID_OPTION_AUDIO_SLIDER_ENGINE", GameOptions.EngineVol, x => { GameOptions.EngineVol = x; GameOptions.ApplyAudio(); });
            int sp = System.Array.IndexOf(GameOptions.SpeakerModes, GameOptions.SpeakerMode);
            Pick(v, "ID_OPTION_AUDIO_CB_SPEAKER", GameOptions.SpeakerModes, sp < 0 ? 0 : sp, i => { GameOptions.SpeakerMode = GameOptions.SpeakerModes[i]; GameOptions.ApplyAudio(); });
        }

        // ---------------- GAME 4280000 ----------------
        static void GameTab(NuiView v)
        {
            // ROTATE_CAR: north up, the arrow turns; ROTATE_MAP: the map turns with the car (goption RotateMinimap)
            Radio(v, "ID_OPTION_GAME_ROTATE_MAP", "ID_OPTION_GAME_ROTATE_CAR", GameOptions.RotateMinimap, on => { GameOptions.RotateMinimap = on; GameOptions.ApplyGame(); });
            Radio(v, "ID_OPTION_GAME_CB_DENYPARTY_ON", "ID_OPTION_GAME_CB_DENYPARTY_OFF", GameOptions.DenyParty, SetDenyParty);
            Radio(v, "ID_OPTION_GAME_CB_SHOWUSERINMAP_ON", "ID_OPTION_GAME_CB_SHOWUSERINMAP_OFF", GameOptions.ShowAnotherUserInMinimap, on => { GameOptions.ShowAnotherUserInMinimap = on; GameOptions.ApplyGame(); });
            Radio(v, "ID_OPTION_GAME_CB_SHOWINSTANTMSG_ON", "ID_OPTION_GAME_CB_SHOWINSTANTMSG_OFF", GameOptions.ShowInstanceMSG, on => GameOptions.ShowInstanceMSG = on);
            Radio(v, "ID_OPTION_GAME_CB_SHOWBALLONCHAT_ON", "ID_OPTION_GAME_CB_SHOWBALLONCHAT_OFF", GameOptions.ShowDialogBallon, on => GameOptions.ShowDialogBallon = on);
            Radio(v, "ID_OPTION_GAME_CB_FRIENDTEAMNOTIFY_ON", "ID_OPTION_GAME_CB_FRIENDTEAMNOTIFY_OFF", GameOptions.FriendTeamNotify, on => GameOptions.FriendTeamNotify = on);
            Radio(v, "ID_OPTION_GAME_CB_NAVIGATION_ON", "ID_OPTION_GAME_CB_NAVIGATION_OFF", GameOptions.Navigation, on => GameOptions.Navigation = on);
            Hide(v, "ID_OPTION_GAME_CB_PASSENGER_ON");   // a leftover lying over the notification row
            // online the server keeps the refuse-party switch (social.options): show what it has
            if (SocialNet.Online)
                SocialNet.Request("social.options", null, r =>
                {
                    if (r == null || !r.ContainsKey("refuseParty") || !v || optTab != v) return;
                    bool deny = r["refuseParty"] is bool b && b;
                    if (deny != GameOptions.DenyParty) { GameOptions.DenyParty = deny; Radio(v, "ID_OPTION_GAME_CB_DENYPARTY_ON", "ID_OPTION_GAME_CB_DENYPARTY_OFF", deny, SetDenyParty); }
                });
        }

        /// <summary>DenyParty: refuse party invitations. Online the server answers invitations with a refusal (social.options refuseParty).</summary>
        public static void SetDenyParty(bool on)
        {
            GameOptions.DenyParty = on;
            if (SocialNet.Online) SocialNet.Request("social.options", new Dictionary<string, object> { ["refuseParty"] = on }, null);
        }

        // ---------------- INPUT 4290000 ----------------
        static void InputTab(NuiView v)
        {
            var devices = new List<string> { "Keyboard" };
#if ENABLE_INPUT_SYSTEM
            foreach (var g in Gamepad.all) devices.Add(string.IsNullOrEmpty(g.displayName) ? "Gamepad" : g.displayName);
#endif
            if (devices.Count == 1) devices.Add("Gamepad");
            int cur = KeyBindings.PadUse ? Mathf.Clamp(GameOptions.GetInt("Controller", "Index") + 1, 1, devices.Count - 1) : 0;
            Pick(v, "ID_OPTION_GAME_COMBO", devices.ToArray(), cur, i =>
            {
                KeyBindings.PadUse = i > 0;
                if (i > 0) GameOptions.Set("Controller", "Index", i - 1);
                OpenPad();
            });
        }

        // ---------------- OPTIONDETAIL 4300000 ----------------
        static readonly string[] TextureCombos = { "ID_OPTIONDETAIL_CB_TEXTURELAND", "ID_OPTIONDETAIL_CB_TEXTUREBIULDING", "ID_OPTIONDETAIL_CB_TEXTURECAR" };
        static readonly string[] OnOff = { "On", "Off" };

        /// <summary>
        /// Advanced video. Applied in Unity: car / building shadows, anti-aliasing, HDR, view distance (traffic density
        /// in this rebuild), HD textures (texture quality), the arcade look (FOV Arcade/Real). Kept in goption.ini only:
        /// animation distance, car reflection, DOF, effects, battle animation. Gamma is inert in the client too (0x539fcf).
        /// </summary>
        static void OpenOptionDetail()
        {
            if (optDetail) return;
            detailAtOpen = GameOptions.Snapshot();
            remakeDetailAtOpen = ReadRemake();
            optDetail = Open(OptionDetailDialog);
            if (!optDetail) return;
            Ui.On("ID_OPTIONDETAIL_APPLY", _ => CloseOptionDetail(false));
            Ui.On("ID_OPTIONDETAIL_CLOSE", _ => CloseOptionDetail(true));
            Ui.On("ID_OPTIONDETAIL_EXIT", _ => CloseOptionDetail(true));
            FillDetail(optDetail);
        }

        static void Custom() { GameOptions.CustomVideoGrade = true; GameOptions.VideoGradeSet = -1; }

        static void FillDetail(NuiView v)
        {
            // texture quality: the HD replacements (next city load or travel)
            foreach (var id in TextureCombos)
                Pick(v, id, new[] { "HD", "Normal" }, TextureLibrary.UseEnhanced ? 0 : 1, i =>
                {
                    TextureLibrary.UseEnhanced = i == 0;
                    GameOptions.TextureQuality = 4;
                    Custom();
                    FillDetail(v);
                });
            Radio(v, "ID_OPTIONDETAIL_CB_FOV_ACADE", "ID_OPTIONDETAIL_CB_FOV_REAL", GameOptions.FovType == 0, arcade => { GameOptions.FovType = arcade ? 0 : 1; SetArcade(arcade); });
            var traffic = Traffic;
            Slider(v, "ID_OPTIONDETAIL_SLIDER_VIEWDISTANCE", (GameOptions.VisibleRange - 1) / 4f, x =>
            {
                GameOptions.VisibleRange = 1 + Mathf.RoundToInt(x * 4f);
                Custom();
                var t = Traffic;
                if (t) t.MaxCars = Mathf.RoundToInt(x * 40f);
            });
            Pick(v, "ID_OPTIONDETAIL_CB_ANIVIEWDISTANCE", new[] { "Near", "Normal", "Far" }, Mathf.Clamp(GameOptions.AniVisibleRange, 0, 2), i => { GameOptions.AniVisibleRange = i; Custom(); });
            Pick(v, "ID_OPTIONDETAIL_CB_SHADOWBUILDING", OnOff, GameOptions.ShowObjectShadow != 0 ? 0 : 1, i => { GameOptions.ShowObjectShadow = i == 0 ? 1 : 0; Custom(); GameOptions.ApplyGraphics(); });
            Pick(v, "ID_OPTIONDETAIL_CB_SHADOWCAR", new[] { "Off", "Flat", "Real" }, GameOptions.VehicleShadow <= 0 ? 0 : GameOptions.VehicleShadow == 1 ? 1 : 2, i =>
            {
                GameOptions.VehicleShadow = i == 2 ? 3 : i;
                Custom();
                GameOptions.ApplyGraphics();
            });
            Pick(v, "ID_OPTIONDETAIL_CB_REFLECTCAR", new[] { "Off", "Low", "High" }, Mathf.Clamp(GameOptions.EnvMapType, 0, 2), i => { GameOptions.EnvMapType = i; Custom(); });
            Pick(v, "ID_OPTIONDETAIL_CB_DOF", OnOff, GameOptions.DofType != 0 ? 0 : 1, i => { GameOptions.DofType = i == 0 ? 1 : 0; Custom(); });
            Pick(v, "ID_OPTIONDETAIL_CB_HDR", OnOff, GameOptions.Hdr != 0 ? 0 : 1, i =>
            {
                GameOptions.Hdr = i == 0 ? 1 : 0;
                // 0x539f78: switching HDR on with DOF off turns DOF on (the client's DOF pass needs the HDR target)
                if (GameOptions.Hdr != 0 && GameOptions.DofType == 0) GameOptions.DofType = 1;
                Custom();
                GameOptions.ApplyGraphics();
                FillDetail(v);
            });
            Combo(v, "ID_OPTIONDETAIL_CB_GAMMA", "Normal", false);
            int aa = GameOptions.MultiSampling;
            Pick(v, "ID_OPTIONDETAIL_CB_ANTIALIAS", new[] { "Off", "2x", "4x", "8x" }, aa >= 8 ? 3 : aa >= 4 ? 2 : aa >= 2 ? 1 : 0, i =>
            {
                GameOptions.MultiSampling = i == 0 ? 0 : 1 << i;
                Custom();
                GameOptions.ApplyGraphics();
            });
            Radio(v, "ID_OPTIONDETAIL_CB_MYEFFECT_ON", "ID_OPTIONDETAIL_CB_MYEFFECT_OFF", GameOptions.ShowEffectMine, on => GameOptions.ShowEffectMine = on);
            Radio(v, "ID_OPTIONDETAIL_CB_YOUREFFECT_ON", "ID_OPTIONDETAIL_CB_YOUREFFECT_OFF", GameOptions.ShowEffectUsers, on => GameOptions.ShowEffectUsers = on);
            Radio(v, "ID_OPTIONDETAIL_CB_BATTLEANIMATION_ON", "ID_OPTIONDETAIL_CB_BATTLEANIMATION_OFF", GameOptions.BattleAnimation, on => GameOptions.BattleAnimation = on);
        }

        static void CloseOptionDetail(bool revert)
        {
            ComboList.CloseOpen();
            if (revert) { GameOptions.Restore(detailAtOpen); WriteRemake(remakeDetailAtOpen); }
            CloseView(optDetail);
            optDetail = null;
            if (optTab && OptionTabShown == 0) VideoTab(optTab);   // the graphics combo shows "Custom" now
        }

        // ---------------- OPTION 3000000: controller / keys ----------------
        static readonly string[] PadKeys = { "ACCEL", "BRAKE", "BOOST", "DRIFT", "ENTER", "ESC", "SPACE", "TAB", "ITEM1", "ITEM2" };

        /// <summary>Opens the controller page over the options (INPUT tab's device combo).</summary>
        public static void OpenPad()
        {
            ComboList.CloseOpen();
            if (optPad) { RefreshPad(); return; }
            padAtOpen = GameOptions.Snapshot();
            optPad = optBase || classic ? Open(ControllerDialog) : Ui.Open(ControllerDialog);
            if (!optPad) return;
            Ui.On("ID_OPTION_CONTROLLER_APPLY", _ => ClosePad(false));
            Ui.On("ID_OPTION_CONTROLLER_ESC", _ => ClosePad(true));
            Ui.On("ID_OPTION_CONTROLLER_CLOSE", _ => ClosePad(true));
            Ui.On("ID_OPTION_CONTROLLER_USE", _ => { KeyBindings.PadUse = true; KeyCapture.Stop(); RefreshPad(); });
            Ui.On("ID_OPTION_CONTROLLER_USENOT", _ => { KeyBindings.PadUse = false; KeyCapture.Stop(); RefreshPad(); });
            Ui.On("ID_OPTION_CONTROLLER_STICK_NO", _ => { KeyBindings.StickType = 0; RefreshPad(); });
            Ui.On("ID_OPTION_CONTROLLER_STICK_TYPE1", _ => { KeyBindings.StickType = 1; RefreshPad(); });
            Ui.On("ID_OPTION_CONTROLLER_STICK_TYPE2", _ => { KeyBindings.StickType = 2; RefreshPad(); });
            for (int i = 0; i < PadKeys.Length; i++)
            {
                var action = (BindAction)i;
                Ui.On("ID_OPTION_CONTROLLER_KEY_" + PadKeys[i], _ => BeginRebind(action));
            }
            RefreshPad();
        }

        /// <summary>Starts waiting for the key (keyboard mode) or pad button (pad mode) for an action.</summary>
        public static void BeginRebind(BindAction action)
        {
            if (!optPad) return;
            bool pad = KeyBindings.PadUse;
            KeyCapture.Start(optPad, action, pad, RefreshPad);
            optPad.SetText("ID_OPTION_CONTROLLER_TEXT_" + PadKeys[(int)action], pad ? "Press a button" : "Press a key");
        }

        static void RefreshPad()
        {
            var v = optPad;
            if (!v) return;
            bool pad = KeyBindings.PadUse;
            // these radios draw their ring as the second element (always shown) and the dot as the checked state
            void Tick(string id, bool on) { var c = v.Control(id); if (c) c.SetChecked(on); }
            Tick("ID_OPTION_CONTROLLER_USE", pad);
            Tick("ID_OPTION_CONTROLLER_USENOT", !pad);
            int type = KeyBindings.StickType;
            Tick("ID_OPTION_CONTROLLER_STICK_NO", type == 0);
            Tick("ID_OPTION_CONTROLLER_STICK_TYPE1", type == 1);
            Tick("ID_OPTION_CONTROLLER_STICK_TYPE2", type == 2);
            foreach (var id in new[] { "ID_OPTION_CONTROLLER_STICK_NO", "ID_OPTION_CONTROLLER_STICK_TYPE1", "ID_OPTION_CONTROLLER_STICK_TYPE2" }) Dim(v.Control(id), !pad);
            for (int i = 0; i < PadKeys.Length; i++)
            {
                var label = v.Control("ID_OPTION_CONTROLLER_TEXT_" + PadKeys[i]);
                if (!label) continue;
                label.SetText(KeyBindings.Label((BindAction)i, pad));
                foreach (var t in label.GetComponentsInChildren<Text>(true)) { t.horizontalOverflow = HorizontalWrapMode.Overflow; t.alignment = TextAnchor.MiddleCenter; }
            }
        }

        static void ClosePad(bool revert)
        {
            KeyCapture.Stop();
            if (revert) GameOptions.Restore(padAtOpen);
            if (optPad) { if (views.Contains(optPad)) CloseView(optPad); else Ui.Close(optPad); }
            optPad = null;
            if (optTab && OptionTabShown == 3) InputTab(optTab);
        }

        /// <summary>Waits for one key / pad button and binds it (Esc cancels).</summary>
        sealed class KeyCapture : MonoBehaviour
        {
            static KeyCapture active;
            BindAction action;
            bool pad;
            System.Action done;
            int startFrame;

            public static bool Active => active;

            public static void Start(NuiView host, BindAction action, bool pad, System.Action done)
            {
                Stop();
                active = host.gameObject.AddComponent<KeyCapture>();
                active.action = action; active.pad = pad; active.done = done; active.startFrame = Time.frameCount;
            }

            public static void Stop() { if (active) Destroy(active); active = null; }

            /// <summary>Binds as if the key were pressed (tests).</summary>
            public static bool Feed(object keyOrButton)
            {
                if (!active) return false;
                active.Bind(keyOrButton);
                return true;
            }

            void Bind(object keyOrButton)
            {
#if ENABLE_INPUT_SYSTEM
                if (keyOrButton is Key k) KeyBindings.SetKey(action, k);
#endif
                if (keyOrButton is int b) KeyBindings.SetButton(action, b);
                var d = done;
                Stop();
                UiSounds.Click();
                d?.Invoke();
            }

            void Update()
            {
                if (Time.frameCount == startFrame) return;   // the click that started it
#if ENABLE_INPUT_SYSTEM
                if (pad)
                {
                    int b = KeyBindings.PadButtonPressedThisFrame();
                    if (b > 0) Bind(b);
                    return;
                }
                var key = KeyBindings.KeyPressedThisFrame();
                if (key == Key.None || key == Key.Escape) return;   // Esc steps back (OptionsBack)
                Bind(key);
#endif
            }
        }

        /// <summary>Rebinds an action while the controller page waits for it (automation / tests).</summary>
        public static bool FeedRebind(object keyOrButton) => KeyCapture.Feed(keyOrButton);
        public static bool RebindWaiting => KeyCapture.Active;

        // ---------------- combo boxes ----------------
        /// <summary>A game.nui combo showing items[selected]; a click drops the list under it (the client's combo list).</summary>
        static void Pick(NuiView v, string id, string[] items, int selected, System.Action<int> picked)
        {
            if (items == null || items.Length == 0) { Combo(v, id, "-", false); return; }
            selected = Mathf.Clamp(selected, 0, items.Length - 1);
            ShowValue(v, id, items[selected], picked != null);
            if (picked == null) return;
            Ui.On(id, view =>
            {
                var c = view ? view.Control(id) : null;
                if (!c) return;
                if (ComboList.IsOpenFor(c)) { ComboList.CloseOpen(); return; }
                ComboList.Show(c, items, selected, i =>
                {
                    ShowValue(view, id, items[i], true);
                    picked(i);
                });
            });
        }

        /// <summary>
        /// The combo's value. The detail page's small fields carry their title inside the field image, so there the value
        /// is right-aligned against the drop arrow instead of drawn over the title.
        /// </summary>
        static void ShowValue(NuiView v, string id, string text, bool enabled)
        {
            Combo(v, id, text, enabled);
            var c = v ? v.Control(id) : null;
            var field = c ? c.transform.Find("e1") as RectTransform : null;
            var arrow = c ? c.transform.Find("e2") as RectTransform : null;
            var value = field ? field.Find("value") as RectTransform : null;
            if (!value || !arrow || v.Dialog.Id != OptionDetailDialog) return;
            value.anchorMin = value.anchorMax = new Vector2(0, 1);
            value.pivot = new Vector2(0, 1);
            value.anchoredPosition = arrow.anchoredPosition - field.anchoredPosition + new Vector2(-62, 0);
            value.sizeDelta = new Vector2(58, arrow.sizeDelta.y);
            if (value.TryGetComponent<Text>(out var t)) t.alignment = TextAnchor.MiddleRight;
        }

        /// <summary>Picks a combo entry without the mouse (tests): the same as opening the list and clicking row i.</summary>
        public static bool PickOption(NuiView v, string id, int index)
        {
            if (!v || !v.Control(id)) return false;
            v.Click(id);
            return ComboList.Choose(index);
        }

        /// <summary>The drop-down list of a combo: rows in the field's font under the field, closed by a pick or a click elsewhere.</summary>
        sealed class ComboList : MonoBehaviour
        {
            static ComboList open;
            NuiControlView owner;
            System.Action<int> picked;
            int count;

            public static bool IsOpen => open;
            public static bool IsOpenFor(NuiControlView c) => open && open.owner == c;
            public static void CloseOpen() { if (open) Destroy(open.gameObject); open = null; }

            public static bool Choose(int i)
            {
                if (!open || i < 0 || i >= open.count) return false;
                var p = open.picked;
                CloseOpen();
                UiSounds.Click();
                p?.Invoke(i);
                return true;
            }

            public static void Show(NuiControlView c, string[] items, int selected, System.Action<int> picked)
            {
                CloseOpen();
                var field = (c.transform.Find("e1") as RectTransform) ?? (RectTransform)c.transform;
                var root = c.GetComponentInParent<Canvas>().rootCanvas.transform;
                // a full-screen catcher closes the list on a click outside it
                var catcher = NuiView.NewRect("combo list", root);
                NuiView.Stretch(catcher);
                catcher.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
                var cl = catcher.gameObject.AddComponent<ComboList>();
                catcher.gameObject.AddComponent<CatchClick>().Click = CloseOpen;
                cl.owner = c; cl.picked = picked; cl.count = items.Length;
                open = cl;

                const float rowH = 18f;
                var corners = new Vector3[4];
                field.GetWorldCorners(corners);   // 0 bottom-left, 1 top-left, 2 top-right
                var panel = NuiView.NewRect("rows", catcher);
                panel.pivot = new Vector2(0, 1);
                panel.position = corners[0];
                float scale = root.lossyScale.x > 0 ? root.lossyScale.x : 1f;
                float width = Mathf.Max(80f, (corners[2].x - corners[1].x) / scale + 22f);
                int shown = Mathf.Min(items.Length, 12);
                panel.sizeDelta = new Vector2(width, rowH * shown + 4);
                panel.gameObject.AddComponent<Image>().color = new Color(0.06f, 0.07f, 0.09f, 0.96f);
                panel.gameObject.AddComponent<Outline>().effectColor = new Color(0.9f, 0.62f, 0.1f, 0.8f);
                int first = Mathf.Clamp(selected - shown / 2, 0, Mathf.Max(0, items.Length - shown));
                var font = UiAssets.Font(c.Data.I("FN1") ?? "Calibri 12 400", out int size, out var style);
                for (int k = 0; k < shown; k++)
                {
                    int i = first + k;
                    var row = NuiView.NewRect("row " + i, panel);
                    row.anchoredPosition = new Vector2(2, -2 - k * rowH);
                    row.sizeDelta = new Vector2(width - 4, rowH);
                    var bg = row.gameObject.AddComponent<Image>();
                    bg.color = i == selected ? new Color(0.9f, 0.62f, 0.1f, 0.35f) : new Color(1, 1, 1, 0.001f);
                    var hover = row.gameObject.AddComponent<RowHover>();
                    hover.Bg = bg; hover.Normal = bg.color;
                    var t = NuiView.NewRect("text", row);
                    NuiView.Stretch(t);
                    t.offsetMin = new Vector2(6, 0);
                    var label = t.gameObject.AddComponent<Text>();
                    label.font = font; label.fontSize = size; label.fontStyle = style;
                    label.color = Color.white; label.alignment = TextAnchor.MiddleLeft; label.raycastTarget = false;
                    label.horizontalOverflow = HorizontalWrapMode.Overflow;
                    label.text = items[i];
                    row.gameObject.AddComponent<CatchClick>().Click = () => Choose(i);
                }
            }

            sealed class CatchClick : MonoBehaviour, IPointerClickHandler
            {
                public System.Action Click;
                public void OnPointerClick(PointerEventData e) => Click?.Invoke();
            }

            sealed class RowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
            {
                public Image Bg; public Color Normal;
                public void OnPointerEnter(PointerEventData e) { if (Bg) Bg.color = new Color(0.9f, 0.62f, 0.1f, 0.55f); }
                public void OnPointerExit(PointerEventData e) { if (Bg) Bg.color = Normal; }
            }
        }
    }
}
