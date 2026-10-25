using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Battle Zone spectator's Pick'em, VIEWING 1980000 "Display Gotcha" (DriftCity_RE\EVENTS.md section 2), where the
    /// Slot Tickets Grade 1-10 (gacha_observe_vN, the slot machine GACHA 4100000) come from.
    ///
    /// Client [exact]: spectator object game+0x54 (vtable 0x87d7ac; dialog handler 0x75bb90, per frame 0x75d830). At the start
    /// of a watched race (spectators only), with 4+ racers, before the leader reaches half distance and while holding a
    /// Pick'em Ticket (gacha_observe), USEITEMPROMPT asks for 8 s (gauge); an AI race refuses with 11398. YES sends 0x834
    /// "init"; the server's 0x835 gives one symbol per finishing place (1 gold, 2 silver, 3 bronze, else bomb) and the first
    /// three racers of the list are picked (0x836 {id, on}); IDSELECTCHECK01-16 beside the VIEWING 1960000 rows change the
    /// picks (at most 3, locked at half race); GOLD/SILVER/BRONZEnn mark the places' symbols; SYMBOL_BG runs three reels
    /// (the symbols under the picked racers, 300 px/s) and MULTIPLY_Xn shows the server's multiplier (0x838) with a 0.3 s pop;
    /// GAUGEBG shows the leader's progress (CAR1 before half, CAR2 after) and the gates. HELPBUTTON toggles HELP.
    /// Remake rules (the server decided): the symbols are max(1, n/5) of each kind shuffled over the places; the ticket grade
    /// = 1 + 3 per gold, 2 per silver, 1 per bronze under the picks' final places (max 10); online the server keeps the
    /// picks and grants the ticket (pickem.start / pick / finish, src/events.js); offline (AI rooms, the remake's stand-in)
    /// the same rule runs locally.
    /// Hook: the spectated field comes from PvpSpectate (its viewer's order, read by reflection until PvpSpectate exposes it).
    /// </summary>
    public static class PickEm
    {
        public const int Dialog = 1980000;
        public const string Ticket = "gacha_observe";
        public const float PromptSeconds = 8f;
        static readonly int[] Points = { 0, 3, 2, 1 };
        static readonly string[] SymbolIds = { "BOMB", "GOLD", "SILVER", "BRONZE" };
        const string V = "ID_VIEWING_GACHA_";

        static PlayerProfile P => PlayerProfile.Current;
        static NuiView view;
        static UiManager hooked;
        static float promptT = -1f;        // seconds the prompt has been up (-1 none, >= PromptSeconds done)
        static bool active, finished, requesting;
        static int[] symbols = new int[0];
        static readonly List<string> picks = new List<string>();
        static int roomId;
        static int lastMultiplier = 1;
        static float popT = 1f;
        static readonly GameObject[] reels = new GameObject[3];
        static readonly int[] reelSym = { -2, -2, -2 };

        public static NuiView View => view;
        public static bool PromptShown => view && view.Control(V + "USEITEMPROMPT_BG") && view.Control(V + "USEITEMPROMPT_BG").gameObject.activeSelf;
        public static bool Active => active;
        public static IReadOnlyList<int> Symbols => symbols;
        public static IReadOnlyList<string> Picks => picks;
        public static int LastGrade { get; private set; }
        /// <summary>Tests and the lead's hook may pass the field; else PvpSpectate's viewer is read.</summary>
        public static Func<List<PvpSpectate.Driver>> Field;

        // ---------------- the runner ----------------
        sealed class Runner : MonoBehaviour { void Update() => Tick(Time.unscaledDeltaTime * EventTime.Scale); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Ensure()
        {
            if (UnityEngine.Object.FindAnyObjectByType<Runner>()) return;
            var go = new GameObject("Pick'em");
            go.AddComponent<Runner>();
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
        }

        /// <summary>The watched field in race order (PvpSpectate's viewer).</summary>
        public static List<PvpSpectate.Driver> Order()
        {
            if (Field != null) return Field() ?? new List<PvpSpectate.Driver>();
            return PvpSpectate.Order;
        }

        static bool Watching => Field != null || PvpSpectate.Watching;
        static float Lead(List<PvpSpectate.Driver> order) => order.Count == 0 ? 0f : order.Max(d => d.Done ? 1f : d.Progress);

        static void Tick(float dt)
        {
            if (!Watching) { if (view || active) Reset(); return; }
            var order = Order();
            if (!view) Open();
            if (!view) return;
            float lead = Lead(order);
            if (!active && promptT < PromptSeconds) Prompt(order, lead, dt);
            if (active) Draw(order, lead, dt);
            DrawGauge(lead);
            if (active && !finished && order.Count > 0 && order.All(d => d.Done)) Finish(order);
        }

        static void Open()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            view = ui.Open(Dialog);
            if (!view) return;
            EventUi.HideAll(view, id => false);
            Banner(false);   // the 1980102 banner (unnamed) shows with the start (0x835)
            promptT = -1f; active = finished = requesting = false; picks.Clear(); symbols = new int[0]; lastMultiplier = 1; LastGrade = 0;
            roomId = PvpWaitRoom.Room != null && PvpWaitRoom.Room.Online ? PvpWaitRoom.Room.Id : 0;
            for (int k = 0; k < 3; k++) { reels[k] = null; reelSym[k] = -2; reelState[k] = null; }
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On(V + "USEITEMPROMPT_YES", _ => Accept());
            ui.On(V + "USEITEMPROMPT_NO", _ => Decline());
            ui.On(V + "USEITEMPROMPT_CLOSE", _ => Decline());
            ui.On(V + "HELPBUTTON", v => v.SetVisible(V + "HELP", !(v.Control(V + "HELP") && v.Control(V + "HELP").gameObject.activeSelf)));
            for (int i = 1; i <= 16; i++) { int row = i - 1; ui.On($"{V}SYMBOL_IDSELECTCHECK{i:00}", _ => TogglePick(row)); }
        }

        public static void Reset()
        {
            var ui = UiManager.Instance;
            if (view && ui) ui.Close(view);
            view = null; active = false; promptT = -1f; picks.Clear();
        }

        // ---------------- the prompt (0x75d830) ----------------
        static void Prompt(List<PvpSpectate.Driver> order, float lead, float dt)
        {
            if (promptT < 0)
            {
                // racers > 3, before half race, holding a ticket; an AI race online: 11398 and no prompt
                if (order.Count < 4 || lead >= 0.5f || P == null || P.Count(Ticket) <= 0) return;
                if (OnlineSession.Active && roomId > 0 && AiField())
                {
                    ChatBox.System(EventUi.T("11398", "Display Gotcha is unavailable during AI Battle play."));
                    promptT = PromptSeconds;
                    return;
                }
                promptT = 0f;
                ShowPrompt(true);
                EventUi.Sfx("SID_SYSWIN_CREATE");
            }
            promptT += dt;
            var g = view.Control(V + "USEITEMPROMPT_GAUGE");
            if (g) g.transform.localScale = new Vector3(Mathf.Clamp01(1f - promptT / PromptSeconds), 1f, 1f);
            if (promptT >= PromptSeconds || lead >= 0.5f) { promptT = PromptSeconds; ShowPrompt(false); }
        }

        static bool AiField() => PvpWaitRoom.Room != null && PvpWaitRoom.Room.Members.Any(m => m.Ai);

        static void Banner(bool on)
        {
            if (!view) return;
            foreach (var c in view.GetComponentsInChildren<NuiControlView>(true)) if (string.IsNullOrEmpty(c.Data.IdName)) c.gameObject.SetActive(on && c.Data.Id == 1980102);
        }

        static void ShowPrompt(bool on)
        {
            if (!view) return;
            foreach (var id in new[] { "BG", "GAUGE", "YES", "NO", "CLOSE" }) view.SetVisible(V + "USEITEMPROMPT_" + id, on);
        }

        static void Decline() { promptT = PromptSeconds; ShowPrompt(false); }

        /// <summary>YES (0x834 "init"): spends the ticket, the symbols per place, the first three racers picked.</summary>
        public static void Accept()
        {
            if (active || requesting) return;
            Decline();
            var order = Order();
            requesting = true;
            EventNet.Request("pickem.start", new Dictionary<string, object> { ["room"] = roomId }, () => StartOffline(P, order.Select(d => d.Name).ToList(), EventNet.Rng), r =>
            {
                requesting = false;
                if (!r.Ok) { ChatBox.System(r.Error); return; }
                symbols = (r.Data.L("symbols") ?? new List<object>()).Select(x => Convert.ToInt32(x)).ToArray();
                picks.Clear();
                picks.AddRange((r.Data.L("picks") ?? new List<object>()).Select(x => x.ToString()));
                active = true; finished = false; lastMultiplier = 1; popT = 1f;
                if (view) { view.SetVisible(V + "SYMBOL_BG", true); view.SetVisible(V + "HELPBUTTON", true); Banner(true); }
                EventUi.Sfx("SID_ITEM_INSTALL");
            });
        }

        /// <summary>Offline twin of pickem.start: the ticket, the layout, the first three picks.</summary>
        public static EventReply StartOffline(PlayerProfile p, List<string> names, System.Random rng)
        {
            if (names.Count < 4) return EventReply.Fail("Pick'em needs four or more racers.");
            if (!EventNet.Take(p, Ticket)) return EventReply.Fail("You don't have a Pick'em Ticket.");
            var r = new EventReply { Ok = true };
            r.Data["symbols"] = Layout(names.Count, rng).Select(x => (object)(double)x).ToList();
            r.Data["picks"] = names.Take(3).Cast<object>().ToList();
            return r;
        }

        /// <summary>max(1, n/5) each of gold, silver, bronze; the rest bombs; shuffled (src/events.js pickemLayout).</summary>
        public static int[] Layout(int n, System.Random rng)
        {
            int each = Math.Max(1, n / 5);
            var s = new List<int>();
            for (int k = 1; k <= 3; k++) for (int i = 0; i < each; i++) s.Add(k);
            while (s.Count < n) s.Add(0);
            var a = s.Take(n).ToArray();
            for (int i = a.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
            return a;
        }

        /// <summary>1 + the symbols' points under the picks' places (0 = DNF), max 10.</summary>
        public static int Grade(int[] syms, IList<string> picked, Func<string, int> placeOf)
        {
            int pts = 0;
            foreach (var n in picked) { int pl = placeOf(n); if (pl > 0 && pl <= syms.Length) pts += Points[Mathf.Clamp(syms[pl - 1], 0, 3)]; }
            return Mathf.Clamp(1 + pts, 1, 10);
        }

        // ---------------- picks (IDSELECTCHECK, 0x836) ----------------
        static void TogglePick(int row)
        {
            if (!active || finished) return;
            var order = Order();
            if (row >= order.Count || Lead(order) >= 0.5f) return;
            string name = order[row].Name;
            bool on = !picks.Contains(name);
            if (on && picks.Count >= 3) return;   // a 4th pick is refused (the box unticks on the next frame)
            if (on) picks.Add(name); else picks.Remove(name);
            if (OnlineSession.Active && roomId > 0)
                EventNet.Request("pickem.pick", new Dictionary<string, object> { ["name"] = name, ["on"] = on }, () => new EventReply { Ok = true }, r =>
                {
                    if (r.Ok && r.Data.L("picks") is List<object> l) { picks.Clear(); picks.AddRange(l.Select(x => x.ToString())); }
                });
        }

        // ---------------- per frame while active ----------------
        static void Draw(List<PvpSpectate.Driver> order, float lead, float dt)
        {
            bool locked = lead >= 0.5f;
            for (int r = 0; r < 16; r++)
            {
                string n = (r + 1).ToString("00");
                bool used = r < order.Count;
                int sym = used && r < symbols.Length ? symbols[r] : 0;
                view.SetVisible(V + "SYMBOL_GOLD" + n, used && sym == 1);
                view.SetVisible(V + "SYMBOL_SILVER" + n, used && sym == 2);
                view.SetVisible(V + "SYMBOL_BRONZE" + n, used && sym == 3);
                bool picked = used && picks.Contains(order[r].Name);
                var cb = view.Control(V + "SYMBOL_IDSELECTCHECK" + n);
                if (cb) { cb.gameObject.SetActive(used); cb.SetChecked(picked); cb.Interactable = !locked; }
                view.SetVisible(V + "SYMBOL_IDSELECTBG" + n, picked);
            }
            // the reels: they spin (300 px/s) before half race and land on the symbol under each picked racer's row
            for (int k = 0; k < 3; k++)
            {
                int row = k < picks.Count ? order.FindIndex(d => d.Name == picks[k]) : -1;
                int sym = row >= 0 && row < symbols.Length ? symbols[row] : 0;
                SetReel(k, sym);
                SpinReel(k, sym, !locked && row >= 0, dt);
            }
            int mult = Grade(symbols, picks, name => { int i = order.FindIndex(d => d.Name == name); return i < 0 ? 0 : i + 1; });
            if (mult != lastMultiplier) { lastMultiplier = mult; popT = 0f; }
            popT += dt;
            for (int x = 2; x <= 10; x++)
            {
                var c = view.Control(V + "MULTIPLY_X" + x);
                if (!c) continue;
                c.gameObject.SetActive(x == mult);
                // the 0.3 s pop: +20 px down to the normal size
                float s = popT < 0.3f ? 1f + 0.25f * (1f - popT / 0.3f) : 1f;
                c.transform.localScale = new Vector3(s, s, 1f);
            }
        }

        /// <summary>A reel: a clipped window over a strip of the four symbols (bomb, gold, silver, bronze, then bomb again).</summary>
        sealed class Reel { public RectTransform Strip; public float Step, Offset, Speed; public int Target = -1; }
        static readonly Reel[] reelState = new Reel[3];
        /// <summary>The reels' spin speed (0x75d830: 300 px a second) and the symbol each one shows (tests).</summary>
        public const float ReelSpeed = 300f;
        public static int ReelShows(int k) { var r = k >= 0 && k < 3 ? reelState[k] : null; return r == null || r.Step <= 0 ? -1 : Mathf.RoundToInt(r.Offset / r.Step) % 4; }
        public static bool ReelSpinning(int k) { var r = k >= 0 && k < 3 ? reelState[k] : null; return r != null && r.Speed > 1f; }

        static void SetReel(int k, int sym)
        {
            if (reels[k]) { reelSym[k] = sym; return; }
            reelSym[k] = sym;
            var bg = view.Control(V + "SYMBOL_BG");
            var first = view.Control(V + "SYMBOL_" + SymbolIds[0]);
            if (!bg || !first) return;
            var size = ((RectTransform)first.transform).sizeDelta;
            if (size.x < 4f || size.y < 4f) size = new Vector2(50, 50);
            var win = NuiView.NewRect("pickem reel " + k, bg.transform.parent);
            win.anchorMin = win.anchorMax = ((RectTransform)bg.transform).anchorMin;
            win.pivot = ((RectTransform)first.transform).pivot;
            win.sizeDelta = size;
            win.anchoredPosition = ((RectTransform)bg.transform).anchoredPosition + new Vector2(10 + 60 * k, -15);
            win.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
            var strip = NuiView.NewRect("strip", win);
            strip.anchorMin = strip.anchorMax = new Vector2(0.5f, 1f); strip.pivot = new Vector2(0.5f, 1f);
            strip.sizeDelta = new Vector2(size.x, size.y * 5);
            for (int i = 0; i < 5; i++)
            {
                var src = view.Control(V + "SYMBOL_" + SymbolIds[i % 4]);
                if (!src) continue;
                var cell = UnityEngine.Object.Instantiate(src.gameObject, strip);
                cell.SetActive(true);
                foreach (var nc in cell.GetComponentsInChildren<NuiControlView>(true)) UnityEngine.Object.Destroy(nc);
                var rt = (RectTransform)cell.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f); rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0, -size.y * i);
            }
            reels[k] = win.gameObject;
            reelState[k] = new Reel { Strip = strip, Step = size.y, Speed = ReelSpeed };
        }

        /// <summary>Spins reel k while <paramref name="spin"/>, else slows it onto <paramref name="sym"/>'s cell.</summary>
        static void SpinReel(int k, int sym, bool spin, float dt)
        {
            var r = reelState[k];
            if (r == null || !r.Strip) return;
            float cycle = r.Step * 4f;
            if (spin) { r.Speed = ReelSpeed; r.Target = -1; }
            else
            {
                float target = Mathf.Clamp(sym, 0, 3) * r.Step;
                float ahead = Mathf.Repeat(target - r.Offset, cycle);
                if (r.Target != sym) { r.Target = sym; if (ahead < r.Step * 0.5f && r.Speed < 1f) ahead = 0f; }
                // slow down over the last cell, stop on it
                r.Speed = ahead <= 0.5f ? 0f : Mathf.Clamp(ahead * 4f, 40f, ReelSpeed);
                if (r.Speed * dt >= ahead) { r.Offset = target; r.Speed = 0f; }
            }
            r.Offset = Mathf.Repeat(r.Offset + r.Speed * dt, cycle);
            r.Strip.anchoredPosition = new Vector2(0f, r.Offset);
        }

        static void DrawGauge(float lead)
        {
            if (!view) return;
            var bg = view.Control(V + "GAUGEBG");
            if (!bg) return;
            bg.gameObject.SetActive(active);
            view.SetVisible(V + "GAUGE_CAR1", active && lead < 0.5f);
            view.SetVisible(V + "GAUGE_CAR2", active && lead >= 0.5f);
            var car = view.Control(lead < 0.5f ? V + "GAUGE_CAR1" : V + "GAUGE_CAR2");
            var b = (RectTransform)bg.transform;
            if (car && active)
            {
                var rt = (RectTransform)car.transform;
                rt.anchoredPosition = new Vector2(b.anchoredPosition.x - 0x23 + b.sizeDelta.x * Mathf.Clamp01(lead), rt.anchoredPosition.y);
            }
        }

        // ---------------- the end of the race ----------------
        static void Finish(List<PvpSpectate.Driver> order)
        {
            finished = true;
            var final = order.Select(d => (d.Name, d.Place)).ToList();
            EventNet.Request("pickem.finish", new Dictionary<string, object> { ["room"] = roomId }, () => FinishOffline(P, final), r =>
            {
                if (!r.Ok) { ChatBox.System(r.Error); return; }
                LastGrade = r.Data.I("grade");
                EventNet.Log(r.Grants);
                EventUi.Sfx("SID_ITEM_DROP_GET1");
            });
        }

        /// <summary>Offline twin of pickem.finish: the grade from the final places and its Slot Ticket.</summary>
        public static EventReply FinishOffline(PlayerProfile p, List<(string name, int place)> final)
        {
            int grade = Grade(symbols, picks, n => final.FirstOrDefault(x => x.name == n).place);
            var g = new List<RewardGrant> { new RewardGrant { Kind = GrantKind.Item, ItemId = "gacha_observe_v" + grade, Count = 1 } };
            EventNet.Apply(p, g);
            var r = new EventReply { Ok = true, Grants = g };
            r.Data["grade"] = (double)grade;
            return r;
        }
    }
}
