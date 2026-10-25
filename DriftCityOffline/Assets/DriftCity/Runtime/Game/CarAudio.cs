using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The in-car music player, CARAUDIO 3040000 with its title strip 3050000 (DriftCity_RE/OPTIONS.md).
    /// Two channels: "Skid Rush" = DriftCity BGM mode, the game's own music (0x5b27f0: UseUserMP3 off, title text
    /// 10482 "DriftCity BGM Mode..."), and "My MP3" = the player's files in the install's BGM folder (0x5b21d0 /
    /// 0x52b5f0: UseUserMP3 on; empty folder: 10481 "Add music to the BGM folder."). Play, pause, stop, previous,
    /// next and shuffle (goption.ini RandomPlay); a finished track goes on to the next. The title slides in over
    /// 0.6 s (0x5b2320) when a track starts. The game's music list and titles are Data\audio\bgm\music.txt.
    /// </summary>
    public sealed class CarAudio : MonoBehaviour
    {
        public const int Dialog = 3040000, TitleDialog = 3050000;
        static CarAudio instance;
        NuiView view, strip;
        bool channelsOpen;

        public static CarAudio Instance => instance;
        public static bool Visible => instance && instance.view;
        public static NuiView View => instance ? instance.view : null;
        public static NuiView Strip => instance ? instance.strip : null;

        /// <summary>The playlist of the current channel (relative to Data\audio) and the track playing.</summary>
        public List<string> Playlist { get; private set; } = new List<string>();
        public int Track { get; private set; } = -1;
        public bool Paused { get; private set; }
        public bool Stopped { get; private set; }
        public string Title { get; private set; } = "";
        public static bool UserChannel => GameOptions.UseUserMP3;

        // ---------------- game music list (music.txt) ----------------
        public sealed class Song { public int No; public string File; public int TurnOver; public string Title; }
        static List<Song> songs;

        public static List<Song> Songs
        {
            get
            {
                if (songs != null) return songs;
                songs = new List<Song>();
                try
                {
                    var path = Path.Combine(AudioLibrary.AudioRoot, "bgm", "music.txt");
                    if (File.Exists(path))
                        foreach (var line in File.ReadAllLines(path).Skip(1))
                        {
                            var f = line.Split('\t');
                            if (f.Length < 4 || !int.TryParse(f[0].Trim(), out int no)) continue;
                            int.TryParse(f[2].Trim(), out int turn);
                            songs.Add(new Song { No = no, File = f[1].Trim(), TurnOver = turn, Title = f[3].Trim() });
                        }
                }
                catch (Exception ex) { Debug.LogWarning("music.txt: " + ex.Message); }
                return songs;
            }
        }

        /// <summary>The music.txt title of a game track ("bgm/DriftCity_1stcity_02.mp3" -> "Urban Runaway").</summary>
        public static string SongTitle(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return "";
            var file = Path.GetFileName(relative);
            var s = Songs.FirstOrDefault(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase));
            return s != null ? s.Title : Path.GetFileNameWithoutExtension(file);
        }

        /// <summary>The player's own MP3 folder: &lt;install&gt;\BGM (the client ships CopyMP3Here.txt in it).</summary>
        public static string UserFolder => DriftCityData.Files != null ? Path.Combine(DriftCityData.Files.Root, "BGM") : null;

        public static List<string> UserTracks()
        {
            var list = new List<string>();
            var dir = UserFolder;
            if (dir == null || !Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.mp3").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                list.Add("../../BGM/" + Path.GetFileName(f));
            return list;
        }

        /// <summary>
        /// The game channel's list: the city tracks of music.txt in order of their TurnOver chain from the music the game
        /// last asked for, then the rest of the soundtrack.
        /// </summary>
        static List<string> GameTracks()
        {
            var list = new List<string>();
            var start = AudioLibrary.LastGameMusic;
            var byFile = Songs.GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(start) && byFile.TryGetValue(Path.GetFileName(start), out var s))
                while (s != null && visited.Add(s.File))
                {
                    list.Add("bgm/" + s.File);
                    s = s.TurnOver > 0 ? Songs.FirstOrDefault(x => x.No == s.TurnOver) : null;
                }
            foreach (var x in Songs) if (visited.Add(x.File) && File.Exists(Path.Combine(AudioLibrary.AudioRoot, "bgm", x.File))) list.Add("bgm/" + x.File);
            return list;
        }

        // ---------------- showing ----------------
        static CarAudio Ensure()
        {
            if (instance) return instance;
            var go = new GameObject("Drift City Car Audio");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<CarAudio>();
            return instance;
        }

        /// <summary>Shows the player on the HUD (Hud.Show) and hooks the music.</summary>
        public static void Show()
        {
            GuideTextDriver.Ensure();   // the HUD's hints come up with the HUD
            var ca = Ensure();
            if (ca.view || !UiManager.Instance) return;
            ca.Open();
        }

        public static void Hide()
        {
            if (!instance) return;
            if (instance.view && UiManager.Instance) UiManager.Instance.Close(instance.view);
            if (instance.strip && UiManager.Instance) UiManager.Instance.Close(instance.strip);
            instance.view = instance.strip = null;
        }

        /// <summary>The main bar's CARAUDIO_OPEN button (basic bar 3020000) / a hotkey.</summary>
        public static void Toggle() { if (Visible) Hide(); else Show(); }

        void Open()
        {
            var ui = UiManager.Instance;
            view = ui.Open(Dialog);
            if (!view) return;
            ui.On("ID_CARAUDIO_PLAY", _ => Play());
            ui.On("ID_CARAUDIO_PAUSE", _ => Pause());
            ui.On("ID_CARAUDIO_STOP", _ => Stop());
            ui.On("ID_CARAUDIO_NEXT", _ => Next());
            ui.On("ID_CARAUDIO_PREV", _ => Prev());
            ui.On("ID_CARAUDIO_SHUFFLE", _ => { GameOptions.RandomPlay = !GameOptions.RandomPlay; GameOptions.Save(); Refresh(); });
            ui.On("ID_CARAUDIO_CLOSE", _ => Hide());
            ui.On("ID_CARAUDIO_AUDIOCHANNEL", _ => { channelsOpen = !channelsOpen; Refresh(); });
            ui.On("ID_CARAUDIO_AUDIOCHANNEL_NEW", _ => { channelsOpen = !channelsOpen; Refresh(); });
            ui.On("ID_CARAUDIO_CH_SKIDRUSH", _ => SetChannel(false));
            ui.On("ID_CARAUDIO_CH_MYMP3", _ => SetChannel(true));
            // tooltips show while their button is hovered, with sound on (0x5b1fd0)
            foreach (var b in new[] { "PREV", "NEXT", "PLAY", "SHUFFLE", "PAUSE", "STOP" })
            {
                var tip = view.Control("ID_CARAUDIO_TOOLTIP_" + b);
                var btn = view.Control("ID_CARAUDIO_" + b);
                if (!tip) continue;
                tip.gameObject.SetActive(false);
                if (btn) { var t = tip; btn.Hovered += on => { if (t) t.gameObject.SetActive(on && GameOptions.SoundEnable); }; }
            }
            AudioLibrary.MusicIntercept = Intercept;
            AudioLibrary.MusicStarted -= OnMusicStarted;
            AudioLibrary.MusicStarted += OnMusicStarted;
            if (UserChannel && (Playlist.Count == 0 || Track < 0)) SetChannel(true, false);
            else if (!UserChannel) SetTitle(GameTitle());
            Refresh();
        }

        string GameTitle()
        {
            var cur = AudioLibrary.LastGameMusic;
            return string.IsNullOrEmpty(cur) ? MissionText.Get("10482") : SongTitle(cur);
        }

        /// <summary>My MP3 keeps the music source: game requests are remembered (to come back to) instead of played.</summary>
        bool Intercept(string relative) => UserChannel && Playlist.Count > 0 && !Stopped;

        void OnMusicStarted(string relative)
        {
            playing = relative;
            if (!UserChannel)
            {
                // the game changed the music (city, chase, shop ...): the strip names it
                SetTitle(SongTitle(relative));
                if (Playlist.Count == 0 || UserTracksShown) Playlist = GameTracks();
                Track = Playlist.FindIndex(p => string.Equals(Path.GetFileName(p), Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase));
            }
            Paused = Stopped = false;
            lastTime = 0f;
            Refresh();
        }

        /// <summary>Switches channel: Skid Rush (the game's music, 0x5b27f0) or My MP3 (0x5b21d0); saved in goption.ini UseUserMP3.</summary>
        public void SetChannel(bool user, bool save = true)
        {
            GameOptions.UseUserMP3 = user;
            if (save) GameOptions.Save();
            channelsOpen = false;
            Paused = Stopped = false;
            if (user)
            {
                Playlist = UserTracks();
                if (Playlist.Count == 0)
                {
                    Track = -1;
                    SetTitle(MissionText.Get("10481"));
                    Refresh();
                    return;
                }
                Track = GameOptions.RandomPlay ? UnityEngine.Random.Range(0, Playlist.Count) : 0;
                PlayTrack();
            }
            else
            {
                SetTitle(MissionText.Get("10482"));
                Playlist = GameTracks();
                Track = Playlist.Count > 0 ? 0 : -1;
                var flow = GameFlow.Instance;
                var back = AudioLibrary.LastGameMusic;
                if (flow && !string.IsNullOrEmpty(back)) AudioLibrary.PlayMusicNow(flow, back);
                else if (Track >= 0) PlayTrack();
            }
            Refresh();
        }

        void PlayTrack()
        {
            if (Track < 0 || Track >= Playlist.Count) return;
            var host = (MonoBehaviour)GameFlow.Instance ?? this;
            Paused = Stopped = false;
            lastTime = 0f;
            var rel = Playlist[Track];
            SetTitle(UserChannel ? Path.GetFileNameWithoutExtension(rel) : SongTitle(rel));
            AudioLibrary.PlayMusicNow(host, rel);
            Refresh();
        }

        public void Play()
        {
            if (Paused) { AudioLibrary.ResumeMusic(); Paused = false; Refresh(); return; }
            if (Stopped || !AudioLibrary.MusicPlaying)
            {
                if (Playlist.Count == 0) Playlist = UserChannel ? UserTracks() : GameTracks();
                if (Track < 0 && Playlist.Count > 0) Track = 0;
                if (Track >= 0) PlayTrack();
                else if (UserChannel) SetTitle(MissionText.Get("10481"));
            }
            Refresh();
        }

        public void Pause()
        {
            if (Stopped) return;
            AudioLibrary.PauseMusic();
            Paused = true;
            Refresh();
        }

        public void Stop()
        {
            AudioLibrary.StopMusic();
            Stopped = true;
            Paused = false;
            Refresh();
        }

        public void Next() => Step(1);
        public void Prev() => Step(-1);

        void Step(int dir)
        {
            if (Playlist.Count == 0) Playlist = UserChannel ? UserTracks() : GameTracks();
            if (Playlist.Count == 0) return;
            if (GameOptions.RandomPlay && Playlist.Count > 1)
            {
                int pick;
                do pick = UnityEngine.Random.Range(0, Playlist.Count); while (pick == Track);
                Track = pick;
            }
            else Track = ((Track < 0 ? 0 : Track + dir) % Playlist.Count + Playlist.Count) % Playlist.Count;
            PlayTrack();
        }

        // ---------------- title strip ----------------
        float slide = -1f, stripTimer;

        /// <summary>Shows a title in the player (ID_CARAUDIO_TITLE) and slides the strip (3050000) in (0x5b2740 / 0x5b2320).</summary>
        public void SetTitle(string text)
        {
            Title = text ?? "";
            if (view) view.SetText("ID_CARAUDIO_TITLE", Title);
            if (!view || !UiManager.Instance) return;
            if (!strip) strip = UiManager.Instance.Open(TitleDialog);
            if (!strip) return;
            strip.SetText("ID_CARAUDIO_TITLE_TEXT", Title);
            strip.gameObject.SetActive(true);
            slide = 0f;
            stripTimer = 5f;
            Animate(0f);
        }

        void Animate(float t)
        {
            if (!strip) return;
            // 0x5b2320: 600 ms, x = base - sin(t * pi/2) travel; the strip comes in from the right
            var origin = strip.transform.Find("origin") as RectTransform;
            if (!origin) return;
            if (!stripBaseSet) { stripBase = origin.anchoredPosition; stripBaseSet = true; }
            float k = Mathf.Sin(Mathf.Clamp01(t / 0.6f) * Mathf.PI * 0.5f);
            origin.anchoredPosition = stripBase + new Vector2((1f - k) * 300f, 0f);
        }

        Vector2 stripBase;
        bool stripBaseSet;
        float lastTime;

        void Update()
        {
            if (slide >= 0f)
            {
                slide += Time.unscaledDeltaTime;
                Animate(slide);
                if (slide >= 0.6f) slide = -1f;
            }
            if (stripTimer > 0f)
            {
                stripTimer -= Time.unscaledDeltaTime;
                if (stripTimer <= 0f && strip) strip.gameObject.SetActive(false);
            }
            if (!view && (!strip || !strip.gameObject.activeSelf)) return;
            // the music source loops a clip: its clock jumping back means the track ended. My MP3 goes on to the next file;
            // the game's music follows music.txt's TurnOver column (a city track hands over to the next city track,
            // chase / shop / race music has none and keeps looping)
            float now = AudioLibrary.MusicTime;
            if (!Paused && !Stopped && now + 0.5f < lastTime)
            {
                if (UserChannel) { if (Track >= 0) Next(); }
                else
                {
                    var song = Songs.FirstOrDefault(x => string.Equals(x.File, Path.GetFileName(playing ?? ""), StringComparison.OrdinalIgnoreCase));
                    var next = song != null && song.TurnOver > 0 ? Songs.FirstOrDefault(x => x.No == song.TurnOver) : null;
                    if (next != null && File.Exists(Path.Combine(AudioLibrary.AudioRoot, "bgm", next.File)))
                    {
                        Track = Playlist.FindIndex(p => string.Equals(Path.GetFileName(p), next.File, StringComparison.OrdinalIgnoreCase));
                        AudioLibrary.PlayMusicNow((MonoBehaviour)GameFlow.Instance ?? this, "bgm/" + next.File);
                    }
                }
            }
            lastTime = now;
        }

        string playing;
        bool UserTracksShown => Playlist.Count > 0 && Playlist[0].StartsWith("../");

        void Refresh()
        {
            if (!view) return;
            view.SetText("ID_CARAUDIO_TITLE", Title);
            // only the action that makes sense shows: PLAY while paused / stopped, PAUSE while playing
            bool playing = !Paused && !Stopped && AudioLibrary.CurrentMusic;
            view.SetVisible("ID_CARAUDIO_PLAY", !playing);
            view.SetVisible("ID_CARAUDIO_PAUSE", playing);
            OriginalMenus.Pressed(view.Control("ID_CARAUDIO_SHUFFLE"), GameOptions.RandomPlay);
            foreach (var id in new[] { "ID_CARAUDIO_CH_BACKGROUND", "ID_CARAUDIO_CH_SKIDRUSH", "ID_CARAUDIO_CH_MYMP3" }) view.SetVisible(id, channelsOpen);
            var skid = view.Control("ID_CARAUDIO_CH_SKIDRUSH");
            var mine = view.Control("ID_CARAUDIO_CH_MYMP3");
            if (skid) skid.Selected = !UserChannel;
            if (mine) mine.Selected = UserChannel;
            view.SetVisible("ID_CARAUDIO_AUDIOCHANNEL_NEW", false);   // the "new" dot of the channel button
        }

        void OnDestroy()
        {
            if (instance != this) return;
            AudioLibrary.MusicStarted -= OnMusicStarted;
            if (AudioLibrary.MusicIntercept == (Func<string, bool>)Intercept) AudioLibrary.MusicIntercept = null;
            instance = null;
        }
    }
}
