using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Period items (DriftCity_RE\INVENTORY.md "Expiring items"): the client prints "Usage period: %d hours" (10398),
    /// "Expired" (10795 / 10405), "This item has already expired." (10454) when one is used, "You cannot discard paid-for
    /// items that have expiration dates." (10455) and marks them in the bag (MYINVEN SLOT_RENTnn, NDLG MYINVEN_EXPIRE_1);
    /// "Usage period will be added to the same item." (11044) when a second one arrives. The shop's periods are 7 / 30 /
    /// 90 days (vshopitem Period 7D..), the set parts' 720 / 180 h and the rental parts' 168 / 336 h (ItemClient.tdf).
    /// Remake rules: a stack's period is one expiry for the whole stack (StackItem.Until, Unix ms) and a new grant adds its
    /// period to it; parts carry their own (PerfPart.Until). When the time is up the server (src/items.js sweep, message
    /// item.expire) and offline this class take stacks away, turn set parts into their "_to" form (bonus lost, restorable
    /// with a lube) and remove rental parts.
    /// </summary>
    public static class ItemPeriods
    {
        static PlayerProfile P => PlayerProfile.Current;

        public static bool Expired(long until, long now) => until > 0 && until <= now;
        public static bool Expired(long until) => Expired(until, ItemUse.Now);

        /// <summary>The expiry of a bag entry (0 = none).</summary>
        public static long Until(BagEntry e)
        {
            if (e == null || P == null) return 0;
            if (e.Kind == BagKind.Part) return e.Part?.Until ?? 0;
            if (e.Kind == BagKind.Stack) return P.Items.Find(i => i.Id == e.Id)?.Until ?? 0;
            return 0;
        }

        public static bool IsTimed(BagEntry e) => Until(e) > 0;
        public static bool IsExpired(BagEntry e)
        {
            if (e == null) return false;
            if (e.Kind == BagKind.Part && SetParts.Of(e.Part)?.Expired == true) return true;
            return Expired(Until(e));
        }

        /// <summary>Hours left, rounded up (0 when expired or unlimited).</summary>
        public static int HoursLeft(long until) => until <= 0 ? 0 : Mathf.Max(0, Mathf.CeilToInt((until - ItemUse.Now) / 3600000f));

        /// <summary>The tooltip's period line: hours left, "Expired", the period a set part starts when fitted, or unlimited.</summary>
        public static string Line(BagEntry e)
        {
            if (IsExpired(e)) return ItemCatalog.Text("10795", "Expired");
            long until = Until(e);
            if (until > 0) return ItemCatalog.Text("10398", "Usage period: %d hours", HoursLeft(until));
            var row = e?.Kind == BagKind.Stack ? SetParts.Get(e.Id) : null;
            if (row != null && row.PeriodHours > 0) return ItemCatalog.Text("10398", "Usage period: %d hours", row.PeriodHours);
            return ItemCatalog.Text("10282", "Usage Period: Unlimited");
        }

        /// <summary>Gives a timed stack: the period is added to what the stack has left (11044). hours &lt;= 0: a plain grant.</summary>
        public static void GiveTimed(PlayerProfile p, string id, int count, int hours)
        {
            if (p == null || string.IsNullOrEmpty(id) || count <= 0) return;
            p.Give(id, count);
            if (hours <= 0) return;
            var it = p.Items.Find(i => i.Id == id);
            long now = ItemUse.Now;
            it.Until = System.Math.Max(it.Until, now) + hours * 3600_000L;
        }

        /// <summary>Takes away / converts what has expired; returns the names of the items that did.</summary>
        public static List<string> Sweep(PlayerProfile p, long now)
        {
            var gone = new List<string>();
            if (p == null) return gone;
            foreach (var it in p.Items.ToList())
            {
                if (!Expired(it.Until, now)) continue;
                gone.Add(ItemCatalog.Name(new BagEntry { Kind = BagKind.Stack, Id = it.Id }));
                p.Items.Remove(it);
            }
            foreach (var part in p.PerfParts.ToList())
            {
                if (!Expired(part.Until, now)) continue;
                var row = SetParts.Of(part);
                gone.Add(row?.Name ?? ItemCatalog.PartName(part));
                var to = row != null ? SetParts.Get(row.Counterpart) : null;
                if (to != null && to.Expired)
                {
                    part.Item = to.Id;
                    part.Until = 0;
                    continue;
                }
                foreach (var c in p.Cars) for (int i = 0; i < c.PerfSlots.Length; i++) if (c.PerfSlots[i] == part.Uid) c.PerfSlots[i] = -1;
                p.PerfParts.Remove(part);
            }
            return gone;
        }

        /// <summary>Anything due in the driver's bag right now?</summary>
        public static bool AnyDue(PlayerProfile p, long now) =>
            p != null && (p.Items.Any(i => Expired(i.Until, now)) || p.PerfParts.Any(x => Expired(x.Until, now)));

        static float nextCheck;
        static bool asking;

        /// <summary>
        /// Runs the expiry (item.expire online; the server sweeps and sends the driver). Called when the inventory opens and
        /// every few seconds by the quick-slot driver; prints "name: This item has already expired." (10454) for each one
        /// (10890 is the client's longer "expired ... still wish to purchase" shop warning).
        /// </summary>
        public static void Check(bool force = false)
        {
            var p = P;
            if (p == null || asking) return;
            if (!force && Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 5f;
            long now = ItemUse.Now;
            if (!AnyDue(p, now)) return;
            var names = p.Items.Where(i => Expired(i.Until, now)).Select(i => ItemCatalog.Name(new BagEntry { Kind = BagKind.Stack, Id = i.Id }))
                .Concat(p.PerfParts.Where(x => Expired(x.Until, now)).Select(x => SetParts.Of(x)?.Name ?? ItemCatalog.PartName(x))).ToList();
            asking = true;
            Progress.Op("item.expire", new Dictionary<string, object>(), () =>
            {
                Sweep(p, now);
                return new Progress.Result { Ok = true };
            }, r =>
            {
                asking = false;
                if (!r.Ok) return;
                foreach (var n in names) ChatBox.System((n.Length > 0 ? n + ": " : "") + ItemCatalog.Text("10454", "This item has already expired."));
                GameFlow.Instance?.ApplyCarStats();
                InventoryWindow.RefreshIfOpen();
                QuickSlots.Refresh();
            });
        }
    }
}
