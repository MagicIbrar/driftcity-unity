using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>What finishing a step quest opens up: column 5 mapped by 0x6c57a0 (record +0x44).</summary>
    public enum QuestUnlock { None = 0, Chase = 1, Arbeit = 2, City = 3, City2nd = 4, City3rd = 5, City4th = 6, City5th = 7, Branton = 8, Omd = 9, Roo = 10 }

    /// <summary>A step's kind, columns 34-38 (record +0x704), as the step handler 0x6fc7b0 switches on it.</summary>
    public enum StepKind
    {
        /// <summary>0: reach the post (10126 "arrived").</summary>
        Arrive = 0,
        /// <summary>1: reach the post and stop there (under 11 km/h, 0x6faf80), 10126.</summary>
        Stop = 1,
        /// <summary>2: drive through the post (10127 "patrolled").</summary>
        Pass = 2,
        /// <summary>3: the post starts a trail: the target car (column 71) drives path column 69, 10128.</summary>
        Trail = 3,
        /// <summary>4: a stake-out: stay at the post until the target car (column 71, path column 69) passes, 10129.</summary>
        Stakeout = 4,
        /// <summary>5: the post starts an HUV chase with HUV column 71; the next step ends with the chase.</summary>
        Chase = 5,
        /// <summary>6: like 3 with the manager's longer trail range (+0x120).</summary>
        TrailFar = 6,
    }

    /// <summary>One of a step quest's five steps (QuestClient.tdf columns 14+i .. 64+i).</summary>
    public sealed class QuestStep
    {
        public string Text, Post, Marker, Script;
        public StepKind Kind;
        /// <summary>Crashes allowed (col 39+i, record +0x718; the client caps the count at 9), 0 = no limit.</summary>
        public int Crashes;
        /// <summary>Seconds for the step (col 44+i, +0x72c), 0 = no limit.</summary>
        public int Seconds;
        /// <summary>Speed range in km/h (cols 49+i / 54+i, +0x740 / +0x754), both 0 = none.</summary>
        public int SpeedMin, SpeedMax;
        /// <summary>Trail distance range in metres (cols 59+i / 64+i, +0x768 / +0x77c), used while the previous step was a trail.</summary>
        public int FollowMin, FollowMax;
    }

    /// <summary>
    /// A step quest of World/Data/QuestClient.tdf (320 rows x 80 columns), read as the loader 0x7093c0 (QuestClient part from
    /// 0x709639) does: id, number, group, kind (0 main / 1 repeat / 2 side / 3 Taipei / 99 event), prerequisite number, unlock,
    /// level range, start post + NPC lines, end post + lines, five steps, the trail path and car, the rewards. See
    /// DriftCity_RE/MISSIONS.md section 11.
    /// </summary>
    public sealed class StepQuest
    {
        public string Id, Group, StartPost, Title, StartScript, EndPost, EndScript;
        public int Number, Kind, Prereq, MinLevel, MaxLevel;
        public QuestUnlock Unlock;
        public List<QuestStep> Steps = new List<QuestStep>();
        /// <summary>Column 69 (+0x790): the path of the trail / stake-out car in &lt;city&gt;\path\path_&lt;city&gt;.chpath (10142 when missing).</summary>
        public int PathId;
        /// <summary>Column 71 (+0x798): vehiclelist Unique Id of the trail car (20xxx traffic, 10xxx HUV) or the chased HUV.</summary>
        public int Vehicle;
        public long Exp, Mito;
        /// <summary>Columns 77-79: item rewards (useitem ids, "repeat_token"...).</summary>
        public List<string> Items = new List<string>();
        /// <summary>The loader's id tests (0x7096d3): an id with "omd" is for OMD drivers only, one with "roo" for Team ROO only.</summary>
        public bool OmdOnly, RooOnly;
        /// <summary>q_00159 / 160 / 161 / 163 / 166 (0x709ce9): the target is a Team ROO leader, so the chase ends on the
        /// beat_success / beat_fail banners (HUV manager +0xe0, CHASES.md section 1).</summary>
        public bool RooTarget;

        public bool IsMain => Kind == 0;
        public bool IsRepeat => Kind == 1;
        public bool IsEvent => Kind == 99;
        /// <summary>MQ / RQ / SQ / NQ, the client's quest-giver marker families (0x6f1fa0).</summary>
        public string Category => Kind switch { 0 => "MQ", 1 => "RQ", 3 => "NQ", 99 => "EQ", _ => "SQ" };
        public string City => MissionPosts.CityOf(StartPost);
        public override string ToString() => $"{Id} #{Number} {Title}";
    }

    /// <summary>
    /// The step quest table and its rules: which quests a driver is offered, the faction branch of the storyline (Team ROO's
    /// "Team ROO Headquarters" #127 and OMD's "Patrol Oros City" #145 both follow #126), the daily reset of the repeat quests.
    /// The original server sent the offered list (manager +0x5c, records of 0x63aa bytes); these are the rebuild's rules
    /// (MISSIONS.md section 11), mirrored by DriftCityServer/src/quests.js.
    /// </summary>
    public static class StepQuests
    {
        static List<StepQuest> all;
        static Dictionary<int, StepQuest> byNumber;
        static readonly string[] RooTargets = { "q_00159", "q_00160", "q_00161", "q_00163", "q_00166" };

        public static List<StepQuest> All { get { Load(); return all; } }

        public static StepQuest Find(int number) { Load(); return byNumber.TryGetValue(number, out var q) ? q : null; }
        public static StepQuest FindId(string id) { Load(); return all.FirstOrDefault(q => q.Id == id); }

        /// <summary>Forgets the table (tests).</summary>
        public static void Reset() { all = null; byNumber = null; branch.Clear(); }

        static void Load()
        {
            if (all != null) return;
            all = new List<StepQuest>();
            byNumber = new Dictionary<int, StepQuest>();
            foreach (var r in MissionTables.Read("questclient.tdf"))
            {
                if (r.Length < 80 || string.IsNullOrEmpty(r[0])) continue;
                var q = Parse(r);
                if (q == null || byNumber.ContainsKey(q.Number)) continue;
                all.Add(q);
                byNumber[q.Number] = q;
            }
        }

        static string Cell(string[] r, int c) { var s = MissionTables.Str(r, c).Trim(); return s == "0" ? "" : s; }

        public static StepQuest Parse(string[] r)
        {
            var q = new StepQuest
            {
                Id = r[0].Trim(), Number = MissionTables.Int(r, 1), Group = r[2].Trim(), Kind = MissionTables.Int(r, 3),
                Prereq = MissionTables.Int(r, 4), Unlock = UnlockOf(r[5]), MinLevel = MissionTables.Int(r, 6), MaxLevel = MissionTables.Int(r, 7, 1000),
                StartPost = Cell(r, 9), Title = r[10].Trim(), StartScript = Cell(r, 11), EndPost = Cell(r, 12), EndScript = Cell(r, 13),
                PathId = MissionTables.Int(r, 69), Vehicle = MissionTables.Int(r, 71),
                Exp = MissionTables.Int(r, 75), Mito = MissionTables.Int(r, 76),
            };
            if (q.Number <= 0 || string.IsNullOrEmpty(q.StartPost)) return null;
            if (q.MaxLevel <= 0) q.MaxLevel = 1000;
            string lower = q.Id.ToLowerInvariant();
            q.OmdOnly = lower.Contains("omd");
            q.RooOnly = !q.OmdOnly && lower.Contains("roo");
            q.RooTarget = Array.IndexOf(RooTargets, q.Id) >= 0;
            for (int c = 77; c <= 79; c++) { var it = Cell(r, c); if (it.Length > 0) q.Items.Add(it); }
            // 0x702b30: the quest is done after step 4, or when the next step has no post
            for (int i = 0; i < 5; i++)
            {
                string post = Cell(r, 19 + 2 * i);
                if (post.Length == 0) break;
                q.Steps.Add(new QuestStep
                {
                    Text = Cell(r, 14 + i), Post = post, Marker = Cell(r, 20 + 2 * i), Script = Cell(r, 29 + i),
                    Kind = (StepKind)Mathf.Clamp(MissionTables.Int(r, 34 + i), 0, 6),
                    Crashes = MissionTables.Int(r, 39 + i), Seconds = MissionTables.Int(r, 44 + i),
                    SpeedMin = MissionTables.Int(r, 49 + i), SpeedMax = MissionTables.Int(r, 54 + i),
                    FollowMin = MissionTables.Int(r, 59 + i), FollowMax = MissionTables.Int(r, 64 + i),
                });
            }
            return q;
        }

        /// <summary>0x6c57a0: CHASE 1, ARBEIT 2, CITY 3, CITY_2ND..CITY_5TH 4-7, BRANTON 8, OMD 9, ROO 10 (case-insensitive).</summary>
        public static QuestUnlock UnlockOf(string s)
        {
            switch ((s ?? "").Trim().ToUpperInvariant())
            {
                case "CHASE": return QuestUnlock.Chase;
                case "ARBEIT": return QuestUnlock.Arbeit;
                case "CITY": return QuestUnlock.City;
                case "CITY_2ND": return QuestUnlock.City2nd;
                case "CITY_3RD": return QuestUnlock.City3rd;
                case "CITY_4TH": return QuestUnlock.City4th;
                case "CITY_5TH": return QuestUnlock.City5th;
                case "BRANTON": return QuestUnlock.Branton;
                case "OMD": return QuestUnlock.Omd;
                case "ROO": return QuestUnlock.Roo;
                default: return QuestUnlock.None;
            }
        }

        // ---------------- the faction branch ----------------

        static readonly Dictionary<int, int> branch = new Dictionary<int, int>();

        /// <summary>
        /// The storyline branch a quest belongs to: -1 none, 0 OMD, 1 Team ROO - the faction of the first join quest
        /// (unlock OMD / ROO) on its prerequisite chain, the quest itself included.
        /// </summary>
        public static int Branch(StepQuest q)
        {
            if (q == null) return -1;
            if (branch.TryGetValue(q.Number, out int b)) return b;
            b = -1;
            var seen = new HashSet<int>();
            for (var x = q; x != null && seen.Add(x.Number); x = x.Prereq > 0 ? Find(x.Prereq) : null)
            {
                if (x.Unlock == QuestUnlock.Omd) { b = Factions.Omd; break; }
                if (x.Unlock == QuestUnlock.Roo) { b = Factions.Roo; break; }
            }
            branch[q.Number] = b;
            return b;
        }

        /// <summary>The faction a driver has chosen by finishing a join quest, or -1 before the choice.</summary>
        public static int ChosenBranch(PlayerProfile p)
        {
            if (p == null) return -1;
            foreach (var n in p.StepQuestsDone)
            {
                var q = Find(n);
                if (q == null) continue;
                if (q.Unlock == QuestUnlock.Roo) return Factions.Roo;
                if (q.Unlock == QuestUnlock.Omd) return Factions.Omd;
            }
            return -1;
        }

        // ---------------- availability ----------------

        /// <summary>UTC day key of the repeat quests' reset.</summary>
        public static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

        /// <summary>A new UTC day takes the repeat quests (kind 1) off the done list (the server does the same).</summary>
        public static bool ResetRepeats(PlayerProfile p)
        {
            if (p == null || p.StepQuestDay == Today) return false;
            p.StepQuestDay = Today;
            p.StepQuestsDone.RemoveAll(n => Find(n)?.IsRepeat == true);
            return true;
        }

        public static bool Done(PlayerProfile p, StepQuest q) => p != null && q != null && p.StepQuestsDone.Contains(q.Number);

        /// <summary>Null when the driver may take the quest, else the reason (the client's 10190 family).</summary>
        public static string Refusal(PlayerProfile p, StepQuest q)
        {
            if (p == null || q == null) return MissionText.Get("10190");
            if (Done(p, q)) return MissionText.Get("10195", q.Title);
            // the event quests (kind 99) are offered while their season runs (the original server listed them with its events)
            if (q.IsEvent && !EventRunning(q)) return MissionText.Get("10190");
            if (p.Level < q.MinLevel || p.Level > q.MaxLevel) return MissionText.Get("10190");
            if (q.Prereq > 0 && Find(q.Prereq) != null && !p.StepQuestsDone.Contains(q.Prereq)) return MissionText.Get("10190");
            int faction = Factions.Of(p);
            if (q.OmdOnly && faction != Factions.Omd) return MissionText.Get("10199");
            if (q.RooOnly && faction != Factions.Roo) return MissionText.Get("10199");
            // after the choice at #126 only one branch goes on: the other side's quests (and its join quest) close
            int chosen = ChosenBranch(p), b = Branch(q);
            if (chosen >= 0 && b >= 0 && b != chosen) return MissionText.Get("10199");
            return null;
        }

        public static bool Available(PlayerProfile p, StepQuest q) => Refusal(p, q) == null;

        /// <summary>The quests a driver can take at a post (a station's QUEST button, a quest giver in the city).</summary>
        public static List<StepQuest> AvailableAt(string postId, PlayerProfile p)
        {
            Load();
            ResetRepeats(p);
            return all.Where(q => string.Equals(q.StartPost, postId, StringComparison.OrdinalIgnoreCase) && Available(p, q))
                      .OrderBy(q => q.Kind == 0 ? 0 : 1).ThenBy(q => q.Number).ToList();
        }

        /// <summary>The quest givers of a city with something to offer (minimap markers): one entry per post.</summary>
        public static List<StepQuest> GiversIn(string city, PlayerProfile p)
        {
            Load();
            ResetRepeats(p);
            return all.Where(q => q.City == city && Available(p, q)).GroupBy(q => q.StartPost).Select(g => g.OrderBy(q => q.Number).First()).ToList();
        }

        public static StepQuest Running(PlayerProfile p) => p != null && p.StepQuest != null && p.StepQuest.QuestId > 0 ? Find(p.StepQuest.QuestId) : null;

        // ---------------- the event quests (kind 99) ----------------

        /// <summary>
        /// The season an event quest belongs to [rebuild]: the two shipped ones are the Indonesian service's
        /// (group event_ini_*): eq_00001 "Independence Day Event" (August 17) runs with the summer festival, eq_00002 the
        /// "Moslem Event" (Idul Fitri, which fell in September / October in the client's years) with the autumn harvest.
        /// Any other kind-99 quest has no season and is never offered. The server maps them the same (src/quests.js).
        /// </summary>
        public static EventSeason EventSeasonOf(StepQuest q)
        {
            if (q == null || !q.IsEvent) return EventSeason.None;
            switch ((q.Group ?? "").ToLowerInvariant())
            {
                case "event_ini_1": return EventSeason.Summer;
                case "event_ini_2": return EventSeason.Harvest;
                default: return EventSeason.None;
            }
        }

        public static bool EventRunning(StepQuest q)
        {
            var s = EventSeasonOf(q);
            return s != EventSeason.None && SeasonEvents.Is(s);
        }

        // ---------------- the server's step bonuses ----------------

        /// <summary>The bonus count is capped here [rebuild] (the server sent a ushort per quest).</summary>
        public const int MaxTries = 10;

        /// <summary>
        /// 0x706420 builds the running entry from the server's quest list: a ushort n per quest gives the time bonus n*3 s
        /// (+0x14, added to a step's col 44+i time when it has one: 0x702f30 / 0x702ad0), the crash bonus n-1 (+0x18, added
        /// to col 39+i, the sum capped at 9: 0x6ff1d0) and the distance bonus n*5 m (+0x20, added to the trail's far limit
        /// col 64+i: 0x553520); +0x1c also gets n*5 (its use not traced). The rebuild's n is the number of times the
        /// running quest was started over after a failure or RETRY (PlayerProfile.StepQuestTries, the server keeps it).
        /// </summary>
        public static int TimeBonus(int n) => 3 * Mathf.Clamp(n, 0, MaxTries);
        public static int CrashBonus(int n) => Mathf.Max(0, Mathf.Clamp(n, 0, MaxTries) - 1);
        public static int DistanceBonus(int n) => 5 * Mathf.Clamp(n, 0, MaxTries);

        /// <summary>A step's seconds with the bonus (0 = no limit stays 0).</summary>
        public static int StepSeconds(QuestStep st, int n) => st == null || st.Seconds <= 0 ? 0 : st.Seconds + TimeBonus(n);
        /// <summary>A step's crash limit with the bonus, capped at 9 (0 = no limit stays 0).</summary>
        public static int StepCrashes(QuestStep st, int n) => st == null || st.Crashes <= 0 ? 0 : Mathf.Min(9, st.Crashes + CrashBonus(n));
        /// <summary>A step's far trail limit with the bonus (0 = none stays 0).</summary>
        public static int StepFollowMax(QuestStep st, int n) => st == null || st.FollowMax <= 0 ? 0 : st.FollowMax + DistanceBonus(n);
    }

    /// <summary>The quest manager's unlock flags (0x70b660 +0x108..+0x110) as 0x703d20 recomputes them.</summary>
    public struct QuestUnlockState
    {
        /// <summary>+0x108: the rookie missions are over (the ARBEIT quest is done): the stations show their full panel,
        /// before it only BEGINNERQUEST (0x5625e0 -> 0x561f80); the parking lot's Battle Zone refuses with 10066 (0x763bd3).</summary>
        public bool Rookie;
        /// <summary>+0x109: the Delivery Service button is enabled (0x5622dc) and its list opens (0x55b4c2, else 10182).</summary>
        public bool Arbeit;
        /// <summary>+0x10a: the HUV calls come (0x6ca119).</summary>
        public bool Chase;
        /// <summary>+0x10b..+0x10e: Koinonia, Cras, Oros, Taipei (Mitro City): the city gates (0x6f9630, else 10746) and the
        /// VIEWMAP city pages (0x5f3170 / 0x5f5c40).</summary>
        public bool City2, City3, City4, City5;
        /// <summary>+0x10f / +0x110: the side's finale is done (q_00149 record +0x90d / q_00167 +0x90e): a Team ROO / OMD
        /// driver's station quest button stays off (0x562030).</summary>
        public bool RooFinale, OmdFinale;
    }

    /// <summary>
    /// The CHASE / ARBEIT / CITY unlocks of QuestClient.tdf column 5 (0x6c57a0) as the client applies them: 0x703d20 clears the
    /// flags and sets them from the level (above 3 the Delivery Service, above 5 the chases, above 13 / 28 / 45 Koinonia /
    /// Cras / Oros when the server's city bits net+0x258 2 / 4 / 8 are on) and from every completed quest's unlock (ARBEIT
    /// sets +0x108 and +0x109, CHASE +0x10a, CITY_2ND..CITY_5TH +0x10b..+0x10e) and finale flag, and opens Taipei when the
    /// mother quest mq_5th_003 is in the driver's list (+0x100). "CITY" (3), "car" and "1" set nothing. The rebuild has
    /// every server city bit on (MISSIONS.md section 14).
    /// </summary>
    public static class QuestUnlocks
    {
        /// <summary>Tests / tools can switch the locks off (everything open).</summary>
        public static bool Enabled = true;
        public const int ArbeitLevel = 4, ChaseLevel = 6, City2Level = 14, City3Level = 29, City4Level = 46;
        /// <summary>0x6f9630: the Taipei gate does nothing below level 71 (0x47).</summary>
        public const int TaipeiGateLevel = 71;
        /// <summary>[rebuild] a driver at this level or above who has finished no step quest at all (a save from before the
        /// step quests) counts as past the rookie missions; the original server never had such a driver.</summary>
        public const int LegacyRookieLevel = 6;
        public const string RooFinaleId = "q_00149", OmdFinaleId = "q_00167", TaipeiMotherQuest = "mq_5th_003";

        public static QuestUnlockState Of(PlayerProfile p)
        {
            var s = new QuestUnlockState();
            if (!Enabled) { s.Rookie = s.Arbeit = s.Chase = s.City2 = s.City3 = s.City4 = s.City5 = true; return s; }
            if (p == null) return s;
            int lvl = p.Level;
            s.Arbeit = lvl >= ArbeitLevel;
            s.Chase = lvl >= ChaseLevel;
            s.City2 = lvl >= City2Level;
            s.City3 = lvl >= City3Level;
            s.City4 = lvl >= City4Level;
            foreach (var n in p.StepQuestsDone)
            {
                var q = StepQuests.Find(n);
                if (q == null) continue;
                switch (q.Unlock)
                {
                    case QuestUnlock.Arbeit: s.Rookie = s.Arbeit = true; break;
                    case QuestUnlock.Chase: s.Chase = true; break;
                    case QuestUnlock.City2nd: s.City2 = true; break;
                    case QuestUnlock.City3rd: s.City3 = true; break;
                    case QuestUnlock.City4th: s.City4 = true; break;
                    case QuestUnlock.City5th: s.City5 = true; break;
                }
                if (q.Id == RooFinaleId) s.RooFinale = true;
                if (q.Id == OmdFinaleId) s.OmdFinale = true;
            }
            if (!s.Rookie && lvl >= LegacyRookieLevel && p.StepQuestsDone.Count == 0) s.Rookie = true;
            if (!s.City5 && QuestTracker.CompletedKey(p, TaipeiMotherQuest)) s.City5 = true;
            return s;
        }

        public static QuestUnlockState Current => Of(PlayerProfile.Current);

        /// <summary>The side's storyline is over: the station's quest button stays off (0x562030 +0x10f / +0x110).</summary>
        public static bool StoryOver(PlayerProfile p)
        {
            var s = Of(p);
            return Factions.Of(p) == Factions.Roo ? s.RooFinale : s.OmdFinale;
        }

        /// <summary>0x6ca110: the HUV call comes once CHASE is open and no delivery or step quest runs.</summary>
        public static bool ChaseOffer(PlayerProfile p) =>
            Of(p).Chase && !DeliveryService.Active && !(Enabled && StepQuests.Running(p) != null);

        /// <summary>Whether the driver may enter a city by its gate (Moon Palace always).</summary>
        public static bool CityOpen(PlayerProfile p, string city)
        {
            var s = Of(p);
            switch ((city ?? "").ToLowerInvariant())
            {
                case "koinonia": return s.City2;
                case "cras": case "craspeda": return s.City3;
                case "oros": return s.City4;
                case "taipei": return s.City5 && (!Enabled || (p != null && p.Level >= TaipeiGateLevel));
                default: return true;
            }
        }

        /// <summary>The gate's refusal (0x6f5520: 10746 "This area is restricted."), or null when the city is open.</summary>
        public static string CityRefusal(PlayerProfile p, string city) => CityOpen(p, city) ? null : MissionText.Get("10746");
        public static string CityRefusal(string city) => CityRefusal(PlayerProfile.Current, city);

        /// <summary>The Delivery Service list (0x55b320: 10182 "You didn't complete the Delivery Service mission."), or null.</summary>
        public static string DeliveryRefusal(PlayerProfile p) => Of(p).Arbeit ? null : MissionText.Get("10182");

        /// <summary>The parking lot's Battle Zone before the rookie missions (0x763840: 10066), or null.</summary>
        public static string BattleZoneRefusal(PlayerProfile p) => Of(p).Rookie ? null : MissionText.Get("10066");
    }

    /// <summary>
    /// The two sides of the storyline: OMD (0, everyone at the start) and Team ROO (1). The client keeps the faction in the
    /// character data (+0xed, set by packet 0x76d) and switches on it: Team ROO calls the Delivery Service "Time Attack"
    /// (labels and the 10659.. text twins), patrols with move_ROO / party_move_ROO, the ROO stations (icon table column 4
    /// "ROO": Roo Outpost, Team Roo HQ) show TEAMROO / SECRETMISSION / TIMEATTACK, and in Oros a station's NEW badge only
    /// shows for its own side (0x562030). The rebuild sets the faction when a join quest (unlock OMD / ROO) is finished.
    /// </summary>
    public static class Factions
    {
        public const int Omd = 0, Roo = 1;

        public static int Of(PlayerProfile p) => p != null && p.Faction == Roo ? Roo : Omd;
        public static int Current => Of(PlayerProfile.Current);
        public static bool IsRoo => Current == Roo;

        static HashSet<string> rooPosts;

        /// <summary>Posts whose icon table row has "ROO" in column 4 (0x562030 tests the station's +0x210).</summary>
        public static bool IsRooPost(string postId)
        {
            if (rooPosts == null)
            {
                rooPosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "moonpalace", "koinonia", "craspeda", "oros", "taipei" })
                    foreach (var r in MissionTables.Read($"icon_{name}_client.tdf"))
                        if (r.Length > 4 && r[4].Trim().Equals("ROO", StringComparison.OrdinalIgnoreCase)) rooPosts.Add(r[0].Trim());
            }
            return !string.IsNullOrEmpty(postId) && rooPosts.Contains(postId);
        }

        /// <summary>The faction's quest marker family for game\mq_*_&lt;side&gt;.nif.</summary>
        public static string MarkerSide => IsRoo ? "roo" : "omd";

        /// <summary>The Delivery Service text, or its Team ROO "Time Attack" twin (10133 -> 10659 ...).</summary>
        public static string DeliveryText(string key)
        {
            if (!IsRoo) return key;
            switch (key)
            {
                case "10133": return "10659";   // [%s] Time Attack Received.
                case "10186": return "10661";   // You cannot do Time Attacks while doing a mission.
                case "10187": return "10662";
                case "10188": return "10663";
                case "10189": return "10664";
                case "10621": return "10668";   // the menu during a chase
                case "10623": return "10669";   // Battle Zone during a Time Attack
                case "10144": return "10660";   // coupon
                case "11118": return "11119";   // Auto-Drive
                default: return key;
            }
        }

        /// <summary>Forgets the post list (tests).</summary>
        public static void Reset() => rooPosts = null;
    }
}
