using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The Undercity's screens (game.nui; DriftCity_RE/UNDERCITY.md): the four play panels UC PLAY1ST..PLAY4TH
    /// (3480000-3510000, one per phase, drawn with their own digit images), PLAYEXIT (3520000), the party counters PARTYUC
    /// (2880000), the HUV health bar UC_HP (3550000), the mission tips (3560000), the "HUB destroyed" / "MAIN HUB
    /// destroyed" flashes (3580000 / 3590000), WAITPLAYER (3530000) and the result board UCRESULT (3570000). The
    /// "Alert from Tatiana" messages (&lt;ucimg&gt; texts) are drawn by the client's QUESTDIALOG class itself; here they
    /// are a small panel of their own.
    /// </summary>
    public sealed class UndercityHud : MonoBehaviour
    {
        public const int Play1st = 3480000, Play2nd = 3490000, Play3rd = 3500000, Play4th = 3510000, PlayExit = 3520000,
                         WaitPlayer = 3530000, RequestInvite = 3540000, HpBar = 3550000, Tip = 3560000, Result = 3570000,
                         HubKill = 3580000, MainHubKill = 3590000, PartyUc = 2880000;

        NuiView phaseView, exitView, partyView, hpView, tipView, waitView, flashView, inviteView;
        float inviteLeft, inviteTotal;
        System.Action<bool> inviteAnswer;
        int phase = -1;
        float tipTimer, flashTimer, waitFrameTimer;
        int waitFrame;
        readonly Dictionary<RectTransform, (Vector2 size, Vector2 pos, Rect uv)> gaugeFull = new Dictionary<RectTransform, (Vector2, Vector2, Rect)>();

        // alert panel
        RectTransform alertRoot;
        Text alertName, alertText;
        RawImage alertPicture;
        Button alertAccept;
        float alertTimer;
        List<List<string>> alertPages;
        float alertPageTime;
        int alertPage;
        System.Action alertAccepted;

        public bool AlertShowing => alertRoot && alertRoot.gameObject.activeSelf;
        public string AlertKey { get; private set; }
        public NuiView PhaseView => phaseView;
        public int Phase => phase;

        static UiManager Ui => UiManager.Instance;

        // ---------------- play panels ----------------

        /// <summary>Shows PLAY1ST..PLAY4TH for phase 0..3 (0x687050), -1 hides them.</summary>
        public void ShowPhase(int index)
        {
            if (index == phase && (index < 0 || phaseView)) return;
            if (phaseView && Ui) Ui.Close(phaseView);
            phaseView = null;
            phase = index;
            if (index < 0 || !Ui) return;
            phaseView = Ui.Open(Play1st + index * 10000);
            if (!phaseView) return;
            // the digit images are copied from hidden template controls (the "9" in the corner of the bare layout)
            foreach (var c in phaseView.Controls)
                if (System.Text.RegularExpressions.Regex.IsMatch(c.Data.IdName ?? "", @"NUM\d$")) c.gameObject.SetActive(false);
            if (!exitView) exitView = Ui.Open(PlayExit);
        }

        public void HidePlay()
        {
            ShowPhase(-1);
            if (exitView && Ui) Ui.Close(exitView);
            exitView = null;
        }

        public NuiView ExitView => exitView;

        /// <summary>PLAY1ST: the roadside HUBs still standing / placed (0x684f00 / 0x684f20).</summary>
        public void SetHubs(int remaining, int total)
        {
            if (phase != 0 || !phaseView) return;
            Digits(phaseView, "ID_UC_PLAY1ST_REDNUM", new[] { "ID_UC_PLAY1ST_RREDNUMPOS1ST", "ID_UC_PLAY1ST_RREDNUMPOS2ND", "ID_UC_PLAY1ST_RREDNUMPOS3RD" }, remaining);
            Digits(phaseView, "ID_UC_PLAY1ST_WHITENUM", new[] { "ID_UC_PLAY1ST_WHITENUMPOS1ST", "ID_UC_PLAY1ST_WHITENUMPOS2ND", "ID_UC_PLAY1ST_WHITENUMPOS3RD" }, total);
        }

        /// <summary>PLAY2ND: clock, barrier HUBs destroyed / placed and +NN = the barriers I destroyed (HUD +0x358: 0xbc7 for
        /// my own id adds 1, 0x684f80; reset at state 4, 0x717400).</summary>
        public void SetMainHub(float seconds, int destroyed, int total, int plus)
        {
            if (phase != 1 || !phaseView) return;
            Clock(phaseView, "ID_UC_PLAY2ND", "ID_UC_PLAY2ND_REDNUM", seconds);
            Digits(phaseView, "ID_UC_PLAY2ND_CURNUM", new[] { "ID_UC_PLAY2ND_CURNUMPOS0", "ID_UC_PLAY2ND_CURNUMPOS1" }, destroyed);
            Digits(phaseView, "ID_UC_PLAY2ND_TOTALNUM", new[] { "ID_UC_PLAY2ND_TOTALNUMPOS0", "ID_UC_PLAY2ND_TOTALNUMPOS1" }, total);
            Digits(phaseView, "ID_UC_PLAY2ND_REMAINNUM", new[] { "ID_UC_PLAY2ND_REMAINNUMPOS1ST", "ID_UC_PLAY2ND_REMAINNUMPOS2ND" }, plus);
        }

        /// <summary>PLAY3RD: clock, HUVs destroyed / in the wave, extra kills (+NN, 0x685000) and the wave's health.</summary>
        public void SetHuvs(float seconds, int destroyed, int total, float health)
        {
            if (phase != 2 || !phaseView) return;
            Clock(phaseView, "ID_UC_PLAY3RD", "ID_UC_PLAY3RD_REDNUM", seconds);
            Digits(phaseView, "ID_UC_PLAY3RD_REDSMALLNUM", new[] { "ID_UC_PLAY3RD_CURNUMPOS1ST", "ID_UC_PLAY3RD_CURNUMPOS2ND" }, Mathf.Min(destroyed, total));
            Digits(phaseView, "ID_UC_PLAY3RD_WHITENUM", new[] { "ID_UC_PLAY3RD_TOTALNUMPOS1ST", "ID_UC_PLAY3RD_TOTALNUMPOS2ND" }, total);
            Digits(phaseView, "ID_UC_PLAY3RD_REMAINNUM", new[] { "ID_UC_PLAY3RD_REMAINNUMPOS1ST", "ID_UC_PLAY3RD_REMAINNUMPOS2ND" }, Mathf.Max(0, destroyed - total));
            Gauge(phaseView.Control("ID_UC_PLAY3RD_HP"), health);
        }

        /// <summary>PLAY4TH: clock, boss HUVs left, and the boss's health gauge.</summary>
        public void SetBoss(float seconds, int left, int total, float health)
        {
            if (phase != 3 || !phaseView) return;
            Clock(phaseView, "ID_UC_PLAY4TH", "ID_UC_PLAY4TH_REDNUM", seconds);
            Digits(phaseView, "ID_UC_PLAY4TH_WHITENUM", new[] { "ID_UC_PLAY4TH_TOTALNUMPOS1ST", "ID_UC_PLAY4TH_TOTALNUMPOS2ND" }, total);
            Digits(phaseView, "ID_UC_PLAY4TH_REMAINNUM", new[] { "ID_UC_PLAY4TH_REMAINNUMPOS1ST", "ID_UC_PLAY4TH_REMAINNUMPOS2ND" }, left);
            Gauge(phaseView.Control("ID_UC_PLAY4TH_HPGAUGE"), health);
        }

        // ---------------- party counters, HUV health, tips, flashes ----------------

        /// <summary>PARTYUC: one box per party member with their count for the phase (0x6850d0 prints it with "%d").</summary>
        public void SetParty(IList<int> counts)
        {
            if (!Ui) return;
            if (counts == null || counts.Count == 0) { if (partyView) Ui.Close(partyView); partyView = null; return; }
            if (!partyView)
            {
                partyView = Ui.Open(PartyUc);
                // the counters sit just under the party bar, where the party list opens too: keep them on top
                if (partyView) partyView.transform.SetAsLastSibling();
            }
            if (!partyView) return;
            for (int i = 0; i < 4; i++)
            {
                bool on = i < counts.Count;
                partyView.SetVisible($"ID_PARTYUC_BASEP{i + 1}", on);
                partyView.SetVisible($"ID_PARTYUC_TEXTP{i + 1}", on);
                if (on) partyView.SetText($"ID_PARTYUC_TEXTP{i + 1}", counts[i].ToString());
            }
        }

        /// <summary>UC_HP: name and health of the HUV being chased (null hides it).</summary>
        public void SetHuvHealth(string name, float fraction)
        {
            if (!Ui) return;
            if (name == null) { if (hpView) Ui.Close(hpView); hpView = null; return; }
            if (!hpView) hpView = Ui.Open(HpBar);
            if (!hpView) return;
            hpView.SetText("ID_UC_HP_HUVNAME", name);
            hpView.SetVisible("ID_UC_HP_DAMAGE", false);
            Gauge(hpView.Control("ID_UC_HP_BAR"), fraction);
        }

        /// <summary>The mission tip of a phase: "JUMP", "BARRIER" or "HUV" (3560000; the three share one spot).</summary>
        public void ShowTip(string which, float seconds = 8f)
        {
            if (!Ui) return;
            if (!tipView)
            {
                tipView = Ui.Open(Tip);
                // the tip sits just above the chat window in the client; the chat window is lifted over the minimap here
                if (tipView && tipView.transform.childCount > 0) ((RectTransform)tipView.transform.GetChild(0)).anchoredPosition += new Vector2(0f, Net.ChatBox.ChatLift);
            }
            if (!tipView) return;
            foreach (var id in new[] { "JUMP", "BARRIER", "HUV" }) tipView.SetVisible("ID_UC_TIP_" + id, id == which);
            tipTimer = seconds;
        }

        /// <summary>UCHUVKILL (a HUB/HUV down) or UCMAINHUVKILL (the Main HUB down) for a moment.</summary>
        public void Flash(bool mainHub, float seconds = 2.5f)
        {
            if (!Ui) return;
            if (flashView) Ui.Close(flashView);
            flashView = Ui.Open(mainHub ? MainHubKill : HubKill);
            flashTimer = seconds;
        }

        public bool FlashShowing(bool mainHub) => flashView && flashView.Dialog.Id == (mainHub ? MainHubKill : HubKill);

        /// <summary>WAITPLAYER: the 15-frame "waiting for the party" animation (null closes it).</summary>
        public void ShowWaiting(bool on)
        {
            if (!Ui) return;
            if (!on) { if (waitView) Ui.Close(waitView); waitView = null; return; }
            if (!waitView) waitView = Ui.Open(WaitPlayer);
            waitFrame = 0; waitFrameTimer = 0;
            StepWaiting();
        }

        /// <summary>WAITPLAYER is up (the run's requester waits for the party's answers).</summary>
        public bool Waiting => waitView;
        public NuiView InviteView => inviteView;

        /// <summary>
        /// REQUESTINVITE 3540000 (handler 0x687e70, mode +0x9c = 0): TEXT 10770 "Do you want to join the Undercity Rescue
        /// Mission? You've been invited by %s.", TEXTLOWER 10772 (the missions cancelled, the portal fee), 15 dots for the
        /// 15 s (+0xa0) to answer; ACCEPT (Enter) sends 0xfd, DENY (Esc) or the timeout 0xfe.
        /// </summary>
        public void ShowRequestInvite(string from, int fee, float seconds, System.Action<bool> answer)
        {
            if (!Ui) return;
            CloseRequestInvite();
            inviteView = Ui.Open(RequestInvite);
            if (!inviteView) { answer?.Invoke(false); return; }
            inviteAnswer = answer;
            inviteTotal = inviteLeft = Mathf.Max(1f, seconds);
            foreach (var c in inviteView.Controls) c.gameObject.SetActive(true);
            inviteView.SetText("ID_UC_REQUESTINVITE_TEXT", MissionText.Get("10770", from));
            inviteView.SetText("ID_UC_REQUESTINVITE_TEXTLOWER", MissionText.Get("10772", fee));
            Ui.On("ID_UC_REQUESTINVITE_ACCEPT", _ => AnswerInvite(true));
            Ui.On("ID_UC_REQUESTINVITE_DENY", _ => AnswerInvite(false));
            StepInviteDots();
            AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE");
        }

        /// <summary>Presses ACCEPT / DENY on REQUESTINVITE (also the test hook).</summary>
        public void AnswerInvite(bool accept)
        {
            if (!inviteView) return;
            var a = inviteAnswer;
            CloseRequestInvite();
            UiSounds.Click();
            a?.Invoke(accept);
        }

        public void CloseRequestInvite()
        {
            if (inviteView && Ui) Ui.Close(inviteView);
            inviteView = null;
            inviteAnswer = null;
        }

        void StepInviteDots()
        {
            if (!inviteView) return;
            int left = Mathf.CeilToInt(inviteLeft / inviteTotal * 15f);
            for (int i = 0; i < 15; i++) inviteView.SetVisible($"ID_UC_REQUESTINVITE_{i:00}", i < left);
        }

        void StepWaiting()
        {
            if (!waitView) return;
            for (int i = 0; i < 15; i++) waitView.SetVisible($"ID_UC_WAITPLAYER_{i:00}", i == waitFrame);
        }

        // ---------------- alert panel ("Alert from Tatiana") ----------------

        /// <summary>
        /// Shows a UC alert for its &lt;ucsec&gt; seconds (or <paramref name="seconds"/>), pages split evenly; with
        /// &lt;btnAccept&gt; an Accept button calls <paramref name="accepted"/> (Enter / Tab press it too).
        /// </summary>
        public void Alert(UndercityData.Message m, float seconds = 0f, System.Action accepted = null)
        {
            if (m == null || !Ui) return;
            BuildAlert();
            AlertKey = m.Key;
            alertName.text = m.Name ?? "Alert from Tatiana";
            alertPages = m.Pages.Count > 0 ? m.Pages : new List<List<string>> { new List<string>() };
            float total = seconds > 0 ? seconds : m.Seconds > 0 ? m.Seconds : 5f;
            alertTimer = total;
            alertPageTime = total / alertPages.Count;
            alertPage = 0;
            alertText.text = string.Join("\n", alertPages[0]);
            var tex = string.IsNullOrEmpty(m.Image) ? null : UiAssets.Texture(m.Image + ".dds");
            alertPicture.texture = tex;
            alertPicture.enabled = tex;
            alertAccept.gameObject.SetActive(m.Accept && accepted != null);
            alertAccepted = accepted;
            alertRoot.gameObject.SetActive(true);
            alertRoot.SetAsLastSibling();
        }

        public void CloseAlert()
        {
            if (alertRoot) alertRoot.gameObject.SetActive(false);
            AlertKey = null;
            alertAccepted = null;
        }

        /// <summary>Presses the alert's Accept button (also the test hook).</summary>
        public bool PressAccept()
        {
            if (!AlertShowing || alertAccepted == null || !alertAccept.gameObject.activeSelf) return false;
            var a = alertAccepted;
            CloseAlert();
            UiSounds.Click();
            a();
            return true;
        }

        void BuildAlert()
        {
            if (alertRoot) return;
            alertRoot = NuiView.NewRect("UC Alert", Ui.Root);
            // under the play panels at the top of the screen, clear of the car and the chat window
            alertRoot.anchorMin = alertRoot.anchorMax = alertRoot.pivot = new Vector2(0.5f, 1f);
            alertRoot.anchoredPosition = new Vector2(0, -78);
            alertRoot.sizeDelta = new Vector2(620, 128);
            var bg = alertRoot.gameObject.AddComponent<Image>();
            bg.color = new Color(0.04f, 0.06f, 0.12f, 0.82f);
            bg.raycastTarget = false;
            var pic = NuiView.NewRect("Picture", alertRoot);
            pic.anchorMin = pic.anchorMax = pic.pivot = new Vector2(0, 0.5f);
            pic.anchoredPosition = new Vector2(8, 0);
            pic.sizeDelta = new Vector2(112, 112);
            alertPicture = pic.gameObject.AddComponent<RawImage>();
            alertPicture.raycastTarget = false;
            alertName = MissionUi.Label(alertRoot, Vector2.zero, new Vector2(470, 22), 15, new Color(1f, 0.78f, 0.15f));
            Place(alertName.rectTransform, new Vector2(128, -8));
            alertText = MissionUi.Label(alertRoot, Vector2.zero, new Vector2(480, 92), 14, Color.white);
            Place(alertText.rectTransform, new Vector2(128, -32));
            var btn = NuiView.NewRect("Accept", alertRoot);
            btn.anchorMin = btn.anchorMax = btn.pivot = new Vector2(1, 0);
            btn.anchoredPosition = new Vector2(-10, 8);
            btn.sizeDelta = new Vector2(96, 28);
            var bimg = btn.gameObject.AddComponent<Image>();
            bimg.color = new Color(0.95f, 0.72f, 0.1f, 1f);
            alertAccept = btn.gameObject.AddComponent<Button>();
            alertAccept.onClick.AddListener(() => PressAccept());
            var lbl = MissionUi.Label(btn, Vector2.zero, new Vector2(96, 28), 15, Color.black, TextAnchor.MiddleCenter);
            lbl.text = "Accept";
            lbl.rectTransform.anchorMin = lbl.rectTransform.anchorMax = lbl.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            lbl.rectTransform.anchoredPosition = Vector2.zero;
            alertRoot.gameObject.SetActive(false);
        }

        static void Place(RectTransform rt, Vector2 topLeft)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = topLeft;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (tipView && (tipTimer -= dt) <= 0 && Ui) { Ui.Close(tipView); tipView = null; }
            if (flashView && (flashTimer -= dt) <= 0 && Ui) { Ui.Close(flashView); flashView = null; }
            if (waitView && (waitFrameTimer += dt) > 0.08f) { waitFrameTimer = 0; waitFrame = (waitFrame + 1) % 15; StepWaiting(); }
            if (inviteView)
            {
                inviteLeft -= dt;
                StepInviteDots();
                if (inviteLeft <= 0) AnswerInvite(false);   // 0x687dd0 on the timeout
            }
            if (AlertShowing)
            {
                alertTimer -= dt;
                int want = alertPageTime > 0 ? Mathf.Clamp(alertPages.Count - 1 - Mathf.FloorToInt(alertTimer / alertPageTime), 0, alertPages.Count - 1) : 0;
                if (want != alertPage) { alertPage = want; alertText.text = string.Join("\n", alertPages[want]); }
                if (alertAccept.gameObject.activeSelf && (MissionUi.TabPressed() || EnterPressed())) PressAccept();
                else if (alertTimer <= 0) CloseAlert();
            }
        }

        static bool EnterPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && !Net.ChatBox.Typing && kb.enterKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Return);
