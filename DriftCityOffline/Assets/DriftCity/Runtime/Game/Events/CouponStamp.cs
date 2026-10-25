using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// COUPONSTAMP 1910000, the older coupon stamp book (DriftCity_RE\EVENTS.md section 6).
    ///
    /// Client [exact]: ui+0x1f8 (vtable 0x872b3c, creator 0x6336c0, refresh 0x633290, click 0x6334d0); the server config picks
    /// it instead of COUPONBOOK when net+0x248 &amp; 0x2000000 is clear, and Coupon Manager Suyeon's Space (COUPONPOPUP 1900000)
    /// opens it. One row per category that has coupons (Init/Coupon DB Type 0-9, compacted): TITLE = the category name
    /// (10245-10253, 10853; row 1 "Number of Time Attacks" 10665 with char+0xed), RECORD = the counter "%8d %s" (10255 times /
    /// 10257 Minutes; the game-mode row "%7d %s &amp; %4d %s"), five cells per row (DB Idx 0-4): TEXT "%d" (the need, minutes
    /// when timed), CARIMGM the car, COUPON_POS + a blinking HOVER while earned (fmod(t,1) >= 0.5), STAMP when claimed;
    /// POS claims (0x630640: 10269 without a free slot, 0x3f2 + 0x3fb, 10847) like the newer book; CLOSE.
    /// Remake: CouponBook's rows, counters and claim; chosen by "couponbook=stamp" in events.cfg (env DC_COUPONBOOK=stamp);
    /// the second game-mode counter (the client's counter 11) has no source here and shows the Undercity runs only.
    /// </summary>
    public static class CouponStamp
    {
        public const int Dialog = 1910000;
        const string S = "ID_COUPONSTAMP_";
        static PlayerProfile P => PlayerProfile.Current;
        static NuiView view;
        static UiManager hooked;
        static readonly List<int> rowCats = new List<int>();

        public static NuiView View => view;
        public static IReadOnlyList<int> RowCategories => rowCats;
        /// <summary>The older book instead of COUPONBOOK (the server switch net+0x248 bit 25 clear).</summary>
        public static bool Preferred => string.Equals(SeasonEvents.ConfigValue("couponbook"), "stamp", StringComparison.OrdinalIgnoreCase);

        public static CouponRow Cell(int cat, int col) => CouponBook.Rows.FirstOrDefault(r => r.Category == cat && r.Index == col && r.Display)
                                                          ?? CouponBook.Rows.Where(r => r.Category == cat && r.Display).OrderBy(r => r.Index).Skip(col).FirstOrDefault();

        public static void Open()
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) return;
            Close();
            Hook(ui);
            view = ui.Open(Dialog);
            EventArtFallback.Apply(view);   // stand-ins for art this install lacks
            if (!view) return;
            view.SetVisible(S + "MSGBOX", false);
            view.gameObject.AddComponent<EventAnim>().Tick = Blink;
            Refresh();
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On(S + "CLOSE", _ => { EventUi.Sfx("SID_SYSWIN_DESTROY"); Close(); });
            for (int r = 0; r < 10; r++)
                for (int c = 0; c < 5; c++)
                {
                    int rr = r, cc = c;
                    ui.On($"{S}SLOT{rr}_COUPON_POS{cc}", _ => Claim(rr, cc));
                }
        }

        public static void Refresh()
        {
            if (!view) return;
            rowCats.Clear();
            for (int cat = 0; cat < 10; cat++) if (CouponBook.Rows.Any(r => r.Category == cat && r.Display)) rowCats.Add(cat);
            for (int r = 0; r < 10; r++)
            {
                bool used = r < rowCats.Count;
                foreach (var id in new[] { $"BASETILE{r:00}", $"TITLE_SLOT{r}", $"RECORD_SLOT{r}", $"SLIDER_SLOT{r}" }) view.SetVisible(S + id, used);
                for (int c = 0; c < 5; c++) DrawCell(r, c, used ? rowCats[r] : -1);
                if (!used) continue;
                int cat = rowCats[r];
                view.SetText($"{S}TITLE_SLOT{r}", CouponBook.CatName(cat));   // (10665 belonged to the Time Attack service flag char+0xed)
                view.SetText($"{S}RECORD_SLOT{r}", cat == 9
                    ? $"{CouponBook.Current(P, cat),7} {CouponBook.Unit(cat)} & {0,4} {CouponBook.Unit(cat)}"
                    : $"{CouponBook.Current(P, cat),8} {CouponBook.Unit(cat)}");
            }
        }

        static void DrawCell(int r, int c, int cat)
        {
            var row = cat >= 0 ? Cell(cat, c) : null;
            string n = $"SLOT{r}_";
            bool earned = row != null && CouponBook.Claimable(P, row);
            bool claimed = row != null && CouponBook.Claimed(P, row);
            view.SetVisible($"{S}{n}TEXT{c}", row != null);
            view.SetVisible($"{S}{n}CARIMGM{c}", row != null);
            view.SetVisible($"{S}{n}COUPON_POS{c}", earned);
            view.SetVisible($"{S}{n}COUPON_HOVER{c}", false);
            view.SetVisible($"{S}{n}STAMP{c}", claimed);
            if (row == null) return;
            view.SetText($"{S}{n}TEXT{c}", row.Need.ToString());
            EventUi.Icon(view, $"{S}{n}CARIMGM{c}", row.CouponItem);
        }

        /// <summary>The earned coupons' HOVER blinks (fmod(t, 1) >= 0.5).</summary>
        static void Blink(float t)
        {
            if (!view) return;
            bool on = t % 1f >= 0.5f;
            for (int r = 0; r < rowCats.Count && r < 10; r++)
                for (int c = 0; c < 5; c++)
                {
                    var row = Cell(rowCats[r], c);
                    view.SetVisible($"{S}SLOT{r}_COUPON_HOVER{c}", on && row != null && CouponBook.Claimable(P, row));
                }
        }

        public static void Claim(int r, int c, Action<bool> done = null)
        {
            if (r >= rowCats.Count) { done?.Invoke(false); return; }
            var row = Cell(rowCats[r], c);
            if (row == null || !CouponBook.Claimable(P, row)) { done?.Invoke(false); return; }
            CouponBook.Claim(row, ok => { Refresh(); done?.Invoke(ok); });
        }

        public static void Close() { if (view && UiManager.Instance) UiManager.Instance.Close(view); view = null; }
    }
}
