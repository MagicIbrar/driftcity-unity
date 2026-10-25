using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// One row of World/Data/Instant_Client.tdf (loader 0x6d1200; the kind names are mapped by 0x6c5e40:
    /// battle 1, auction 2, quick_rank 3, shop 4, knife 5, teambattle_win 6, combo 7, move 8, move_event 9, move_ROO 10,
    /// mission_item 11 ... party_move 0x20, party_move_ROO 0x21, Mission_Battle 0x22..0x25, Battle_* 0x26..0x2b,
    /// Chasing_* 0x2c..0x30, Arrest_HUV 0x31). Columns: 0 type (instant / patrol / day_mission), 1-2 level range,
    /// 4 category (NewInstant: City / Chasing / Battle), 5 title, 6 flag, 7 minutes, 8 NPC lines, 9 kind, 10 count,
    /// 12 EXP (+0x1130), 13 Mito (+0x1134), 14 patrol completion EXP (+0x1138), 15 patrol posts (+0x113c), 16.. posts.
    /// </summary>
    public sealed class InstantRow
    {
        public int Index;
        public string Type, Category, Title, Ment, Kind, ExpText, MitoText;
        public int MinLevel, MaxLevel, Minutes, Count;
        public bool Flag;
        public int PostExp, PostMito, CompleteExp, MaxPosts;   // patrol columns 12-15
        public List<string> Posts = new List<string>();

        public bool IsPatrol => Type == "patrol";
        public bool IsParty => Kind.StartsWith("party_move");
        public bool FitsLevel(int level) => level >= MinLevel && level <= MaxLevel;

        /// <summary>EXP of an instant: column 12 (the client doubles it in an EXP event, 0x5c4f90).</summary>
        public long ExpFor(int level) => (long)InstantFormula.Eval(ExpText, level);
        /// <summary>Mito of an instant: column 13, a number or a formula such as INT(({Level}^1.7)/5+10).</summary>
        public long MitoFor(int level) => (long)InstantFormula.Eval(MitoText, level);

        static List<InstantRow> rows;

        public static List<InstantRow> All
        {
            get
            {
                if (rows != null) return rows;
                rows = new List<InstantRow>();
                var table = MissionTables.Read("instant_client.tdf");
                for (int i = 0; i < table.Count; i++)
                {
                    var r = table[i];
                    if (r.Length < 16 || string.IsNullOrEmpty(r[0])) continue;
                    var row = new InstantRow
                    {
                        Index = i, Type = r[0].Trim(), MinLevel = MissionTables.Int(r, 1), MaxLevel = MissionTables.Int(r, 2, 1000),
                        Category = r[4].Trim(), Title = r[5].Trim(), Flag = r[6].Trim().Equals("True", StringComparison.OrdinalIgnoreCase),
                        Minutes = MissionTables.Int(r, 7), Ment = r[8], Kind = r[9].Trim(), Count = MissionTables.Int(r, 10),
                        ExpText = r[12].Trim(), MitoText = r[13].Trim(),
                        PostExp = MissionTables.Int(r, 12), PostMito = MissionTables.Int(r, 13), CompleteExp = MissionTables.Int(r, 14), MaxPosts = MissionTables.Int(r, 15),
                    };
                    for (int c = 16; c < r.Length; c++) { var p = r[c].Trim(); if (p.Length > 0 && p != "n/a") row.Posts.Add(p); }
                    rows.Add(row);
                }
                return rows;
            }
        }
    }

    /// <summary>The table's reward formulas: INT(({Level}^2)/7+10) and the like; unknown terms count 0.</summary>
    public static class InstantFormula
    {
        public static double Eval(string text, int level)
        {
            if (string.IsNullOrWhiteSpace(text) || text == "n/a") return 0;
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double plain)) return plain;
            string s = text.Replace("{Level}", level.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace(" ", "");
            if (s.Contains("{")) return 0;   // e.g. {체이싱보상} (the chase reward), decided by the server
            int pos = 0;
            try { double v = Expr(s, ref pos); return pos == s.Length ? Math.Max(0, v) : 0; }
            catch (Exception) { return 0; }
        }

        static double Expr(string s, ref int p)
        {
            double v = Term(s, ref p);
            while (p < s.Length && (s[p] == '+' || s[p] == '-')) { char o = s[p++]; double t = Term(s, ref p); v = o == '+' ? v + t : v - t; }
            return v;
        }

        static double Term(string s, ref int p)
        {
            double v = Power(s, ref p);
            while (p < s.Length && (s[p] == '*' || s[p] == '/')) { char o = s[p++]; double t = Power(s, ref p); v = o == '*' ? v * t : (t == 0 ? 0 : v / t); }
            return v;
        }

        static double Power(string s, ref int p)
        {
            double v = Atom(s, ref p);
            if (p < s.Length && s[p] == '^') { p++; v = Math.Pow(v, Power(s, ref p)); }
            return v;
        }

        static double Atom(string s, ref int p)
        {
            if (s.Length - p >= 4 && string.Compare(s, p, "INT(", 0, 4, StringComparison.OrdinalIgnoreCase) == 0) { p += 4; double v = Expr(s, ref p); p++; return Math.Floor(v); }
            if (s[p] == '(') { p++; double v = Expr(s, ref p); p++; return v; }
            if (s[p] == '-') { p++; return -Atom(s, ref p); }
            int start = p;
            while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.')) p++;
            return double.Parse(s.Substring(start, p - start), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Instant missions ("Call Mission", INSTANTMISSION dialogs): a short objective offered while driving around.
    /// <list type="bullet">
    /// <item>The offer is the "Call Mission" notice (860000: one of NOTIFY1-4, Refuse, Accept on Tab).</item>
    /// <item>Accepted, the mission's description shows in 1790000 (text, reward lines) and its progress in the ADV
    /// running-mission list (240000: "%d/%d achieved", 10461, 0x5c4640); a click opens the confirm dialog 1800000
    /// (0x5c36d0: "%d Exp" / "%d Mito", Abandon asks 10194).</item>
    /// <item>The kinds that work offline: knife (skill passes = the speed combo's near misses), combo (speed combo
    /// tricks), Arrest_HUV (HUV chases won) and battle (races finished). The other kinds need the online server
    /// (auction, ranks, team battles, Battle Zone rooms, chase variants, the Mission Girl's day missions).</item>
    /// </list>
    /// The server chose which mission to offer and paid it; offline a random row for the driver's level is offered every
    /// <see cref="OfferInterval"/> seconds of free driving (City rows of the NewInstant system first, NewInstant = 1 in
    /// globalcontent.xlt), column 7 is taken as the time limit in minutes and the reward is column 12 EXP plus column
    /// 13 Mito (formulas evaluated with the driver's level). Failing prints 10992. Server mirror: economy.js "instant".
    /// </summary>
    public sealed class InstantMissions : MonoBehaviour
    {
        public static readonly string[] OfflineKinds = { "knife", "combo", "Arrest_HUV", "battle" };
        public const int DescId = 1790000, ConfirmId = 1800000, CallId = 860000;

        public float OfferInterval = 150f, FirstOffer = 90f, OfferTimeout = 15f, DescSeconds = 8f;

        public static InstantMissions Instance { get; private set; }
        /// <summary>The running instant mission's row, or null.</summary>
        public InstantRow Current => running ? row : null;
        public int Achieved { get; private set; }
        public float TimeLeft { get; private set; }

        public CitySession Session;
        InstantRow row, offered;
        bool running;
        float offerTimer, offerAge, descTimer;
        NuiView call, desc;
        int lastHuv, lastRaces, lastCombo = -1;

        public static InstantMissions Attach(CitySession session, string city)
        {
            if (!session) return null;
            if (!session.TryGetComponent(out InstantMissions m)) m = session.gameObject.AddComponent<InstantMissions>();
            m.Session = session;
            m.offerTimer = m.FirstOffer;
            Instance = m;
            return m;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            CloseCall();
            Stop();
        }

        /// <summary>Rows the offline game can offer a driver of <paramref name="level"/> (NewInstant City rows preferred).</summary>
        public static List<InstantRow> Eligible(int level)
        {
            var list = InstantRow.All.Where(r => r.Type == "instant" && OfflineKinds.Contains(r.Kind) && r.FitsLevel(level) && r.Count > 0).ToList();
            var city = list.Where(r => r.Category == "City").ToList();
            return city.Count > 0 ? city : list;
        }

        // ---------------- offer ----------------

        /// <summary>Shows the Call Mission notice for <paramref name="r"/> (a random eligible row when null).</summary>
        public bool Offer(InstantRow r = null)
        {
            if (running) return false;
            if (r == null)
            {
                var list = Eligible(PlayerProfile.Current?.Level ?? 1);
                if (list.Count == 0) return false;
                r = list[UnityEngine.Random.Range(0, list.Count)];
            }
            offered = r;
            offerAge = 0;
            call = MissionUi.CallMission(() => { call = null; Begin(offered); }, () => { call = null; offered = null; });
            return call;
        }

        void CloseCall()
        {
            if (call && UiManager.Instance) UiManager.Instance.Close(call);
            call = null;
        }

        bool Busy()
        {
            var flow = GameFlow.Instance;
            return GameFlow.MissionActive || ChaseMission.Active || DeliveryService.Active || PatrolMission.Active || (flow && flow.MenuOpen) ||
                   RushTime.Joined || VirtualChase.Active || AutoDrive.Active || ChaseScreens.OfferView ||
                   MissionUi.ScriptOpen || DeliveryService.BoardOpen || (UiManager.Instance && (UiManager.Instance.Find(CallId) || UiManager.Instance.Find(4400000)));
        }

        // ---------------- run ----------------

        public void Begin(InstantRow r)
        {
            if (r == null) return;
            CloseCall();
            row = r;
            running = true;
            Achieved = 0;
            TimeLeft = r.Minutes > 0 ? r.Minutes * 60f : 0f;
            var p = PlayerProfile.Current;
            lastHuv = p?.HuvCaught ?? 0;
            lastRaces = p?.RacesEntered ?? 0;
            lastCombo = -1;
            Net.ChatBox.System(MissionText.Get("10122", r.Title));
            ShowDesc();
            UpdateTracker();
        }

        void ShowDesc()
        {
            var ui = UiManager.Instance;
            if (desc) ui.Close(desc);
            desc = ui.Open(DescId);
            if (!desc) return;
            var pages = Core.NpcScript.Parse((row.Ment ?? "").Replace("<sub_text", "<text"));
            string lines = string.Join(" ", pages.SelectMany(pg => pg.Lines)).Replace("$name", PlayerProfile.Current?.Name ?? "Driver");
            desc.SetText("ID_INSTANTMISSION_DESC_TEXT", row.Title + (lines.Length > 0 ? "\n" + lines : ""));
            int level = PlayerProfile.Current?.Level ?? 1;
            desc.SetText("ID_INSTANTMISSION_DESC_NUMBER1ST", MissionText.Printf("%d Exp", row.ExpFor(level)));
            desc.SetText("ID_INSTANTMISSION_DESC_NUMBER2ND", MissionText.Printf("%d Mito", row.MitoFor(level)));
            descTimer = DescSeconds;
        }

        string ProgressText() => MissionText.Get("10461", Mathf.Min(Achieved, row.Count), row.Count) +
                                 (TimeLeft > 0 ? $"  {Mathf.FloorToInt(TimeLeft / 60)}:{Mathf.FloorToInt(TimeLeft % 60):00}" : "");

        void UpdateTracker()
        {
            if (!running) { MissionTracker.Remove(this); return; }
            MissionTracker.Set(this, GameTables.Text("11870"), row.Title, ProgressText(), ShowConfirm);
        }

        /// <summary>1800000: the running mission with Abandon (10194) and Close.</summary>
        public void ShowConfirm()
        {
            if (!running) return;
            var ui = UiManager.Instance;
            if (desc) { ui.Close(desc); desc = null; }
            var old = ui.Find(ConfirmId);
            if (old) ui.Close(old);
            var v = ui.Open(ConfirmId);
            if (!v) return;
            int level = PlayerProfile.Current?.Level ?? 1;
            v.SetText("ID_INSTANTMISSION_CONFIRM_TEXT", row.Title + "\n" + ProgressText());
            v.SetText("ID_INSTANTMISSION_CONFIRM_NUMBER1ST", MissionText.Printf("%d Exp", row.ExpFor(level)));
            v.SetText("ID_INSTANTMISSION_CONFIRM_NUMBER2ND", MissionText.Printf("%d Mito", row.MitoFor(level)));
            ui.On("ID_INSTANTMISSION_CONFIRM_CLOSE", view => ui.Close(view));
            ui.On("ID_INSTANTMISSION_CONFIRM_FORGIVE", view => MissionUi.Confirm(MissionText.Get("10194"), () => { ui.Close(view); GiveUp(); }));
        }

        /// <summary>Counts progress of the running mission's kind (the observers below call it; tests may too).</summary>
        public void Report(string kind, int amount = 1)
        {
            if (!running || amount <= 0 || row.Kind != kind) return;
            Achieved += amount;
            if (Achieved >= row.Count) Complete();
            else UpdateTracker();
        }

        void Update()
        {
            if (call)
            {
                offerAge += Time.deltaTime;
                if (MissionUi.TabPressed()) { UiSounds.Click(); call.Click("ID_INSTANTMISSION_TABBUTTON"); }
                else if (offerAge > OfferTimeout) { CloseCall(); offered = null; }
            }
            if (desc)
            {
                descTimer -= Time.deltaTime;
                if (descTimer <= 0) { UiManager.Instance?.Close(desc); desc = null; }
            }
            if (!running)
            {
                if (call || Busy() || !(GameFlow.Instance && GameFlow.Instance.PlayerCar)) return;
                offerTimer -= Time.deltaTime;
                if (offerTimer <= 0) { offerTimer = OfferInterval; Offer(); }
                return;
            }
            if (TimeLeft > 0)
            {
                float before = TimeLeft;
                TimeLeft -= Time.deltaTime;
                if (TimeLeft <= 0) { Fail(); return; }
                if (Mathf.FloorToInt(before) != Mathf.FloorToInt(TimeLeft)) UpdateTracker();
            }
        }

        void LateUpdate()
        {
            if (!running) return;
            var p = PlayerProfile.Current;
            if (p != null)
            {
                if (p.HuvCaught > lastHuv) { int n = p.HuvCaught - lastHuv; lastHuv = p.HuvCaught; Report("Arrest_HUV", n); if (!running) return; }
                else lastHuv = p.HuvCaught;
                if (p.RacesEntered > lastRaces) { int n = p.RacesEntered - lastRaces; lastRaces = p.RacesEntered; Report("battle", n); if (!running) return; }
                else lastRaces = p.RacesEntered;
            }
            // the speed combo (DriftCombo on the HUD): every trick adds one; a near miss (ID_SPEEDCOMBO_STUNT) is a
            // "Skill Pass" (knife)
            var combo = GameFlow.Instance ? GameFlow.Instance.GetComponent<DriftCombo>() : null;
            if (!combo) return;
            int count = combo.Count;
            if (lastCombo < 0 || count < lastCombo) { lastCombo = count; return; }
            if (count > lastCombo)
            {
                int n = count - lastCombo;
                lastCombo = count;
                var sv = UiManager.Instance ? UiManager.Instance.Find(300000) : null;
                var stunt = sv ? sv.Control("ID_SPEEDCOMBO_STUNT") : null;
                if (stunt && stunt.gameObject.activeSelf) Report("knife", n);
                Report("combo", n);
            }
        }

        void Complete()
        {
            var r = row;
            Stop();
            var rec = MissionRecords.Current;
            rec.InstantsCompleted++;
            rec.Save();
            AudioLibrary.PlaySfx("sfx/SID_INSTANT_FINISH");
            GameEffects.InstantMission(true, false);   // 0x6f6560: im_success
            Net.ChatBox.System(MissionText.Get("10123", r.Title));
            var p = PlayerProfile.Current;
            if (p == null) return;
            int level = p.Level;
            var data = new Dictionary<string, object> { ["row"] = r.Index, ["kind"] = r.Kind, ["count"] = r.Count };
            Progress.Reward("instant", data, () =>
            {
                long exp = MitaDrink.Apply(r.ExpFor(level)), mito = r.MitoFor(level);
                int before = p.Level;
                p.AddExp(exp); p.Mito += mito;
                return new Progress.Result { Ok = true, Exp = exp, Mito = mito, LevelUps = p.Level - before };
            }, res => UiPopup.Message(res.Ok
                ? $"{GameTables.Text("10178")}\n{r.Title}\nEXP +{res.Exp}   Mito +{res.Mito}" + (res.LevelUps > 0 ? $"\nLevel up! You are now level {PlayerProfile.Current.Level}." : "")
                : $"{GameTables.Text("10178")}\n{r.Title}\n(no reward: {res.Error})"));
        }

        void Fail()
        {
            AudioLibrary.PlaySfx("sfx/SID_QUEST_FAIL");
            GameEffects.InstantMission(false, false);   // 0x4efa30: im_failed
            Net.ChatBox.System(GameTables.Text("10992"));
            Stop();
        }

        public void GiveUp()
        {
            if (!running) return;
            AudioLibrary.PlaySfx("sfx/SID_QUEST_CANCEL");
            Net.ChatBox.System(MissionText.Get("10124", row.Title));
            Stop();
        }

        void Stop()
        {
            running = false;
            MissionTracker.Remove(this);
            var ui = UiManager.Instance;
            if (ui)
            {
                if (desc) ui.Close(desc);
                var c = ui.Find(ConfirmId);
                if (c) ui.Close(c);
            }
            desc = null;
            offerTimer = OfferInterval;
        }
    }
}
