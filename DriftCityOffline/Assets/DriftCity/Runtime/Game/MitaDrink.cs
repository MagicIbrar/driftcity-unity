using System;
using System.Collections.Generic;
using System.IO;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Mita 500 "Special Drink" (the "Mittron quests" of the code map; DriftCity_RE/MISSIONS.md section 7):
    /// <list type="bullet">
    /// <item>Spots: &lt;city&gt;\mitaquest.qpos ("qpos\0agt\0kjc\0ef2", u8 1, u32 count, count x float3; loader 0x6ed090) -
    /// 500 points per city. The server sent today's purchase count and the live spots {index, seconds left}
    /// (0x504fe0); the client lists a city's spots only while the drink value is under 201 and the driver's level suits
    /// the city: Moon Palace below 21, Koinonia below 31, Cras below 46, Oros and Taipei always (0x6eb6c0).</item>
    /// <item>Driving within 10 m of a live spot (while fewer than three bottles were bought today) buys one
    /// (0x701850 -> 0x78ad30); ID_MINIMAP_ICON_MITA marks the spots, game\mita_icon.nif stands on them.</item>
    /// <item>The reply (0x4f83c0) shows the salesman's line: Mita500_1 for the first bottle, Mita500_4 above level 70
    /// ("it'll never work on you"), Mita500_2 when the effect was 150, else Mita500_3 (Halloween variants with the event
    /// flag); the effect value 150/200/250 (= bonus % + 100) prints 11058 "[%d%% Special Drink effect] applied" and 10894
    /// "[Special Drink %d%% effect] disappeared." when it ends (0x6eba00).</item>
    /// </list>
    /// What the effect multiplies is not traced; the remake applies it to the EXP of the missions (chase, delivery,
    /// instant, Rush Time, Virtual HUV) for <see cref="BuffSeconds"/> (the car effect's 1800 s fade, 0x49c7d0), sells a
    /// bottle for 500 Mito (Mita500_1), at most three a day, and keeps <see cref="SpotCount"/> live spots that last
    /// <see cref="SpotSeconds"/> each (the server's Mita500RegenCnt / RegenSec are unknown). Server mirror: economy.js "mita".
    /// </summary>
    public static class MitaDrink
    {
        public const int Price = 500, PerDay = 3, SpotCount = 5;
        public const float BuffSeconds = 1800f, SpotSeconds = 300f, Radius = 10f;

        /// <summary>The effect value after the n-th bottle of the day (1-based): 150, 200, 250.</summary>
        public static int ValueFor(int bottle) => bottle <= 1 ? 150 : bottle == 2 ? 200 : 250;

        /// <summary>The running effect (100 = none).</summary>
        public static int Value
        {
            get
            {
                var r = MissionRecords.Current;
                if (r.MitaValue <= 100 || !DateTime.TryParse(r.MitaUntil, null, System.Globalization.DateTimeStyles.RoundtripKind, out var until) || DateTime.UtcNow >= until) return 100;
                return r.MitaValue;
            }
        }

        public static bool Active => Value > 100;

        /// <summary>Bottles bought today (UTC day).</summary>
        public static int BoughtToday
        {
            get
            {
                var r = MissionRecords.Current;
                return r.MitaDay == Today ? r.MitaCount : 0;
            }
        }

        static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

        /// <summary>EXP with the drink's effect (offline rewards; the server applies its own copy online).</summary>
        public static long Apply(long exp) => Active ? exp * Value / 100 : exp;

        /// <summary>0x6eb6c0: whether the city's spots are listed for this driver.</summary>
        public static bool CityAllowed(string city, int level) => city switch
        {
            "moonpalace" => level < 21,
            "koinonia" => level < 31,
            "cras" => level < 46,
            _ => true,
        };

        /// <summary>Spots are shown and sold while fewer than three bottles were bought and the effect is under 201.</summary>
        public static bool CanBuy => BoughtToday < PerDay && Value < 201;

        /// <summary>The spots of &lt;city&gt;\mitaquest.qpos in Unity space (cached).</summary>
        public static List<Vector3> Spots(string city)
        {
            if (string.IsNullOrEmpty(city)) return new List<Vector3>();
            if (cache.TryGetValue(city, out var list)) return list;
            list = new List<Vector3>();
            try
            {
                var fs = DriftCityData.Files;
                string path = $@"{city}\mitaquest.qpos";
                if (fs != null && fs.Exists(path)) list = ParseQpos(fs.Read(path));
            }
            catch (Exception ex) { Debug.LogWarning("mitaquest.qpos: " + ex.Message); }
            cache[city] = list;
            return list;
        }
        static readonly Dictionary<string, List<Vector3>> cache = new Dictionary<string, List<Vector3>>();

        /// <summary>"qpos\0agt\0kjc\0ef2" (16 bytes), u8 1, u32 count, count x (x, y, z) Gamebryo floats.</summary>
        public static List<Vector3> ParseQpos(byte[] d)
        {
            var list = new List<Vector3>();
            if (d == null || d.Length < 21) return list;
            int count = BitConverter.ToInt32(d, 17);
            for (int i = 0; i < count && 21 + i * 12 + 12 <= d.Length; i++)
            {
                int o = 21 + i * 12;
                list.Add(new Vector3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 8), BitConverter.ToSingle(d, o + 4)));
            }
            return list;
        }

        /// <summary>The salesman's line for a purchase (0x4f83c0).</summary>
        public static string LineKey(int level, int previousValue, bool firstToday)
        {
            string k = firstToday ? "Mita500_1" : level > 70 ? "Mita500_4" : previousValue == 150 ? "Mita500_2" : "Mita500_3";
            if (level > 70 && !firstToday) k = "Mita500_4";
            return SeasonEvents.Is(EventSeason.Halloween) ? k.Replace("Mita500_", "Mita500_Halloween_") : k;
        }

        /// <summary>Buys a bottle at spot <paramref name="index"/>: 500 Mito (the server's copy online), the line, the effect.</summary>
        public static void Buy(int index, Action<bool> done = null)
        {
            var p = PlayerProfile.Current;
            if (p == null || !CanBuy) { done?.Invoke(false); return; }
            int bought = BoughtToday, before = Value, level = p.Level;
            int value = level > 70 ? 100 : ValueFor(bought + 1);
            Progress.Reward("mita", new Dictionary<string, object> { ["index"] = index }, () =>
            {
                if (p.Mito < Price) return new Progress.Result { Ok = false, Error = "not enough Mito" };
                p.Mito -= Price;
                return new Progress.Result { Ok = true, Mito = -Price };
            }, r =>
            {
                if (!r.Ok) { UiPopup.Message(r.Error ?? "The salesman has nothing for you."); done?.Invoke(false); return; }
                var rec = MissionRecords.Current;
                if (rec.MitaDay != Today) { rec.MitaDay = Today; rec.MitaCount = 0; }
                rec.MitaCount++;
                if (value > 100)
                {
                    rec.MitaValue = value;
                    rec.MitaUntil = DateTime.UtcNow.AddSeconds(BuffSeconds).ToString("o");
                    ChatBox.System(MissionText.Get("11058", value - 100));
                }
                rec.Save();
                string line = GameTables.Text(LineKey(level, before, bought == 0));
                MissionUi.Script((line ?? "").Replace("<mita500img", "<pimg"), "Mita 500 Salesperson", false, null, null);
                AudioLibrary.PlaySfx("sfx/SID_ITEM_DROP_GET1");
                done?.Invoke(true);
            });
        }

        /// <summary>The effect ran out: 10894 once.</summary>
        public static void CheckExpiry()
        {
            var r = MissionRecords.Current;
            if (r.MitaValue > 100 && Value == 100)
            {
                // 11701 during the Halloween event (the KR text names the Halloween drink), else 10894
                ChatBox.System(MissionText.Get(SeasonEvents.Is(EventSeason.Halloween) ? "11701" : "10894", r.MitaValue - 100));
                r.MitaValue = 100;
                r.Save();
            }
        }
    }

    /// <summary>The live Mita 500 spots of the current city (minimap MITA icons, mita_icon.nif markers, buying on contact).</summary>
    public sealed class MitaSpots : MonoBehaviour
    {
        public sealed class Spot { public int Index; public Vector3 Position; public float Left; public MissionBeacon Marker; }

        public static MitaSpots Instance { get; private set; }
        public string City;
        public readonly List<Spot> Live = new List<Spot>();
        float check;
        bool buying;

        public static MitaSpots Attach(CitySession session, string city)
        {
            if (!session) return null;
            if (!session.TryGetComponent(out MitaSpots m)) m = session.gameObject.AddComponent<MitaSpots>();
            m.City = city;
            Instance = m;
            return m;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (var s in Live) if (s.Marker) Destroy(s.Marker.gameObject);
            Live.Clear();
        }

        bool Shown()
        {
            var p = PlayerProfile.Current;
            return p != null && MitaDrink.CanBuy && MitaDrink.CityAllowed(City, p.Level) && MitaDrink.Spots(City).Count > 0;
        }

        void Update()
        {
            check -= Time.deltaTime;
            if (check > 0) return;
            check = 0.2f;
            MitaDrink.CheckExpiry();
            bool shown = Shown();
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                Live[i].Left -= 0.2f;
                if (Live[i].Left <= 0 || !shown) Drop(i);
            }
            if (!shown) return;
            var all = MitaDrink.Spots(City);
            while (Live.Count < MitaDrink.SpotCount) Add(all);
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (!car || buying) return;
            for (int i = 0; i < Live.Count; i++)
                if (MissionPosts.Flat(car.transform.position, Live[i].Position) < MitaDrink.Radius && Mathf.Abs(car.transform.position.y - Live[i].Position.y) < 7f)
                {
                    var s = Live[i];
                    Drop(i);
                    buying = true;
                    MitaDrink.Buy(s.Index, _ => buying = false);
                    break;
                }
        }

        /// <summary>A random spot, preferring ones 200 m - 2 km from the car (the server picked them).</summary>
        void Add(List<Vector3> all)
        {
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            int pick = UnityEngine.Random.Range(0, all.Count);
            for (int tries = 0; tries < 40 && car; tries++)
            {
                int k = UnityEngine.Random.Range(0, all.Count);
                float d = MissionPosts.Flat(all[k], car.transform.position);
                if (d > 200f && d < 2000f && !Live.Exists(s => s.Index == k)) { pick = k; break; }
            }
            var spot = new Spot { Index = pick, Position = all[pick], Left = MitaDrink.SpotSeconds };
            spot.Marker = MissionBeacon.Create("Mita 500 Spot", @"game\mita_icon.nif", new Color(0.3f, 1f, 0.4f, 0.15f));
            spot.Marker.MoveTo(spot.Position);
            Live.Add(spot);
        }

        void Drop(int i)
        {
            if (Live[i].Marker) Destroy(Live[i].Marker.gameObject);
            Live.RemoveAt(i);
        }

        /// <summary>Minimap markers (ID_MINIMAP_ICON_MITA) for MissionMarkers.</summary>
        public IEnumerable<MissionMarkers.Marker> Markers()
        {
            foreach (var s in Live) yield return new MissionMarkers.Marker { Icon = "ID_MINIMAP_ICON_MITA", Position = s.Position, Label = "Mita 500" };
        }
    }
}
