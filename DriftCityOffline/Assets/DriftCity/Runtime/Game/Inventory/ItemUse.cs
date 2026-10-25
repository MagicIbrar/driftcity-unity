using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>A timed drink effect (PlayerProfile.Buffs): "exp", "mito" or "drop" until Until (Unix ms).</summary>
    [Serializable]
    public sealed class ItemBuff
    {
        public string Id;
        public long Until;
    }

    /// <summary>
    /// Using, discarding and selling bag items. The client checks the item kind and the place (Undercity 11478, chases
    /// 11862, "You cannot use this item at this time." 11100) and asks its server; online every change goes through our
    /// server (item.use / item.discard / item.sell, src/items.js + src/workshop.js), offline the same rules run here.
    /// Effects of the consumables are this rebuild's own (the client's server decided them): drinks last an hour
    /// (+30 % EXP, +50 % Mito, double drops as their descriptions say; one of a kind at a time, 11059), Mito cheques pay
    /// their value, Gio (i_d_repair01) repairs the car, Mittron oil fills the booster gauge.
    /// </summary>
    public static class ItemUse
    {
        static PlayerProfile P => PlayerProfile.Current;
        static Dictionary<string, object> D(params (string k, object v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);

        public static readonly Dictionary<string, string[]> Drinks = new Dictionary<string, string[]>
        {
            ["i_d_drk_01"] = new[] { "exp" }, ["i_d_drk_02"] = new[] { "mito" }, ["i_d_drk_03"] = new[] { "drop" },
            ["3ddrink"] = new[] { "exp", "mito", "drop" }, ["i_package_02"] = new[] { "exp", "mito", "drop" },
        };
        public const int ExpDrinkPct = 30, MitoDrinkPct = 50;
        public const long DrinkMs = 60L * 60 * 1000;
        static readonly Dictionary<string, string> Instant = new Dictionary<string, string>
        {
            ["i_d_repair01"] = "repair", ["mitron_oil"] = "boost", ["mitron_oil2"] = "boost", ["mitron_oil3"] = "boost",
            ["i_n_00004"] = "fuel", ["i_n_00005"] = "fuel", ["i_n_00006"] = "fuel",   // Mittron fuel (Game/Fuel/MittronFuel.cs): 2 / 10 / 300 litres
        };
        /// <summary>The parts-shop counter buys its goods back for half (src/items.js COUNTER_SELL).</summary>
        static readonly Dictionary<string, int> CounterSell = new Dictionary<string, int>
        {
            ["i_n_00003"] = 2500, ["i_n_00003_plus"] = 5000, ["i_n_00003_deluxe"] = 10000, ["i_n_Seed_N"] = 5000, ["i_n_Seed_S"] = 20000, ["i_n_Seed_H"] = 60000,
            ["user_create_kit1"] = 1500, ["user_create_kit2"] = 3000, ["user_create_kit3"] = 6000,
        };
        static readonly int[] PartSell = { 8000, 32000, 120000, 300000 };

        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Value of a Mito cheque (i_d_m_10k .. i_d_m_10g), 0 otherwise.</summary>
        public static long ChequeValue(string id)
        {
            var m = Regex.Match(id ?? "", @"^i_d_m_(\d+)(k|m|g)$");
            if (!m.Success) return 0;
            long n = long.Parse(m.Groups[1].Value);
            return n * (m.Groups[2].Value == "k" ? 1000L : m.Groups[2].Value == "m" ? 1000000L : 1000000000L);
        }

        /// <summary>Lucky bags, coupons, scratch cards, token books and event boxes: the events system opens these.</summary>
        public static bool IsEventItem(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (EventApi("IsBag", id) is bool b) return b;
            return id.StartsWith("lotto_") || id.StartsWith("gacha_") || id.StartsWith("coupon_") || id.StartsWith("event_") || id.StartsWith("sumfest_eventbox")
                || id.StartsWith("i_sp_giftbox") || id.Contains("luckybag") || id.Contains("tokenbook") || id == "i_d_partsbox";
        }

        /// <summary>What using the item does (the tooltip line), or null when it isn't a usable item.</summary>
        public static string Describe(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (Drinks.TryGetValue(id, out var kinds))
                return string.Join(", ", kinds.Select(k => k == "exp" ? $"+{ExpDrinkPct}% EXP" : k == "mito" ? $"+{MitoDrinkPct}% Mito" : "double item drops")) + " for an hour (not on quests)";
            if (SpecialDrink.Is(id)) return SpecialDrink.Describe(id);
            long cheque = ChequeValue(id);
            if (cheque > 0) return $"Use it to receive {cheque:N0} Mito.";
            if (Instant.TryGetValue(id, out var fx)) return fx == "repair" ? "Repairs your car on the spot." : fx == "fuel" ? $"Adds {MittronFuel.ItemLitres(id)} litres of Mittron fuel." : "Fills your booster gauge.";
            var crafted = Crafting.UseText(id);
            if (crafted != null) return crafted;
            if (IsEventItem(id)) return "Right-click to open it.";
            return null;
        }

        public static bool Usable(BagEntry e) => e != null && (e.Kind == BagKind.Part || e.Kind == BagKind.Licence || e.Kind == BagKind.Shop && ShopData.Paints.Any(x => x.UniqueId == e.ItemId)
                                                              || e.Kind == BagKind.Stack && (Describe(e.Id) != null || SetParts.Is(e.Id) || e.Id == SetParts.AlphaLube || e.Id == SetParts.BetaLube));

        /// <summary>Usable from a quick slot while driving (the consumables).</summary>
        public static bool QuickUsable(BagEntry e) => e != null && e.Kind == BagKind.Stack && (Drinks.ContainsKey(e.Id) || SpecialDrink.Is(e.Id) || Instant.ContainsKey(e.Id) || ChequeValue(e.Id) > 0
                                                                                              || e.Id.StartsWith("i_bacchus_"));

        public static long SellPrice(BagEntry e)
        {
            if (e == null) return 0;
            if (e.Kind == BagKind.Stack) return CounterSell.TryGetValue(e.Id, out int v) ? (long)v * e.Count : 0;
            if (e.Kind == BagKind.Part && e.Part != null) return (long)Mathf.Floor(PartSell[Mathf.Clamp(e.Part.Grade, 0, 3)] * (1 + 0.1f * e.Part.Enhance));
            return 0;
        }

        /// <summary>One-unit sale price (the tooltip and the sell confirmation).</summary>
        public static long UnitSellPrice(BagEntry e) => e != null && e.Kind == BagKind.Stack ? (CounterSell.TryGetValue(e.Id, out int v) ? v : 0) : SellPrice(e);

        /// <summary>Everything in the bag can be traded (fitted parts never are in the bag) except licences; src/trade.js applies the same rule.</summary>
        public static bool Tradeable(BagEntry e) => e != null && e.Kind != BagKind.Licence && !(ItemPeriods.IsTimed(e) && !ItemPeriods.IsExpired(e));

        // ---------------- timed drinks ----------------
        public static List<ItemBuff> Active(PlayerProfile p)
        {
            if (p == null) return new List<ItemBuff>();
            if (p.Buffs == null) p.Buffs = new List<ItemBuff>();
            long now = Now;
            p.Buffs.RemoveAll(b => b == null || b.Until <= now);
            return p.Buffs;
        }

        public static bool HasBuff(PlayerProfile p, string kind) => Active(p).Any(b => b.Id == kind);
        /// <summary>Reward bonus percents of the active drinks (mirror of src/items.js buffBonus): add to Progress.Extras' expPct / mitoPct.</summary>
        public static int ExpBonus(PlayerProfile p, string kind) => kind != "quest" && HasBuff(p, "exp") ? ExpDrinkPct : 0;
        public static int MitoBonus(PlayerProfile p, string kind) => kind != "quest" && HasBuff(p, "mito") ? MitoDrinkPct : 0;
        public static bool DoubleDrops(PlayerProfile p) => HasBuff(p, "drop");

        /// <summary>The remaining time of the drink an item starts, for the tooltip.</summary>
        public static string BuffLine(string id)
        {
            if (!Drinks.TryGetValue(id ?? "", out var kinds)) return null;
            var b = Active(P).Where(x => kinds.Contains(x.Id)).OrderByDescending(x => x.Until).FirstOrDefault();
            if (b == null) return null;
            var left = TimeSpan.FromMilliseconds(b.Until - Now);
            return $"Active: {(int)left.TotalMinutes}:{left.Seconds:00} left";
        }

        // ---------------- use ----------------
        /// <summary>
        /// Uses an item: consumables are spent, paints are applied, parts are fitted on <paramref name="carSlot"/> (the car the
        /// inventory shows). quiet: no confirmation popups (quick slots). done(true) when something happened.
        /// </summary>
        public static void Use(BagEntry e, bool quiet = false, Action<bool> done = null, int carSlot = -1)
        {
            if (e == null || P == null) { done?.Invoke(false); return; }
            if (carSlot < 0) carSlot = P.CurrentCar;
            switch (e.Kind)
            {
                case BagKind.Part: Equip(e.Part, carSlot, done); return;
                case BagKind.Shop: ApplyPaint(e, carSlot, quiet, done); return;
                case BagKind.Licence: LicenceInfo.Mount(e.LicenceId, done); return;
            }
            string id = e.Id;
            if (id == Services.DriverRename.Kit) { Services.DriverRename.Open(); done?.Invoke(false); return; }   // the rename kit opens its dialog
            if (ItemPeriods.IsExpired(e)) { Refuse(ItemCatalog.Text("10454", "This item has already expired."), quiet); done?.Invoke(false); return; }
            if (SetParts.Is(id)) { SetParts.Fit(carSlot, id, done); return; }   // a set part from a reward: fitting starts its period
            if (id == SetParts.AlphaLube || id == SetParts.BetaLube) { InventoryWindow.ShowRestore(id); done?.Invoke(false); return; }
            if (UndercityStage.Active && !quiet) { Refuse(ItemCatalog.Text("11478", "This item cannot be used in Undercity. Please move to the city and make the request again."), quiet); done?.Invoke(false); return; }
            if (Crafting.UseText(id) != null)
            {
                if (quiet) { WorkshopMenus.UseItem(id); done?.Invoke(true); return; }
                UiPopup.Show($"{e.Name}  x{P.Count(id)}\n{Crafting.UseText(id)}\nUse it now?", b => { if (b == UiPopup.Yes) WorkshopMenus.UseItem(id); done?.Invoke(b == UiPopup.Yes); }, UiPopup.Yes, UiPopup.No);
                return;
            }
            if (SpecialDrink.Is(id)) { SpecialDrink.Use(id, ok => { InventoryWindow.RefreshIfOpen(); QuickSlots.Refresh(); done?.Invoke(ok); }); return; }   // buff_item: Halloween Drink (Events/PropEvent.cs)
            if (IsEventItem(id)) { OpenEventItem(id, done); return; }
            if (!Drinks.ContainsKey(id) && !Instant.ContainsKey(id) && ChequeValue(id) <= 0)
            {
                Refuse(ItemCatalog.Text("11100", "You cannot use this item at this time."), quiet);
                done?.Invoke(false);
                return;
            }
            if (quiet) { Consume(id, done); return; }
            UiPopup.Show($"{e.Name}  x{P.Count(id)}\n{Describe(id)}\nUse it now?", b => { if (b == UiPopup.Yes) Consume(id, done); else done?.Invoke(false); }, UiPopup.Yes, UiPopup.No);
        }

        static void Refuse(string text, bool quiet)
        {
            if (quiet) ChatBox.System(text);
            else UiPopup.Message(text);
        }

        /// <summary>Spends a consumable (item.use on the server) and applies its effect to the car / profile.</summary>
        static void Consume(string id, Action<bool> done)
        {
            int slot = P.CurrentCar;
            string effect = null;
            long mito = 0;
            Progress.Op("item.use", D(("id", id), ("slot", slot)), () =>
            {
                if (P.Count(id) <= 0) return new Progress.Result { Error = "You don't have that." };
                if (Drinks.TryGetValue(id, out var kinds))
                {
                    var active = Active(P);
                    if (kinds.Any(k => active.Any(b => b.Id == k)))
                        return new Progress.Result { Error = ItemCatalog.Text("11059", "Having too much Special Drink will cause serious health problem. Please wait until the effect wears off.") };
                    foreach (var k in kinds) active.Add(new ItemBuff { Id = k, Until = Now + DrinkMs });
                    effect = "drink";
                }
                else if (ChequeValue(id) > 0) { mito = ChequeValue(id); P.Mito += mito; effect = "mito"; }
                else effect = Instant.TryGetValue(id, out var fx) ? fx : null;
                P.Give(id, -1);
                return new Progress.Result { Ok = true, Mito = mito };
            }, r =>
            {
                if (r.Ok)
                {
                    if (effect == null) effect = Drinks.ContainsKey(id) ? "drink" : ChequeValue(id) > 0 ? "mito" : Instant.TryGetValue(id, out var fx) ? fx : null;
                    ApplyEffect(id, effect);
                }
                InventoryWindow.RefreshIfOpen();
                QuickSlots.Refresh();
                done?.Invoke(r.Ok);
            });
        }

        /// <summary>The part of an effect that lives on the driven car (the server only spends the item).</summary>
        static void ApplyEffect(string id, string effect)
        {
            var flow = GameFlow.Instance;
            var car = flow ? flow.PlayerCar : null;
            string name = ItemCatalog.Name(new BagEntry { Kind = BagKind.Stack, Id = id });
            switch (effect)
            {
                case "repair":
                    var fx = car ? car.GetComponentInChildren<CarEffects>() : null;
                    if (fx) fx.Damage01 = 0f;
                    GameEffects.RepairGirl(car, id == "i_d_repair02");   // the repair girl's banner and car NIFs
                    break;
                case "fuel":
                    MittronFuel.Add(MittronFuel.ItemLitres(id));
                    AudioLibrary.PlaySfx("sfx/SID_CHARGINGSTATION_FINISH");
                    break;
                case "boost":
                    if (car) car.BoostGauge = 1f;
                    AudioLibrary.PlaySfx("sfx/SID_TURBO_USE");
                    break;
                default:
                    AudioLibrary.PlaySfx("sfx/SID_ITEM_INSTALL");
                    break;
            }
            ChatBox.System($"Used {name}." + (effect == "mito" ? $" Mito +{ChequeValue(id):N0}" : effect == "drink" ? $" ({Describe(id)})" : ""));
            if (flow) flow.RefreshHud();
        }

        /// <summary>Fits a performance part on a car's slot of its type (perf.fit), replacing what was there.</summary>
        public static void Equip(PerfPart part, int carSlot, Action<bool> done = null)
        {
            if (part == null || P == null || carSlot < 0 || carSlot >= P.Cars.Count) { done?.Invoke(false); return; }
            if (ItemPeriods.Expired(part.Until)) { UiPopup.Message(ItemCatalog.Text("10454", "This item has already expired.")); ItemPeriods.Check(true); done?.Invoke(false); return; }
            var rentRefusal = Services.RentalCars.UpgradeRefusal(P.Cars[carSlot]);
            if (rentRefusal != null) { UiPopup.Message(rentRefusal); done?.Invoke(false); return; }   // rental cars take no parts (10952)
            var setRow = SetParts.Of(part);
            if (setRow != null && P.Level < setRow.Level)
            {
                UiPopup.Message(ItemCatalog.Text("10451", "Your level is not high enough to equip this item."));
                done?.Invoke(false);
                return;
            }
            if (setRow == null && part.Grade < 3 && P.Level < PerfRules.ReqLevel[part.Grade])
            {
                UiPopup.Message(ItemCatalog.Text("10451", "Your level is not high enough to equip this item."));
                done?.Invoke(false);
                return;
            }
            if (carSlot == P.CurrentCar && GameFlow.BlockedByMission()) { done?.Invoke(false); return; }
            int uid = part.Uid, type = part.Type;
            Progress.Op("perf.fit", D(("slot", carSlot), ("type", type), ("uid", uid)), () =>
            {
                if (!P.PerfParts.Exists(x => x.Uid == uid)) return new Progress.Result { Error = "No such part." };
                foreach (var c in P.Cars) for (int i = 0; i < c.PerfSlots.Length; i++) if (c.PerfSlots[i] == uid) c.PerfSlots[i] = -1;
                P.Cars[carSlot].PerfSlots[type] = uid;
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (r.Ok) { AudioLibrary.PlaySfx("sfx/SID_ITEM_INSTALL"); GameFlow.Instance?.ApplyCarStats(); }
                InventoryWindow.RefreshIfOpen();
                done?.Invoke(r.Ok);
            });
        }

        /// <summary>Takes the part of a stat slot off a car (perf.fit with uid -1); it goes back into the bag.</summary>
        public static void Unequip(int carSlot, int type, Action<bool> done = null)
        {
            if (P == null || carSlot < 0 || carSlot >= P.Cars.Count || type < 0 || type > 3) { done?.Invoke(false); return; }
            if (carSlot == P.CurrentCar && GameFlow.BlockedByMission()) { done?.Invoke(false); return; }
            Progress.Op("perf.fit", D(("slot", carSlot), ("type", type), ("uid", -1)), () =>
            {
                P.Cars[carSlot].PerfSlots[type] = -1;
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (r.Ok) GameFlow.Instance?.ApplyCarStats();
                InventoryWindow.RefreshIfOpen();
                done?.Invoke(r.Ok);
            });
        }

        /// <summary>Paints in the bag recolour the car they are applied to (the paint item stays in the bag, as before).</summary>
        static void ApplyPaint(BagEntry e, int carSlot, bool quiet, Action<bool> done)
        {
            var paint = ShopData.Paints.FirstOrDefault(x => x.UniqueId == e.ItemId);
            if (paint == null) { UiPopup.Message(ItemCatalog.Text("10449", "This item is not equippable.")); done?.Invoke(false); return; }
            var car = P.Cars[Mathf.Clamp(carSlot, 0, P.Cars.Count - 1)];
            string carName = OriginalMenus.CarName(car);
            if (car.PaintItem == paint.UniqueId) { UiPopup.Message($"Your {carName} already wears {paint.Name}."); done?.Invoke(false); return; }
            System.Action apply = () =>
            {
                car.PaintItem = paint.UniqueId;
                P.Save();
                if (OnlineSession.Active) OnlineSession.Instance.PushProfileUpdate();
                if (P.Cars.IndexOf(car) == P.CurrentCar) GameFlow.Instance?.RepaintCar();
                AudioLibrary.PlaySfx("sfx/SID_ITEM_INSTALL");
                InventoryWindow.RefreshIfOpen();
                done?.Invoke(true);
            };
            if (quiet) { apply(); return; }
            UiPopup.Show($"{paint.Name} paint\nApply it to your {carName}?", id => { if (id == UiPopup.Yes) apply(); else done?.Invoke(false); }, UiPopup.Yes, UiPopup.No);
        }

        // ---------------- discard / sell ----------------
        /// <summary>Throws <paramref name="count"/> of an entry away (item.discard). The window asks first (10509 / 11126).</summary>
        public static void Discard(BagEntry e, int count, Action<bool> done = null)
        {
            if (e == null || P == null) { done?.Invoke(false); return; }
            string refuse = DiscardRefusal(e);
            if (refuse != null) { UiPopup.Message(refuse); done?.Invoke(false); return; }
            count = e.Kind == BagKind.Part ? 1 : Mathf.Clamp(count, 1, e.Count);
            var body = e.Ref(count);
            var key = e.Key;
            Progress.Op("item.discard", body, () =>
            {
                var now = ItemCatalog.Find(P, key);
                if (now == null || now.Count < count) return new Progress.Result { Error = "You don't have that." };
                Remove(now, count);
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (r.Ok) AudioLibrary.PlaySfx("sfx/SID_ITEM_DROP");
                QuickSlots.Refresh();
                InventoryWindow.RefreshIfOpen();
                done?.Invoke(r.Ok);
            });
        }

        /// <summary>Why an entry can't be thrown away: licences (10456), period items that are still running (10455).</summary>
        public static string DiscardRefusal(BagEntry e)
        {
            if (e == null) return null;
            if (e.Kind == BagKind.Licence) return ItemCatalog.Text("10456", "This item cannot be discarded.");
            if (ItemPeriods.IsTimed(e) && !ItemPeriods.IsExpired(e)) return ItemCatalog.Text("10455", "You cannot discard paid-for items that have expiration dates.");
            return null;
        }

        /// <summary>Sells an entry (all of a stack) for its sale price (item.sell).</summary>
        public static void Sell(BagEntry e, int count, Action<bool> done = null)
        {
            if (e == null || P == null) { done?.Invoke(false); return; }
            count = e.Kind == BagKind.Part ? 1 : Mathf.Clamp(count, 1, e.Count);
            long unit = UnitSellPrice(e);
            if (unit <= 0) { UiPopup.Message("This item cannot be sold."); done?.Invoke(false); return; }
            var key = e.Key;
            Progress.Op("item.sell", e.Ref(count), () =>
            {
                var now = ItemCatalog.Find(P, key);
                if (now == null || now.Count < count) return new Progress.Result { Error = "You don't have that." };
                Remove(now, count);
                P.Mito += unit * count;
                return new Progress.Result { Ok = true, Mito = unit * count };
            }, r =>
            {
                if (r.Ok) { AudioLibrary.PlaySfx("sfx/SID_ITEM_SELL"); ChatBox.System($"Sold for {unit * count:N0} Mito."); }
                QuickSlots.Refresh();
                InventoryWindow.RefreshIfOpen();
                done?.Invoke(r.Ok);
            });
        }

        static void Remove(BagEntry e, int count)
        {
            switch (e.Kind)
            {
                case BagKind.Stack: P.Give(e.Id, -count); break;
                case BagKind.Shop:
                    var it = P.Inventory.Find(x => x.ItemId == e.ItemId);
                    if (it != null) { it.Count -= count; if (it.Count <= 0) P.Inventory.Remove(it); }
                    break;
                case BagKind.Part:
                    foreach (var c in P.Cars) for (int i = 0; i < c.PerfSlots.Length; i++) if (c.PerfSlots[i] == e.Part.Uid) c.PerfSlots[i] = -1;
                    P.PerfParts.RemoveAll(x => x.Uid == e.Part.Uid);
                    break;
            }
        }

        // ---------------- events system hook ----------------
        // Lucky bags, coupons and token books belong to the events system (another module). It is looked up by name so this
        // file compiles with or without it: a static class DriftCity.Game.LuckyBag with Open(string itemId, Action<bool> done)
        // (and optionally bool IsBag(string itemId)). HOOK: if the events module uses another name, add it to EventTypes.
        static readonly string[] EventTypes = { "DriftCity.Game.LuckyBag", "DriftCity.Game.Events.LuckyBag", "DriftCity.Game.EventItems" };

        static object EventApi(string method, string id, Action<bool> done = null)
        {
            foreach (var name in EventTypes)
            {
                var t = Type.GetType(name) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(x => x != null);
                if (t == null) continue;
                var withDone = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Action<bool>) }, null);
                if (withDone != null) return withDone.Invoke(null, new object[] { id, done }) ?? true;
                var plain = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (plain != null) return plain.Invoke(null, new object[] { id }) ?? true;
            }
            return null;
        }

        static void OpenEventItem(string id, Action<bool> done)
        {
            object r = null;
            try { r = EventApi("Open", id, ok => { InventoryWindow.RefreshIfOpen(); QuickSlots.Refresh(); done?.Invoke(ok); }); }
            catch (Exception ex) { Debug.LogException(ex); }
            if (r != null) return;
            Debug.LogWarning($"Inventory: no events module to open {id} (expected DriftCity.Game.LuckyBag.Open(string, Action<bool>))");
            UiPopup.Message($"{ItemCatalog.Name(new BagEntry { Kind = BagKind.Stack, Id = id })}\nThis item is opened by the event system, which isn't available here yet.");
            done?.Invoke(false);
        }
    }
}
