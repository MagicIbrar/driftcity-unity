using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>A row of Init/Coupon (init\coupon.xlt; loader 0x62f6e0, rows "%d%d%d%d%s%u%s%d%d%d%s" into 0xa4 bytes).</summary>
    public sealed class CouponRow
    {
        public int Category, Index, Count = -1, Minutes = -1, CarId, Grade;
        public bool Display;
        public string CarName, CouponItem;
        /// <summary>Requirement in the category's unit (times, or minutes for driving / reckless time).</summary>
        public int Need => Count >= 0 ? Count : Minutes;
        public bool Timed => Count < 0;
        public string Key => Category + ":" + Index;
    }

    /// <summary>
    /// The coupon book (F5): play counters earn coupons, and a coupon lets you buy its car at the dealer.
    ///
    /// Client [exact] (DriftCity_RE\EVENTS.md): counters live in the coupon singleton 0x6ce4b0 (categories 0..9: battles,
    /// deliveries, chases, combos, driving time, drifts, skill passes, jumps, reckless time, game modes; time in seconds,
    /// shown in minutes), bumped from the race result, delivery, chase, combo, drift/jump/skill-pass and driving code.
    /// When a counter reaches the next row's requirement the client sends op 0x3f2 (counters) + 0x3f7 {dbType, dbIdx}; the
    /// server answers 0x3f8 (record state 1) and the client shows a centre notice (10143..10151, 10790), blinks the COUPON
    /// icon 1850000 (fmod(t,1) &lt; 0.5) and plays the coupon sound. The notify text (10270..10278, 10792) has OK -> op 0x3f9
    /// (state 2). COUPONBOOK 3690000 (ui+0x1fc, ctor 0x62faa0, creator 0x630ec0, handler 0x630860) has 8 pages V2..V9 (page =
    /// car grade - 2) of up to 5 coupons: BTN (claimable), ALRAM, CARNAME, CONDOTION, CONDITIONNUM "%d", CURNUM "%d %s", STAMP
    /// (claimed); hovering fills the detail panel. A claim (0x630640) needs a free slot and sends 0x3f2 + 0x3fb; the reply
    /// 0x3fc stamps it and 10847 "You've received a %s Coupon." prints. Coupon cars at the dealer need the coupon item
    /// (AUTOSHOP check 0x59dfc0 -> 10489). COUPONPOPUP 1900000 is Coupon Manager Suyeon's prompt (10644/10645, Space opens the
    /// book) at the "Instructor Duffy Coupon" posts.
    /// Remake rules: counters are counted on the client (as in the original) and sent with coupon.sync; the server caps each
    /// category's growth per second of play (src/events.js COUPON_RATE) and checks the requirement on coupon.claim.
    /// </summary>
    public static class CouponBook
    {
        static PlayerProfile P => PlayerProfile.Current;
        public const int Dialog = 3690000, NotifyDialog = 1850000, NpcDialog = 1900000;
        public enum Cat { Battle, Delivery, Chase, Combo, DrivingTime, Drift, SkillPass, Jump, Reckless, GameMode }
        static readonly string[] NameKey = { "10245", "10246", "10247", "10248", "10249", "10250", "10251", "10252", "10253", "10853" };
        static readonly string[] NameFallback = { "Battle Zone", "Delivery", "Chase", "Combo", "Driving Time", "Drift", "Skill Pass", "Jump", "Reckless", "Undercity" };
        static readonly string[] NoticeKey = { "10143", "10144", "10145", "10146", "10147", "10148", "10149", "10150", "10151", "10790" };
        static readonly string[] NotifyKey = { "10270", "10271", "10272", "10273", "10274", "10275", "10276", "10277", "10278", "10792" };

        static List<CouponRow> rows;

        public static List<CouponRow> Rows
        {
            get
            {
                if (rows != null) return rows;
                rows = new List<CouponRow>();
                byte[] data = null;
                foreach (var path in new[] { @"data\init\coupon.xlt", @"init\coupon.xlt" })
                {
                    try { if (DriftCityData.Files != null && DriftCityData.Files.Exists(path)) { data = DriftCityData.Files.Read(path); break; } } catch (Exception) { }
                }
                if (data == null) return rows;
                var t = XltTable.Parse(data);
                foreach (var r in t.Rows)
                {
                    if (r.Length < 11 || !int.TryParse(r[9].Trim(), out int cat) || !int.TryParse(r[1].Trim(), out int idx)) continue;
                    var row = new CouponRow
                    {
                        Category = cat, Index = idx, Display = r[2].Trim() == "1", Count = Int(r[3]), Minutes = Int(r[4]), CarName = r[5].Trim(),
                        CarId = Int(r[6]), CouponItem = r[7].Trim(), Grade = Int(r[8]),
                    };
                    if (row.Count < 0 && row.Minutes < 0) continue;
                    rows.Add(row);
                }
                // the client sorts by car grade (std::sort 0x62f890); stable here: grade, then file order
                rows = rows.Select((x, i) => (x, i)).OrderBy(p => p.x.Grade).ThenBy(p => p.i).Select(p => p.x).ToList();
                return rows;
            }
        }

        static int Int(string s) => int.TryParse((s ?? "").Trim(), out int v) ? v : -1;

        public static string CatName(int c) => c >= 0 && c < 10 ? EventUi.T(NameKey[c], NameFallback[c]) : "";
        public static string Unit(int c) => c == 4 || c == 8 ? EventUi.T("10257", "Minutes") : EventUi.T("10255", "times");

        /// <summary>The counter in the row's unit (minutes for the timed categories).</summary>
        public static int Current(PlayerProfile p, int cat)
        {
            var st = EventNet.State(p);
            if (cat < 0 || cat >= 10) return 0;
            float v = st.Coupon[cat];
            return cat == 4 || cat == 8 ? Mathf.FloorToInt(v / 60f) : Mathf.FloorToInt(v);
        }

        public static bool Claimed(PlayerProfile p, CouponRow r) => EventNet.State(p).CouponClaimed.Contains(r.Key);
        public static bool Claimable(PlayerProfile p, CouponRow r) => r.Display && !Claimed(p, r) && Current(p, r.Category) >= r.Need;

        // ---------------- counting ----------------

        /// <summary>Unsynced counter growth since the last coupon.sync (online).</summary>
        static readonly float[] pending = new float[10];
        static float nextSync;

        /// <summary>Adds to a counter (seconds for driving / reckless time). Called by EventObjects' tracker and by hooks.</summary>
        public static void Count(Cat cat, float amount = 1f)
        {
            var p = P;
            if (p == null || amount <= 0 || UndercityStage.Active && cat != Cat.GameMode) return;
            int c = (int)cat;
            var st = EventNet.State(p);
            var before = Rows.Where(r => r.Category == c && Claimable(p, r)).Select(r => r.Key).ToList();
            st.Coupon[c] += amount;
            pending[c] += amount;
            foreach (var r in Rows.Where(r => r.Category == c && r.Display && Claimable(p, r) && !before.Contains(r.Key) && !st.CouponSeen.Contains(r.Key)))
                Announce(r);
        }

        /// <summary>A coupon became available: the centre notice line, the coupon sound and the blinking COUPON icon.</summary>
        static void Announce(CouponRow r)
        {
            ChatBox.System(EventUi.T(NoticeKey[r.Category], "%d done. Coupon now available.", r.Need));
            EventUi.Sfx("SID_COUPON");
            ShowNotify();
        }

        /// <summary>Sends the counters to the server (every 30 s while online, and before a claim).</summary>
        public static void Sync(Action done = null, bool force = false)
        {
            if (!OnlineSession.Active) { done?.Invoke(); return; }
            if (!force && Time.unscaledTime < nextSync) { done?.Invoke(); return; }
            nextSync = Time.unscaledTime + 30f;
            var add = pending.Select(x => (object)(double)x).ToList();
            Array.Clear(pending, 0, 10);
            OnlineSession.Instance.Net.Request("coupon.sync", new Dictionary<string, object> { ["add"] = add }, m =>
            {
                // the server keeps its own bounded counters; take them back
                var st = EventNet.State(P);
                var srv = m.L("counters");
                if (srv != null) for (int i = 0; i < 10 && i < srv.Count; i++) st.Coupon[i] = Convert.ToSingle(srv[i]);
                done?.Invoke();
            });
        }

        // ---------------- the book ----------------

        static NuiView book;
        static int hovered = -1;
        public static NuiView View => book;

        public static void Open()
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) return;
            Close();
            book = ui.Open(Dialog);
            if (!book) return;
            ui.On("ID_COUPONBOOK_CLOSE", _ => { EventUi.Sfx("SID_SYSWIN_DESTROY"); Close(); });
            for (int page = 0; page < 8; page++)
                for (int j = 1; j <= 5; j++)
                {
                    int pg = page, jj = j;
                    string btn = $"ID_COUPONBOOK_V{page + 2}_BTN{j:00}";
                    ui.On(btn, _ => { var r = Slot(pg, jj); if (r != null) Claim(r); });
                    var c = book.Control(btn);
                    if (c) c.Hovered += on => { if (on) Detail(Slot(pg, jj)); };
                }
            Sync(() => Refresh(), true);
            Refresh();
            Detail(Rows.FirstOrDefault(r => r.Display && r.Grade >= 2));
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        /// <summary>The coupon in page (grade - 2) slot j (1..5): the page's Display rows in sort order.</summary>
        public static CouponRow Slot(int page, int j) => Rows.Where(r => r.Display && r.Grade == page + 2).Skip(j - 1).FirstOrDefault();

        public static void Refresh()
        {
            if (!book || P == null) return;
            for (int page = 0; page < 8; page++)
                for (int j = 1; j <= 5; j++)
                {
                    var r = Slot(page, j);
                    int n = page * 5 + j;
                    string v = $"ID_COUPONBOOK_V{page + 2}_";
                    bool claimed = r != null && Claimed(P, r), ready = r != null && Claimable(P, r);
                    book.SetVisible(v + $"BTN{j:00}", r != null);
                    book.SetEnabled(v + $"BTN{j:00}", ready);
                    book.SetVisible(v + $"ALRAM{j:00}", ready);
                    book.SetVisible(v + $"CARNAME{j:00}", r != null);
                    if (r != null) OriginalMenus.Plate(book, v + $"CARNAME{j:00}", CarName(r), Car(r));
                    book.SetText($"ID_COUPONBOOK_CONDOTION{n:00}", r != null ? CatName(r.Category) : "");
                    book.SetText($"ID_COUPONBOOK_CONDITIONNUM{n:00}", r != null ? r.Need.ToString() : "");
                    book.SetText($"ID_COUPONBOOK_CURNUM{n:00}", r != null ? $"{Current(P, r.Category)} {Unit(r.Category)}" : "");
                    book.SetVisible($"ID_COUPONBOOK_STAMP{n:00}", claimed);
                }
        }

        static VehicleInfo Car(CouponRow r) => GameTables.PlayerCars?.FirstOrDefault(v => v.UniqueId == r.CarId);
        static string CarName(CouponRow r) => Car(r)?.UiName ?? r.CarName;

        static void Detail(CouponRow r)
        {
            if (!book || r == null) return;
            var car = Car(r);
            EventUi.Icon(book, "ID_COUPONBOOK_COUPON_POS", r.CouponItem);
            book.SetText("ID_COUPONBOOK_CONDITIONTEXT", $"{CatName(r.Category)} {r.Need} {Unit(r.Category)}");
            book.SetText("ID_COUPONBOOK_CONDITIONNUM", $"{Current(P, r.Category)} {Unit(r.Category)}");
            OriginalMenus.Plate(book, "ID_COUPONBOOK_CARNAME", CarName(r), car);
            var gc = book.Control("ID_COUPONBOOK_CARGRADE");
            var gtex = OriginalMenus.CmnImage(OriginalMenus.GradeM, "V" + Mathf.Max(1, r.Grade), out var guv);
            var gimg = gc ? gc.GetComponentInChildren<UnityEngine.UI.RawImage>(true) : null;
            if (gtex && gimg) { gimg.texture = gtex; gimg.uvRect = guv; gimg.enabled = true; }
            else book.SetText("ID_COUPONBOOK_CARGRADE", "V" + Mathf.Max(1, r.Grade));
            book.SetText("ID_COUPONBOOK_NEEDLEVEL", EventUi.T("10484", "Lv. %u+", car?.ReqLevel ?? 1));
            int[] stats = { car?.Speed ?? 0, car?.Accel ?? 0, car?.Crash ?? 0, car?.Boost ?? 0 };
            string[] ids = { "SPD", "ACC", "COL", "BST" };
            for (int i = 0; i < 4; i++) book.SetText("ID_COUPONBOOK_GAUGETEXT_" + ids[i], $"[{stats[i],3}]");
            book.SetText("ID_COUPONBOOK_GAUGETEXT_PRICE", "-");
        }

        /// <summary>Claims a coupon: the server checks the counter and gives the coupon item.</summary>
        public static void Claim(CouponRow r, Action<bool> done = null)
        {
            if (r == null || P == null) { done?.Invoke(false); return; }
            Sync(() =>
            {
                EventNet.Request("coupon.claim", new Dictionary<string, object> { ["cat"] = r.Category, ["idx"] = r.Index }, () => ClaimOffline(P, r.Category, r.Index), res =>
                {
                    if (!res.Ok) { UiPopup.Message(res.Error ?? "That didn't work."); done?.Invoke(false); return; }
                    EventUi.Sfx("SID_COUPON");
                    ChatBox.System(EventUi.T("10847", "You've received a %s Coupon.", CarName(r)));
                    Refresh();
                    done?.Invoke(true);
                });
            }, true);
        }

        /// <summary>Offline twin of coupon.claim.</summary>
        public static EventReply ClaimOffline(PlayerProfile p, int cat, int idx)
        {
            var r = Rows.FirstOrDefault(x => x.Category == cat && x.Index == idx);
            if (r == null || !r.Display) return EventReply.Fail(EventUi.T("20093", "No coupons available."));
            if (Claimed(p, r)) return EventReply.Fail(EventUi.T("20066", "Failed to obtain the coupon."));
            if (Current(p, cat) < r.Need) return EventReply.Fail(EventUi.T("20066", "Failed to obtain the coupon."));
            var st = EventNet.State(p);
            st.CouponClaimed.Add(r.Key);
            if (!st.CouponSeen.Contains(r.Key)) st.CouponSeen.Add(r.Key);
            var g = new List<RewardGrant> { new RewardGrant { Kind = GrantKind.Item, ItemId = r.CouponItem, Count = 1 } };
            EventNet.Apply(p, g);
            return new EventReply { Ok = true, Grants = g };
        }

        // ---------------- the dealer's rule (hook) ----------------

        /// <summary>Coupon cars (coupon.xlt car ids) need their coupon item to be bought (AUTOSHOP 0x59dfc0 -> 10489).</summary>
        public static string CouponFor(VehicleInfo car) => car == null ? null : Rows.FirstOrDefault(r => r.CarId == car.UniqueId)?.CouponItem;
        public static bool CanBuy(PlayerProfile p, VehicleInfo car) { var c = CouponFor(car); return c == null || (p != null && p.Count(c) > 0); }
        /// <summary>The dealer spends the coupon when the car is bought.</summary>
        public static void SpendCoupon(PlayerProfile p, VehicleInfo car) { var c = CouponFor(car); if (c != null && p != null && p.Count(c) > 0) p.Give(c, -1); }

        /// <summary>Using a coupon item from the bag: its car and the dealer hint (11809 "Press F5...").</summary>
        public static void UseCouponItem(string item)
        {
            var r = Rows.FirstOrDefault(x => x.CouponItem.Equals(item, StringComparison.OrdinalIgnoreCase));
            UiPopup.Message($"{EventItems.Name(item)}\n{EventItems.Desc(item)}\n\n{EventUi.T("11804", "Coupon Vehicle")}: {(r != null ? CarName(r) : "-")}");
        }

        // ---------------- COUPON 1850000 notify ----------------

        static NuiView notify;
        public static NuiView NotifyView => notify;

        /// <summary>The next coupon that is ready but not acknowledged yet.</summary>
        public static CouponRow Unseen(PlayerProfile p) => p == null ? null : Rows.FirstOrDefault(r => Claimable(p, r) && !EventNet.State(p).CouponSeen.Contains(r.Key));

        public static void ShowNotify()
        {
            var ui = UiManager.Instance;
            var r = Unseen(P);
            if (!ui || r == null || notify) return;
            notify = ui.Open(NotifyDialog);
            if (!notify) return;
            notify.SetVisible("ID_COUPON_NOTIFY_ACCEPT", false);
            notify.SetVisible("ID_COUPON_NOTIFY_LATER", false);
            notify.SetText("ID_COUPON_NOTIFY_TXT", EventUi.T(NotifyKey[r.Category], "Coupon is now available.\nOpen the Coupon Book menu (F5) to obtain the Coupon!", r.Need));
            notify.gameObject.AddComponent<EventAnim>().Tick = t => { if (notify) EventUi.Alpha(notify, "ID_COUPON_NOTIFY_POS_COUPON", t % 1f < 0.5f ? 1f : 0.2f); };
            EventUi.Icon(notify, "ID_COUPON_NOTIFY_POS_COUPON", r.CouponItem);
            ui.On("ID_COUPON_NOTIFY_OK", _ => { Seen(r); CloseNotify(); });
            ui.On("ID_COUPON_NOTIFY_LATER", _ => CloseNotify());
        }

        static void Seen(CouponRow r)
        {
            var st = EventNet.State(P);
            if (!st.CouponSeen.Contains(r.Key)) st.CouponSeen.Add(r.Key);
            if (OnlineSession.Active) OnlineSession.Instance.Net.Request("coupon.seen", new Dictionary<string, object> { ["cat"] = r.Category, ["idx"] = r.Index }, _ => { });
            else P?.Save();
        }

        static void CloseNotify() { if (notify && UiManager.Instance) UiManager.Instance.Close(notify); notify = null; }

        // ---------------- COUPONPOPUP 1900000: Coupon Manager at the coupon posts ----------------

        static NuiView npc;
        public static NuiView NpcView => npc;

        public static void ShowNpc()
        {
            var ui = UiManager.Instance;
            if (!ui || npc) return;
            npc = ui.Open(NpcDialog);
            if (!npc) return;
            npc.SetText("ID_COUPONPOPUP_NAME", EventUi.T("10644", "Coupon Manager Suyeon"));
            npc.SetText("ID_COUPONPOPUP_CONTENT", EventUi.T("10645", "If you want to know about Coupons, press the [Spacebar]."));
            ui.On("ID_COUPONPOPUP_SPACE", _ => { HideNpc(); OpenFromNpc(); });
        }

        /// <summary>Suyeon's Space: the stamp book COUPONSTAMP 1910000 when configured (the server's old-book switch), else COUPONBOOK.</summary>
        public static void OpenFromNpc() { if (CouponStamp.Preferred) CouponStamp.Open(); else Open(); }

        public static void HideNpc() { if (npc && UiManager.Instance) UiManager.Instance.Close(npc); npc = null; }

        // ---------------- coupon codes (INVEN 3200000 COUPON_TEXT / COUPON_INPUT) ----------------

        /// <summary>Wires the inventory's coupon-code page (3200000): Enter / INPUT sends the typed code.</summary>
        public static void BindCodeEntry(NuiView v)
        {
            if (!v || !UiManager.Instance) return;
            UiManager.Instance.On("ID_INVEN_COUPON_INPUT", view => RedeemCode(view.GetInput("ID_INVEN_COUPON_TEXT")));
        }

        /// <summary>Redeems a code (op 0x9d -> 0x5dd: 10754 "Coupon has been issued." / 10755). Codes are the server's
        /// (env DC_COUPON_CODES) or, offline, "code=CODE:item:count" lines in events.cfg; each works once per driver.</summary>
        public static void RedeemCode(string code, Action<bool> done = null)
        {
            code = (code ?? "").Trim().ToUpperInvariant();
            EventNet.Request("coupon.code", new Dictionary<string, object> { ["code"] = code }, () => RedeemOffline(P, code), r =>
            {
                if (!r.Ok) { UiPopup.Message(r.Error ?? EventUi.T("10755", "This coupon code does not exist or has already been used.")); done?.Invoke(false); return; }
                UiPopup.Message(EventUi.T("10754", "Coupon has been issued."));
                EventNet.Log(r.Grants);
                done?.Invoke(true);
            });
        }

        public static EventReply RedeemOffline(PlayerProfile p, string code)
        {
            var codes = OfflineCodes();
            var st = EventNet.State(p);
            string used = "code:" + code;
            if (code.Length == 0 || code.Length > 10 || !codes.TryGetValue(code, out var g) || st.CouponSeen.Contains(used))
                return EventReply.Fail(EventUi.T("10755", "This coupon code does not exist or has already been used."));
            st.CouponSeen.Add(used);
            var grants = new List<RewardGrant> { g };
            EventNet.Apply(p, grants);
            return new EventReply { Ok = true, Grants = grants };
        }

        static Dictionary<string, RewardGrant> OfflineCodes()
        {
            var d = new Dictionary<string, RewardGrant>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { Application.persistentDataPath, System.IO.Path.GetDirectoryName(Application.dataPath) })
            {
                try
                {
                    var f = System.IO.Path.Combine(dir ?? "", "events.cfg");
                    if (!System.IO.File.Exists(f)) continue;
                    foreach (var line in System.IO.File.ReadAllLines(f))
                    {
                        if (!line.Trim().StartsWith("code=", StringComparison.OrdinalIgnoreCase)) continue;
                        var a = line.Trim().Substring(5).Split(':');
                        if (a.Length >= 2) d[a[0].Trim().ToUpperInvariant()] = new RewardGrant { Kind = GrantKind.Item, ItemId = a[1].Trim(), Count = a.Length > 2 && int.TryParse(a[2], out int n) ? Mathf.Max(1, n) : 1 };
                    }
                }
                catch (Exception) { }
            }
            return d;
        }

        public static void Close() { if (book && UiManager.Instance) UiManager.Instance.Close(book); book = null; }
    }
}
