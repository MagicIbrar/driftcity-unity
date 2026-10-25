using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The post-race reward pick PVP_REWARD 4310000 (ui +0x418, 0x58bf70..0x58d9b0; shown by the race end 0x72ea60 in the
    /// EXP arenas, not to spectators, not in tournament channels): 16 face-down cards (SLOT01..16), the cursor (SEL) starts
    /// on a random one of the middle four, every driver picks one (0x1e3) and the cards turn over as the server reveals
    /// them (0x1e4: picker's name and the reward: 1 an item's name, 3 11197 "%d Mito Received", 4 11199 mileage, 5 11198
    /// "%d EXP Received"). The traffic light runs blue 2.5 s, yellow to 5 s, then red; DESC 11799 warns that the server
    /// picks for whoever has not. OK (4310073) unlocks once all 16 are shown and goes on to the result screens. Arrow keys
    /// move the cursor (4 x 4, or 8 x 2 with globalcontent PVPReward8X2), Enter / Space picks.
    /// The cards come from init\rewardgroup.xlt rw_battle_low / _mid / _high (remake: 2X-3X / 4X-6X / 8X arenas).
    /// </summary>
    public static class PvpRewardPick
    {
        public const int Dialog = 4310000, Cards = 16;
        public const float OfflineSeconds = 10f;

        public sealed class Card { public int Type; public int Amount; public string Item; public string Picker = ""; public bool Shown; }

        static NuiView view;
        static Runner runner;
        static Card[] cards;
        static int cursor, myPick = -1;
        static float openedAt;
        static Action done;
        static bool online, closing;
        static List<string> offlinePickers;
        static UiManager hooked;

        public static bool IsOpen => view;
        public static NuiView View => view;
        public static IReadOnlyList<Card> Shown => cards;
        public static int MyPick => myPick;
        public static bool AllShown => cards != null && cards.All(c => c.Shown);

        sealed class Runner : MonoBehaviour { void Update() => Tick(); }

        static bool Wide => Pvp.Config("PVPReward8X2", 1) != 0;

        /// <summary>The group of an arena (remake rule).</summary>
        public static string Group(int arena) => arena >= 4 ? "rw_battle_high" : arena >= 2 ? "rw_battle_mid" : "rw_battle_low";

        /// <summary>Offline: 16 cards drawn from the arena's group by probability (the server's draw online).</summary>
        public static Card[] Draw(int arena)
        {
            var g = Pvp.RewardGroup(Group(arena));
            var outp = new Card[Cards];
            float total = g.Sum(r => Mathf.Max(0f, r.Prob));
            for (int i = 0; i < Cards; i++)
            {
                if (g.Count == 0 || total <= 0f) { outp[i] = i % 2 == 0 ? new Card { Type = 3, Amount = 300 } : new Card { Type = 5, Amount = 100 }; continue; }
                float x = UnityEngine.Random.value * total;
                var row = g[g.Count - 1];
                foreach (var r in g) { x -= Mathf.Max(0f, r.Prob); if (x < 0f) { row = r; break; } }
                outp[i] = row.Type == "Exp" ? new Card { Type = 5, Amount = row.Count } : row.Type == "Mito" ? new Card { Type = 3, Amount = row.Count } : new Card { Type = 1, Item = row.Item, Amount = Mathf.Max(1, row.Count) };
            }
            return outp;
        }

        /// <summary>Opens the pick. Offline the cards are known (drawn here) and the AI drivers pick too; online they come from race.picked.</summary>
        public static void Open(int arena, IEnumerable<string> aiPickers, Action onDone)
        {
            var ui = UiManager.Instance;
            done = onDone;
            if (!ui) { Finish(); return; }
            Close(false);
            online = OnlineSession.Active;
            cards = online ? Enumerable.Range(0, Cards).Select(_ => new Card()).ToArray() : Draw(arena);
            foreach (var c in cards) c.Shown = false;
            offlinePickers = (aiPickers ?? Enumerable.Empty<string>()).ToList();
            myPick = -1; closing = false;
            if (hooked != ui)
            {
                hooked = ui;
                for (int i = 0; i < Cards; i++) { int s = i; ui.On($"ID_PVP_REWARD_SLOT{i + 1:00}", _ => Pick(s)); }
            }
            view = ui.Open(Dialog);
            if (!view) { Finish(); return; }
            // the popup's base (NATION F=10) lies under the cards in the client: it shares the cards' origin here
            var slot1 = view.Control("ID_PVP_REWARD_SLOT01");
            var baseCtl = view.Control("ID_PVP_REWARD_BASE");
            if (slot1 && baseCtl && baseCtl.transform.parent != slot1.transform.parent)
            {
                var rt = (RectTransform)baseCtl.transform;
                var pos = rt.anchoredPosition;
                rt.SetParent(slot1.transform.parent, false);
                var srt = (RectTransform)slot1.transform;
                rt.anchorMin = srt.anchorMin; rt.anchorMax = srt.anchorMax; rt.pivot = srt.pivot;
                rt.anchoredPosition = pos;
                rt.SetAsFirstSibling();
            }
            int[] middle = { 4, 5, 9, 10 };
            cursor = middle[UnityEngine.Random.Range(0, middle.Length)];
            openedAt = Time.time;
            view.SetText("ID_PVP_REWARD_DESC", Pvp.Text("11799"));
            var ok = Pvp.ById(view, 4310073);
            if (ok)
            {
                ok.gameObject.SetActive(true);
                ok.Interactable = false;
                var trigger = ok.gameObject.GetComponent<UnityEngine.EventSystems.EventTrigger>() ?? ok.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                var entry = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerClick };
                entry.callback.AddListener(_ => { if (AllShown) Confirm(); });
                trigger.triggers.Add(entry);
            }
            if (!runner) runner = new GameObject("PvP Reward Pick").AddComponent<Runner>();
            if (!online) runner.StartCoroutine(OfflineRivals());
            Refresh();
        }

        /// <summary>A card (0x58ca80): once; online the server answers with the reward.</summary>
        public static void Pick(int slot)
        {
            if (!view || myPick >= 0 || slot < 0 || slot >= Cards || cards[slot].Shown) return;
            cursor = slot;
            if (online)
            {
                myPick = slot;
                OnlineSession.Instance.Net.Request("race.pick", new Dictionary<string, object> { ["slot"] = slot }, r =>
                {
                    if (!r.B("ok")) { myPick = -1; if (view) UiPopup.Message(r.S("error")); return; }
                    var c = r.O("card");
                    if (c != null) Reveal(c.I("slot"), PlayerProfile.Current?.Name ?? "", c.I("type"), c.I("amount"), c.S("item"));
                });
                Refresh();
                return;
            }
            myPick = slot;
            Grant(cards[slot]);
            cards[slot].Picker = PlayerProfile.Current?.Name ?? "Driver";
            cards[slot].Shown = true;
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RESULT");
            Refresh();
        }

        /// <summary>race.picked: cards turned over (done = every card).</summary>
        internal static void OnPicked(Dictionary<string, object> m)
        {
            if (cards == null) return;
            foreach (var o in (m.L("picks") ?? new List<object>()).OfType<Dictionary<string, object>>())
                Reveal(o.I("slot"), o.S("name"), o.I("type"), o.I("amount"), o.S("item"));
        }

        static void Reveal(int slot, string name, int type, int amount, string item)
        {
            if (cards == null || slot < 0 || slot >= Cards) return;
            var c = cards[slot];
            c.Type = type; c.Amount = amount; c.Item = item; c.Picker = name ?? ""; c.Shown = true;
            if (!string.IsNullOrEmpty(name) && name == PlayerProfile.Current?.Name) myPick = slot;
            Refresh();
        }

        static void Grant(Card c)
        {
            var p = PlayerProfile.Current;
            if (p == null) return;
            int before = p.Level;
            if (c.Type == 3) p.Mito += c.Amount;
            else if (c.Type == 5) p.AddExp(c.Amount);
            else if (c.Type == 1 && !string.IsNullOrEmpty(c.Item)) p.Give(c.Item, c.Amount);
            p.Save();
            GameFlow.Instance?.RefreshHud();
            Net.ChatBox.System(CardText(c));
            if (p.Level > before) { Net.ChatBox.System("Congratulations! You have leveled up!"); GameEffects.LevelUp(); }
        }

        public static string CardText(Card c)
        {
            switch (c.Type)
            {
                case 3: return Pvp.Text("11197", c.Amount);
                case 4: return Pvp.Text("11199", c.Amount);
                case 5: return Pvp.Text("11198", c.Amount);
                default: return string.IsNullOrEmpty(c.Item) ? "" : EventItems.Name(c.Item) + (c.Amount > 1 ? $" x{c.Amount}" : "");
            }
        }

        /// <summary>Offline: each AI driver picks a free card after a moment; at the time limit the player's card is drawn.</summary>
        static IEnumerator OfflineRivals()
        {
            foreach (var name in offlinePickers.ToList())
            {
                yield return new WaitForSeconds(UnityEngine.Random.Range(0.4f, 1.6f));
                if (!view) yield break;
                var free = Enumerable.Range(0, Cards).Where(i => !cards[i].Shown && i != cursor).ToList();
                if (free.Count == 0) break;
                int s = free[UnityEngine.Random.Range(0, free.Count)];
                cards[s].Picker = name; cards[s].Shown = true;
                Refresh();
            }
        }

        static void Tick()
        {
            if (!view) return;
            Keys();
            float t = Time.time - openedAt;
            if (!online && t >= OfflineSeconds && !AllShown)
            {
                // 11799: the time is up - the player's card is drawn, then every card turns over
                if (myPick < 0) { var free = Enumerable.Range(0, Cards).Where(i => !cards[i].Shown).ToList(); if (free.Count > 0) Pick(free[UnityEngine.Random.Range(0, free.Count)]); }
                foreach (var c in cards) c.Shown = true;
                Refresh();
            }
            if (!online && myPick >= 0 && offlinePickers.All(n => cards.Any(c => c.Picker == n)) && !AllShown)
            {
                foreach (var c in cards) c.Shown = true;
                Refresh();
            }
            // everything shown: OK, or on by itself a few seconds later
            if (AllShown && !closing) { closing = true; runner.StartCoroutine(AutoClose()); }
            DrawLights(t);
        }

        static IEnumerator AutoClose()
        {
            yield return new WaitForSeconds(3f);
            if (view) Confirm();
        }

        static void Keys()
        {
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null || UiPopup.IsOpen) return;
            int cols = Wide ? 8 : 4;
            if (k.leftArrowKey.wasPressedThisFrame && cursor % cols > 0) cursor--;
            if (k.rightArrowKey.wasPressedThisFrame && cursor % cols < cols - 1) cursor++;
            if (k.upArrowKey.wasPressedThisFrame) cursor = (cursor - cols + Cards) % Cards;
            if (k.downArrowKey.wasPressedThisFrame) cursor = (cursor + cols) % Cards;
            if (k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame || k.spaceKey.wasPressedThisFrame) { if (AllShown) Confirm(); else Pick(cursor); }
            if (k.anyKey.wasPressedThisFrame) Refresh();
#endif
        }

        static void DrawLights(float t)
        {
            bool blue = t < 2.5f, yellow = t >= 2.5f && t < 5f, red = t >= 5f;
            view.SetVisible("ID_PVP_REWARD_SIGNAL_B", blue); view.SetVisible("ID_PVP_REWARD_SIGNAL_GREY_B", !blue);
            view.SetVisible("ID_PVP_REWARD_SIGNAL_Y", yellow); view.SetVisible("ID_PVP_REWARD_SIGNAL_GREY_Y", !yellow);
            view.SetVisible("ID_PVP_REWARD_SIGNAL_R", red); view.SetVisible("ID_PVP_REWARD_SIGNAL_GREY_R", !red);
        }

        public static void Refresh()
        {
            if (!view || cards == null) return;
            for (int i = 0; i < Cards; i++)
            {
                string n = (i + 1).ToString("00");
                var c = cards[i];
                var slot = view.Control("ID_PVP_REWARD_SLOT" + n);
                if (slot) { slot.gameObject.SetActive(true); slot.Interactable = !c.Shown && myPick < 0; }
                // a turned card shows its reward's icon (0x58e1b0: the item's icon, i_d_m_1m Mito, i_n_00050 mileage, exp_icon)
                string icon = !c.Shown ? null : c.Type == 3 ? "i_d_m_1m" : c.Type == 4 ? "i_n_00050" : c.Type == 5 ? "exp_icon" : c.Item;
                if (slot) EventUi.Icon(view, "ID_PVP_REWARD_SLOT" + n, icon, true);
                view.SetVisible("ID_PVP_REWARD_SEL" + n, i == cursor && !AllShown);
                view.SetVisible("ID_PVP_REWARD_NAME" + n, c.Shown && c.Picker.Length > 0);
                view.SetText("ID_PVP_REWARD_NAME" + n, c.Picker);
                view.SetVisible("ID_PVP_REWARD_ITEM" + n, c.Shown);
                view.SetText("ID_PVP_REWARD_ITEM" + n, c.Shown ? CardText(c) : "");
            }
            var ok = Pvp.ById(view, 4310073);
            if (ok) ok.Interactable = AllShown;
        }

        static void Confirm() { Close(true); }

        static void Finish() { var d = done; done = null; d?.Invoke(); }

        public static void Close(bool finish)
        {
            var ui = UiManager.Instance;
            if (view) { if (ui) ui.Close(view); else UnityEngine.Object.Destroy(view.gameObject); }
            view = null;
            if (runner) runner.StopAllCoroutines();
            if (finish) Finish();
        }
    }
}
