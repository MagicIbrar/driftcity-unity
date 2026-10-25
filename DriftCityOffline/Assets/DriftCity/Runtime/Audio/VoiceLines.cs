namespace DriftCity
{
    /// <summary>
    /// The announcer's voice lines (SID_PVP_VO_*, effect.txt 231..243 = exe ids 0x4f07..0x4f13) and where the client plays
    /// them (DriftCity_RE/AUDIO.md 3), all through the sound manager 0x8d79c8 (0x52b980):
    /// <list type="bullet">
    /// <item>finish rank (0x72407c, own finish handler 0x723fe0): 1st / 2nd / 3rd, unless the room is a mission battle
    ///   (feature 0x10000 of 0x8c7f88); then SID_PVP_RACE_WINNER / FINISH;</item>
    /// <item>gate effects (PlayerEvent -> 0x726f20, jump table 0x7275c8): type 0 NOTHING, 1 BOOST50, 2 BOOST100, 3 SKIDRUSH,
    ///   4 REPAIR, 5 UNTOUCHABLE, 6 BOOOOST; 7 (team boost, 0x7271c9) TEAMBOOST;</item>
    /// <item>taking the lead in a race (0x72b8ea: the local car's rank became 1): FIRST;</item>
    /// <item>a jump take-off in the Battle Zone (0x4851dc, only while a PvP stage runs): JUMP;</item>
    /// <item>the first parking-lot arrival of a run (0x762f70, ParkingLotBoost): BOOST100.</item>
    /// </list>
    /// </summary>
    public static class VoiceLines
    {
        public const string First = "sfx/SID_PVP_VO_FIRST", Jump = "sfx/SID_PVP_VO_JUMP", Boost100 = "sfx/SID_PVP_VO_BOOST100";

        static readonly string[] GateFiles =
        {
            "sfx/SID_PVP_VO_NOTHING", "sfx/SID_PVP_VO_BOOST50", "sfx/SID_PVP_VO_BOOST100", "sfx/SID_PVP_VO_SKIDRUSH",
            "sfx/SID_PVP_VO_REPAIR", "sfx/SID_PVP_VO_UNTOUCHABLE", "sfx/SID_PVP_VO_BOOOOST", "sfx/SID_PVP_VO_TEAMBOOST",
        };

        /// <summary>The finish-rank line for rank 1..3 (null otherwise).</summary>
        public static string RankFile(int rank) => rank == 1 ? "sfx/SID_PVP_VO_1ST" : rank == 2 ? "sfx/SID_PVP_VO_2ND" : rank == 3 ? "sfx/SID_PVP_VO_3RD" : null;

        /// <summary>The voice of a gate effect type (0..7), null for others.</summary>
        public static string GateEffectFile(int type) => type >= 0 && type < GateFiles.Length ? GateFiles[type] : null;

        /// <summary>The last line asked for (tests).</summary>
        public static string Last { get; private set; }

        static void Say(string file)
        {
            if (file == null) return;
            Last = file;
            AudioLibrary.PlaySfx(file);
        }

        /// <summary>0x72407c: the local driver's finish rank (no line in a mission battle).</summary>
        public static void Rank(int rank, bool missionBattle = false) { if (!missionBattle) Say(RankFile(rank)); }

        /// <summary>0x726f20: a gate effect given to the local driver.</summary>
        public static void GateEffect(int type) => Say(GateEffectFile(type));

        /// <summary>0x72b8ea: the local car moved into first place.</summary>
        public static void TookLead() => Say(First);

        /// <summary>0x4851dc: take-off in a PvP race.</summary>
        public static void Jumped() => Say(Jump);

        /// <summary>0x762f70: the ParkingLotBoost line.</summary>
        public static void ParkingLotBoost() => Say(Boost100);
    }
}
