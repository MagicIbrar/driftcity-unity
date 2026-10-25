using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The driver's event progress (PlayerProfile.Events; the server keeps the same object in its profile JSON):
    /// coupon book counters and claims, the daily event-drop bookkeeping and the offline stand-ins for the server-wide
    /// state (scratch-card gauge, Christmas trees, harvest receipts). DriftCity_RE\EVENTS.md.
    /// </summary>
    [Serializable]
    public sealed class EventState
    {
        /// <summary>Coupon counters per category 0..9 (battles, deliveries, chases, combos, driving seconds, drifts,
        /// skill passes, jumps, reckless seconds, game modes), as the client's coupon singleton 0x6ce4b0 keeps them.</summary>
        public float[] Coupon = new float[10];
        /// <summary>Claimed coupons ("cat:idx", record state 3) and acknowledged notifications (state 2).</summary>
        public List<string> CouponClaimed = new List<string>();
        public List<string> CouponSeen = new List<string>();
        /// <summary>UTC day (yyyy-MM-dd) of the daily counters.</summary>
        public string Day = "";
        public int DayTokens, DayDrops, DayHuvBase = -1, DayRaceBase = -1;
        /// <summary>HUV / race counters already turned into event drops (Santa boxes, harvest boxes, mission tokens).</summary>
        public int SeenHuv = -1, SeenRaces = -1;
        public bool DayMission;
        /// <summary>Offline stand-ins for the server-wide state.</summary>
        public string LottoDay = "";
        public int LottoCards;
        public int[] XmasMask = new int[4];
        public long HarvestReceipts;
        public string AttendDay = "";
        /// <summary>The last VALEN result code (1-24: recipe, paint, grade; op 0x89 sub 26) per basket item, "id=code".</summary>
        public List<string> BasketCodes = new List<string>();
        /// <summary>UTC day the props NPC (Indy4) reward was taken.</summary>
        public string PropsDay = "";

        public void Normalize()
        {
            if (Coupon == null || Coupon.Length != 10) { var c = new float[10]; if (Coupon != null) Array.Copy(Coupon, c, Math.Min(10, Coupon.Length)); Coupon = c; }
            CouponClaimed ??= new List<string>();
            CouponSeen ??= new List<string>();
            if (XmasMask == null || XmasMask.Length != 4) XmasMask = new int[4];
            Day ??= ""; LottoDay ??= ""; AttendDay ??= ""; PropsDay ??= "";
            BasketCodes ??= new List<string>();
        }
    }

    /// <summary>A row of World/Data/UseItemClient.tdf (the consumable item table): id | type | name | pool | ... | desc | price | sell | value.</summary>
    public sealed class UseItemRow
    {
        public string Id, Type, Name, Pool, Col4, Col5, Desc;
        public int Price, Sell, Value;
    }

    /// <summary>Item names, types and reward pools of the event items (useitemclient.tdf, init\rewardgroup.xlt).</summary>
    public static class EventItems
    {
        static Dictionary<string, UseItemRow> items;
        static RewardGroups pools;

        public static RewardGroups Pools
        {
            get
            {
                if (pools != null) return pools;
                byte[] data = null;
                try { data = DriftCityData.Files?.Read(@"init\rewardgroup.xlt"); } catch (Exception ex) { Debug.LogWarning("rewardgroup.xlt: " + ex.Message); }
                return pools = RewardGroups.Parse(data);
            }
        }

        static Dictionary<string, UseItemRow> Items
        {
            get
            {
                if (items != null) return items;
                items = new Dictionary<string, UseItemRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in MissionTables.Read("useitemclient.tdf"))
                {
                    if (r.Length < 10 || string.IsNullOrWhiteSpace(r[0])) continue;
                    var row = new UseItemRow
                    {
                        Id = r[0].Trim(), Type = r[1].Trim(), Name = Clean(r[2]), Pool = Clean(r[3]), Col4 = Clean(r[4]), Col5 = Clean(r[5]),
                        Desc = r[6] == "n/a" ? "" : r[6], Price = MissionTables.Int(r, 7), Sell = MissionTables.Int(r, 8), Value = MissionTables.Int(r, 9),
                    };
                    if (!items.ContainsKey(row.Id)) items[row.Id] = row;
                }
                return items;
            }
        }

        static string Clean(string s) => s == null || s.Trim() == "n/a" ? "" : s.Trim();

        public static UseItemRow Row(string id) => id != null && Items.TryGetValue(id.Trim(), out var r) ? r : null;
        public static string Type(string id) => Row(id)?.Type ?? "";
        public static string Pool(string id) => Row(id)?.Pool ?? "";

        /// <summary>The item's name: the visual shop / manufacturing names first (as the inventory shows them), else the item table's.</summary>
        /// <summary>English names for the Korean-only event items the remake uses (the Indy4 props, 2008).</summary>
        static readonly Dictionary<string, string> KrOnly = new Dictionary<string, string>
        {
            ["i_n_00051"] = "Indy's Fedora", ["i_n_00052"] = "Indy's Whip", ["i_n_00053"] = "Crystal Skull", ["i_n_00054"] = "Indiana Jones 4 Raffle Ticket",
        };

        public static string Name(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            if (KrOnly.TryGetValue(id, out var kr)) return kr;
            string n = null;
            try { n = ItemCatalog.Name(new BagEntry { Kind = BagKind.Stack, Id = id }); } catch (Exception) { }
            if (!string.IsNullOrEmpty(n) && n != id) return n;
            var row = Row(id);
            if (row != null && row.Name.Length > 0 && !row.Name.Any(c => c >= '가' && c <= '힣')) return row.Name;
            var car = CarForKey(id);
            if (car != null) return car.UiName ?? car.CarName ?? id;
            return row != null && row.Name.Length > 0 ? row.Name : id;
        }

        public static string Desc(string id) => ItemCatalog.Clean(Row(id)?.Desc ?? "");

        /// <summary>A car key item (pc_0063s, type "car" or a reward pool row): the player car whose model starts with the key's "pc_NNNN".</summary>
        public static VehicleInfo CarForKey(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith("pc_", StringComparison.OrdinalIgnoreCase) || id.Length < 7) return null;
            string stem = id.Substring(0, 7).ToLowerInvariant();
            return GameTables.PlayerCars?.FirstOrDefault(v => v.ModelBase.StartsWith(stem));
        }

        public static string Describe(RewardGrant g)
        {
            switch (g.Kind)
            {
                case GrantKind.Exp: return $"{g.Count:N0} EXP";
                case GrantKind.Mito: return $"{g.Count:N0} Mito";
                case GrantKind.Bust: return Desc("lotto_00001") is string s && s.Length > 0 ? s : "Sorry, maybe next time!";
                default:
                    var car = CarForKey(g.ItemId);
                    if (car != null) return $"{car.UiName ?? car.CarName} (V{Mathf.Max(1, g.Etc)})";
                    return g.Count > 1 ? $"{Name(g.ItemId)} x{g.Count}" : Name(g.ItemId);
            }
        }

        /// <summary>The icon name a grant shows (an item's id; Mito the client's "i_d_m_10k" cheque, EXP "exp_icon").</summary>
        public static string Icon(RewardGrant g) => g.Kind == GrantKind.Mito ? "i_d_m_10k" : g.Kind == GrantKind.Exp ? "exp_icon" : g.Kind == GrantKind.Bust ? "lotto_00001" : g.ItemId;
    }

    /// <summary>A server/offline answer of an event action.</summary>
    public sealed class EventReply
    {
        public bool Ok;
        public string Error;
        public List<RewardGrant> Grants = new List<RewardGrant>();
        public Dictionary<string, object> Data = new Dictionary<string, object>();
        public static EventReply Fail(string error) => new EventReply { Ok = false, Error = error };
    }

    /// <summary>
    /// Every event action goes through here: online the server decides (draws, costs, limits; src/events.js) and sends the
    /// driver back; offline the same rule runs on the local profile.
    /// </summary>
    public static class EventNet
    {
        static PlayerProfile P => PlayerProfile.Current;

        /// <summary>Tests: a fixed seed for the offline draws.</summary>
        public static System.Random Rng = new System.Random();

        public static void Request(string type, Dictionary<string, object> body, Func<EventReply> offline, Action<EventReply> done)
        {
            EventObjects.Ensure();
            if (!OnlineSession.Active)
            {
                EventReply r;
                try { r = offline(); }
                catch (Exception ex) { Debug.LogException(ex); r = EventReply.Fail("That didn't work."); }
                if (r.Ok) { P?.Save(); GameFlow.Instance?.RefreshHud(); }
                done?.Invoke(r);
                return;
            }
            OnlineSession.Instance.Net.Request(type, body ?? new Dictionary<string, object>(), m =>
            {
                OnlineSession.ApplyDriver(m.O("driver"));
                var r = new EventReply { Ok = m.B("ok"), Error = m.S("error"), Grants = ParseGrants(m.L("grants")), Data = m };
                done?.Invoke(r);
            });
        }

        public static List<RewardGrant> ParseGrants(List<object> list)
        {
            var res = new List<RewardGrant>();
            if (list == null) return res;
            foreach (var o in list)
            {
                if (!(o is Dictionary<string, object> d)) continue;
                var kind = d.S("kind", "item");
                res.Add(new RewardGrant
                {
                    Kind = kind == "exp" ? GrantKind.Exp : kind == "mito" ? GrantKind.Mito : kind == "bust" ? GrantKind.Bust : GrantKind.Item,
                    ItemId = d.S("item"), Count = d.I("count", 1), Etc = d.I("etc"), Days = d.I("days"),
                });
            }
            return res;
        }

        /// <summary>Gives the grants to the local driver (offline rule; the server does the same in src/events.js grant()).</summary>
        public static void Apply(PlayerProfile p, List<RewardGrant> grants)
        {
            if (p == null || grants == null) return;
            foreach (var g in grants)
            {
                switch (g.Kind)
                {
                    case GrantKind.Exp: p.AddExp(g.Count); break;
                    case GrantKind.Mito: p.Mito += g.Count; break;
                    case GrantKind.Item:
                        var car = EventItems.CarForKey(g.ItemId);
                        if (car != null && !p.Cars.Exists(c => c.VehicleIndex == car.Index) && p.Cars.Count < 40)
                            p.Cars.Add(new OwnedCar { VehicleIndex = car.Index, Color = 0, Grade = Mathf.Max(0, g.Etc - 1) });
                        else p.Give(g.ItemId, Mathf.Max(1, g.Count));
                        break;
                }
            }
        }

        /// <summary>Takes one item (false when the driver has none).</summary>
        public static bool Take(PlayerProfile p, string id, int n = 1)
        {
            if (p == null || string.IsNullOrEmpty(id) || p.Count(id) < n) return false;
            p.Give(id, -n);
            return true;
        }

        public static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

        /// <summary>Chat lines for what was obtained (11393/10096-style "Obtained ..." lines).</summary>
        public static void Log(List<RewardGrant> grants)
        {
            foreach (var g in grants)
            {
                if (g.Kind == GrantKind.Bust) continue;
                ChatBox.System($"Obtained {EventItems.Describe(g)}.");
            }
        }

        public static EventState State(PlayerProfile p)
        {
            if (p == null) return new EventState();
            if (p.Events == null) p.Events = new EventState();
            p.Events.Normalize();
            return p.Events;
        }
    }

    /// <summary>Small UI helpers shared by the event screens.</summary>
    static class EventUi
    {
        /// <summary>Puts an item's icon into a control (its first image), or the client's "not available" icon.</summary>
        public static void Icon(NuiView v, string control, string item, bool show = true)
        {
            var c = v ? v.Control(control) : null;
            if (!c) return;
            c.gameObject.SetActive(show);
            if (!show) return;
            var holder = c.transform.Find("event icon") as RectTransform;
            if (string.IsNullOrEmpty(item)) { if (holder) holder.gameObject.SetActive(false); return; }
            var tex = OriginalMenus.ItemIcon(item, out var uv);
            if (!tex) tex = OriginalMenus.CmnImage(1870000, item, out uv);   // CMN_CARCOUPON: the coupon items' art
            if (!tex) tex = OriginalMenus.NotAvailableIcon(out uv);
            if (!holder)
            {
                holder = NuiView.NewRect("event icon", c.transform);
                var crt = (RectTransform)c.transform;
                var size = crt.sizeDelta.x > 4 ? crt.sizeDelta : new Vector2(64, 64);
                var first = c.GetComponentInChildren<RectTransform>(true);
                foreach (Transform ch in c.transform) if (ch is RectTransform r && r.sizeDelta.x > 4) { size = r.sizeDelta; break; }
                holder.sizeDelta = new Vector2(Mathf.Min(64, size.x), Mathf.Min(64, size.y));
                holder.anchoredPosition = new Vector2((size.x - holder.sizeDelta.x) / 2, -(size.y - holder.sizeDelta.y) / 2);
                var ri = holder.gameObject.AddComponent<UnityEngine.UI.RawImage>();
                ri.raycastTarget = false;
            }
            holder.gameObject.SetActive(tex);
            var img = holder.GetComponent<UnityEngine.UI.RawImage>();
            img.texture = tex; img.uvRect = uv; img.color = Color.white;
        }

        public static void Alpha(NuiView v, string control, float a)
        {
            var c = v ? v.Control(control) : null;
            if (!c) return;
            foreach (var g in c.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) { var col = g.color; col.a = a; g.color = col; }
        }

        /// <summary>A dark panel behind a dialog whose background art isn't in this install (TOKENBOOK's Menu_DailyToken.dds, LOTTO's ui_JAPAN.dds).</summary>
        public static void Backdrop(NuiView v, Vector2 pos, Vector2 size)
        {
            if (!v) return;
            var anyControl = v.Controls.FirstOrDefault();
            var parent = anyControl ? anyControl.transform.parent : v.transform;
            var rt = NuiView.NewRect("event backdrop", parent);
            rt.anchoredPosition = new Vector2(pos.x, -pos.y);
            rt.sizeDelta = size;
            rt.SetAsFirstSibling();
            var img = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
            img.color = new Color(0.08f, 0.1f, 0.14f, 0.94f);
            img.raycastTarget = true;
        }

        /// <summary>Keeps every control hidden except those listed (dialogs whose K=0 art the client reveals by code).</summary>
        public static void HideAll(NuiView v, Func<string, bool> keep)
        {
            if (!v) return;
            foreach (var c in v.Controls) if (c && c.Data.IdName != null && !keep(c.Data.IdName)) c.gameObject.SetActive(false);
        }

        public static string T(string key, string fallback, params object[] args) => ItemCatalog.Text(key, fallback, args).Replace("\\n", "\n");

        /// <summary>An NPC "ment" from globaltext with the event portrait tags (&lt;daymissionimg&gt;, &lt;ghrestaurantimg&gt;...) as the plain &lt;pimg&gt; QUESTDIALOG reads.</summary>
        public static string Ment(string key)
        {
            var s = GameTables.Text(key) ?? "";
            if (s == key) return "";
            s = s.Replace("\\n", "\n");
            return System.Text.RegularExpressions.Regex.Replace(s, "<(daymissionimg|ghrestaurantimg|ghdistributionimg|eventimg|dormantimg|mita500img|bacchusimg) ", "<pimg ");
        }

        public static void Sfx(string name) => AudioLibrary.PlaySfx("sfx/" + name);
    }
}
