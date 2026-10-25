using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Undercity's data as the client reads it (DriftCity_RE/UNDERCITY.md): the posts of each city's icon table the
    /// exe names (entrance, party starts, Main HUB, barrier HUBs, red HUBs with their jump ramps), init\uchuvparam.xlt
    /// (the HUV waves per city and stage) and the "UC_*" alert texts of localize\globaltext.xlt.
    /// </summary>
    public static class UndercityData
    {
        // ---------------- posts (icon tables, World/Data/Icon_<city>_Client.tdf) ----------------

        /// <summary>One city's Undercity layout. City index 1 Moon Palace, 2 Koinonia, 3 Cras, 4 Oros (the exe's 0x3b4ec).</summary>
        public sealed class Layout
        {
            public string City;
            public int CityIndex;
            public CityMapInfo.Icon Entrance;
            /// <summary>Party slots 1-4 and the posts they face (0x715f60 -> 0x715e20: heading = atan2(direction - start)).</summary>
            public CityMapInfo.Icon[] Starts = new CityMapInfo.Icon[4], Directions = new CityMapInfo.Icon[4];
            /// <summary>The Main HUB (main communication centre) and the post it faces.</summary>
            public CityMapInfo.Icon Mcc, MccDirection;
            /// <summary>Barrier_2p_1..5, _3p_1..5, _4p_1..5 (15 consecutive posts, 0x715680's second loop).</summary>
            public CityMapInfo.Icon[] Barriers = new CityMapInfo.Icon[15];
            /// <summary>Red_HUB_1..10 and Jump_1..10 (0x715680's first loop, start ids at 0x87b0c8).</summary>
            public CityMapInfo.Icon[] RedHubs = new CityMapInfo.Icon[10], Jumps = new CityMapInfo.Icon[10];

            public bool Complete => Entrance != null && Starts.All(s => s != null) && Mcc != null && Barriers.Count(b => b != null) >= 5
                                    && RedHubs.Count(r => r != null) > 0;

            /// <summary>The barrier HUBs a party of <paramref name="players"/> fights: 5 per player beyond the first (2p, +3p, +4p sets).</summary>
            public List<CityMapInfo.Icon> BarriersFor(int players)
            {
                int sets = Mathf.Clamp(players - 1, 1, 3);
                return Barriers.Take(5 * sets).Where(b => b != null).ToList();
            }

            /// <summary>Unity heading (yaw, degrees) from a post towards another, as 0x715e20 computes it.</summary>
            public static Quaternion Facing(CityMapInfo.Icon from, CityMapInfo.Icon to)
            {
                if (from == null || to == null) return Quaternion.identity;
                var d = to.Position - from.Position; d.y = 0;
                return d.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(d.normalized, Vector3.up) : Quaternion.identity;
            }
        }

        static readonly string[] Prefix = { null, "post_1st_", "post_2nd_", "post_3rd_", "post_4rd_" };
        // entrance posts named at 0x879cc8..0x879e2c (one per city)
        static readonly int[] EntranceId = { 0, 784, 789, 791, 626 };
        // 0x715f60: Start1..4 then Direction1..4
        static readonly int[] StartId = { 0, 785, 793, 792, 627 };
        // 0x87b0c8 table: red HUB and jump-ramp start ids
        static readonly int[] RedHubId = { 0, 810, 818, 817, 652 }, JumpId = { 0, 820, 828, 827, 662 };
        // 0x87b088 table: first of the 15 barrier posts
        static readonly int[] BarrierId = { 0, 795, 803, 802, 637 };
        // Main HUB and its direction (strings post_1st_00793/794, post_2nd_00801/802, post_3rd_00800/801, post_4rd_00635/636)
        static readonly int[] MccId = { 0, 793, 801, 800, 635 };

        static readonly Dictionary<string, Layout> layouts = new Dictionary<string, Layout>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The city's layout, or null for a city without an Undercity (Taipei).</summary>
        public static Layout For(string city)
        {
            if (string.IsNullOrEmpty(city)) return null;
            if (layouts.TryGetValue(city, out var cached)) return cached;
            int ci = CityMapInfo.CityIndex(city);
            Layout l = null;
            if (ci >= 1 && ci <= 4)
            {
                var byId = new Dictionary<string, CityMapInfo.Icon>(StringComparer.OrdinalIgnoreCase);
                foreach (var i in CityMapInfo.Icons(city)) if (!byId.ContainsKey(i.Id)) byId[i.Id] = i;
                CityMapInfo.Icon Post(int n) => byId.TryGetValue($"{Prefix[ci]}{n:00000}", out var icon) ? icon : null;
                l = new Layout { City = city, CityIndex = ci, Entrance = Post(EntranceId[ci]), Mcc = Post(MccId[ci]), MccDirection = Post(MccId[ci] + 1) };
                for (int k = 0; k < 4; k++) { l.Starts[k] = Post(StartId[ci] + k); l.Directions[k] = Post(StartId[ci] + 4 + k); }
                for (int k = 0; k < 15; k++) l.Barriers[k] = Post(BarrierId[ci] + k);
                for (int k = 0; k < 10; k++) { l.RedHubs[k] = Post(RedHubId[ci] + k); l.Jumps[k] = Post(JumpId[ci] + k); }
                if (!l.Complete) l = null;
            }
            layouts[city] = l;
            return l;
        }

        // ---------------- HUV waves (init\uchuvparam.xlt, "Init/UCHuvParam" 0x4ac5fa) ----------------

        public sealed class HuvRow
        {
            public int CityId, Stage, HuvId, Level, Num;
            public override string ToString() => $"city {CityId} stage {Stage}: {Num} x {HuvId} lv {Level}";
        }

        static List<HuvRow> huvRows;

        /// <summary>
        /// Every row of the table. This install carries two copies: init\ (HUV ids 10001-10040 of this vehiclelist, levels
        /// 30-70) and an older data\init\ (ids such as 10104 that the vehicle list doesn't have); init\ is read first.
        /// </summary>
        public static List<HuvRow> HuvRows
        {
            get
            {
                if (huvRows != null) return huvRows;
                huvRows = new List<HuvRow>();
                var fs = DriftCityData.Files;
                foreach (var path in new[] { @"init\uchuvparam.xlt", @"data\init\uchuvparam.xlt" })
                {
                    if (fs == null || !fs.Exists(path)) continue;
                    try
                    {
                        var t = XltTable.Parse(fs.Read(path));
                        foreach (var r in t.Rows)
                        {
                            var row = new HuvRow { CityId = t.GetInt(r, "CityID"), Stage = t.GetInt(r, "Stage"), HuvId = t.GetInt(r, "HuvID"), Level = t.GetInt(r, "Level"), Num = t.GetInt(r, "Num") };
                            if (row.CityId > 0 && row.Stage > 0 && row.HuvId > 0 && row.Num > 0) huvRows.Add(row);
                        }
                    }
                    catch (Exception ex) { Debug.LogWarning($"{path}: {ex.Message}"); continue; }
                    if (huvRows.Count > 0) break;
                }
                return huvRows;
            }
        }

        /// <summary>The rows of one wave: stages 1-4 are the HUV groups (UC_HUV, UC_HUVAgain, 2, 3), stage 5 the boss.</summary>
        public static List<HuvRow> Wave(int cityId, int stage) => HuvRows.Where(r => r.CityId == cityId && r.Stage == stage).ToList();

        public const int BossStage = 5;

        /// <summary>The vehiclelist row of an HUV unique id (HUV or HUV BOSS).</summary>
        public static VehicleInfo Huv(int uniqueId) =>
            GameTables.Huvs.FirstOrDefault(h => h.UniqueId == uniqueId) ?? GameTables.BossHuvs.FirstOrDefault(h => h.UniqueId == uniqueId);

        // ---------------- alert texts ("UC_*" rows of globaltext.xlt) ----------------

        /// <summary>One UC message: &lt;ucimg&gt;/&lt;ucenter_img&gt; picture, &lt;ucname&gt;/&lt;name&gt;, the lines
        /// (&lt;ucmsg&gt;/&lt;text&gt;, pages split at &lt;next&gt;), &lt;ucsec n&gt; seconds on screen, &lt;btnAccept&gt;.</summary>
        public sealed class Message
        {
            public string Key, Image, Name;
            public List<List<string>> Pages = new List<List<string>>();
            public float Seconds;
            public bool Accept;
            public bool EntranceStyle;   // <ucenter_img>: the NPC dialog at the entrance (QUESTDIALOG)
            public string AllText => string.Join("\n", Pages.SelectMany(p => p));
        }

        static readonly Regex Tag = new Regex(@"<(\w+)\s*([^>]*)>", RegexOptions.Compiled);

        /// <summary>Parses the markup of a UC text (see <see cref="Message"/>); $name becomes the driver's name.</summary>
        public static Message Parse(string key, string markup, string driver = null)
        {
            var m = new Message { Key = key };
            var page = new List<string>();
            foreach (Match t in Tag.Matches(markup ?? ""))
            {
                string tag = t.Groups[1].Value.ToLowerInvariant(), arg = t.Groups[2].Value.Trim();
                switch (tag)
                {
                    case "ucimg": m.Image = arg; break;
                    case "ucenter_img": m.Image = arg; m.EntranceStyle = true; break;
                    case "ucname": case "name": m.Name = arg; break;
                    case "ucmsg": case "text": page.Add(arg.Replace("$name", driver ?? PlayerProfile.Current?.Name ?? "Driver")); break;
                    case "next": if (page.Count > 0) m.Pages.Add(page); page = new List<string>(); break;
                    case "ucsec": float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out m.Seconds); break;
                    case "btnaccept": m.Accept = true; break;
                }
            }
            if (page.Count > 0) m.Pages.Add(page);
            return m;
        }

        /// <summary>The UC text <paramref name="key"/> (US column), parsed.</summary>
        public static Message Text(string key) => Parse(key, GameTables.Text(key));

        // ---------------- rules this rebuild sets where the server decided (mirrored in DriftCityServer/src/economy.js) ----------------

        /// <summary>Roadside HUBs (type 0) placed along the lanes after the 10 red HUBs: 0x716f80 fills slots 10..99.</summary>
        public const int TowerSlots = 100, RedHubCount = 10;
        /// <summary>Mito the portal costs ("UC_NeedMito %d"; the amount came from the server).</summary>
        public const int EntryFee = 2000;
        /// <summary>The portal's fee by city number (10772 "%d Mito"; net+0x3b4ec: 1 moonpalace .. 4 oros): 600, 1200, 2100, 4200.</summary>
        public static int FeeFor(string city)
        {
            int ci = CityMapInfo.CityIndex(city);
            return ci >= 1 && ci <= 4 ? new[] { 600, 1200, 2100, 4200 }[ci - 1] : EntryFee;
        }
        /// <summary>Phase time limits (the jamming device; the client only counts down what the server sends).</summary>
        public const float HubPhaseSeconds = 300f, MainHubSeconds = 180f, HuvWaveSeconds = 240f, BossSeconds = 120f;
        /// <summary>Boss seconds added per red HUB taken from a jump (UC_TIP_JUMP: "Jump Attack will increase the BossHUV time!").</summary>
        public const float BossSecondsPerJump = 6f;
        /// <summary>Main HUB attempts: the first, UC_Fail, UC_Fail2 (0x7190c0 state 9, counter +0x28).</summary>
        public const int MainHubAttempts = 3;
        /// <summary>Seconds a UFO ("traffic tower") adds to the clock when rammed (UC_TimeSave).</summary>
        public const float UfoBonusSeconds = 10f;
        /// <summary>
        /// UC_HUVTimeReduce (0x4f2a20, the server's next-wave packet 0xc3 carries the new clock and its change; a negative
        /// change shows the alert after UC_HUVAgain*). (remake rule, server src/undercity.js) An HUV group that took longer
        /// than <see cref="WavePar"/> seconds costs half its overrun, at most <see cref="WaveCutMax"/> seconds.
        /// </summary>
        public const float WavePar = 60f, WaveCutMax = 20f;
        /// <summary>The seconds the next group's clock loses after a group that took <paramref name="waveSeconds"/>.</summary>
        public static float WaveTimeCut(float waveSeconds) => Mathf.Min(WaveCutMax, Mathf.Max(0f, Mathf.Floor((waveSeconds - WavePar) / 2f)));

        /// <summary>
        /// UCRESULT's badges (0x716480 -> 0x684820: PARTYn_120 / PARTYn_SET from the record's bytes +0x4e / +0x4f; 0xbcc +2 = 1
        /// brings 11114 "Undercity 120% item has been applied."). (remake rules, server src/undercity.js ucBadges) 120% = the
        /// driver's EXP drink was on for the reward; SET = the driver's car runs a full set in its four performance slots.
        /// </summary>
        public static (bool boost120, bool set) Badges(PlayerProfile p)
        {
            if (p == null) return (false, false);
            bool boost = ItemUse.HasBuff(p, "exp");
            var (set, _, _) = SetParts.CarSet(p, p.Car);
            return (boost, set != null);
        }

        /// <summary>The run's counts for one driver (UCRESULT columns HUB / Jump / HUV / BOSS).</summary>
        [Serializable]
        public struct Score
        {
            public int Hubs, Jumps, Barriers, MainHub, Huvs, Boss;
            /// <summary>Hits on HUVs (the server's 0xc0 HuvHit adds 1 to the member's counter).</summary>
            public int Hits;
            /// <summary>The member's PARTYUC counter (party member +0x44): HUBs, red HUBs and barriers destroyed (0xbbd /
            /// 0xbc7) plus HUV hits (0xc0); reset when the HUB phase starts (0x717400).</summary>
            public int PartyUc => Hubs + Jumps + Barriers + MainHub + Hits;
        }

        /// <summary>EXP and Mito of a run (offline and the server's 'undercity' claim use the same formula). The entry fee
        /// is taken out of the Mito here so the server stays the only one moving money online.</summary>
        public static (long exp, long mito) Reward(int level, Score s, bool cleared, int fee = EntryFee)
        {
            long exp = 15L * s.Hubs + 50L * s.Jumps + 40L * s.Barriers + 400L * s.MainHub + 120L * s.Huvs + 1500L * s.Boss
                       + (cleared ? 1000 + 100L * level : 0);
            long mito = 40L * s.Hubs + 150L * s.Jumps + 100L * s.Barriers + 1500L * s.MainHub + 400L * s.Huvs + 8000L * s.Boss
                        + (cleared ? 5000 + 300L * level : 0) - fee;
            return (exp, mito);
        }

        /// <summary>UCRESULT class S..D (0 = D .. 4 = S): cleared on the first Main HUB attempt within 12 minutes is S.</summary>
        public static int Class(bool cleared, int attempts, float seconds, bool mainHubDown)
        {
            if (!cleared) return mainHubDown ? 1 : 0;
            if (attempts <= 1 && seconds <= 720f) return 4;
            if (attempts <= 2 && seconds <= 960f) return 3;
            return 2;
        }
    }
}
