using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's goption.ini (DriftCity_RE/OPTIONS.md). The exe keeps one option object (0x8d2850) that 0x4c9440
    /// fills from init\goption.ini, else init\def_option.ini, and 0x4ca130 writes back to init\goption.ini whenever the
    /// OPTION dialogs apply. Same key names, sections and defaults here; the file the remake writes lives in the
    /// player's data folder (Application.persistentDataPath\goption.ini) so the installed client is left untouched,
    /// and the first run copies the installed goption.ini (or def_option.ini).
    /// Values the remake can't render (DOF, environment maps, CPU detail levels ...) are kept so the file round-trips.
    /// </summary>
    public static class GameOptions
    {
        // ---------------- file ----------------
        sealed class Section { public string Name; public readonly List<KeyValuePair<string, string>> Keys = new List<KeyValuePair<string, string>>(); }
        static List<Section> sections;
        static string loadedFrom;

        /// <summary>Tests point this at a scratch file; null = persistentDataPath\goption.ini.</summary>
        public static string PathOverride;
        /// <summary>The player's goption.ini; tests that redirect the profile folder (PlayerProfile.SaveDirOverride) get their own.</summary>
        public static string FilePath => PathOverride ?? Path.Combine(PlayerProfile.SaveDirOverride ?? Application.persistentDataPath, "goption.ini");
        static string loadedKey;
        static string Key => PathOverride ?? PlayerProfile.SaveDirOverride ?? "";
        /// <summary>Where the values came from last load: the player's file, the installed goption.ini, def_option.ini or built-in defaults.</summary>
        public static string LoadedFrom => loadedFrom;

        /// <summary>Raised after Set/Apply changed a value (section, key).</summary>
        public static event Action<string, string> Changed;

        static void Ensure() { if (sections == null || !ReferenceEquals(loadedKey, Key) && loadedKey != Key) Load(); }

        /// <summary>Reads the player's goption.ini, else the installed client's goption.ini / def_option.ini (0x4c9440's order).</summary>
        public static void Load()
        {
            loadedKey = Key;
            sections = new List<Section>();
            loadedFrom = null;
            try
            {
                if (File.Exists(FilePath)) { Parse(File.ReadAllBytes(FilePath)); loadedFrom = FilePath; }
            }
            catch (Exception ex) { Debug.LogWarning("goption.ini: " + ex.Message); sections.Clear(); }
            if (loadedFrom == null && DriftCityData.Files != null)
                foreach (var f in new[] { @"init\goption.ini", @"init\def_option.ini" })
                {
                    try
                    {
                        if (!DriftCityData.Files.Exists(f)) continue;
                        Parse(DriftCityData.Files.Read(f));
                        loadedFrom = f;
                        firstRun = true;
                        break;
                    }
                    catch (Exception ex) { Debug.LogWarning(f + ": " + ex.Message); sections.Clear(); }
                }
            if (loadedFrom == null) { loadedFrom = "defaults"; firstRun = true; }
            FillDefaults();
            KeyBindings.Reload();
        }

        static bool firstRun;

        static void Parse(byte[] data)
        {
            string text = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? Encoding.Unicode.GetString(data, 2, data.Length - 2) : Encoding.UTF8.GetString(data);
            text = text.TrimStart((char)0xFEFF);
            Section s = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                if (line[0] == '[') { int end = line.IndexOf(']'); s = GetSection(end > 1 ? line.Substring(1, end - 1) : "", true); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0 || s == null) continue;
                // def_option.ini writes "0.3f" and "false;": strip the C leftovers
                var v = line.Substring(eq + 1).Trim().TrimEnd(';').Trim();
                if (v.Length > 1 && (v.EndsWith("f") || v.EndsWith("F")) && float.TryParse(v.Substring(0, v.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = v.Substring(0, v.Length - 1);
                Put(s, line.Substring(0, eq).Trim(), v);
            }
        }

        static Section GetSection(string name, bool create)
        {
            var s = sections.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (s == null && create) { s = new Section { Name = name }; sections.Add(s); }
            return s;
        }

        static void Put(Section s, string key, string value)
        {
            for (int i = 0; i < s.Keys.Count; i++)
                if (string.Equals(s.Keys[i].Key, key, StringComparison.OrdinalIgnoreCase)) { s.Keys[i] = new KeyValuePair<string, string>(s.Keys[i].Key, value); return; }
            s.Keys.Add(new KeyValuePair<string, string>(key, value));
        }

        /// <summary>The reader's defaults (0x4c9440's last argument per key) for keys the file lacks, in the writer's order (0x4ca130).</summary>
        static readonly (string sec, string key, string def)[] Defaults =
        {
            ("Graphic", "Fullscreen", "false"), ("Graphic", "MainUIType", "1"), ("Graphic", "ScreenWidth", "1024"), ("Graphic", "ScreenHeight", "768"),
            ("Graphic", "ColorDepth", "32"), ("Graphic", "DepthFormat", "16"), ("Graphic", "MultiSampling", "0"), ("Graphic", "ShadowQuality", "3"),
            ("Graphic", "CarDetail", "4"), ("Graphic", "EnvironmentDetail", "4"), ("Graphic", "EffectDetail", "4"), ("Graphic", "CarSpecular", "true"),
            ("Graphic", "FogEnable", "true"), ("Graphic", "MotionBlur", "false"), ("Graphic", "ShadowAffectCar", "true"), ("Graphic", "VehicleShadow", "3"),
            ("Graphic", "ShowObjectShadow", "1"), ("Graphic", "VisibleRange", "5"), ("Graphic", "CarTextureQuality", "4"), ("Graphic", "ObjectTextureQuality", "4"),
            ("Graphic", "TerrainTextureQuality", "4"), ("Graphic", "ObjectAnimation", "true"), ("Graphic", "BattleAnimation", "true"), ("Graphic", "ShowEffectMine", "true"),
            ("Graphic", "ShowEffectUsers", "true"), ("Graphic", "VideoGradeSet", "-1"), ("Graphic", "CustomVideoGrade", "false"), ("Graphic", "Hdr", "0"),
            ("Graphic", "ShadowRange", "1"), ("Graphic", "CarShader", "2"), ("Graphic", "ShadowMapType", "0"), ("Graphic", "EnvMapType", "1"), ("Graphic", "Occ", "0"),
            ("Graphic", "AniVisibleRange", "1"), ("Graphic", "DofType", "0"), ("Graphic", "FovType", "0"),
            ("Audio", "Enable", "true"), ("Audio", "MusicVol", "0.300"), ("Audio", "EngineVol", "0.800"), ("Audio", "FXSoundVol", "1.000"), ("Audio", "UISoundVol", "0.400"),
            ("Audio", "UseUserMP3", "false"), ("Audio", "RandomPlay", "false"),
            ("Game", "RotateMinimap", "false"), ("Game", "ShowDialogBallon", "true"), ("Game", "ShowInstanceMSG", "true"), ("Game", "ShowAnotherUserInMinimap", "true"),
            ("Game", "DenyParty", "false"), ("Game", "DenyInvite", "false"), ("Game", "PublicMyPos", "true"), ("Game", "DefaultPlayerCamera", "2"),
            ("Game", "FriendTeamNotify", "true"), ("Game", "StrainNotify", "true"), ("Game", "GhostBattleMode", "true"), ("Game", "ShowPassenger", "false"), ("Game", "Navigation", "true"),
            ("RoomMirror", "PosX", "500"), ("RoomMirror", "PosY", "100"),
            ("Controller", "Use", "false"), ("Controller", "Index", "0"), ("Controller", "Type", "0"),
            ("Controller", "ButtonAccel", "1"), ("Controller", "ButtonBrake", "2"), ("Controller", "ButtonBooster", "3"), ("Controller", "ButtonDrift", "4"),
            ("Controller", "ButtonEnter", "5"), ("Controller", "ButtonESC", "6"), ("Controller", "ButtonSpace", "7"), ("Controller", "ButtonTab", "8"),
            ("Controller", "ButtonItem1", "9"), ("Controller", "ButtonItem2", "10"),
        };

        static void FillDefaults()
        {
            foreach (var (sec, key, def) in Defaults)
            {
                var s = GetSection(sec, true);
                if (!s.Keys.Any(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase))) s.Keys.Add(new KeyValuePair<string, string>(key, def));
            }
            // the shipped goption.ini has the [Controller] buttons all 0: the reader's defaults (1..10) stand in for 0
            var c = GetSection("Controller", true);
            for (int i = 0; i < c.Keys.Count; i++)
                if (c.Keys[i].Key.StartsWith("Button", StringComparison.OrdinalIgnoreCase) && c.Keys[i].Value.Trim() == "0")
                {
                    var d = Defaults.First(x => x.sec == "Controller" && string.Equals(x.key, c.Keys[i].Key, StringComparison.OrdinalIgnoreCase));
                    c.Keys[i] = new KeyValuePair<string, string>(c.Keys[i].Key, d.def);
                }
            if (firstRun) AdoptRemakeSettings();
        }

        /// <summary>First run: the volume / look settings earlier remake builds kept in PlayerPrefs win over the shipped file.</summary>
        static void AdoptRemakeSettings()
        {
            firstRun = false;
            if (PlayerPrefs.HasKey("dc_music_volume")) SetRaw("Audio", "MusicVol", F(PlayerPrefs.GetFloat("dc_music_volume")));
            if (PlayerPrefs.HasKey("dc_sfx_volume")) SetRaw("Audio", "FXSoundVol", F(PlayerPrefs.GetFloat("dc_sfx_volume")));
            if (PlayerPrefs.HasKey("dc_sound_on")) SetRaw("Audio", "Enable", B(PlayerPrefs.GetInt("dc_sound_on") == 1));
            // the remake opens in a window at the player's own size unless they choose otherwise
            SetRaw("Graphic", "Fullscreen", B(Screen.fullScreen));
            if (Screen.width >= 640 && Screen.height >= 480) { SetRaw("Graphic", "ScreenWidth", Screen.width.ToString()); SetRaw("Graphic", "ScreenHeight", Screen.height.ToString()); }
        }

        const string Crlf = "\r\n";

        static string Serialize()
        {
            var sb = new StringBuilder();
            foreach (var s in sections)
            {
                sb.Append('[').Append(s.Name).Append(']').Append(Crlf);
                foreach (var kv in s.Keys) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(Crlf);
            }
            return sb.ToString();
        }

        /// <summary>Writes the file in the client's format ("Key=value", true/false, volumes with 3 decimals).</summary>
        public static void Save()
        {
            Ensure();
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, Serialize(), new UTF8Encoding(false));
                loadedFrom = FilePath;
            }
            catch (Exception ex) { Debug.LogWarning("goption.ini save: " + ex.Message); }
        }

        /// <summary>The current values as file text (the options window keeps one to put back on Close / Esc).</summary>
        public static string Snapshot() { Ensure(); return Serialize(); }

        /// <summary>Puts a snapshot back and applies it (not saved).</summary>
        public static void Restore(string snapshot)
        {
            if (snapshot == null) return;
            sections = new List<Section>();
            Parse(Encoding.UTF8.GetBytes(snapshot));
            FillDefaults();
            KeyBindings.Reload();
            Apply();
            Changed?.Invoke(null, null);
        }

        // ---------------- typed access ----------------
        public static string Get(string section, string key, string fallback = "")
        {
            Ensure();
            var s = GetSection(section, false);
            if (s == null) return fallback;
            foreach (var kv in s.Keys) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return fallback;
        }

        public static bool GetBool(string section, string key, bool fallback = false)
        {
            var v = Get(section, key, null);
            if (v == null) return fallback;
            v = v.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(v, out int n) ? n != 0 : fallback;
        }

        public static int GetInt(string section, string key, int fallback = 0)
        {
            var v = Get(section, key, null);
            if (v == null) return fallback;
            v = v.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0;
            return int.TryParse(v, out int n) ? n : fallback;
        }

        public static float GetFloat(string section, string key, float fallback = 0f)
        {
            var v = Get(section, key, null);
            return v != null && float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        static void SetRaw(string section, string key, string value) => Put(GetSection(section, true), key, value);

        public static void Set(string section, string key, string value)
        {
            Ensure();
            if (Get(section, key, null) == value) return;
            SetRaw(section, key, value);
            Changed?.Invoke(section, key);
        }

        public static void Set(string section, string key, bool value) => Set(section, key, B(value));
        public static void Set(string section, string key, int value) => Set(section, key, value.ToString(CultureInfo.InvariantCulture));
        public static void Set(string section, string key, float value) => Set(section, key, F(value));

        static string B(bool v) => v ? "true" : "false";
        static string F(float v) => Mathf.Clamp01(v).ToString("0.000", CultureInfo.InvariantCulture);

        // ---------------- the client's values ----------------
        public static bool Fullscreen { get => GetBool("Graphic", "Fullscreen"); set => Set("Graphic", "Fullscreen", value); }
        /// <summary>0 = the basic main bar (MAINBUTTON 3020000), 1 = the ADV bar (3010000) the remaster shows.</summary>
        public static int MainUIType { get => GetInt("Graphic", "MainUIType", 1); set => Set("Graphic", "MainUIType", value); }
        public static int ScreenWidth { get => GetInt("Graphic", "ScreenWidth", 1024); set => Set("Graphic", "ScreenWidth", value); }
        public static int ScreenHeight { get => GetInt("Graphic", "ScreenHeight", 768); set => Set("Graphic", "ScreenHeight", value); }
        /// <summary>0 / 2 / 4 / 8 samples.</summary>
        public static int MultiSampling { get => GetInt("Graphic", "MultiSampling"); set => Set("Graphic", "MultiSampling", value); }
        /// <summary>Car shadows: 0 none, 1 flat, 2-3 real (the reader falls back to DynamicShadow when out of 0..4).</summary>
        public static int VehicleShadow { get => GetInt("Graphic", "VehicleShadow", 3); set => Set("Graphic", "VehicleShadow", value); }
        /// <summary>Building / object shadows on (1) or off (0).</summary>
        public static int ShowObjectShadow { get => GetInt("Graphic", "ShowObjectShadow", 1); set => Set("Graphic", "ShowObjectShadow", value); }
        /// <summary>View distance 1..5 (the reader clamps to 5).</summary>
        public static int VisibleRange { get => Mathf.Clamp(GetInt("Graphic", "VisibleRange", 5), 1, 5); set => Set("Graphic", "VisibleRange", Mathf.Clamp(value, 1, 5)); }
        public static int AniVisibleRange { get => GetInt("Graphic", "AniVisibleRange", 1); set => Set("Graphic", "AniVisibleRange", value); }
        public static bool BattleAnimation { get => GetBool("Graphic", "BattleAnimation", true); set => Set("Graphic", "BattleAnimation", value); }
        public static bool ShowEffectMine { get => GetBool("Graphic", "ShowEffectMine", true); set => Set("Graphic", "ShowEffectMine", value); }
        public static bool ShowEffectUsers { get => GetBool("Graphic", "ShowEffectUsers", true); set => Set("Graphic", "ShowEffectUsers", value); }
        /// <summary>-1 custom, else the preset (the remake maps it to Unity's quality levels).</summary>
        public static int VideoGradeSet { get => GetInt("Graphic", "VideoGradeSet", -1); set => Set("Graphic", "VideoGradeSet", value); }
        public static bool CustomVideoGrade { get => GetBool("Graphic", "CustomVideoGrade"); set => Set("Graphic", "CustomVideoGrade", value); }
        public static int Hdr { get => GetInt("Graphic", "Hdr"); set => Set("Graphic", "Hdr", value); }
        public static int EnvMapType { get => GetInt("Graphic", "EnvMapType", 1); set => Set("Graphic", "EnvMapType", value); }
        public static int DofType { get => GetInt("Graphic", "DofType"); set => Set("Graphic", "DofType", value); }
        /// <summary>0 arcade, 1 real (the detail page's FOV switch).</summary>
        public static int FovType { get => GetInt("Graphic", "FovType"); set => Set("Graphic", "FovType", value); }
        public static int TextureQuality { get => GetInt("Graphic", "CarTextureQuality", 4); set { Set("Graphic", "CarTextureQuality", value); Set("Graphic", "ObjectTextureQuality", value); Set("Graphic", "TerrainTextureQuality", value); } }

        public static bool SoundEnable { get => GetBool("Audio", "Enable", true); set => Set("Audio", "Enable", value); }
        public static float MusicVol { get => GetFloat("Audio", "MusicVol", 0.3f); set => Set("Audio", "MusicVol", value); }
        public static float EngineVol { get => GetFloat("Audio", "EngineVol", 0.8f); set => Set("Audio", "EngineVol", value); }
        public static float FXSoundVol { get => GetFloat("Audio", "FXSoundVol", 1f); set => Set("Audio", "FXSoundVol", value); }
        public static float UISoundVol { get => GetFloat("Audio", "UISoundVol", 0.4f); set => Set("Audio", "UISoundVol", value); }
        /// <summary>The car audio's channel: true = My MP3 (the install's BGM folder), false = the game's music (0x5b21d0 / 0x5b27f0).</summary>
        public static bool UseUserMP3 { get => GetBool("Audio", "UseUserMP3"); set => Set("Audio", "UseUserMP3", value); }
        public static bool RandomPlay { get => GetBool("Audio", "RandomPlay"); set => Set("Audio", "RandomPlay", value); }
        /// <summary>Remake key: Unity's speaker mode (the client left it to the Miles driver).</summary>
        public static string SpeakerMode { get => Get("Audio", "SpeakerMode", "Stereo"); set => Set("Audio", "SpeakerMode", value); }

        public static bool RotateMinimap { get => GetBool("Game", "RotateMinimap"); set => Set("Game", "RotateMinimap", value); }
        public static bool ShowDialogBallon { get => GetBool("Game", "ShowDialogBallon", true); set => Set("Game", "ShowDialogBallon", value); }
        public static bool ShowInstanceMSG { get => GetBool("Game", "ShowInstanceMSG", true); set => Set("Game", "ShowInstanceMSG", value); }
        public static bool ShowAnotherUserInMinimap { get => GetBool("Game", "ShowAnotherUserInMinimap", true); set => Set("Game", "ShowAnotherUserInMinimap", value); }
        public static bool DenyParty { get => GetBool("Game", "DenyParty"); set => Set("Game", "DenyParty", value); }
        public static bool FriendTeamNotify { get => GetBool("Game", "FriendTeamNotify", true); set => Set("Game", "FriendTeamNotify", value); }
        public static bool Navigation { get => GetBool("Game", "Navigation", true); set => Set("Game", "Navigation", value); }
        /// <summary>1 = normalview1, 2 = normalview2 (the writer stores the camera in use).</summary>
        public static int DefaultPlayerCamera { get => GetInt("Game", "DefaultPlayerCamera", 2); set => Set("Game", "DefaultPlayerCamera", value); }

        // ---------------- applying ----------------
        /// <summary>Pushes every value the remake can render into Unity and the game's systems (audio, screen, quality, minimap).</summary>
        public static void Apply()
        {
            Ensure();
            ApplyAudio();
            ApplyScreen();
            ApplyGraphics();
            ApplyGame();
        }

        public static void ApplyAudio()
        {
            AudioLibrary.SoundOn = SoundEnable;
            AudioLibrary.MusicVolume = MusicVol;
            AudioLibrary.SfxVolume = FXSoundVol;
            AudioLibrary.EngineVolume = EngineVol;
            AudioLibrary.UiVolume = UISoundVol;
            AudioLibrary.ApplySoundOn();
            AudioLibrary.ApplyMusicVolume();
            AudioLibrary.SaveVolumes();   // the PlayerPrefs copy GameFlow reads at boot stays in step
            ApplySpeakerMode();
        }

        public static readonly string[] SpeakerModes = { "Stereo", "Mono", "Quad", "Surround", "5.1", "7.1" };
        static readonly AudioSpeakerMode[] SpeakerUnity = { AudioSpeakerMode.Stereo, AudioSpeakerMode.Mono, AudioSpeakerMode.Quad, AudioSpeakerMode.Surround, AudioSpeakerMode.Mode5point1, AudioSpeakerMode.Mode7point1 };

        static void ApplySpeakerMode()
        {
            int i = Array.IndexOf(SpeakerModes, SpeakerMode);
            if (i < 0 || Application.isBatchMode) return;   // no audio device in batch runs
            var cfg = AudioSettings.GetConfiguration();
            if (cfg.speakerMode == SpeakerUnity[i]) return;
            cfg.speakerMode = SpeakerUnity[i];
            // resetting the audio device stops what plays: the music is started again
            // a reset also empties the clips built from the game's WAVs: they are decoded again on next use
            if (AudioSettings.Reset(cfg)) { AudioLibrary.ClearCache(); AudioLibrary.RestartMusic(); }
        }

        public static void ApplyScreen()
        {
            if (Application.isEditor || Application.isBatchMode) return;   // the editor's game view owns its size
            int w = ScreenWidth, h = ScreenHeight;
            var mode = Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            if (w < 640 || h < 480) { w = Screen.width; h = Screen.height; }
            if (Screen.width != w || Screen.height != h || Screen.fullScreenMode != mode) Screen.SetResolution(w, h, mode);
        }

        public static void ApplyGraphics()
        {
            int grade = VideoGradeSet;
            if (grade >= 0 && grade < QualitySettings.names.Length && QualitySettings.GetQualityLevel() != grade) QualitySettings.SetQualityLevel(grade, true);
            int aa = MultiSampling;
            QualitySettings.antiAliasing = aa >= 8 ? 8 : aa >= 4 ? 4 : aa >= 2 ? 2 : 0;
            // buildings: the sun's shadow pass; cars: their own renderers (CarShadows below)
            QualitySettings.shadows = ShowObjectShadow != 0 ? ShadowQuality.All : (VehicleShadow >= 2 ? ShadowQuality.All : ShadowQuality.Disable);
            ApplyCarShadows();
            var cam = Camera.main;
            if (cam) cam.allowHDR = Hdr != 0;
        }

        /// <summary>VehicleShadow 2-3 lets the cars cast real shadows; 0-1 turns them off (the client's flat shadow isn't rebuilt).</summary>
        public static void ApplyCarShadows()
        {
            bool real = VehicleShadow >= 2;
            foreach (var car in UnityEngine.Object.FindObjectsByType<DriftCar>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                CarShadows(car.gameObject, real);
        }

        internal static void CarShadows(GameObject car, bool real)
        {
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
            {
                bool tagged = r.GetComponent<OptionShadowTag>();
                if (real && tagged)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    UnityEngine.Object.Destroy(r.GetComponent<OptionShadowTag>());
                }
                else if (!real && !tagged && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                {
                    r.gameObject.AddComponent<OptionShadowTag>();
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }
        }

        public static void ApplyGame()
        {
            foreach (var m in UnityEngine.Object.FindObjectsByType<Minimap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                m.SetRotate(RotateMinimap);
                m.ShowOtherUsers = ShowAnotherUserInMinimap;
            }
            OptionsHost.Ensure();
        }

        /// <summary>Loads goption.ini and applies it (startup; GameFlow's boot can call it right after AudioLibrary.LoadVolumes).</summary>
        public static void LoadAndApply()
        {
            Load();
            Apply();
            if (!File.Exists(FilePath)) Save();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            sections = null;
            if (DriftCityData.Files != null) LoadAndApply();
            else OptionsHost.Ensure();   // applies once the game data is mounted
        }

    }

    /// <summary>Marks a renderer whose shadow the options turned off (so turning them on restores only those).</summary>
    public sealed class OptionShadowTag : MonoBehaviour { }

    /// <summary>
    /// Keeps the options applied to things that appear later: a new minimap (each city load reads the installed
    /// goption.ini in Minimap.ReadOptions) or a new player car, and loads goption.ini once the game data is mounted.
    /// </summary>
    public sealed class OptionsHost : MonoBehaviour
    {
        static OptionsHost instance;
        Minimap lastMinimap;
        DriftCar lastCar;
        bool applied;
        float next;

        public static void Ensure()
        {
            if (instance || !Application.isPlaying) return;
            var go = new GameObject("Drift City Options");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<OptionsHost>();
        }

        void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            if (!applied && DriftCityData.Files != null) { applied = true; if (GameOptions.LoadedFrom == null || GameOptions.LoadedFrom == "defaults") GameOptions.LoadAndApply(); }
            var m = FindAnyObjectByType<Minimap>();
            if (m && m != lastMinimap)
            {
                lastMinimap = m;
                m.SetRotate(GameOptions.RotateMinimap);
                m.ShowOtherUsers = GameOptions.ShowAnotherUserInMinimap;
            }
            var flow = GameFlow.Instance;
            var car = flow ? flow.PlayerCar : null;
            if (car && car != lastCar)
            {
                lastCar = car;
                GameOptions.CarShadows(car.gameObject, GameOptions.VehicleShadow >= 2);
                var cam = Camera.main;
                if (cam) cam.allowHDR = GameOptions.Hdr != 0;
            }
        }
    }
}
