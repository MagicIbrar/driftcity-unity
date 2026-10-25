using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The car's Mittron fuel tank (DriftCity_RE/SCREENS.md "Mittron fuel"). The client keeps it per car as a double in
    /// litres (car +0x800) with a capacity (+0x2c0, 100 L by default, 0x479510) and an efficiency (+0x2c4, 5 km per litre):
    /// 0x475ad0 / 0x484196 take <c>distance / (efficiency * 1000)</c> litres per metre driven and clamp at empty. An
    /// empty tank caps the top speed at 150 km/h (0x476f90), stops combos from counting (0x4804d0) and refuses Auto-Drive
    /// (11116). It is filled at a station's Mittron filling station (CHARGER 940000, <see cref="MittronCharger"/>), with
    /// the Mittron Fuel items i_n_00004 / 00005 / 00006 (2 L / 10 L / 300 L, useitem.xlt) and, in the Battle Zone, with
    /// the PvP charger (CHARGERPVP 960000, <see cref="PvpCharger"/>) when the tank is at 20 % or less.
    /// This is not the booster gauge (car +0x9fc, <see cref="DriftCar.BoostGauge"/>), which the remake also calls Mittron.
    /// The client's server sent each car's capacity and efficiency (+0x28 / +0x2c of the car data); 0x47fde0 / 0x480050 scale
    /// them by the car's "gage" / "saving" assists (option parts: Over Mittron, Fuel Economy / Duty), see <see cref="Capacity"/>.
    /// The remake's base is the physics default (100 L, 5 km/L) for every car. The litres live in a file next to the save (one per driver), like the mission
    /// records: online the server only charges the Mito (fuel.buy) and spends the items.
    /// </summary>
    public static class MittronFuel
    {
        public const float DefaultCapacity = 100f;   // 0x479510: car +0x2c0
        public const float KmPerLitre = 5f;          // 0x479510: car +0x2c4
        public const float EmptyTopSpeedKmh = 150f;  // 0x476f90: vmax capped at 150 with no fuel
        public const float LowFraction = 0.2f;       // 0x72d8c7 / 0x5129a6: the PvP charger and auto-refill threshold
        /// <summary>Mito per litre at a filling station (the server set it, 0x54fec0; the remake's rule follows the
        /// CHARGER4GATE's default text "1L/10 Mito").</summary>
        public const int PricePerLitre = 10;

        /// <summary>Mittron Fuel items (useitem.xlt "Fills fuel tank by 2L / 10L / 300L") in the client's charger table
        /// order (0x8c3fe8: index, litres, item code).</summary>
        public static readonly (string id, int litres)[] FuelItems = { ("i_n_00004", 2), ("i_n_00005", 10), ("i_n_00006", 300) };

        /// <summary>Tests and modes that should not use fuel turn this off.</summary>
        public static bool Enabled = true;

        public static event Action Changed;

        [Serializable]
        public sealed class Tank { public int Vehicle; public float Litres; }

        [Serializable]
        sealed class Store
        {
            public List<Tank> Tanks = new List<Tank>();
        }

        static Store store;
        static string storePath;

        static PlayerProfile P => PlayerProfile.Current;

        static string PathFor(PlayerProfile p)
        {
            if (p != null && p.OnlineDriver)
            {
                string safe = Regex.Replace(p.Name ?? "driver", @"[^A-Za-z0-9_-]", "_");
                return Path.Combine(PlayerProfile.SaveDirOverride ?? Application.persistentDataPath, $"online_{safe}.fuel.json");
            }
            return PlayerProfile.SavePath + ".fuel.json";
        }

        static Store Current
        {
            get
            {
                string path = PathFor(P);
                if (store != null && storePath == path) return store;
                storePath = path;
                store = null;
                try { if (File.Exists(path)) store = JsonUtility.FromJson<Store>(File.ReadAllText(path)); }
                catch (Exception ex) { Debug.LogWarning("fuel: " + ex.Message); }
                store ??= new Store();
                store.Tanks ??= new List<Tank>();
                return store;
            }
        }

        /// <summary>Forgets the cached tanks (tests switch save folders).</summary>
        public static void Reset() { store = null; storePath = null; }

        /// <summary>The tank of a car (a car never seen before comes full).</summary>
        public static Tank TankOf(OwnedCar car)
        {
            if (car == null) return null;
            var s = Current;
            var t = s.Tanks.Find(x => x.Vehicle == car.VehicleIndex);
            if (t == null) { t = new Tank { Vehicle = car.VehicleIndex, Litres = CapacityOf(car) }; s.Tanks.Add(t); }
            return t;
        }

        /// <summary>
        /// The current car's tank (car +0x2c0): 0x47fde0 / 0x480050 set it to (100 + the "gage" assist percent) / 100 x the
        /// car's litres from the server's car data (+0x28); the remake's car data is the physics default, 100 L.
        /// </summary>
        public static float Capacity => CapacityOf(P?.Car);
        /// <summary>The current car's mileage, km per litre (car +0x2c4 = (100 + "saving" percent) / 100 x +0x2c, default 5).</summary>
        public static float Mileage => MileageOf(P?.Car);

        public static float CapacityOf(OwnedCar car) => DefaultCapacity * (100f + Bonus(car).tank) / 100f;
        public static float MileageOf(OwnedCar car) => KmPerLitre * (100f + Bonus(car).mileage) / 100f;

        /// <summary>The part bonuses of a car's assists (option / set parts).</summary>
        static (float tank, float mileage) Bonus(OwnedCar car)
        {
            if (car == null) return (0f, 0f);
            try { var a = CarAssists.For(P, car); return (Mathf.Max(-90f, a.TankPercent), Mathf.Max(-90f, a.MileagePercent)); }
            catch (Exception) { return (0f, 0f); }
        }
        /// <summary>Litres in the current car's tank.</summary>
        public static float Litres => TankOf(P?.Car)?.Litres ?? Capacity;
        public static float Fraction => Capacity > 0f ? Mathf.Clamp01(Litres / Capacity) : 1f;
        public static bool Empty => Enabled && P?.Car != null && Litres <= 0f;
        public static bool Low => Enabled && Fraction <= LowFraction;
        /// <summary>Litres the current car can still take.</summary>
        public static float Room => Mathf.Max(0f, Capacity - Litres);

        /// <summary>Sets the current car's litres (clamped); saves when asked.</summary>
        public static void Set(float litres, bool save = true)
        {
            var t = TankOf(P?.Car);
            if (t == null) return;
            t.Litres = Mathf.Clamp(litres, 0f, Capacity);
            if (save) Save();
            Changed?.Invoke();
        }

        /// <summary>Adds litres to the current car (a filling station, a fuel item); returns what went in.</summary>
        public static float Add(float litres)
        {
            float before = Litres;
            Set(before + Mathf.Max(0f, litres));
            return Litres - before;
        }

        /// <summary>Burns fuel for <paramref name="metres"/> driven (0x475ad0: metres / (km per litre * 1000)).</summary>
        public static void Consume(float metres)
        {
            if (!Enabled || metres <= 0f) return;
            var t = TankOf(P?.Car);
            if (t == null || t.Litres <= 0f) return;
            t.Litres = Mathf.Max(0f, t.Litres - metres / (Mileage * 1000f));
        }

        /// <summary>The top speed the fuel allows the player's car (no cap while there is fuel).</summary>
        public static float TopSpeedCapKmh(DriftCar car)
        {
            var flow = GameFlow.Instance;
            if (!Empty || !car || !flow || flow.PlayerCar != car) return float.MaxValue;
            return EmptyTopSpeedKmh;
        }

        /// <summary>Litres a Mittron Fuel item puts in the tank, 0 for other items.</summary>
        public static int ItemLitres(string id)
        {
            foreach (var (code, litres) in FuelItems) if (code == id) return litres;
            return 0;
        }

        public static void Save()
        {
            if (store == null || string.IsNullOrEmpty(storePath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storePath));
                File.WriteAllText(storePath, JsonUtility.ToJson(store, true));
            }
            catch (Exception ex) { Debug.LogWarning("fuel save: " + ex.Message); }
        }
    }

    /// <summary>
    /// Burns the player's fuel while driving (a city, a race, the Battle Zone; not the parking lot or menus) and
    /// shows the guide strips: FUELWARNING when the tank drops to 20 % (the client's low mark), FUELMEMPTY when it runs
    /// dry. Saves every 15 s of driving and when a mark is crossed.
    /// </summary>
    public sealed class MittronFuelMeter : MonoBehaviour
    {
        static MittronFuelMeter instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static void Ensure()
        {
            if (instance) return;
            var go = new GameObject("Mittron Fuel");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MittronFuelMeter>();
        }

        DriftCar last;
        Vector3 lastPos;
        float saveIn = 15f;
        float lastFraction = -1f;

        void Update()
        {
            var flow = GameFlow.Instance;
            var car = flow ? flow.PlayerCar : null;
            if (!car || !flow.InCity || flow.InParkingLot || !MittronFuel.Enabled || PlayerProfile.Current?.Car == null) { last = null; return; }
            var pos = car.transform.position;
            if (car != last) { last = car; lastPos = pos; lastFraction = MittronFuel.Fraction; return; }
            var d = new Vector2(pos.x - lastPos.x, pos.z - lastPos.z).magnitude;
            lastPos = pos;
            if (d > 60f) return;   // a teleport (respawn, warp), not driving
            MittronFuel.Consume(d);
            float f = MittronFuel.Fraction;
            bool crossed = false;
            if (lastFraction > MittronFuel.LowFraction && f <= MittronFuel.LowFraction) { GuideText.Flash(GuideKind.FuelWarning, 3f); crossed = true; }
            if (lastFraction > 0f && f <= 0f) { GuideText.Flash(GuideKind.FuelEmpty, 3f); crossed = true; }
            lastFraction = f;
            saveIn -= Time.unscaledDeltaTime;
            if (crossed || saveIn <= 0f) { saveIn = 15f; MittronFuel.Save(); }
        }

        void OnDestroy()
        {
            MittronFuel.Save();
            if (instance == this) instance = null;
        }
    }
}
