using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The classic option pages of the basic main bar (MainUIType 0; DriftCity_RE/OPTIONS.md 8, SCREENS.md 5): OPTION
    /// 2980000 (setup 0x538fa0, handler 0x538a20; VIDEO / AUDIO / INPUT / GAME tabs over one window, the video and audio
    /// rows lying on the same spot and shown by tab) and VIDEOADV 2990000 (setup 0x540240: texture sliders, view distance,
    /// anti-aliasing, building / car shadows, animations, effects). They edit the same goption.ini keys as the remaster's
    /// OPTIONBASE pages; APPLY saves, CANCEL / CLOSE / Esc put back what was set when the window opened. The tabs show the
    /// game.nui groups of the window (graphic / audio / keys / game); INPUT's device list opens the controller page 3000000.
    /// Remake rules: the resolution / speaker lists drop down under their fields (the combo list of the other pages); the
    /// quality slider's four stops map onto Unity's quality levels, the 5th box is "custom".
    /// </summary>
    public static partial class OriginalMenus
    {
        public const int ClassicOptionDialog = 2980000, ClassicVideoAdvDialog = 2990000, ClassicGameDialog = 4280000;
        static readonly string[] ClassicTabs = { "VIDEO", "AUDIO", "INPUT", "GAME" };
        static NuiView classic, classicAdv, classicGame;
        static string classicAtOpen, classicAdvAtOpen;
        public static NuiView ClassicView => classic;
        public static NuiView ClassicVideoAdvView => classicAdv;
        public static int ClassicTabShown { get; private set; } = -1;
        static int classicLastTab = 1;

        /// <summary>The options of the main bar's button: the classic window with the basic UI (MainUIType 0), else OPTIONBASE.</summary>
        public static void OptionsForMainUi() => Options();

        public static void ClassicOptions(int tab = 1)
        {
            classicAtOpen = GameOptions.Snapshot();
            classic = OpenScreen(ClassicOptionDialog, ClassicBack);
            if (!classic) return;
            OnClose(() => { ComboList.CloseOpen(); KeyCapture.Stop(); classic = classicAdv = classicGame = null; optPad = null; ClassicTabShown = -1; GameOptions.Save(); });
            classicAdv = classicGame = null;
            foreach (var t in ClassicTabs) classic.SetVisible("ID_OPTION_TABKEY_" + t, false);
            Hide(classic, "ID_OPTION_LIST_RESOLUTION", "ID_OPTION_LIST_SPEAKER");
            for (int i = 0; i < ClassicTabs.Length; i++) { int n = i; Ui.On("ID_OPTION_TABBTN_" + ClassicTabs[i], _ => ClassicTab(n)); }
            Ui.On("ID_OPTION_APPLY", _ => ApplyOptions());
            Ui.On("ID_OPTION_CANCEL", _ => CancelClassic());
            Ui.On("ID_OPTION_CLOSE", _ => CancelClassic());
            ClassicTab(Mathf.Clamp(tab, 0, 3));
        }

        static void CancelClassic()
        {
            GameOptions.Restore(classicAtOpen);
            GameOptions.ApplyAudio(); GameOptions.ApplyGraphics();
            Close();
        }

        static bool ClassicBack()
        {
            if (KeyCapture.Active) { KeyCapture.Stop(); RefreshPad(); return true; }
            if (ComboList.IsOpen) { ComboList.CloseOpen(); return true; }
            if (optPad) { ClosePad(true); return true; }
            if (classicAdv) { CloseClassicAdv(true); return true; }
            if (classicGame) { CloseView(classicGame); classicGame = null; ClassicTab(1); return true; }
            GameOptions.Restore(classicAtOpen);
            return false;
        }

        public static void ClassicTab(int tab)
        {
            var v = classic;
            if (!v) return;
            ComboList.CloseOpen();
            if (classicGame) { CloseView(classicGame); classicGame = null; }
            ClassicTabShown = tab; classicLastTab = tab;
            for (int i = 0; i < ClassicTabs.Length; i++) Pressed(v.Control("ID_OPTION_TABBTN_" + ClassicTabs[i]), i == tab);
            // the four tabs' rows lie on the same spot: game.nui groups them (그래픽 / 오디오 / 키설정 / 게임설정)
            foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
            {
                int g = System.Array.IndexOf(ClassicGroups, c.Data.Group ?? "");
                if (g >= 0) c.gameObject.SetActive(g == tab);
            }
            Hide(v, "ID_OPTION_LIST_RESOLUTION", "ID_OPTION_LIST_SPEAKER", "ID_TAB_INPUT_LB_JOYSTICK");
            if (tab == 0) ClassicVideo(v);
            else if (tab == 1) ClassicAudio(v);
            else if (tab == 2) ClassicInput(v);
            else ClassicGame(v);
        }

        static readonly string[] ClassicGroups = { "그래픽", "오디오", "키설정", "게임설정" };

        // ---------------- INPUT: the device list; a pad opens the controller page 3000000 ----------------
        static void ClassicInput(NuiView v)
        {
            var devices = new List<string> { "Keyboard" };
#if ENABLE_INPUT_SYSTEM
            foreach (var g in UnityEngine.InputSystem.Gamepad.all) devices.Add(string.IsNullOrEmpty(g.displayName) ? "Gamepad" : g.displayName);
#endif
            if (devices.Count == 1) devices.Add("Gamepad");
            int cur = KeyBindings.PadUse ? Mathf.Clamp(GameOptions.GetInt("Controller", "Index") + 1, 1, devices.Count - 1) : 0;
            v.SetText("ID_TAB_INPUT_CURSEL_JOYSTICK", devices[cur]);
            Ui.On("ID_TAB_INPUT_BTN_SELJOYSTICK", view =>
            {
                var field = view ? view.Control("ID_TAB_INPUT_CURSEL_JOYSTICK") : null;
                if (!field) return;
                if (ComboList.IsOpenFor(field)) { ComboList.CloseOpen(); return; }
                ComboList.Show(field, devices.ToArray(), cur, i =>
                {
                    KeyBindings.PadUse = i > 0;
                    if (i > 0) GameOptions.Set("Controller", "Index", i - 1);
                    ClassicInput(view);
                    OpenPad();
                });
            });
        }

        // ---------------- GAME: the classic window's own rows ----------------
        static void ClassicGame(NuiView v)
        {
            Radio(v, "ID_TAB_GAME_CB_ORIGINMAP", "ID_TAB_GAME_CB_ORIGINCAR", GameOptions.RotateMinimap, on => { GameOptions.RotateMinimap = on; GameOptions.ApplyGame(); });
            Radio(v, "ID_TAB_GAME_CB_CHATMSG", "ID_TAB_GAME_CB_CHATMSG_OFF", GameOptions.ShowDialogBallon, on => GameOptions.ShowDialogBallon = on);
            Radio(v, "ID_TAB_GAME_CB_INSTANTMSG", "ID_TAB_GAME_CB_INSTANTMSG_OFF", GameOptions.ShowInstanceMSG, on => GameOptions.ShowInstanceMSG = on);
            Radio(v, "ID_TAB_GAME_CB_SHOWUSERINMAP", "ID_TAB_GAME_CB_SHOWUSERINMAP_OFF", GameOptions.ShowAnotherUserInMinimap, on => { GameOptions.ShowAnotherUserInMinimap = on; GameOptions.ApplyGame(); });
            Radio(v, "ID_TAB_GAME_CB_DENYPARTY", "ID_TAB_GAME_CB_DENYPARTY_OFF", GameOptions.DenyParty, SetDenyParty);
            // kept in goption.ini only (the remake's server has no Battle Zone invite refusal / position sharing switch)
            Radio(v, "ID_TAB_GAME_CB_DENYPVP", "ID_TAB_GAME_CB_DENYPVP_OFF", GameOptions.GetBool("Game", "DenyPvp"), on => GameOptions.Set("Game", "DenyPvp", on));
            Radio(v, "ID_TAB_GAME_CB_PVPPOSDISPLAY", "ID_TAB_GAME_CB_PVPPOSDISPLAY_OFF", GameOptions.GetBool("Game", "PvpPosDisplay", true), on => GameOptions.Set("Game", "PvpPosDisplay", on));
        }

        // ---------------- VIDEO ----------------
        /// <summary>The quality slider's stop for the current grade (0..3), or -1 for custom.</summary>
        public static int ClassicQualityStop()
        {
            int n = Mathf.Max(1, QualitySettings.names.Length);
            if (GameOptions.CustomVideoGrade || GameOptions.VideoGradeSet < 0) return -1;
            return Mathf.Clamp(Mathf.RoundToInt(GameOptions.VideoGradeSet * 3f / Mathf.Max(1, n - 1)), 0, 3);
        }

        public static void SetClassicQuality(int stop)
        {
            int n = Mathf.Max(1, QualitySettings.names.Length);
            int level = Mathf.Clamp(Mathf.RoundToInt(stop * (n - 1) / 3f), 0, n - 1);
            GameOptions.CustomVideoGrade = false;
            GameOptions.VideoGradeSet = level;
            QualitySettings.SetQualityLevel(level, true);
            GameOptions.ApplyGraphics();
        }

        static void ClassicVideo(NuiView v)
        {
            bool wide = IsWide(GameOptions.ScreenWidth, GameOptions.ScreenHeight);
            var res = Resolutions(wide).Concat(Resolutions(!wide)).Distinct().OrderBy(r => r.x * 10000 + r.y).ToList();
            int at = Mathf.Max(0, res.IndexOf(new Vector2Int(GameOptions.ScreenWidth, GameOptions.ScreenHeight)));
            v.SetText("ID_OPTION_STATIC_RESOLUTION", $"{GameOptions.ScreenWidth} x {GameOptions.ScreenHeight}");
            Ui.On("ID_OPTION_BTN_RESOLUTION", view =>
            {
                var field = view ? view.Control("ID_OPTION_STATIC_RESOLUTION") : null;
                if (!field) return;
                if (ComboList.IsOpenFor(field)) { ComboList.CloseOpen(); return; }
                ComboList.Show(field, res.Select(r => $"{r.x} x {r.y}").ToArray(), at, i => { SetResolution(res[i].x, res[i].y); ClassicVideo(view); });
            });
            int stop = ClassicQualityStop();
            Slider(v, "ID_OPTION_SLIDER_VIDEOQUALITY", stop < 0 ? 1f : stop / 3f, x =>
            {
                int s = Mathf.Clamp(Mathf.RoundToInt(x * 3f), 0, 3);
                if (s != ClassicQualityStop()) { SetClassicQuality(s); var c5 = v.Control("ID_OPTION_CHK_VIDEOQUALITY_5TH"); if (c5) Mark(c5, false); }
            });
            var custom = v.Control("ID_OPTION_CHK_VIDEOQUALITY_5TH");
            if (custom) Mark(custom, stop < 0);
            Ui.On("ID_OPTION_CHK_VIDEOQUALITY_5TH", _ => { Custom(); if (custom) Mark(custom, true); });
            Ui.On("ID_OPTION_BTN_CUSTOM", _ => OpenClassicAdv());
            Ui.On("ID_OPTION_BTN_VIDEOADV", _ => OpenClassicAdv());
        }

        // ---------------- AUDIO ----------------
        static void ClassicAudio(NuiView v)
        {
            // CHK_MUTE_OFF (labelled "On") = sound on, CHK_MUTE = muted (goption Audio.Enable)
            Radio(v, "ID_OPTION_CHK_MUTE_OFF", "ID_OPTION_CHK_MUTE", GameOptions.SoundEnable, on => { GameOptions.SoundEnable = on; GameOptions.ApplyAudio(); });
            Slider(v, "ID_OPTION_SLIDE_BGM", GameOptions.MusicVol, x => { GameOptions.MusicVol = x; GameOptions.ApplyAudio(); });
            Slider(v, "ID_OPTION_SLIDE_3D", GameOptions.FXSoundVol, x => { GameOptions.FXSoundVol = x; GameOptions.ApplyAudio(); });
            Slider(v, "ID_OPTION_SLIDE_SFX", GameOptions.UISoundVol, x => { GameOptions.UISoundVol = x; GameOptions.ApplyAudio(); });
            Slider(v, "ID_OPTION_SLIDE_ENGINE", GameOptions.EngineVol, x => { GameOptions.EngineVol = x; GameOptions.ApplyAudio(); });
            int sp = Mathf.Max(0, System.Array.IndexOf(GameOptions.SpeakerModes, GameOptions.SpeakerMode));
            v.SetText("ID_OPTION_STATIC_SPEAKER", GameOptions.SpeakerModes.Length > 0 ? GameOptions.SpeakerModes[sp] : "");
            Ui.On("ID_OPTION_BTN_SPEAKER", view =>
            {
                var field = view ? view.Control("ID_OPTION_STATIC_SPEAKER") : null;
                if (!field || GameOptions.SpeakerModes.Length == 0) return;
                if (ComboList.IsOpenFor(field)) { ComboList.CloseOpen(); return; }
                ComboList.Show(field, GameOptions.SpeakerModes, sp, i => { GameOptions.SpeakerMode = GameOptions.SpeakerModes[i]; GameOptions.ApplyAudio(); ClassicAudio(view); });
            });
        }

        // ---------------- VIDEOADV 2990000 ----------------
        public static void OpenClassicAdv()
        {
            if (classicAdv) return;
            classicAdvAtOpen = GameOptions.Snapshot();
            classicAdv = classic ? Open(ClassicVideoAdvDialog) : Ui.Open(ClassicVideoAdvDialog);
            if (!classicAdv) return;
            HideClassic(true);   // VIDEOADV's art is see-through: the window under it steps aside (its Esc guard keeps running)
            Hide(classicAdv, "ID_VIDEOADV_TABKEY_DETAIL", "ID_VIDEOADV_TABKEY_SCREEN");
            Ui.On("ID_VIDEOADV_APPLY", _ => CloseClassicAdv(false));
            Ui.On("ID_VIDEOADV_CANCEL", _ => CloseClassicAdv(true));
            Ui.On("ID_VIDEOADV_CLOSE", _ => CloseClassicAdv(true));
            Ui.On("ID_VIDEOADV_TABBTN_DETAIL", _ => ClassicAdvTab(0));
            Ui.On("ID_VIDEOADV_TABBTN_SCREEN", _ => ClassicAdvTab(1));
            FillClassicAdv(classicAdv);
            ClassicAdvTab(0);
        }

        /// <summary>VIDEOADV's two tabs: the game.nui groups 세밀한그래픽 (textures, view distance) and 화면 (the rest).</summary>
        public static void ClassicAdvTab(int tab)
        {
            var v = classicAdv;
            if (!v) return;
            Pressed(v.Control("ID_VIDEOADV_TABBTN_DETAIL"), tab == 0);
            Pressed(v.Control("ID_VIDEOADV_TABBTN_SCREEN"), tab == 1);
            foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
            {
                string g = c.Data.Group ?? "";
                if (g == "세밀한그래픽") c.gameObject.SetActive(tab == 0);
                else if (g == "화면") c.gameObject.SetActive(tab == 1);
            }
        }

        static void FillClassicAdv(NuiView v)
        {
            // texture sliders (car 3D / car / building / land): low = the original textures, high = the HD replacements
            foreach (var t in new[] { "CAR3D", "CAR", "BUILD", "LAND" })
                Slider(v, "ID_VIDEOADV_SLIDE_" + t, TextureLibrary.UseEnhanced ? 1f : 0f, x =>
                {
                    bool hd = x >= 0.5f;
                    if (hd != TextureLibrary.UseEnhanced) { TextureLibrary.UseEnhanced = hd; GameOptions.TextureQuality = 4; Custom(); FillClassicAdv(v); }
                });
            Slider(v, "ID_VIDEOADV_SLIDE_VIEWDISTANCE", (GameOptions.VisibleRange - 1) / 4f, x =>
            {
                GameOptions.VisibleRange = 1 + Mathf.RoundToInt(x * 4f);
                Custom();
                var t = Traffic;
                if (t) t.MaxCars = Mathf.RoundToInt(x * 40f);
            });
            int aa = GameOptions.MultiSampling;
            Slider(v, "ID_VIDEOADV_SLIDE_ANTIALIAS", (aa >= 8 ? 3 : aa >= 4 ? 2 : aa >= 2 ? 1 : 0) / 3f, x =>
            {
                int i = Mathf.Clamp(Mathf.RoundToInt(x * 3f), 0, 3);
                int want = i == 0 ? 0 : 1 << i;
                if (want != GameOptions.MultiSampling) { GameOptions.MultiSampling = want; Custom(); GameOptions.ApplyGraphics(); }
            });
            Radio(v, "ID_VIDEOADV_CB_BACKGROUND_BUILDING_ON", "ID_VIDEOADV_CB_BACKGROUND_BUILDING_OFF", GameOptions.ShowObjectShadow != 0, on => { GameOptions.ShowObjectShadow = on ? 1 : 0; Custom(); GameOptions.ApplyGraphics(); });
            // car shadow: none / flat / real (three boxes)
            int sh = GameOptions.VehicleShadow <= 0 ? 0 : GameOptions.VehicleShadow == 1 ? 1 : 2;
            string[] shIds = { "ID_VIDEOADV_CB_CARSHADOW_NONE", "ID_VIDEOADV_CB_CARSHADOW_FLAT", "ID_VIDEOADV_CB_CARSHADOW_REAL" };
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                Mark(v.Control(shIds[i]), i == sh);
                Ui.On(shIds[i], _ => { GameOptions.VehicleShadow = k == 2 ? 3 : k; Custom(); GameOptions.ApplyGraphics(); for (int j = 0; j < 3; j++) Mark(v.Control(shIds[j]), j == k); });
            }
            Radio(v, "ID_VIDEOADV_CHK_ANI_BACKGROUND_ON", "ID_VIDEOADV_CHK_ANI_BACKGROUND_OFF", GameOptions.AniVisibleRange > 0, on => { GameOptions.AniVisibleRange = on ? 1 : 0; Custom(); });
            Radio(v, "ID_VIDEOADV_CHK_ANI_PVPINTRO_ON", "ID_VIDEOADV_CHK_ANI_PVPINTRO_OFF", GameOptions.BattleAnimation, on => GameOptions.BattleAnimation = on);
            Radio(v, "ID_VIDEOADV_CHK_EFFECT_MINE_ON", "ID_VIDEOADV_CHK_EFFECT_MINE_OFF", GameOptions.ShowEffectMine, on => GameOptions.ShowEffectMine = on);
            Radio(v, "ID_VIDEOADV_CHK_EFFECT_YOUR_ON", "ID_VIDEOADV_CHK_EFFECT_YOUR_OFF", GameOptions.ShowEffectUsers, on => GameOptions.ShowEffectUsers = on);
        }

        static void CloseClassicAdv(bool revert)
        {
            if (revert && classicAdvAtOpen != null) { GameOptions.Restore(classicAdvAtOpen); GameOptions.ApplyGraphics(); }
            if (classicAdv) { if (views.Contains(classicAdv)) CloseView(classicAdv); else Ui.Close(classicAdv); }
            classicAdv = null;
            HideClassic(false);
            if (classic && ClassicTabShown == 0) ClassicVideo(classic);
        }

        static void HideClassic(bool hide)
        {
            if (!classic) return;
            var g = classic.TryGetComponent(out CanvasGroup haveCanvasGroup) ? haveCanvasGroup : classic.gameObject.AddComponent<CanvasGroup>();
            g.alpha = hide ? 0f : 1f;
            g.blocksRaycasts = !hide;
            g.interactable = !hide;
        }
    }
}
