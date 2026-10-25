using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Engine sound from Data\audio\engine\idle&lt;n&gt;.wav / mid&lt;n&gt;.wav, picked through sfx\car.ini
    /// ([car&lt;vehicleId&gt;] low = n). Two loops are crossfaded and pitched from the car's speed.
    /// </summary>
    [RequireComponent(typeof(DriftCar))]
    public sealed class EngineAudio : MonoBehaviour
    {
        public int EngineNumber = 1;
        AudioSource idle, mid;
        DriftCar car;
        static Dictionary<string, int> engineByCar;

        public static int LookupEngine(int vehicleId)
        {
            if (engineByCar == null)
            {
                engineByCar = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var path = Path.Combine(AudioLibrary.AudioRoot, "sfx", "car.ini");
                if (File.Exists(path))
                {
                    string section = null;
                    foreach (var raw in Core.Cp949.Decode(File.ReadAllBytes(path)).Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.StartsWith("[") && line.EndsWith("]")) section = line.Substring(1, line.Length - 2);
                        else if (section != null && line.StartsWith("low", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split('=');
                            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int n)) engineByCar[section] = n;
                        }
                    }
                }
            }
            return engineByCar.TryGetValue("car" + vehicleId, out int e) ? e : 1;
        }

        void Start()
        {
            car = GetComponent<DriftCar>();
            idle = MakeSource(AudioLibrary.Wav($"engine/idle{EngineNumber}") ?? AudioLibrary.Wav("engine/idle1"));
            mid = MakeSource(AudioLibrary.Wav($"engine/mid{EngineNumber}") ?? AudioLibrary.Wav("engine/mid1"));
        }

        AudioSource MakeSource(AudioClip clip)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = true;
            s.spatialBlend = 0.6f;
            s.volume = 0;
            s.dopplerLevel = 0;
            if (clip) s.Play();
            return s;
        }

        void Update()
        {
            if (!car) return;
            // the car model's engine RPM: idle .. the 7000 rpm cut
            float rpm = Mathf.Clamp01((car.Rpm - 800f) / 6200f);
            float vol = AudioLibrary.EngineVolume;   // goption.ini EngineVol (OPTION AUDIO engine slider)
            idle.volume = Mathf.Lerp(idle.volume, (1f - rpm) * 0.8f * vol, Time.deltaTime * 8f);
            mid.volume = Mathf.Lerp(mid.volume, Mathf.Clamp01(rpm * 1.2f) * 0.9f * vol, Time.deltaTime * 8f);
            idle.pitch = 0.9f + rpm * 0.4f;
            mid.pitch = 0.75f + rpm * 0.8f;
        }
    }

    public static class CityMusic
    {
        // city archive -> soundtrack chapter (from the object name prefixes: moonpalace = 1st, cras = 3rd, ...)
        static readonly Dictionary<string, string> Chapter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "moonpalace", "1st" }, { "koinonia", "2nd" }, { "cras", "3rd" }, { "oros", "4th" }, { "taipei", "5th" },
        };

        public static string For(string city)
        {
            string chapter = Chapter.TryGetValue(city ?? "", out var c) ? c : "1st";
            int tracks = chapter == "4th" ? 2 : chapter == "5th" ? 1 : 3;
            return $"bgm/DriftCity_{chapter}city_0{UnityEngine.Random.Range(1, tracks + 1)}.mp3";
        }
    }
}
