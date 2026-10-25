using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>A driver in a Battle Zone room: a player (local or remote) or an AI driver filling a slot.</summary>
    public sealed class PvpMember
    {
        public int Id;
        public string Name;
        public int Level = 1;
        public bool Ready;
        public bool Ai;
        public bool Me;
        public int Slot;
        /// <summary>AI drivers: the car they bring and how hard they drive (AiDriver.Skill).</summary>
        public VehicleInfo Car;
        public float Skill = 0.9f;
        public int Team => Slot & 1;
        /// <summary>Simulated best lap of an AI rival (lap-time battle, offline).</summary>
        public float Lap;
    }

    /// <summary>
    /// A Battle Zone room (DriftCity_RE/PVP.md sections 3-5): the client's room settings from CREATEROOM 1020000 and the
    /// wait room's 16-slot array. Offline rooms live here and are filled with AI drivers; online rooms mirror the server's
    /// race room (race.js) and are rebuilt from every race.room message.
    /// </summary>
    public sealed class PvpRoom
    {
        public const int SlotCount = 16;
        public int Id;
        public string Title = "";
        public bool Team;
        /// <summary>Practice room (SELECTPRACTICE): one player; in the remake it is a local room where AI drivers may join.</summary>
        public bool Practice;
        public int Max = 16;
        public string Password = "";
        public bool Locked;           // online rooms: the server only says whether there is a password
        public bool Spectate = true;
        public bool NoGate;
        /// <summary>Channel Battle room (crew vs crew, SOCIAL.md section 5): side A (even slots) and side B crews.</summary>
        public bool Crew;
        public string CrewA = "", CrewB = "";
        /// <summary>A Channel Battle round: a server crew room, or the offline stand-in room ChannelBattle.Enter opens (its
        /// title ends in "Channel Battle"; <see cref="ChannelBattleRound"/> can be set instead). Its race is kept as a replay.</summary>
        public bool ChannelBattleRound;
        public bool IsChannelBattle => Crew || ChannelBattleRound || (Title ?? "").EndsWith("Channel Battle");
        /// <summary>Wager grade (room flags 0x7000 >> 12): 1 S .. 5 D = 10000 / 5000 / 1000 / 500 / 100 Mito, 0 none.</summary>
        public int Wager;
        /// <summary>Mission-battle room (flag 0x10000): 0 none, else the GM mode it was made in (PvpMissionBattle.Kind).</summary>
        public int Mission;
        /// <summary>Spectators (WAIT16 OBSERVE_CNT 10244) and whether I am one of them (room-info +0x78).</summary>
        public int Observers;
        public List<string> ObserverNames = new List<string>();
        public bool Spectating;
        public int Arena;
        public int MapFlags = Pvp.MapCityAll | Pvp.TrafficRandom;
        public RaceTrack Track;
        public string TrackKey;       // online: the server's track key (city/track)
        public string TrackName;      // online: the server's track name
        public int Laps;
        public int HostId;
        /// <summary>"lobby" (waiting), "countdown", "racing", "done" as the server names them.</summary>
        public string State = "lobby";
        public bool Online;
        /// <summary>The client's slot array: 0 open, -1 closed, else the member id (0x57d9f0).</summary>
        public readonly int[] Slots = new int[SlotCount];
        public readonly List<PvpMember> Members = new List<PvpMember>();

        public PvpMember Me => Members.FirstOrDefault(m => m.Me);
        public bool IAmHost => Me != null && Me.Id == HostId;
        public PvpMember Host => Members.FirstOrDefault(m => m.Id == HostId);
        public PvpMember At(int slot) => slot >= 0 && slot < SlotCount && Slots[slot] > 0 ? Members.FirstOrDefault(m => m.Id == Slots[slot]) : null;
        public bool Waiting => State == "lobby";
        /// <summary>Real Match room (flag 0x100: created in the RealMatch arena).</summary>
        public bool RealMatch => Arena == Pvp.ArenaRealMatch;
        public int Players => Members.Count;

        /// <summary>
        /// The client's canStart (0x57b060): more than one driver and everyone ready (the host counts as ready). The client
        /// counts real players only (id &lt; 0x10000); offline the AI drivers stand in for them.
        /// </summary>
        public bool CanStart => Members.Count > 1 && Members.All(m => m.Ready || m.Id == HostId);

        public void ResetSlots()
        {
            for (int i = 0; i < SlotCount; i++) Slots[i] = i < Max ? 0 : -1;
        }

        /// <summary>First open slot; in team mode on the given team (0 = even slots, 1 = odd), or on the smaller side.</summary>
        public int OpenSlot(int team = -1)
        {
            if (Team && team < 0)
            {
                int a = Members.Count(m => m.Team == 0);
                team = a <= Members.Count - a ? 0 : 1;
                int s = Free(team);
                return s >= 0 ? s : Free(-1);
            }
            return Free(Team ? team : -1);
        }

        int Free(int parity)
        {
            for (int i = 0; i < Max && i < SlotCount; i++)
                if (Slots[i] == 0 && (parity < 0 || (i & 1) == parity)) return i;
            return -1;
        }

        public PvpMember Add(PvpMember m, int slot = -1)
        {
            if (slot < 0) slot = OpenSlot();
            if (slot < 0) return null;
            m.Slot = slot;
            Slots[slot] = m.Id;
            Members.Add(m);
            return m;
        }

        public void Remove(PvpMember m)
        {
            if (m == null) return;
            if (m.Slot >= 0 && m.Slot < SlotCount && Slots[m.Slot] == m.Id) Slots[m.Slot] = 0;
            Members.Remove(m);
            if (m.Id == HostId && Members.Count > 0) HostId = (Members.FirstOrDefault(x => !x.Ai) ?? Members[0]).Id;
        }

        public void Move(PvpMember m, int slot)
        {
            if (m == null || slot < 0 || slot >= Max || Slots[slot] != 0) return;
            Slots[m.Slot] = 0;
            Slots[slot] = m.Id;
            m.Slot = slot;
        }

        /// <summary>The room-list flags word (0x57a3a0): 1 team, 4 password, 0x80 spectating, 0x100 Real Match, 0x400 no
        /// gate, 0x7000 wager grade.</summary>
        public int Flags => (Team ? 1 : 0) | (HasPassword ? 4 : 0) | (Spectate ? 0x80 : 0) | (RealMatch ? 0x100 : 0) | (NoGate ? 0x400 : 0) | ((Wager & 7) << 12);
        public bool HasPassword => Locked || !string.IsNullOrEmpty(Password);

        public string MapName => Track != null ? Pvp.TrackName(Track) : !string.IsNullOrEmpty(TrackName) ? Pvp.Text(TrackName) : "";
    }

    /// <summary>Battle Zone rules and tables shared by the lobby, the wait room, the race and the results.</summary>
    public static class Pvp
    {
        // ---- the wait room's map-settings word (0x592540) ----
        public const int MapRandom = 0x1, MapReverse = 0x2, TrafficIdle = 0x4, TrafficNormal = 0x8, TrafficJam = 0x10,
                         MapCityAll = 0x20, MapCity1 = 0x40, MapRecommend = 0x800;
        public const int TrafficRandom = 0;   // none of the traffic bits

        /// <summary>Arena tabs 0..6 (0x57e750): EXP multipliers 2X/3X/4X/6X/8X, Free, Real Match.</summary>
        public static readonly int[] ArenaExp = { 2, 3, 4, 6, 8, 1, 1 };
        public static readonly string[] ArenaTabs =
        {
            "ID_PVP_RMLOBBY_CHN_EXP_2X", "ID_PVP_RMLOBBY_CHN_EXP_3X", "ID_PVP_RMLOBBY_CHN_EXP_4X", "ID_PVP_RMLOBBY_CHN_EXP_6X",
            "ID_PVP_RMLOBBY_CHN_EXP_8X", "ID_PVP_RMLOBBY_CHN_EXP_FREE", "ID_PVP_RMLOBBY_CHN_EXP_RM",
        };
        /// <summary>Arena names: 11090..11094 "Amateur/Rookie/Pro/Star/Superstar Arena", 10307 "Free Arena", 10823 "RealMatch Arena".</summary>
        public static readonly string[] ArenaText = { "11090", "11091", "11092", "11093", "11094", "10307", "10823" };
        public const int ArenaFree = 5, ArenaRealMatch = 6;

        /// <summary>The remake's server feature word (the client's net +0x270, names from 0x453c40): RealMatch (RMLOBBY), all
        /// arenas, PracticeMode, BattingJackpot (wager rooms), LapTime, AllGateReport; Real Match time comes from the
        /// timetable (<see cref="RealMatchOpen"/>).</summary>
        public const bool WagerEnabled = true, LapTimeBattle = true;

        // ---- wager rooms (0x507330, 11066 / 11067 / 11077) ----
        public static readonly int[] WagerFees = { 0, 10000, 5000, 1000, 500, 100 };
        public static readonly string[] WagerLetters = { "", "S", "A", "B", "C", "D" };
        public static int WagerFee(int grade) => grade >= 1 && grade <= 5 ? WagerFees[grade] : 0;
        /// <summary>A driver needs ten times the entry fee to take part (11066 / 11067).</summary>
        public static int WagerNeed(int grade) => WagerFee(grade) * 10;

        // ---- Real Match time (0x1c8): a 50-minute session every 3 hours on the UTC clock (remake timetable, shared with
        // the server's race.rm; online the server's word wins) ----
        public const int RealMatchMinutes = 50, RealMatchEveryHours = 3;
        static float serverRmAt = -1f; static bool serverRmOpen; static double serverRmLeft;

        /// <summary>Real Match state: open, and seconds left (open) or until the next session (closed).</summary>
        public static (bool open, double seconds) RealMatchState()
        {
            if (serverRmAt >= 0f && Net.OnlineSession.Active)
            {
                double left = Math.Max(0, serverRmLeft - (UnityEngine.Time.realtimeSinceStartup - serverRmAt));
                return (serverRmOpen && left > 0, left);
            }
            var now = DateTime.UtcNow;
            double into = now.Minute * 60 + now.Second;
            if (now.Hour % RealMatchEveryHours == 0 && into < RealMatchMinutes * 60) return (true, RealMatchMinutes * 60 - into);
            return (false, (RealMatchEveryHours - now.Hour % RealMatchEveryHours) * 3600 - into);
        }

        public static bool RealMatchOpen => ForceRealMatch || RealMatchState().open;
        /// <summary>Tests: Real Match time regardless of the clock.</summary>
        public static bool ForceRealMatch;

        /// <summary>race.rm from the server: { open, endsIn, startsIn } (ms).</summary>
        internal static void SetRealMatch(bool open, double endsInMs, double startsInMs)
        {
            serverRmAt = UnityEngine.Time.realtimeSinceStartup; serverRmOpen = open; serverRmLeft = (open ? endsInMs : startsInMs) / 1000.0;
        }

        /// <summary>The Real Match car (RMCARINFO 1150000: "Mitron Motors", logo sp07_02_a = vehiclelist "Duel"): in a Real
        /// Match room everyone races its ratings (remake: the player's car takes the Duel's ratings for the race).</summary>
        public static (int accel, int speed, int crash, int boost) RealMatchStats()
        {
            var duel = RealMatchCar();
            return duel != null ? (duel.Accel, duel.Speed, duel.Crash, duel.Boost) : (149, 155, 84, 149);
        }

        /// <summary>The Real Match car (vehiclelist row 62 "Duel").</summary>
        public static VehicleInfo RealMatchCar()
        {
            bool Duel(VehicleInfo c) => (c.CarName ?? "").IndexOf("Duel", StringComparison.OrdinalIgnoreCase) >= 0 || (c.UiName ?? "").IndexOf("Duel", StringComparison.OrdinalIgnoreCase) >= 0;
            return GameTables.PlayerCars?.FirstOrDefault(Duel) ?? GameTables.PlayerCars?.FirstOrDefault(c => c.Index == 62);
        }

        /// <summary>One row of effects/realmatch/visual.xlt: Color (VehicleColor paint id), Decal (decal layout of the car),
        /// DecalColor (VehicleDecalColor id) and Neon (VehicleNeonColor id).</summary>
        public struct RealMatchLook { public int Color, Decal, DecalColor, Neon; }

        static List<RealMatchLook> rmLooks;

        /// <summary>
        /// The Real Match car's 16 looks (0x70de00 reads effects/realmatch/visual.xlt into 16 records of the character's car
        /// table +0x74c; 0x1c7 -> 0x6e7070 -> 0x70e240 then copies the room's RM car record into all 16): the car of the
        /// driver in room slot i wears row i, so the 16 identical Duels are told apart by colour, decal and neon [exact for
        /// the table; slot -> row inferred from the 16 rows].
        /// </summary>
        public static RealMatchLook RealMatchLookFor(int slot)
        {
            if (rmLooks == null)
            {
                rmLooks = new List<RealMatchLook>();
                try
                {
                    var fs = DriftCityData.Files;
                    string path = fs != null && fs.Exists(@"effects\realmatch\visual.xlt") ? @"effects\realmatch\visual.xlt" : null;
                    if (path != null)
                        foreach (var line in Cp949.Decode(fs.Read(path)).Split((char)10).Skip(1))
                        {
                            var c = line.Trim().Split((char)9);
                            if (c.Length < 4 || !int.TryParse(c[0], out int col)) continue;
                            int.TryParse(c[1], out int dec); int.TryParse(c[2], out int dc); int.TryParse(c[3], out int neon);
                            rmLooks.Add(new RealMatchLook { Color = col, Decal = dec, DecalColor = dc, Neon = neon });
                        }
                }
                catch { }
            }
            return rmLooks.Count == 0 ? new RealMatchLook() : rmLooks[((slot % rmLooks.Count) + rmLooks.Count) % rmLooks.Count];
        }

        /// <summary>The paint colour of a Real Match look (VehicleColor.nxt).</summary>
        public static Color RealMatchPaint(int slot) =>
            VehicleColors.TryGet(VehicleColors.Table.Paint, RealMatchLookFor(slot).Color, out var c) ? c : CarFactory.Paints[Mathf.Abs(slot) % CarFactory.Paints.Length];

        // ---- emoticons (init\emoticon.xlt: "/command" -> ch_e_00NN; 1130000 draws ch_e_0007..0038 over the slots) ----
        static Dictionary<string, string> emoticons;

        /// <summary>The emoticon icon of a chat line ("/Hi" -> "ch_e_0010"), or null.</summary>
        public static string Emoticon(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '/') return null;
            if (emoticons == null)
            {
                emoticons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var t = XltTable.Parse(DriftCityData.Files.Read(@"init\emoticon.xlt"));
                    foreach (var r in t.Rows)
                    {
                        if (r.Length < 5) continue;
                        string cmd = (r[1] ?? "").Trim(), icon = (r[4] ?? "").Trim();
                        if (cmd.Length > 0 && icon.StartsWith("ch_e_") && !emoticons.ContainsKey(cmd)) emoticons[cmd] = icon;
                    }
                }
                catch (Exception e) { Debug.LogWarning("Drift City: emoticon.xlt: " + e.Message); }
            }
            return emoticons.TryGetValue(text.Substring(1).Trim(), out var v) ? v : null;
        }

        // ---- init\rewardgroup.xlt (reward cards rw_battle_*, GT Championship rewards rw_gtc_*) ----
        public sealed class RewardRow { public string Type, Desc, Item; public int Count, Period; public float Prob; }
        static Dictionary<string, List<RewardRow>> rewardGroups;

        public static List<RewardRow> RewardGroup(string id)
        {
            if (rewardGroups == null)
            {
                rewardGroups = new Dictionary<string, List<RewardRow>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var t = XltTable.Parse(DriftCityData.Files.Read(@"init\rewardgroup.xlt"));
                    foreach (var r in t.Rows)
                    {
                        var g = t.Get(r, "GroupId").Trim();
                        if (g.Length == 0) continue;
                        if (!rewardGroups.TryGetValue(g, out var list)) rewardGroups[g] = list = new List<RewardRow>();
                        float.TryParse(t.Get(r, "Probability"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float prob);
                        list.Add(new RewardRow
                        {
                            Type = t.Get(r, "Reward_Type").Trim(), Desc = t.Get(r, "Reward_Description").Trim(), Item = t.Get(r, "ItemId").Trim(),
                            Count = t.GetInt(r, "Reward_Count", 1), Period = t.GetInt(r, "Reward_Period", 0), Prob = prob,
                        });
                    }
                }
                catch (Exception e) { Debug.LogWarning("Drift City: rewardgroup.xlt: " + e.Message); }
            }
            return rewardGroups.TryGetValue(id ?? "", out var v) ? v : new List<RewardRow>();
        }

        /// <summary>Random room titles 10223..10232 (0x576620).</summary>
        public static string RandomTitle() => Text((10223 + UnityEngine.Random.Range(0, 10)).ToString());

        public static string Text(string key, params object[] args) => MissionText.Get(key, args);

        // ---- globalcontent.xlt (US column) ----
        static Dictionary<string, string> content;

        public static int Config(string key, int fallback)
        {
            if (content == null)
            {
                content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var t = XltTable.Parse(DriftCityData.Files.Read(@"localize\globalcontent.xlt"));
                    foreach (var r in t.Rows)
                    {
                        var k = t.Get(r, "Key");
                        if (!string.IsNullOrEmpty(k)) content[k] = t.Get(r, "Value_US");
                    }
                }
                catch (Exception e) { Debug.LogWarning("Drift City: globalcontent.xlt: " + e.Message); }
            }
            return content.TryGetValue(key, out var v) && int.TryParse(v, out int n) ? n : fallback;
        }

        /// <summary>The current car's class (vehiclelist "Lvl", the client's car +0xc grade).</summary>
        public static int CarGrade()
        {
            var p = PlayerProfile.Current;
            if (p?.Car == null) return 1;
            return Mathf.Max(1, GameTables.Car(p.Car.VehicleIndex)?.Grade ?? 1);
        }

        /// <summary>
        /// Tab enable rule (0x575970): MinBattleNXGrade &lt;= grade &lt;= MaxBattleNXGrade and level &lt;= MaxBattleNXLevel
        /// (Free: the Free keys). Real Match needs the server's Real Match time, which the remake has not got.
        /// </summary>
        public static bool ArenaOpen(int arena, int grade, int level)
        {
            if (arena == ArenaRealMatch && !RealMatchOpen) return false;
            string k = arena == ArenaFree ? "Free" : arena == ArenaRealMatch ? "RealMatch" : ArenaExp[arena] + "X";
            int gMin = Config($"MinBattle{k}Grade", 1), gMax = Config($"MaxBattle{k}Grade", 10);
            int lMin = Config($"MinBattle{k}Level", 0), lMax = Config($"MaxBattle{k}Level", 1000);
            return grade >= gMin && grade <= gMax && level >= lMin && level <= lMax;
        }

        public static int FirstOpenArena()
        {
            int g = CarGrade(), l = PlayerProfile.Current?.Level ?? 1;
            for (int i = 0; i < ArenaTabs.Length; i++) if (ArenaOpen(i, g, l)) return i;
            return ArenaFree;
        }

        /// <summary>BattleRoomAI (US 1) and arena tab &lt;= 4 (0x57d9f0); the track must have an AI line (see TrackAllowsAi).</summary>
        public static bool AiAllowed(PvpRoom room) => room != null && Config("BattleRoomAI", 1) != 0 && (room.Arena <= 4 || room.Practice) && room.Wager == 0 && !room.Crew;

        /// <summary>
        /// The client allows AI drivers only on tracks whose pvptrackinfo AI column is 1 (0x7234c0); our AI drives the track's
        /// aipath.nac line, so every track with a line takes AI drivers in the remake.
        /// </summary>
        public static bool TrackAllowsAi(RaceTrack t) => t != null && t.Line.Count > 20;

        /// <summary>Track display name: the pvptrackinfo "name" column is a globaltext key ("moonpalace_track1" -> "Moon Palace Downtown").</summary>
        public static string TrackName(RaceTrack t)
        {
            if (t == null) return "";
            var s = GameTables.Text(t.Name);
            return string.IsNullOrEmpty(s) || s == t.Name ? RaceMission.Pretty(t.Name) : s;
        }

        public static string TrackKey(RaceTrack t) => $"{t.City}/{t.Track.ToLowerInvariant()}";

        public static RaceTrack FindTrack(string city, string key) => RaceMission.TracksFor(city).FirstOrDefault(t => TrackKey(t) == key);

        /// <summary>
        /// The wait room's browsable map list (0x5912a0): battle tracks (CityType 1) of the enabled cities whose Reverse
        /// column matches the REVERSE box; RECOMMEND keeps the tracks recommended for the room's arena. Races run in the open
        /// city, so only the current city's tracks are listed (its tab is the only one enabled).
        /// </summary>
        public static List<RaceTrack> MapList(string city, int mapFlags, int arena)
        {
            bool reverse = (mapFlags & MapReverse) != 0, recommend = (mapFlags & MapRecommend) != 0;
            var all = RaceMission.TracksFor(city).Where(t => t.CityType == 1).ToList();
            var list = all.Where(t => recommend ? t.Recommended(arena) : t.Reverse == reverse).ToList();
            return list.Count > 0 ? list : all;
        }

        /// <summary>Traffic during the race from the TRAFFIC buttons (IDLE / NORMAL / JAM / RANDOM): cars kept on the road.</summary>
        public static int TrafficCars(int mapFlags)
        {
            if ((mapFlags & TrafficIdle) != 0) return 0;
            if ((mapFlags & TrafficNormal) != 0) return 8;
            if ((mapFlags & TrafficJam) != 0) return 20;
            return new[] { 0, 8, 20 }[UnityEngine.Random.Range(0, 3)];
        }

        /// <summary>
        /// Ladder points for a place (remake rule; the client only shows the server's delta): +10 for the winner down to -10
        /// for the last, DNF counts as last. Only EXP arenas (not Free / Real Match / practice) and, in the 8X arena, only with
        /// at least LadderCalcNumX8 (US 12) drivers.
        /// </summary>
        public static int LadderDelta(int place, int field)
        {
            if (field < 2) return 0;
            if (place <= 0 || place > field) place = field;
            return Mathf.RoundToInt(10f * (field + 1 - 2 * place) / (field - 1));
        }

        public static bool LadderCounts(PvpRoom room, int field) =>
            room != null && !room.Practice && room.Arena >= 0 && room.Arena <= 4 && field >= 2 && (room.Arena != 4 || field >= Config("LadderCalcNumX8", 12));

        /// <summary>The race reward the server's "race" claim pays (economy.js rewards.race with the solo field), times the
        /// arena's EXP factor (multiplier / 2: Amateur 1x .. Superstar 4x, Free / Real Match 0.5x).</summary>
        public static (long exp, long mito) Reward(int level, int place, int arena)
        {
            long exp = place == 1 ? 400 + 50L * level : place == 2 ? 200 + 25L * level : 60;
            long mito = place == 1 ? 3000 + 300L * level : place == 2 ? 1200 : 300;
            if (arena >= 0 && arena < ArenaExp.Length) exp = (long)Math.Round(exp * ArenaExp[arena] / 2.0);
            return (exp, mito);
        }

        // ---- result bonus icons (PCROOMBONUS / EXPBONUS / SANTABONUS / TREEBONUS / 3RDBONUS, 1480000..1610000) ----
        /// <summary>Result entry bonus flags (0x588100 single / 0x58a070 team, entry dword +0x8c): 1 PC-room Mito, 2 PC-room
        /// EXP, 8 golden time (plays the goldentime banner), 0x10 EXP item, 0x20 Mito item (both = the BOTH icon), 0x200 /
        /// 0x400 Santa EXP 25% / 50% (both = 75%), 0x800 3rd anniversary. The tree icons (EXP_MITO 30/40/50/60%) follow the
        /// Christmas-tree event stage 1..4 (0x7733a0), not a flag.</summary>
        public const int BonusPcMito = 1, BonusPcExp = 2, BonusGolden = 8, BonusExpItem = 0x10, BonusMitoItem = 0x20,
            BonusSanta25 = 0x200, BonusSanta50 = 0x400, BonusAnniversary = 0x800;

        /// <summary>
        /// The bonus flags the remake can give a driver [remake]: the EXP / Mito drinks (ItemUse buffs) as the EXP / Mito item
        /// bonuses, the scratch-card Battle EXP goal (Lotto.BattleExpBonusPct) and the golden hours as golden time, Santa
        /// (Christmas season) and the 3rd anniversary from <see cref="Multipliers"/>. There are no PC rooms in the remake.
        /// </summary>
        public static int BonusFlags(PlayerProfile p)
        {
            if (p == null) return 0;
            int f = 0;
            try
            {
                if (ItemUse.ExpBonus(p, "race") > 0) f |= BonusExpItem;
                if (ItemUse.MitoBonus(p, "race") > 0) f |= BonusMitoItem;
                if (Lotto.BattleExpBonusPct > 0) f |= BonusGolden;
                f |= Multipliers.ResultFlags();
            }
            catch { }
            return f;
        }

        /// <summary>The Christmas-tree bonus stage 1..4 (cities whose tree is done; 0 = none) behind the TREEBONUS icons.</summary>
        public static int TreeStage()
        {
            try { return Mathf.Clamp(SeasonEvents.XmasCitiesDone, 0, 4); } catch { return 0; }
        }

        // ---- AI drivers (init\aicarparam.xlt; these names only without the table) ----
        static readonly string[] AiNames =
        {
            "Rio", "Duke", "Mika", "Jin", "Sora", "Kai", "Nova", "Taro", "Yuna", "Leon", "Hana", "Max", "Zed", "Aki", "Rex",
            "Luna", "Ivan", "Mei", "Toby", "Ken", "Sasha", "Bo", "Remi", "Gus",
        };
        static int nextAiId = 0x10000;   // the client numbers AI drivers from 0x10000 (slot array values >= 0x10000)

        public static PvpMember NewAi(PvpRoom room, int level)
        {
            var used = new HashSet<string>(room.Members.Select(m => m.Name));
            // the client's AI drivers (init\aicarparam.xlt): one whose AI level is near the player's, in one of its cars
            var table = GameTables.AiDrivers.Where(a => !used.Contains(a.Name) && GameTables.AiCars(a).Count > 0).ToList();
            if (table.Count > 0)
            {
                int best = table.Min(a => Mathf.Abs(a.Level - level));
                var near = table.Where(a => Mathf.Abs(a.Level - level) <= Mathf.Max(3, best)).ToList();
                var ai = near[UnityEngine.Random.Range(0, near.Count)];
                var aiCars = GameTables.AiCars(ai);
                return new PvpMember
                {
                    Id = nextAiId++, Name = ai.Name, Ai = true, Ready = true, Car = aiCars[UnityEngine.Random.Range(0, aiCars.Count)],
                    Level = Mathf.Clamp(ai.Level, 1, 99), Skill = UnityEngine.Random.Range(0.82f, 0.97f),
                };
            }
            var free = AiNames.Where(n => !used.Contains(n)).ToList();
            string name = free.Count > 0 ? free[UnityEngine.Random.Range(0, free.Count)] : "AI " + (room.Members.Count + 1);
            var cars = GameTables.PlayerCars.Where(c => c.ReqLevel <= level + 8).ToList();
            var car = cars.Count > 0 ? cars[UnityEngine.Random.Range(0, cars.Count)] : GameTables.PlayerCars.FirstOrDefault();
            return new PvpMember
            {
                Id = nextAiId++, Name = name, Ai = true, Ready = true, Car = car,
                Level = Mathf.Clamp(level + UnityEngine.Random.Range(-3, 4), 1, 99),
                Skill = UnityEngine.Random.Range(0.82f, 0.97f),
            };
        }

        public static string Ordinal(int n) => n == 1 ? "1st" : n == 2 ? "2nd" : n == 3 ? "3rd" : n + "th";

        /// <summary>Race time as the result rows print it: "%2d:%02d.%03d".</summary>
        public static string Time(float seconds)
        {
            int ms = Mathf.Max(0, Mathf.RoundToInt(seconds * 1000f));
            return $"{ms / 60000,2}:{ms / 1000 % 60:00}.{ms % 1000:000}";
        }

        /// <summary>A control by its numeric game.nui id (rows the client fills by id: base + index).</summary>
        public static NuiControlView ById(NuiView v, int id)
        {
            if (!v) return null;
            foreach (var c in v.GetComponentsInChildren<NuiControlView>(true)) if (c.Data.Id == id) return c;
            return null;
        }

        public static void Show(NuiControlView c, bool on) { if (c) c.gameObject.SetActive(on); }

        public static void Label(NuiControlView c, string text) { if (c) { c.gameObject.SetActive(true); c.SetText(text); } }

        public static void HideUnnamedTexts(NuiView v)
        {
            if (!v) return;
            foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
                if (string.IsNullOrEmpty(c.Data.IdName) && c.Data.I("T1") != null) c.gameObject.SetActive(false);
        }
    }
}
