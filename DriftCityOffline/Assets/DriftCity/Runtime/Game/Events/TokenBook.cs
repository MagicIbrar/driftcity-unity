using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>A row of Init/TokenBook (init\tokenbook.xlt): pay Count of Exchange_ID for up to four gifts.</summary>
    public sealed class TokenRow
    {
        public int Index, Count;
        public string Currency, Tooltip;
        public List<(string item, int count)> Gifts = new List<(string, int)>();
    }

    /// <summary>
    /// TOKENBOOK 3700000: the Mission Girl's token exchange (mode 0, Mission Token i_n_00049, rows 0-4) and the Item
    /// Exchange Attendant's (mode 1, rows 5-9: Mittron Crystal Powder / Repeatable Token).
    ///
    /// Client [exact] (DriftCity_RE\EVENTS.md): object ui+0x3b0 (vtable 0x872b98, creator 0x633da0, handler vtable[10] 0x6339c0,
    /// show 0x634380). Row i shows gifts 1-3 (POSITEM, ITEMNAME) and BUTTON0i, enabled when the driver holds Count of the
    /// row's currency (TOKENCOUNT shows it); a click asks 10916 "Do you want to exchange %d %ss with this item?" and YES sends
    /// op 0x48a {row}. The Mission Girl (the coupon NPC when InstantMission.DayMission is on) first says DayMission_Ing /
    /// _Complete / _Already; a finished daily mission pays one Mission Token (op 0x3e2 -> 0x489).
    /// Remake rules: the daily mission is "catch 5 HUVs or finish 3 races today (UTC)"; the server checks it from its own
    /// counters (HuvCaught, races) on token.daily and pays 1 Mission Token once a day.
    /// </summary>
    public static class TokenBook
    {
        static PlayerProfile P => PlayerProfile.Current;
        public const int Dialog = 3700000;
        public const string MissionToken = "i_n_00049";
        public const int DailyHuvs = 5, DailyRaces = 3;
        static List<TokenRow> rows;
        static NuiView view;
        static int mode;
        public static NuiView View => view;

        public static List<TokenRow> Rows
        {
            get
            {
                if (rows != null) return rows;
                rows = new List<TokenRow>();
                byte[] data = null;
                foreach (var path in new[] { @"data\init\tokenbook.xlt", @"init\tokenbook.xlt" })
                {
                    try { if (DriftCityData.Files != null && DriftCityData.Files.Exists(path)) { data = DriftCityData.Files.Read(path); break; } } catch (Exception) { }
                }
                if (data == null) return rows;
                var t = XltTable.Parse(data);
                foreach (var r in t.Rows)
                {
                    if (r.Length < 8 || !int.TryParse(r[0].Trim(), out int idx)) continue;
                    var row = new TokenRow { Index = idx, Currency = r[2].Trim(), Count = Int(r[3]), Tooltip = r[4] };
                    // gifts: (name, id, count) triples from column 5; the newer file shifts empty "reserved" cells
                    for (int c = 5; c + 2 < r.Length; c += 3)
                    {
                        string id = r[c + 1].Trim();
                        int n = Int(r[c + 2]);
                        if (id.Length == 0 || id == "0" || id.Equals("reserved", StringComparison.OrdinalIgnoreCase) || n <= 0) continue;
                        row.Gifts.Add((id, n));
                    }
                    if (row.Count > 0 && row.Currency.Length > 0) rows.Add(row);
                }
                return rows;
            }
        }

        static int Int(string s) => int.TryParse((s ?? "").Trim(), out int v) ? v : 0;

        /// <summary>Opens the book (mode 0 Mission Token, 1 item trade) after the NPC's line.</summary>
        public static void Open(int bookMode)
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) return;
            Close();
            view = ui.Open(Dialog);
            EventArtFallback.Apply(view);   // stand-ins for art this install lacks
            if (!view) return;
            mode = Mathf.Clamp(bookMode, 0, 1);
            EventUi.Backdrop(view, new Vector2(0, 0), new Vector2(414, 531));
            view.SetVisible("ID_TOKENBOOK_BASE", mode == 0);
            view.SetVisible("ID_TOKENBOOK_ITEMTRADE_TITLE", mode == 1);
            for (int i = 1; i <= 5; i++) { int row = i - 1; ui.On($"ID_TOKENBOOK_BUTTON{i:00}", _ => Ask(row)); }
            ui.On("ID_TOKENBOOK_CLOSE", _ => { EventUi.Sfx("SID_SYSWIN_DESTROY"); Close(); });
            Refresh();
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        static TokenRow Row(int i) { var list = Rows; int k = mode * 5 + i; return k < list.Count ? list[k] : null; }

        public static void Refresh()
        {
            if (!view) return;
            for (int i = 0; i < 5; i++)
            {
                var r = Row(i);
                for (int g = 0; g < 3; g++)
                {
                    string pos = $"ID_TOKENBOOK_POSITEM{i * 3 + g + 1:00}", name = $"ID_TOKENBOOK_ITEMNAME{i * 3 + g + 1:00}";
                    var gift = r != null && g < r.Gifts.Count ? r.Gifts[g] : default;
                    view.SetText(pos, "");
                    EventUi.Icon(view, pos, gift.item);
                    view.SetText(name, gift.item != null ? $"{EventItems.Name(gift.item)} {gift.count}" : "");
                }
                view.SetVisible($"ID_TOKENBOOK_BUTTON{i + 1:00}", r != null);
                view.SetEnabled($"ID_TOKENBOOK_BUTTON{i + 1:00}", r != null && P.Count(r.Currency) >= r.Count);
            }
            var first = Row(0);
            view.SetVisible("ID_TOKENBOOK_TOKENCOUNT", true);
            view.SetText("ID_TOKENBOOK_TOKENCOUNT", first != null ? P.Count(first.Currency).ToString() : "0");
        }

        static void Ask(int i)
        {
            var r = Row(i);
            if (r == null) return;
            MissionUi.Confirm(EventUi.T("10916", "Do you want to exchange %d %ss with this item?", r.Count, EventItems.Name(r.Currency)), () => Exchange(mode * 5 + i));
        }

        public static void Exchange(int row, Action<bool> done = null)
        {
            EventNet.Request("token.exchange", new Dictionary<string, object> { ["row"] = row }, () => ExchangeOffline(P, row), r =>
            {
                if (!r.Ok) UiPopup.Message(r.Error ?? "That didn't work.");
                else { EventUi.Sfx("SID_ITEM_BUY"); EventNet.Log(r.Grants); }
                Refresh();
                done?.Invoke(r.Ok);
            });
        }

        public static EventReply ExchangeOffline(PlayerProfile p, int row)
        {
            var r = row >= 0 && row < Rows.Count ? Rows[row] : null;
            if (r == null) return EventReply.Fail("No such exchange.");
            if (p.Count(r.Currency) < r.Count) return EventReply.Fail($"You need {r.Count} {EventItems.Name(r.Currency)}.");
            p.Give(r.Currency, -r.Count);
            var g = r.Gifts.Select(x => new RewardGrant { Kind = GrantKind.Item, ItemId = x.item, Count = x.count }).ToList();
            EventNet.Apply(p, g);
            return new EventReply { Ok = true, Grants = g };
        }

        // ---------------- the daily mission ----------------

        /// <summary>The Mission Girl: today's mission state, the token when it is done, then the book.</summary>
        public static void MissionGirl()
        {
            if (P == null) return;
            EventNet.Request("token.daily", new Dictionary<string, object>(), () => DailyOffline(P), r =>
            {
                string key = r.Ok ? "DayMission_Complete" : r.Error == "already" ? "DayMission_Already" : "DayMission_Ing";
                if (r.Ok) EventNet.Log(r.Grants);
                MissionUi.Script(EventUi.Ment(key), "Mission Girl", false, null, () => Open(0));
            });
        }

        /// <summary>Offline twin of token.daily: progress since the day's base counters.</summary>
        public static EventReply DailyOffline(PlayerProfile p)
        {
            var st = EventNet.State(p);
            EventObjects.RollDay(p);
            if (st.DayMission) return EventReply.Fail("already");
            int huvs = p.HuvCaught - st.DayHuvBase, races = p.RacesEntered + p.PvpRaces - st.DayRaceBase;
            if (huvs < DailyHuvs && races < DailyRaces) return EventReply.Fail("ing");
            st.DayMission = true;
            var g = new List<RewardGrant> { new RewardGrant { Kind = GrantKind.Item, ItemId = MissionToken, Count = 1 } };
            EventNet.Apply(p, g);
            return new EventReply { Ok = true, Grants = g };
        }

        public static void Close() { if (view && UiManager.Instance) UiManager.Instance.Close(view); view = null; }
    }
}
