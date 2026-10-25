using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DriftCity.Game
{
    [Serializable]
    public sealed class OwnedCar
    {
        public int VehicleIndex;
        public int Color;          // 0..3, the creation-screen paint slots
        public int BodyVariant = 1;
        public int Grade;              // index into the car's vehicleupgrade grades (V1, V2, ...)
        public int PaintItem = -1;     // vshopitem UniqueId of a bought paint, or -1 for the creation colour
        // custom shop: parts bought for this car (vshopitem UniqueIds) and what is fitted per slot (-1 = stock)
        public List<int> Parts = new List<int>();
        /// <summary>Parts given by items (roof boxes...): owned, but not paid for, so they don't add to resale value.</summary>
        public List<int> GiftParts = new List<int>();
        /// <summary>Period parts (the custom shop's 7 / 30-day prices): part ids and their expiry, Unix ms (Shops/ShopPeriods.cs).</summary>
        public List<int> TimedParts = new List<int>();
        public List<long> TimedUntil = new List<long>();
        public int Aero = -1, Hood = -1, Wing = -1, Wheels = -1, Booster = -1, Neon = -1;
        public int Plate = -1, Duct = -1, Roof = -1, Tint = -1, Decal = -1, Horn = -1, Engine = -1;
        public long Spent;             // Mito paid into this car (purchase, upgrades, parts); caps its resale value online
        public int[] AeroPoints = { 0, 0, 0, 0 };   // AEROSET 3900000 bonus points: speed, accel, crash, boost (Game/AeroSet.cs)
        /// <summary>Performance parts fitted per stat (speed, accel, durability, booster): PerfPart uids, -1 = empty.</summary>
        public int[] PerfSlots = { -1, -1, -1, -1 };
        /// <summary>Rental cars (Services/RentalCars.cs): 0 own car, 1 time-limited, 2 mileage-limited; the return time (Unix ms),
        /// the vshopitem UniqueId and name of the rental.</summary>
        public int RentKind;
        public long RentUntil;
        public int RentItem;
        public string RentName;
        /// <summary>NDLG parts page row 2 (SLOTN_02/04/06/08): a second performance part per stat (Inventory/CarParts.cs).</summary>
        public int[] PerfSlots2 = { -1, -1, -1, -1 };
        /// <summary>Option / docking part on each of the 8 part slots (OPTSLOT_01..08, index = stat * 2 + row): ItemClient.tdf ids, "" = empty.</summary>
        public string[] OptSlots = { "", "", "", "", "", "", "", "" };

        public int Fitted(PartSlot slot) => slot switch
        {
            PartSlot.Aero => Aero, PartSlot.Hood => Hood, PartSlot.Wing => Wing, PartSlot.Wheels => Wheels, PartSlot.Booster => Booster,
            PartSlot.Neon => Neon, PartSlot.Plate => Plate, PartSlot.Duct => Duct, PartSlot.Roof => Roof, PartSlot.Tint => Tint,
            PartSlot.Decal => Decal, PartSlot.Horn => Horn, _ => Engine,
        };

        public void Fit(PartSlot slot, int item)
        {
            switch (slot)
            {
                case PartSlot.Aero: Aero = item; break;
                case PartSlot.Hood: Hood = item; break;
                case PartSlot.Wing: Wing = item; break;
                case PartSlot.Wheels: Wheels = item; break;
                case PartSlot.Booster: Booster = item; break;
                case PartSlot.Neon: Neon = item; break;
                case PartSlot.Plate: Plate = item; break;
                case PartSlot.Duct: Duct = item; break;
                case PartSlot.Roof: Roof = item; break;
                case PartSlot.Tint: Tint = item; break;
                case PartSlot.Decal: Decal = item; break;
                case PartSlot.Horn: Horn = item; break;
                default: Engine = item; break;
            }
        }
    }

    /// <summary>Offline replacement for the server-side character record. Saved as JSON locally.</summary>
    [Serializable]
    public sealed class InventoryItem
    {
        public int ItemId;
        public int Count;
    }

    /// <summary>A stack of a named item (enhancers, dust, seeds, kits, crafted goods).</summary>
    [Serializable]
    public sealed class StackItem
    {
        public string Id;
        public int Count;
        /// <summary>Period items (Inventory/ItemPeriods.cs): expiry in Unix ms, 0 = unlimited.</summary>
        public long Until;
    }

    /// <summary>A performance part: stat type 0..3 (speed, accel, durability, booster), grade 0..3 (N, S, H, U), enhance level.</summary>
    [Serializable]
    public sealed class PerfPart
    {
        public int Uid;
        public int Type;
        public int Grade;
        public int Enhance;
        /// <summary>Set / rental parts (Inventory/SetParts.cs): the itemclient.tdf item id ("si_s01_003"), null for shop parts.</summary>
        public string Item;
        /// <summary>Expiry in Unix ms (period parts), 0 = unlimited.</summary>
        public long Until;
    }

    [Serializable]
    public sealed class QuestProgress
    {
        public int QuestId;
        public int Step;
        public int Counter;
    }

    [Serializable]
    public sealed class PlayerProfile
    {
        public string Name = "Driver";
        public int Avatar = 1;      // driver portrait ch_u_000N (1..8), picked on the CREATECHARACTER screen
        public string Created;      // yyyy.MM.dd, shown on the character licence
        public int Level = 1;
        public long Exp;
        public long Mito = 50000;   // in-game currency
        public List<OwnedCar> Cars = new List<OwnedCar>();
        public int CurrentCar;
        public string City = "cras";
        public List<int> CompletedQuests = new List<int>();
        public List<QuestProgress> ActiveQuests = new List<QuestProgress>();
        /// <summary>Step quests of World/Data/QuestClient.tdf (StepQuests.cs): completed quest numbers, the running one
        /// (QuestId = number, Step = step index, Counter = 1 once every step is done; QuestId 0 = none), the UTC day the
        /// repeat quests were last reset, and the faction (0 OMD, 1 Team ROO: chardata +0xed, packet 0x76d).</summary>
        public List<int> StepQuestsDone = new List<int>();
        public QuestProgress StepQuest = new QuestProgress();
        public string StepQuestDay = "";
        public int Faction;
        /// <summary>Times the running step quest was started over (the server's per-quest bonus count: StepQuests.TimeBonus).</summary>
        public int StepQuestTries;
        /// <summary>The apprentice course (ApprenticeCourse.cs, DriftCity_RE/TUTORIAL.md): set for a new driver until the reward
        /// car is picked (the server's Main.Apprentice bit, net+0x24c &amp; 0x80000), and the best course time in seconds.</summary>
        public bool Apprentice;
        public float ApprenticeBest;
        public List<InventoryItem> Inventory = new List<InventoryItem>();
        public int HuvCaught, BossesCaught, RacesWon, RacesEntered;
        public float BestLapSeconds;
        public float DistanceKm;
        // licences (license.xlt): earned ids, the mounted one, and the counters their conditions use
        public List<int> Licences = new List<int>();
        public int MountedLicence = -1;
        public int RacePoints, RacesCras, RacesMoon, RacesKoi, RacesOros, MaxEnhance;
        // Battle Zone (PvpRoom.cs): ladder points (the server keeps them online), PvP races/wins counted for the ladder,
        // best GT Championship stage reached offline (0 none .. 4 champion)
        public int LadderPoints = 1000, PvpRaces, PvpWins, TourBest;
        // parts shop and manufacturing
        public List<PerfPart> PerfParts = new List<PerfPart>();
        public int NextPartUid = 1;
        public List<StackItem> Items = new List<StackItem>();
        public List<string> SkillBooks = new List<string>();
        /// <summary>Timed drink effects from the item bag (Inventory/ItemUse.cs; the server keeps them online).</summary>
        public List<ItemBuff> Buffs = new List<ItemBuff>();
        public long CraftExp;
        // events (Game/Events, DriftCity_RE/EVENTS.md): coupon book counters, daily event drops, offline event state
        public EventState Events = new EventState();
        // shops (Game/Shops, DriftCity_RE/SHOPS.md): mileage points (1 per 100 Mito spent in the dealer and the custom shop),
        // the custom shop's wish list (vshopitem UniqueIds) and the car dealer's (vehicle indexes); the server keeps them online
        public long Mileage;
        public List<int> WishList = new List<int>();
        public List<int> WishCars = new List<int>();
        /// <summary>Driver rename (Services/DriverRename.cs): when the name last changed (Unix ms; 60 days between changes), and
        /// recommend-a-driver (Services/Recommend.cs): the driver has named who recommended them.</summary>
        public long NameChangedAt;
        public bool RecommendDone;
        /// <summary>The driver's custom mark (Game/CustomMarks.cs): a 128 px PNG as base64 ("" = none) and its version.</summary>
        public string CustomMark = "";
        public int CustomMarkVer;

        public int Count(string id) { var it = Items.Find(i => i.Id == id); return it?.Count ?? 0; }

        public void Give(string id, int count = 1)
        {
            var it = Items.Find(i => i.Id == id);
            if (it == null) Items.Add(it = new StackItem { Id = id });
            it.Count += count;
            if (it.Count <= 0) Items.Remove(it);
        }

        public int ItemCount(int itemId) { var it = Inventory.Find(i => i.ItemId == itemId); return it?.Count ?? 0; }

        public void AddItem(int itemId, int count = 1)
        {
            var it = Inventory.Find(i => i.ItemId == itemId);
            if (it == null) Inventory.Add(it = new InventoryItem { ItemId = itemId });
            it.Count += count;
            if (it.Count <= 0) Inventory.Remove(it);
        }

        public OwnedCar Car => Cars.Count == 0 ? null : Cars[Mathf.Clamp(CurrentCar, 0, Cars.Count - 1)];

        /// <summary>Tests point this at a temp folder so they never touch the real save.</summary>
        public static string SaveDirOverride;
        public static string SavePath => SlotPath(Slot);
        public static bool Exists => File.Exists(SavePath);

        /// <summary>Character slots on the SELECT screen (the client shows three licences per page).</summary>
        public const int Slots = 3;
        /// <summary>The slot Load/Save use; slot 0 keeps the original file name so older saves stay slot 1.</summary>
        public static int Slot;
        public static string SlotPath(int slot) =>
            Path.Combine(SaveDirOverride ?? Application.persistentDataPath, slot <= 0 ? "driftcity_offline_profile.json" : $"driftcity_offline_profile_{slot + 1}.json");

        /// <summary>Reads a slot's driver for the character list without making it current (null when empty or unreadable).</summary>
        public static PlayerProfile Peek(int slot)
        {
            foreach (var path in new[] { SlotPath(slot), SlotPath(slot) + ".bak" })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var p = JsonUtility.FromJson<PlayerProfile>(File.ReadAllText(path));
                    if (p == null || p.Cars == null) continue;
                    p.Normalize();
                    return p;
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>Removes a slot's save (and its backup); clears Current when it was that slot.</summary>
        public static void DeleteSlot(int slot)
        {
            foreach (var f in new[] { SlotPath(slot), SlotPath(slot) + ".bak" }) if (File.Exists(f)) File.Delete(f);
            if (slot == Slot) Current = null;
        }

        public static PlayerProfile Current { get; set; }

        /// <summary>Set for a driver loaded from a server; it is never written over the offline save.</summary>
        [NonSerialized] public bool OnlineDriver;

        static string BackupPath => SavePath + ".bak";

        /// <summary>True when a save file exists but couldn't be read (and no backup worked either).</summary>
        public static bool LoadFailed { get; private set; }

        public static PlayerProfile Load()
        {
            LoadFailed = false;
            foreach (var path in new[] { SavePath, BackupPath })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var p = JsonUtility.FromJson<PlayerProfile>(File.ReadAllText(path));
                    if (p == null || p.Cars == null) throw new Exception("empty save");
                    p.Normalize();
                    if (path == BackupPath) Debug.LogWarning("profile: main save unreadable, restored from backup");
                    return Current = p;
                }
                catch (Exception ex) { Debug.LogWarning($"profile load failed ({Path.GetFileName(path)}): {ex.Message}"); LoadFailed = true; }
            }
            return Current = null;
        }

        /// <summary>Repairs fields that old saves or server JSON may lack (null lists, short slot arrays, bad indexes).</summary>
        public void Normalize()
        {
            Cars ??= new List<OwnedCar>();
            CompletedQuests ??= new List<int>();
            ActiveQuests ??= new List<QuestProgress>();
            ActiveQuests.RemoveAll(q => q == null);
            StepQuestsDone ??= new List<int>(); StepQuest ??= new QuestProgress(); StepQuestDay ??= "";
            Inventory ??= new List<InventoryItem>();
            Licences ??= new List<int>();
            PerfParts ??= new List<PerfPart>();
            PerfParts.RemoveAll(x => x == null);
            Items ??= new List<StackItem>();
            Items.RemoveAll(x => x == null || string.IsNullOrEmpty(x.Id) || x.Count <= 0);
            SkillBooks ??= new List<string>();
            Events ??= new EventState(); Events.Normalize();
            Buffs ??= new List<ItemBuff>();
            WishList ??= new List<int>(); WishCars ??= new List<int>();
            if (Mileage < 0) Mileage = 0;
            Cars.RemoveAll(c => c == null);
            foreach (var c in Cars)
            {
                c.Parts ??= new List<int>();
                c.GiftParts ??= new List<int>();
                if (c.PerfSlots == null || c.PerfSlots.Length != 4)
                {
                    var slots = new[] { -1, -1, -1, -1 };
                    if (c.PerfSlots != null) for (int i = 0; i < Mathf.Min(4, c.PerfSlots.Length); i++) slots[i] = c.PerfSlots[i];
                    c.PerfSlots = slots;
                }
                // a part can't be fitted if it no longer exists
                for (int i = 0; i < 4; i++) if (c.PerfSlots[i] >= 0 && !PerfParts.Exists(x => x.Uid == c.PerfSlots[i])) c.PerfSlots[i] = -1;
            }
            if (Cars.Count > 0) CurrentCar = Mathf.Clamp(CurrentCar, 0, Cars.Count - 1);
            if (Mito < 0) Mito = 0;
            if (Avatar < 1 || Avatar > 8) Avatar = 1;
            if (NextPartUid <= 0 || PerfParts.Exists(x => x.Uid >= NextPartUid)) NextPartUid = PerfParts.Count == 0 ? 1 : PerfParts.Max(x => x.Uid) + 1;
        }

        public void Save()
        {
            // online drivers live on the server; only non-economic fields are pushed (money/EXP go through Progress)
            if (Net.OnlineSession.Active) { Net.OnlineSession.Instance.PushProfileUpdate(); return; }
            if (OnlineDriver) return;
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
            // write a temp file, keep the previous save as .bak, then swap: a crash mid-write can't destroy the driver
            if (LoadFailed)
            {
                // keep the unreadable save (and its backup) aside instead of rotating them away
                foreach (var f in new[] { SavePath, BackupPath })
                    if (File.Exists(f)) File.Copy(f, f + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                LoadFailed = false;
            }
            var tmp = SavePath + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(this, true));
            if (File.Exists(SavePath)) File.Replace(tmp, SavePath, BackupPath, true);
            else File.Move(tmp, SavePath);
        }

        public static void Delete() { if (Exists) File.Delete(SavePath); Current = null; }

        /// <summary>Experience needed for the next level (simple curve; the original lives server-side).</summary>
        public long ExpToNext => 1000L * Level * Level;

        public void AddExp(long amount)
        {
            Exp += amount;
            while (Exp >= ExpToNext) { Exp -= ExpToNext; Level++; }
        }
    }
}