#endif
        }

        void OnDestroy() => CloseAll();

        public void CloseAll()
        {
            HidePlay();
            SetParty(null);
            SetHuvHealth(null, 0);
            ShowWaiting(false);
            CloseRequestInvite();
            if (Ui)
            {
                if (tipView) Ui.Close(tipView);
                if (flashView) Ui.Close(flashView);
            }
            tipView = flashView = null;
            if (alertRoot) Destroy(alertRoot.gameObject);
        }

        // ---------------- result board ----------------

        public sealed class ResultRow
        {
            public string Name;
            public int Level;
            public bool Self;
            public UndercityData.Score Score;
            public long Exp, Mito;
            public string Item;
            /// <summary>The "120%" (EXP drink) and "SET" (a full set on the car) badges (0x684820).</summary>
            public bool Boost120, Set;
        }

        /// <summary>
        /// UCRESULT (0x716480 -> 0x6844d0..0x684870): success or failure art, class S..D, mission time and Main HUB
        /// attempts, then one row per party member (own row highlighted) with HUB / Jump / HUV / Boss counts, EXP, Mito
        /// and up to three items; the best driver (most points) gets the crown.
        /// </summary>
        public static NuiView ShowResult(bool cleared, int cls, float seconds, int attempts, IList<ResultRow> rows)
        {
            var ui = Ui;
            if (!ui) return null;
            var v = ui.Open(Result);
            if (!v) return null;
            v.SetVisible("ID_UCRESULT_SUCCESS", cleared);
            v.SetVisible("ID_UCRESULT_FAIL", !cleared);
            string[] classes = { "D", "C", "B", "A", "S" };
            for (int i = 0; i < 5; i++) v.SetVisible("ID_UCRESULT_CLASS_" + classes[i], i == cls);
            int secs = Mathf.Max(0, Mathf.FloorToInt(seconds));
            v.SetText("ID_UCRESULT_TIME", $"{secs / 60}m {secs % 60:00}s");
            v.SetText("ID_UCRESULT_TRYCOUNT", attempts.ToString());
            string[] nth = { "1ST", "2ND", "3RD", "4TH" };
            int best = -1; long bestPts = -1;
            for (int i = 0; i < rows.Count && i < 4; i++)
            {
                var s = rows[i].Score;
                long pts = s.Hubs + 3L * s.Jumps + 2L * s.Barriers + 5L * s.Huvs + 20L * s.Boss + 10L * s.MainHub;
                if (pts > bestPts) { bestPts = pts; best = i; }
            }
            for (int i = 0; i < 4; i++)
            {
                string n = nth[i];
                bool on = i < rows.Count;
                var r = on ? rows[i] : null;
                v.SetVisible("ID_UCRESULT_MYSELFBASE_" + n, on && r.Self);
                // 0x684820: the record's +0x4e / +0x4f bytes show the "120%" and "SET" badges
                v.SetVisible("ID_UCRESULT_PARTY" + n + "_120", on && r.Boost120);
                v.SetVisible("ID_UCRESULT_PARTY" + n + "_SET", on && r.Set);
                for (int p = 1; p <= 4; p++) v.SetVisible($"ID_UCRESULT_PARTY{n}_P{p}", on && p == i + 1);
                foreach (var prefix in new[] { "MY", "YOUR" })
                {
                    bool mine = prefix == "MY";
                    bool show = on && r.Self == mine;
                    string cnt = mine ? "" : "CNT_";
                    void Set(string field, string text)
                    {
                        string id = field == "LEVEL" || field == "NAME" ? $"ID_UCRESULT_{prefix}{field}{n}" : $"ID_UCRESULT_{prefix}{cnt}{field}{n}";
                        v.SetVisible(id, show);
                        if (show) v.SetText(id, text);
                    }
                    Set("LEVEL", on ? "Lv " + r.Level : "");
                    Set("NAME", on ? r.Name : "");
                    Set("HUB", on ? (r.Score.Hubs + r.Score.Barriers + r.Score.MainHub).ToString() : "");
                    Set("JUMP", on ? r.Score.Jumps.ToString() : "");
                    Set("HUV", on ? r.Score.Huvs.ToString() : "");
                    Set("BOSS", on ? r.Score.Boss.ToString() : "");
                    Set("EXP", on ? r.Exp.ToString() : "");
                    Set("MITO", on ? r.Mito.ToString() : "");
                }
                v.SetVisible("ID_UCRESULT_ITEMBASE" + n, on);
                for (int k = 0; k < 3; k++)
                {
                    string id = $"ID_UCRESULT_POSITEM_{n}{nth[k]}";
                    bool item = on && k == 0 && !string.IsNullOrEmpty(r.Item);
                    v.SetVisible(id, item);
                    if (item) ItemIcon(v.Control(id), r.Item);
                }
            }
            // the crown sits on the best driver's row: move it to that row's name
            var crown = v.Control("ID_UCRESULT_BESTDRIVER");
            if (crown)
            {
                crown.gameObject.SetActive(best >= 0 && bestPts > 0);
                if (best > 0)
                {
                    var first = v.Control(rows[0].Self ? "ID_UCRESULT_MYNAME1ST" : "ID_UCRESULT_YOURNAME1ST");
                    var target = v.Control((rows[best].Self ? "ID_UCRESULT_MYNAME" : "ID_UCRESULT_YOURNAME") + nth[best]);
                    if (first && target)
                    {
                        var rt = (RectTransform)crown.transform;
                        rt.position += target.transform.position - first.transform.position;
                    }
                }
            }
            return v;
        }

        static void ItemIcon(NuiControlView c, string itemId)
        {
            if (!c) return;
            var tex = OriginalMenus.ItemIcon(itemId, out var uv);
            var img = c.GetComponentInChildren<RawImage>(true);
            if (img && tex) { img.texture = tex; img.uvRect = uv; }
        }

        // ---------------- digits and gauges ----------------

        /// <summary>
        /// Writes <paramref name="value"/> with leading zeros into the destination controls (left to right on screen)
        /// using the template digit images <paramref name="srcPrefix"/>0..9, as the client copies them.
        /// </summary>
        public static void Digits(NuiView v, string srcPrefix, string[] dst, int value)
        {
            if (!v) return;
            var slots = dst.Select(id => v.Control(id)).Where(c => c).OrderBy(c => c.transform.position.x).ToList();
            if (slots.Count == 0) return;
            value = Mathf.Clamp(value, 0, (int)Mathf.Pow(10, slots.Count) - 1);
            string s = value.ToString().PadLeft(slots.Count, '0');
            for (int i = 0; i < slots.Count; i++)
            {
                var src = MissionUi.Image(v, srcPrefix + s[i]);
                var img = slots[i].GetComponentInChildren<RawImage>(true);
                if (!src || !img) continue;
                img.texture = src.texture; img.uvRect = src.uvRect; img.enabled = src.texture;
                slots[i].gameObject.SetActive(true);
            }
        }

        /// <summary>mm'ss.fff into &lt;prefix&gt;_MIN1ST/2ND, _SEC1ST/2ND, _MS1ST/2ND/3TH.</summary>
        public static void Clock(NuiView v, string prefix, string srcPrefix, float seconds)
        {
            int ms = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0, seconds) * 1000f), 0, 99 * 60000 + 59999);
            Digits(v, srcPrefix, new[] { prefix + "_MIN1ST", prefix + "_MIN2ND" }, ms / 60000);
            Digits(v, srcPrefix, new[] { prefix + "_SEC1ST", prefix + "_SEC2ND" }, ms / 1000 % 60);
            Digits(v, srcPrefix, new[] { prefix + "_MS1ST", prefix + "_MS2ND", prefix + "_MS3TH" }, ms % 1000);
        }

        /// <summary>Shortens a bar control from the right (its image is cropped, not squeezed).</summary>
        public void Gauge(NuiControlView c, float fraction)
        {
            if (!c) return;
            fraction = Mathf.Clamp01(fraction);
            foreach (var img in c.GetComponentsInChildren<RawImage>(true))
            {
                var rt = img.rectTransform;
                if (!gaugeFull.TryGetValue(rt, out var full)) gaugeFull[rt] = full = (rt.sizeDelta, rt.anchoredPosition, img.uvRect);
                float w = full.size.x * fraction;
                rt.sizeDelta = new Vector2(w, full.size.y);
                rt.anchoredPosition = new Vector2(full.pos.x - rt.pivot.x * full.size.x + rt.pivot.x * w, full.pos.y);
                img.uvRect = new Rect(full.uv.x, full.uv.y, full.uv.width * fraction, full.uv.height);
            }
        }
    }
}
