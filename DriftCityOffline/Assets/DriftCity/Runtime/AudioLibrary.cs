using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace DriftCity
{
    /// <summary>Loads the client's loose audio (Data\audio\{bgm,engine,env,sfx}) at runtime.</summary>
    public static class AudioLibrary
    {
        static readonly Dictionary<string, AudioClip> cache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        static AudioSource sfxSource, musicSource;
        public static float MusicVolume = 0.6f, SfxVolume = 1f;
        /// <summary>goption.ini EngineVol / UISoundVol (Game/Options/GameOptions.cs sets them; OPTION AUDIO's engine and UI sliders).</summary>
        public static float EngineVolume = 0.8f, UiVolume = 1f;
        /// <summary>The options' "all sound" switch: off mutes everything through the listener.</summary>
        public static bool SoundOn = true;

        /// <summary>Reads the saved volume settings (called once at startup on the main thread).</summary>
        public static void LoadVolumes()
        {
            MusicVolume = Mathf.Clamp01(PlayerPrefs.GetFloat("dc_music_volume", 0.6f));
            SfxVolume = Mathf.Clamp01(PlayerPrefs.GetFloat("dc_sfx_volume", 1f));
            SoundOn = PlayerPrefs.GetInt("dc_sound_on", 1) == 1;
            ApplySoundOn();
        }
        static int musicRequest;

        /// <summary>Saves the volume settings.</summary>
        public static void SaveVolumes()
        {
            PlayerPrefs.SetFloat("dc_music_volume", MusicVolume);
            PlayerPrefs.SetFloat("dc_sfx_volume", SfxVolume);
            PlayerPrefs.SetInt("dc_sound_on", SoundOn ? 1 : 0);
            PlayerPrefs.Save();
        }

        public static void ApplySoundOn() => AudioListener.volume = SoundOn ? 1f : 0f;

        public static string AudioRoot => Path.Combine(DriftCityData.Files.Root, "Data", "audio");

        /// <summary>Synchronously loads a PCM WAV, e.g. "sfx/SID_CLICK_YES".</summary>
        public static bool Exists(string relative)
        {
            if (string.IsNullOrEmpty(relative) || DriftCityData.Files == null) return false;
            var path = Path.Combine(AudioRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? path : path + ".wav");
        }

        /// <summary>Forgets the decoded clips (after an audio device reset, which empties clips made with AudioClip.Create).</summary>
        public static void ClearCache() => cache.Clear();

        public static AudioClip Wav(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            if (cache.TryGetValue(relative, out var c)) return c;
            var path = Path.Combine(AudioRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) path += ".wav";
            c = File.Exists(path) ? ParseWav(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path)) : null;
            cache[relative] = c;
            return c;
        }

        static readonly int[] ImaStep =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
            157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552,
            1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
            12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
        };
        static readonly int[] ImaIndex = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        /// <summary>Microsoft IMA ADPCM (WAV format 17): per block a 4-byte header per channel, then 4-byte groups of
        /// eight 4-bit samples, interleaved per channel. Returns interleaved float samples.</summary>
        static float[] DecodeImaAdpcm(byte[] d, int start, int size, int channels, int blockAlign)
        {
            if (blockAlign <= 4 * channels) return new float[0];
            int samplesPerBlock = (blockAlign - 4 * channels) * 8 / (4 * channels) + 1;
            int blocks = (size + blockAlign - 1) / blockAlign;
            var outData = new List<float>(blocks * samplesPerBlock * channels);
            var pred = new int[channels]; var index = new int[channels];
            var block = new float[samplesPerBlock * channels];
            for (int b = 0; b < blocks; b++)
            {
                int o = start + b * blockAlign;
                int end = Math.Min(start + size, o + blockAlign);
                if (o + 4 * channels > end) break;
                for (int c = 0; c < channels; c++)
                {
                    pred[c] = BitConverter.ToInt16(d, o + c * 4);
                    index[c] = Mathf.Clamp(d[o + c * 4 + 2], 0, 88);
                    block[c] = pred[c] / 32768f;
                }
                int p = o + 4 * channels;
                int produced = 1;
                while (p + 4 * channels <= end)
                {
                    for (int c = 0; c < channels; c++)
                        for (int k = 0; k < 8; k++)
                        {
                            int nibble = (d[p + c * 4 + k / 2] >> ((k & 1) * 4)) & 0xF;
                            int step = ImaStep[index[c]];
                            int diff = ((2 * (nibble & 7) + 1) * step) >> 3;
                            pred[c] = Mathf.Clamp((nibble & 8) != 0 ? pred[c] - diff : pred[c] + diff, -32768, 32767);
                            index[c] = Mathf.Clamp(index[c] + ImaIndex[nibble], 0, 88);
                            int si = produced + k;
                            if (si < samplesPerBlock) block[si * channels + c] = pred[c] / 32768f;
                        }
                    produced += 8;
                    p += 4 * channels;
                }
                int count = Math.Min(produced, samplesPerBlock);
                for (int i = 0; i < count * channels; i++) outData.Add(block[i]);
            }
            return outData.ToArray();
        }

        static AudioClip ParseWav(byte[] d, string name)
        {
            if (d.Length < 44 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F') return null;
            int channels = 1, rate = 22050, bits = 16, format = 1, blockAlign = 0;
            int p = 12;
            while (p + 8 <= d.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(d, p, 4);
                int size = BitConverter.ToInt32(d, p + 4);
                int body = p + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(d, body);
                    channels = BitConverter.ToInt16(d, body + 2);
                    rate = BitConverter.ToInt32(d, body + 4);
                    blockAlign = BitConverter.ToInt16(d, body + 12);
                    bits = BitConverter.ToInt16(d, body + 14);
                }
                else if (id == "data")
                {
                    size = Math.Min(size, d.Length - body);
                    if (format == 17)
                    {
                        var pcm = DecodeImaAdpcm(d, body, size, channels, blockAlign);
                        var adpcm = AudioClip.Create(name, pcm.Length / channels, channels, rate, false);
                        adpcm.SetData(pcm, 0);
                        return adpcm;
                    }
                    if (format != 1) { Debug.LogWarning($"{name}: unsupported WAV format {format}"); return null; }
                    int bytesPer = bits / 8;
                    int samples = size / bytesPer;
                    var data = new float[samples];
                    for (int i = 0; i < samples; i++)
                    {
                        int o = body + i * bytesPer;
                        data[i] = bits == 8 ? (d[o] - 128) / 128f
                                : bits == 16 ? BitConverter.ToInt16(d, o) / 32768f
                                : bits == 24 ? ((d[o] | (d[o + 1] << 8) | ((sbyte)d[o + 2] << 16)) / 8388608f)
                                : BitConverter.ToInt32(d, o) / 2147483648f;
                    }
                    var clip = AudioClip.Create(name, samples / channels, channels, rate, false);
                    clip.SetData(data, 0);
                    return clip;
                }
                p = body + size + (size & 1);
            }
            return null;
        }

        /// <summary>Streams an MP3 from disk (e.g. "bgm/DriftCity_1stcity_01.mp3").</summary>
        public static IEnumerator Mp3(string relative, Action<AudioClip> done)
        {
            if (cache.TryGetValue(relative, out var cached)) { done(cached); yield break; }
            var path = Path.Combine(AudioRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { done(null); yield break; }
            using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = true;
                yield return req.SendWebRequest();
                AudioClip clip = req.result == UnityWebRequest.Result.Success ? DownloadHandlerAudioClip.GetContent(req) : null;
                if (clip) clip.name = Path.GetFileNameWithoutExtension(path);
                cache[relative] = clip;
                done(clip);
            }
        }

        static void EnsureSources()
        {
            if (sfxSource && musicSource) return;
            var go = GameObject.Find("Drift City Audio") ?? new GameObject("Drift City Audio");
            UnityEngine.Object.DontDestroyOnLoad(go);
            if (!sfxSource) { sfxSource = go.AddComponent<AudioSource>(); sfxSource.spatialBlend = 0; }
            if (!musicSource) { musicSource = go.AddComponent<AudioSource>(); musicSource.loop = true; musicSource.spatialBlend = 0; }
        }

        public static void PlaySfx(string relative, float volume = 1f)
        {
            if (!Application.isPlaying || DriftCityData.Files == null) return;
            var clip = Wav(relative);
            if (!clip && Substitute(relative) is Sub sub)
            {
                // a sound the US data lacks: the closest one it ships (DriftCity_RE/AUDIO.md 4)
                if (sub.TailSeconds > 0f) { PlaySfxTail(sub.File, sub.TailSeconds, volume); return; }
                clip = Wav(sub.File);
            }
            if (!clip) return;
            EnsureSources();
            sfxSource.PlayOneShot(clip, volume * SfxVolume);
        }

        /// <summary>A stand-in for a missing sound: the file to play instead and, when &gt; 0, the clip's last seconds only.</summary>
        public sealed class Sub
        {
            public string File;
            public float TailSeconds;
            public Sub(string file, float tail = 0f) { File = file; TailSeconds = tail; }
        }

        /// <summary>
        /// Sounds the exe names that the US data does not ship, and the closest shipped sound for each (AUDIO.md 4). The four
        /// the remake plays: SID_PATROL_PARTY_COUNT (the party patrol's last 10.5 s, 0x61d829) -> the last 10.5 s of
        /// SID_PVP_RACE_COUNT (the same countdown, one beep every 2 s; the full clip is the 20 s race-over count);
        /// SID_GUARD (a part conversion, 10732) -> SID_UPGRADE_SUCCESS (the parts remodelling success);
        /// SID_VALENTAIN_COMPLETE (a wrapped basket / harvest box revealed) -> SID_LUCKY (the reward jingle of a Mittron
        /// enhancer drop); SID_XMAS_EVENT_SUCCESS (a tree decoration given) -> SID_QUEST_SUCCESS (the event-style success
        /// jingle). The others belong to screens the remake does not drive (mission battles, the harvest slot machine).
        /// </summary>
        public static readonly Dictionary<string, Sub> Substitutes = new Dictionary<string, Sub>(StringComparer.OrdinalIgnoreCase)
        {
            ["SID_PATROL_PARTY_COUNT"] = new Sub("sfx/SID_PVP_RACE_COUNT", 10.5f),
            ["SID_GUARD"] = new Sub("sfx/SID_UPGRADE_SUCCESS"),
            ["SID_VALENTAIN_COMPLETE"] = new Sub("sfx/SID_LUCKY"),
            ["SID_XMAS_EVENT_SUCCESS"] = new Sub("sfx/SID_QUEST_SUCCESS"),
            ["SID_XMAS_EVENT_FAIL"] = new Sub("sfx/SID_QUEST_FAIL"),
            ["SID_VALENTAIN_SEND"] = new Sub("sfx/SID_MESSAGE"),
            ["SID_VALENTAIN_WRAPPING"] = new Sub("sfx/SID_ITEM_INSTALL"),
            ["SID_VALENTAIN_EATING"] = new Sub("sfx/SID_ITEM_DROP_GET2"),
            ["SID_VALENTAIN_EXP"] = new Sub("sfx/SID_PVP_RESULT_LEVELUP"),
            ["SID_GACHA_EFFECT"] = new Sub("sfx/SID_LUCKY"),
            ["SID_GOODHARVEST_BOX1"] = new Sub("sfx/SID_ITEM_DROP_CHECK"),
            ["SID_GOODHARVEST_BOX2"] = new Sub("sfx/SID_ITEM_DROP_CHECK"),
            ["SID_GOODHARVEST_BOX3"] = new Sub("sfx/SID_ITEM_DROP_CHECK"),
            ["SID_GOODHARVEST_SLOT"] = new Sub("sfx/SID_CAR_LIST"),
            ["SID_GOODHARVEST_WOW1"] = new Sub("sfx/SID_LUCKY"),
            ["SID_GOODHARVEST_WOW2"] = new Sub("sfx/SID_LUCKY"),
            ["SID_GOODHARVEST_WOW3"] = new Sub("sfx/SID_LUCKY"),
            ["SID_GOODHARVEST_WOW4"] = new Sub("sfx/SID_LUCKY"),
            ["SID_GOODHARVEST_WOW5"] = new Sub("sfx/SID_LUCKY"),
            ["SID_RING"] = new Sub("sfx/SID_CALLSIGN_RING"),
            ["SID_METEOR"] = new Sub("sfx/SID_HUV_EXPLOSION"),
            ["SID_ROCK"] = new Sub("sfx/SID_HIT_BUILDING"),
            ["SID_UFO"] = new Sub("sfx/SID_PULSE"),
            ["SID_UFOCRASH"] = new Sub("sfx/SID_HUV_EXPLOSION"),
        };

        /// <summary>The stand-in for a missing "sfx/SID_..." file (null when it exists or has none).</summary>
        public static Sub Substitute(string relative)
        {
            if (string.IsNullOrEmpty(relative) || Exists(relative)) return null;
            string name = Path.GetFileNameWithoutExtension(relative.Replace('\\', '/'));
            return Substitutes.TryGetValue(name, out var s) ? s : null;
        }

        static readonly List<AudioSource> tailSources = new List<AudioSource>();

        /// <summary>Plays the last <paramref name="seconds"/> of a clip (a countdown started late).</summary>
        public static void PlaySfxTail(string relative, float seconds, float volume = 1f)
        {
            if (!Application.isPlaying || DriftCityData.Files == null) return;
            var clip = Wav(relative);
            if (!clip) return;
            EnsureSources();
            AudioSource src = null;
            foreach (var s in tailSources) if (s && !s.isPlaying) { src = s; break; }
            if (!src) { src = sfxSource.gameObject.AddComponent<AudioSource>(); src.spatialBlend = 0; tailSources.Add(src); }
            src.clip = clip;
            src.volume = volume * SfxVolume;
            src.time = Mathf.Clamp(clip.length - seconds, 0f, Mathf.Max(0f, clip.length - 0.01f));
            src.Play();
        }

        /// <summary>The client's sound table (Data\audio\sfx\effect.txt, read by 0x5232b0): SOUND_ID -> file. The exe plays ids
        /// as 20000 + SOUND_ID (0x4f0c = 20236 = SID_PVP_VO_BOOST100).</summary>
        static Dictionary<int, string> soundIds;

        public static string SoundFile(int exeId)
        {
            if (soundIds == null)
            {
                soundIds = new Dictionary<int, string>();
                try
                {
                    var path = Path.Combine(AudioRoot, "sfx", "effect.txt");
                    if (DriftCityData.Files != null && File.Exists(path))
                        foreach (var line in File.ReadAllLines(path))
                        {
                            var c = line.Split('\t');
                            if (c.Length > 1 && int.TryParse(c[0].Trim(), out int id) && c[1].Trim().Length > 0)
                                soundIds[id] = "sfx/" + Path.GetFileNameWithoutExtension(c[1].Trim());
                        }
                }
                catch (Exception e) { Debug.LogWarning("effect.txt: " + e.Message); }
            }
            int n = exeId >= 20000 ? exeId - 20000 : exeId;
            return soundIds.TryGetValue(n, out var f) ? f : null;
        }

        /// <summary>Plays a sound by the exe's id (20000 + effect.txt SOUND_ID).</summary>
        public static void PlayId(int exeId, float volume = 1f) { var f = SoundFile(exeId); if (f != null) PlaySfx(f, volume); }

        /// <summary>Car audio's My MP3 channel (Game/CarAudio.cs): returns true to keep a game music request from replacing the player's own music.</summary>
        public static Func<string, bool> MusicIntercept;
        /// <summary>The game music last asked for (city, chase, race ...), also while My MP3 holds the music source.</summary>
        public static string LastGameMusic { get; private set; }
        /// <summary>Raised when a music clip starts (its file, relative to Dataudio).</summary>
        public static event Action<string> MusicStarted;

        public static void PlayMusic(MonoBehaviour host, string relative)
        {
            if (!Application.isPlaying || DriftCityData.Files == null) return;
            LastGameMusic = relative;
            if (MusicIntercept != null && MusicIntercept(relative)) return;
            PlayMusicNow(host, relative);
        }

        /// <summary>Plays a music file now, past the car audio's intercept (relative to Dataudio; "../../BGM/x.mp3" for the player's folder).</summary>
        public static void PlayMusicNow(MonoBehaviour host, string relative)
        {
            if (!Application.isPlaying || DriftCityData.Files == null || !host) return;
            EnsureSources();
            int request = ++musicRequest;   // a slower, older load must not replace newer music
            host.StartCoroutine(Mp3(relative, clip =>
            {
                if (!clip || !musicSource || request != musicRequest) return;
                musicSource.clip = clip;
                musicSource.volume = MusicVolume;
                musicSource.Play();
                MusicStarted?.Invoke(relative);
            }));
        }

        public static void StopMusic() { if (musicSource) musicSource.Stop(); }

        /// <summary>The clip the music source holds (null before any music loaded).</summary>
        public static AudioClip CurrentMusic => musicSource ? musicSource.clip : null;
        public static bool MusicPlaying => musicSource && musicSource.isPlaying;

        /// <summary>Starts the current music clip again (after the audio device was reset, e.g. a speaker mode change).</summary>
        public static void RestartMusic() { if (musicSource && musicSource.clip) musicSource.Play(); }

        public static void PauseMusic() { if (musicSource) musicSource.Pause(); }
        public static void ResumeMusic() { if (musicSource && musicSource.clip) { if (musicSource.time > 0f) musicSource.UnPause(); else musicSource.Play(); } }
        /// <summary>Seconds into the current music clip (0 without one).</summary>
        public static float MusicTime => musicSource && musicSource.clip ? musicSource.time : 0f;

        public static void ApplyMusicVolume() { if (musicSource) musicSource.volume = MusicVolume; }
    }
}

namespace DriftCity.UI
{
    public static class UiSounds
    {
        public static void Click() => AudioLibrary.PlaySfx("sfx/SID_CLICK_YES", AudioLibrary.UiVolume);
        public static void Cancel() => AudioLibrary.PlaySfx("sfx/SID_CLICK_NO", AudioLibrary.UiVolume);
    }
}
