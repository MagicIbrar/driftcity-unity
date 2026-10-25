using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    public enum QuestEvent { HuvCaught, HuvEscaped, RaceWon, RaceLost, CarBought, CarUpgraded, DistanceKm }

    /// <summary>A main quest from init\motherquest.xlt with an offline objective.</summary>
    public sealed class QuestInfo
    {
        public int Id;
        public string Key, Title, StartScript, EndScript;
        public int ReqLevel;
        /// <summary>Quest that must be completed first (the table's prerequisite), or -1.</summary>
        public int Requires = -1;
        public QuestEvent Goal;
        public int GoalCount;
        public long RewardExp, RewardMito;

        public string GoalText => Goal switch
        {
            QuestEvent.HuvCaught => $"Catch {GoalCount} HUV(s)",
            QuestEvent.RaceWon => $"Win {GoalCount} race(s)",
            QuestEvent.CarUpgraded => $"Upgrade your car {GoalCount} time(s)",
            QuestEvent.CarBought => "Buy a new car",
            QuestEvent.DistanceKm => $"Drive {GoalCount} km",
            _ => "",
        };
    }

    /// <summary>
    /// Offline quest system: the original main quest chain (titles, operator scripts, level gates) with
    /// objectives built from the systems this offline build has (the original child quests were server-side).
    /// </summary>
    public static class QuestTracker
    {
        static List<QuestInfo> quests;
        static PlayerProfile P => PlayerProfile.Current;

        public static List<QuestInfo> All
        {
            get
            {
                if (quests != null) return quests;
                quests = new List<QuestInfo>();
                var t = XltTable.Parse(DriftCityData.Files.Read(@"init\motherquest.xlt"));
                var goals = new[] { QuestEvent.HuvCaught, QuestEvent.RaceWon, QuestEvent.DistanceKm, QuestEvent.CarUpgraded, QuestEvent.HuvCaught, QuestEvent.CarBought };
                // this client's chain is the level-80 Mitro City storyline: it is played in story order (each quest
                // after its prerequisite) with the level gate ramping from 1 to 75 so every driver can follow it
                var keyToId = new Dictionary<string, int>();
                var prereqKey = new Dictionary<int, string>();
                int position = 0;
                foreach (var r in t.Rows.OrderBy(r => t.GetInt(r, "Index", int.MaxValue)))
                {
                    int id = t.GetInt(r, "Index", -1);
                    string title = t.Get(r, "Mother Quest Title");
                    if (id < 0 || string.IsNullOrEmpty(title)) continue;
                    int tableLevel = r.Length > 2 && int.TryParse(r[2].Trim(), out int l) ? l : 1;
                    int lvl = Mathf.Min(tableLevel, 1 + 2 * position++);
                    keyToId[t.Get(r, "MotherQuest_ID").Trim()] = id;
                    if (r.Length > 4) prereqKey[id] = r[4].Trim();
                    var q = new QuestInfo
                    {
                        Id = id, Key = t.Get(r, "MotherQuest_ID"), Title = title,
                        StartScript = t.Get(r, "Start Script"), EndScript = t.Get(r, "End Script"),
                        ReqLevel = Mathf.Max(1, lvl),
                    };
                    q.Goal = goals[quests.Count % goals.Length];
                    int tier = 1 + q.ReqLevel / 15;
                    q.GoalCount = q.Goal switch { QuestEvent.DistanceKm => 5 * tier, QuestEvent.CarBought => 1, _ => Mathf.Min(1 + tier, 6) };
                    q.RewardExp = 800 + 120L * q.ReqLevel;
                    q.RewardMito = 5000 + 900L * q.ReqLevel;
                    quests.Add(q);
                }
                foreach (var q in quests)
                    if (prereqKey.TryGetValue(q.Id, out var key) && keyToId.TryGetValue(key, out int before) && before != q.Id) q.Requires = before;
                return quests;
            }
        }

        static QuestInfo Find(int id) => All.FirstOrDefault(q => q.Id == id);

        /// <summary>
        /// Whether the driver has the mother quest with this MotherQuest_ID ("mq_5th_003") completed: 0x703d20 opens Taipei
        /// (quest manager +0x10e) when mq_5th_003 is in the driver's mother quest list (+0x100).
        /// </summary>
        public static bool CompletedKey(PlayerProfile p, string key)
        {
            if (p == null || string.IsNullOrEmpty(key) || p.CompletedQuests.Count == 0) return false;
            try
            {
                var q = All.FirstOrDefault(x => string.Equals((x.Key ?? "").Trim(), key, System.StringComparison.OrdinalIgnoreCase));
                return q != null && p.CompletedQuests.Contains(q.Id);
            }
            catch { return false; }   // no game data (tests without the client files)
        }

        public static void ShowBoard()
        {
            var rows = new List<ListWindow.Row>();
            foreach (var a in P.ActiveQuests.ToList())
            {
                var q = Find(a.QuestId);
                if (q == null) continue;
                bool done = a.Counter >= q.GoalCount;
                rows.Add(new ListWindow.Row($"[Active] {q.Title}", $"{q.GoalText}: {Mathf.Min(a.Counter, q.GoalCount)}/{q.GoalCount}", done ? "Complete" : "Give up", () =>
                {
                    ListWindow.CloseOpen();
                    // look the entry up again: an online reply may have replaced the profile since the board opened
                    var live = P.ActiveQuests.FirstOrDefault(x => x.QuestId == q.Id);
                    if (live == null) { ShowBoard(); return; }
                    if (done) Complete(q, live); else { P.ActiveQuests.Remove(live); P.Save(); ShowBoard(); }
                }));
            }
            foreach (var q in All)
            {
                if (P.CompletedQuests.Contains(q.Id) || P.ActiveQuests.Any(a => a.QuestId == q.Id)) continue;
                bool can = CanAccept(q);
                var qq = q;
                rows.Add(new ListWindow.Row(q.Title, $"Lv {q.ReqLevel}   {q.GoalText}   reward EXP {q.RewardExp:N0}, Mito {q.RewardMito:N0}", "Accept", () =>
                {
                    ListWindow.CloseOpen();
                    ShowScript(qq.StartScript, qq.Title, accept: true, onAccept: () => Accept(qq));
                }, can));
                if (rows.Count > 40) break;
            }
            ListWindow.Show("Quests", rows, $"{P.CompletedQuests.Count} completed   Active quests progress as you play.");
        }

        /// <summary>Level reached and the previous quest of the story done (the server applies the same rule).</summary>
        public static bool CanAccept(QuestInfo q) => P.Level >= q.ReqLevel && (q.Requires < 0 || P.CompletedQuests.Contains(q.Requires));

        static void Accept(QuestInfo q)
        {
            if (!CanAccept(q)) { UiPopup.Message(q.Requires >= 0 && !P.CompletedQuests.Contains(q.Requires) ? "Finish the previous story quest first." : $"Needs level {q.ReqLevel}."); return; }
            if (P.ActiveQuests.Any(a => a.QuestId == q.Id)) return;
            if (P.ActiveQuests.Count >= 3) { UiPopup.Message("You can have at most three active quests."); return; }
            P.ActiveQuests.Add(new QuestProgress { QuestId = q.Id });
            P.Save();
            UiPopup.Message($"Quest accepted: {q.Title}\n{q.GoalText}");
        }

        static void Complete(QuestInfo q, QuestProgress a)
        {
            var data = new System.Collections.Generic.Dictionary<string, object> { ["questId"] = q.Id, ["reqLevel"] = q.ReqLevel };
            Progress.Reward("quest", data, () =>
            {
                P.ActiveQuests.RemoveAll(x => x.QuestId == q.Id);
                if (!P.CompletedQuests.Contains(q.Id)) P.CompletedQuests.Add(q.Id);
                int before = P.Level;
                P.AddExp(q.RewardExp);
                P.Mito += q.RewardMito;
                return new Progress.Result { Ok = true, Exp = q.RewardExp, Mito = q.RewardMito, LevelUps = P.Level - before };
            }, r => ShowScript(q.EndScript, q.Title, accept: false, onAccept: null, after: () =>
                UiPopup.Message(r.Ok
                    ? $"Quest complete: {q.Title}\nEXP +{r.Exp:N0}   Mito +{r.Mito:N0}" + (r.LevelUps > 0 ? $"\nLevel up! Level {P.Level}" : "")
                    : $"Quest could not be completed: {r.Error}")));
        }

        /// <summary>Called by gameplay systems; advances active quests and announces completion.</summary>
        public static void Notify(QuestEvent e, int amount = 1)
        {
            if (P == null) return;
            bool changed = false;
            foreach (var a in P.ActiveQuests)
            {
                var q = Find(a.QuestId);
                if (q == null || q.Goal != e || a.Counter >= q.GoalCount) continue;
                a.Counter += amount;
                changed = true;
                if (a.Counter >= q.GoalCount) UiPopup.Message($"Quest objective done: {q.Title}\nOpen Quests (Q) to claim your reward.");
            }
            if (changed) P.Save();
        }

        /// <summary>Feeds distance-based quests (call with whole kilometres).</summary>
        public static void TickDistance(float kmDelta)
        {
            if (P == null) return;
            accumulatedKm += kmDelta;
            if (accumulatedKm >= 1f) { int km = Mathf.FloorToInt(accumulatedKm); accumulatedKm -= km; Notify(QuestEvent.DistanceKm, km); }
        }
        static float accumulatedKm;

        // ---------------- operator dialog (original QUESTDIALOG) ----------------
        static List<ScriptPage> pages;
        static int pageIndex;
        static NuiView dialog;
        static RawImage portrait;

        static void ShowScript(string script, string title, bool accept, System.Action onAccept, System.Action after = null)
        {
            pages = NpcScript.Parse(script);
            if (pages.Count == 0)
            {
                if (accept) onAccept?.Invoke(); else after?.Invoke();
                return;
            }
            var ui = UiManager.Instance;
            CloseDialog();   // also forgets the old portrait, which dies with the old dialog
            dialog = ui.Open(600000);
            pageIndex = 0;
            foreach (var id in new[] { "ID_QUESTDIALOG_RECV", "ID_QUESTDIALOG_REWARD", "ID_QUESTDIALOG_PREVNEXT_PREV", "ID_QUESTDIALOG_PREVNEXT_NEXT", "ID_QUESTDIALOG_WHATISUC" })
                dialog.SetVisible(id, false);
            System.Action render = null;
            render = () =>
            {
                var page = pages[pageIndex];
                dialog.SetText("ID_QUESTDIALOG_NAME", page.Speaker ?? title);
                dialog.SetText("ID_QUESTDIALOG_CONTENT_1ST", string.Join("\n", page.Lines));
                dialog.SetText("ID_QUESTDIALOG_CONTENT_2ND", "");
                dialog.SetText("ID_QUESTDIALOG_CONTENT_3RD", "");
                bool last = pageIndex == pages.Count - 1;
                dialog.SetVisible("ID_QUESTDIALOG_PREV", pageIndex > 0);
                dialog.SetVisible("ID_QUESTDIALOG_NEXT", !last);
                dialog.SetVisible("ID_QUESTDIALOG_ACCEPT", last && accept);
                dialog.SetVisible("ID_QUESTDIALOG_DENY", last && accept);
                dialog.SetVisible("ID_QUESTDIALOG_DONE", last && !accept);
                ShowPortrait(page.Portrait);
            };
            render();
            ui.On("ID_QUESTDIALOG_NEXT", _ => { pageIndex = Mathf.Min(pageIndex + 1, pages.Count - 1); render(); });
            ui.On("ID_QUESTDIALOG_PREV", _ => { pageIndex = Mathf.Max(pageIndex - 1, 0); render(); });
            ui.On("ID_QUESTDIALOG_ACCEPT", v => { CloseDialog(); onAccept?.Invoke(); });
            ui.On("ID_QUESTDIALOG_DENY", v => CloseDialog());
            ui.On("ID_QUESTDIALOG_DONE", v => { CloseDialog(); after?.Invoke(); });
        }

        static void ShowPortrait(string name)
        {
            if (!dialog) return;
            if (!portrait)
            {
                var rt = NuiView.NewRect("Portrait", dialog.transform);
                rt.anchorMin = rt.anchorMax = new Vector2(0, 0);
                rt.pivot = new Vector2(0, 0);
                rt.anchoredPosition = new Vector2(10, 10);
                rt.sizeDelta = new Vector2(300, 420);
                rt.SetAsFirstSibling();
                portrait = rt.gameObject.AddComponent<RawImage>();
                portrait.raycastTarget = false;
            }
            var tex = string.IsNullOrEmpty(name) ? null : UiAssets.Texture(name + ".dds");
            portrait.texture = tex;
            portrait.enabled = tex;
            portrait.uvRect = new Rect(0, 1, 1, -1);
            if (tex) ((RectTransform)portrait.transform).sizeDelta = new Vector2(420f * tex.width / Mathf.Max(1, tex.height), 420);
        }

        static void CloseDialog()
        {
            if (dialog && UiManager.Instance) UiManager.Instance.Close(dialog);
            dialog = null; portrait = null;
        }
    }

    /// <summary>
    /// QUESTPLAYINFO 720000, the running quest's bar (0x5592d0 on message 0x2001, handler 0x55c100): CONTENT is the current
    /// step's text, or 10178 "Mission Completed." once done; RETRY (720005) asks 10125 "Would you like to attempt this
    /// mission again?" (done: 10193); VIEWQUEST (720006) opens the quest log. Hidden while no quest runs. The remake's main
    /// quests have one counted objective, so CONTENT shows "(n/m) title: objective" and RETRY starts the count again. The
    /// step timer QUESTPLAYINFO 730000 (0x5559b0) and the crash / speed / follow panels 740000-770000 belong to the step
    /// quests of World/Data/QuestClient.tdf (StepQuestRunner; MISSIONS.md section 11).
    /// </summary>
    public sealed class QuestPlayInfo : MonoBehaviour
    {
        public const int DialogId = 720000;
        public static QuestPlayInfo Instance { get; private set; }
        NuiView view;
        float poll;
        int shownQuest = -1;

        public static QuestPlayInfo Attach(CitySession session)
        {
            if (!session) return null;
            if (!session.TryGetComponent(out QuestPlayInfo q)) q = session.gameObject.AddComponent<QuestPlayInfo>();
            Instance = q;
            return q;
        }

        public NuiView View => view;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (view && UiManager.Instance) UiManager.Instance.Close(view);
        }

        /// <summary>The bar's text for the first active quest: "(counter/goal) title: objective" or 10178 when done.</summary>
        public static string Content(QuestInfo q, QuestProgress a)
        {
            if (q == null || a == null) return "";
            if (a.Counter >= q.GoalCount) return GameTables.Text("10178");
            return $"({Mathf.Min(a.Counter, q.GoalCount)}/{q.GoalCount}) {q.Title}: {q.GoalText}";
        }

        /// <summary>The bar's text for a step quest: the running step's text, 10178 once every step is done.</summary>
        public static string StepContent(StepQuest q, QuestProgress run)
        {
            if (q == null || run == null) return "";
            if (run.Counter > 0) return GameTables.Text("10178");
            // 11 steps have no text in the table ("0"): the quest's title stands in
            if (run.Step < 0 || run.Step >= q.Steps.Count) return q.Title;
            string text = q.Steps[run.Step].Text;
            return $"({run.Step + 1}/{q.Steps.Count}) {(string.IsNullOrEmpty(text) ? q.Title : text)}";
        }

        void Update()
        {
            poll -= Time.unscaledDeltaTime;
            if (poll > 0) return;
            poll = 0.25f;
            var p = PlayerProfile.Current;
            var ui = UiManager.Instance;
            if (!ui) return;
            var a = p != null && p.ActiveQuests.Count > 0 ? p.ActiveQuests[0] : null;
            var q = a != null ? QuestTracker.All.FirstOrDefault(x => x.Id == a.QuestId) : null;
            // a running step quest (QuestClient.tdf) owns the bar: its step's text, as 0x5592d0 shows it
            var step = StepQuests.Running(p);
            // the delivery's bar (790000 / 800000) takes the same place while a job runs
            bool show = (q != null || step != null) && !DeliveryService.Active && !UndercityStage.Active && !(GameFlow.Instance && GameFlow.Instance.InParkingLot);
            if (!show) { if (view) ui.Close(view); view = null; shownQuest = -1; return; }
            if (!view || !ui.IsOpen(view))
            {
                view = ui.Open(DialogId);
                if (!view) return;
                ui.On("ID_QUESTPLAYINFO_VIEWQUEST", _ => StepQuestRunner.ShowLog());
                ui.On("ID_QUESTPLAYINFO_RETRY", _ =>
                {
                    var pp = PlayerProfile.Current;
                    var sq = StepQuests.Running(pp);
                    if (sq != null)
                    {
                        // 0x554580: done -> 10193, else 10125 and the quest starts over
                        if (pp.StepQuest.Counter > 0) { UiPopup.Message(GameTables.Text("10193")); return; }
                        MissionUi.Confirm(MissionText.Get("10125"), StepQuestRunner.Retry);
                        return;
                    }
                    var live = pp != null && pp.ActiveQuests.Count > 0 ? pp.ActiveQuests[0] : null;
                    var info = live != null ? QuestTracker.All.FirstOrDefault(x => x.Id == live.QuestId) : null;
                    if (info == null) return;
                    if (live.Counter >= info.GoalCount) { UiPopup.Message(GameTables.Text("10193")); return; }
                    MissionUi.Confirm(MissionText.Get("10125"), () => { live.Counter = 0; pp.Save(); poll = 0; });
                });
            }
            if (step != null)
            {
                shownQuest = step.Number;
                view.SetText("ID_QUESTPLAYINFO_CONTENT", StepContent(step, p.StepQuest));
                return;
            }
            shownQuest = q.Id;
            view.SetText("ID_QUESTPLAYINFO_CONTENT", Content(q, a));
        }
    }
}
