using System;
using System.Collections.Generic;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// AEROSET 3900000, the custom aero set's bonus points (DriftCity_RE/SCREENS.md 5 / 8.1) [exact for the screen]: FREE_POINT
    /// (+0x7c) over four stats OPTION1-4 (+0x80..+0x8c); UP moves a free point in (with none left it pulls all of that stat's
    /// points back), DOWN the reverse; ETC_OPTION1-4 / FIELD_OPTION1-4 are one-of-four choices shown only when the item has
    /// them (+0x98 / +0x99), COVER plates over the rest; OK: points left -> 11014, a needed choice missing -> 11015, else 11041
    /// -> the server, which answers 0x4be (11064 "Aeroset Setting completed.").
    ///
    /// Which item opens it and its budget were the server's; the remake's rule ([remake], mirrored by
    /// DriftCityServer/src/smallscreens.js): a car whose vehiclelist AeroSet column is 1 with an aero part fitted gets
    /// <see cref="Budget"/> points over Speed / Accel / Crash / Boost (OPTION1-4), at most <see cref="StatMax"/> on one stat,
    /// each point one rating level on top of the car's grade (<see cref="Bonus"/>); the remake's aero parts carry no
    /// ETC / FIELD choices, so those rows stay covered. Online the server checks and keeps the points (aero.set).
    /// </summary>
    public static class AeroSet
    {
        public const int Dialog = 3900000, Budget = 6, StatMax = 3;
        const string P = "ID_AEROSET_";
        static NuiView view;
        static UiManager hooked;
        static OwnedCar editing;
        static readonly int[] pts = new int[4];
        static Action<bool> done;
        static HashSet<int> aeroCars;

        public static NuiView View => view;
        public static int Free => Budget - pts[0] - pts[1] - pts[2] - pts[3];
        public static IReadOnlyList<int> Points => pts;
        public static string LastText { get; private set; }

        /// <summary>vehiclelist AeroSet = 1 (106 cars in the US data).</summary>
        public static bool CarTakesAeroSet(int vehicleIndex)
        {
            if (aeroCars == null)
            {
                aeroCars = new HashSet<int>();
                try
                {
                    var t = XltTable.Parse(DriftCityData.Files.Read(@"init\vehiclelist.xlt"), 3);
                    foreach (var r in t.Rows) if (t.Get(r, "AeroSet") == "1") aeroCars.Add(t.GetInt(r, "Index", -1));
                }
                catch (Exception ex) { Debug.LogWarning("aeroset: " + ex.Message); }
            }
            return aeroCars.Contains(vehicleIndex);
        }

        public static bool Allowed(OwnedCar car) => car != null && car.Aero >= 0 && CarTakesAeroSet(car.VehicleIndex);

        /// <summary>The rating levels the car's aero points add (ShopData.Stats hook): speed, accel, crash, boost.</summary>
        public static (int speed, int accel, int crash, int boost) Bonus(OwnedCar car)
        {
            if (!Allowed(car) || car.AeroPoints == null || car.AeroPoints.Length < 4) return (0, 0, 0, 0);
            int C(int i) => Mathf.Clamp(car.AeroPoints[i], 0, StatMax);
            return (C(0), C(1), C(2), C(3));
        }

        /// <summary>The workshop's hook after an aero part is fitted: offers the screen when the car takes points and has none set.</summary>
        public static void Offer(OwnedCar car)
        {
            if (!Allowed(car)) return;
            int sum = 0; if (car.AeroPoints != null) foreach (var x in car.AeroPoints) sum += x;
            if (sum < Budget) Open(car);
        }

        public static void Open(OwnedCar car, Action<bool> onDone = null)
        {
            var ui = UiManager.Instance;
            if (!ui || car == null) { onDone?.Invoke(false); return; }
            if (!Allowed(car)) { UiPopup.Message("This car takes no aero set."); onDone?.Invoke(false); return; }
            Hook(ui);
            if (!view) { view = ui.Open(Dialog); EventArtFallback.Apply(view); }   // its panel (3900001) points at ui_contents534, missing here
            if (!view) { onDone?.Invoke(false); return; }
            editing = car; done = onDone;
            for (int i = 0; i < 4; i++) pts[i] = car.AeroPoints != null && i < car.AeroPoints.Length ? Mathf.Clamp(car.AeroPoints[i], 0, StatMax) : 0;
            // the remake's aero parts have no ETC / FIELD choices: their rows stay under the COVER plates
            for (int i = 1; i <= 4; i++) { view.SetVisible(P + "ETC_OPTION" + i, false); view.SetVisible(P + "FIELD_OPTION" + i, false); }
            for (int i = 1; i <= 8; i++) view.SetVisible(P + "COVER" + i, i > 4);
            Refresh();
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                ui.On($"{P}OPTION{k + 1}_UP", _ => Up(k));
                ui.On($"{P}OPTION{k + 1}_DOWN", _ => Down(k));
            }
            ui.On(P + "OK", _ => Ok());
            ui.On(P + "CANCEL", _ => Close(false));
            ui.On(P + "CLOSE", _ => Close(false));
        }

        /// <summary>UP: a free point into the stat; with none left, all of that stat's points go back (the client's rule).</summary>
        public static void Up(int k)
        {
            if (Free > 0 && pts[k] < StatMax) pts[k]++;
            else if (Free <= 0) pts[k] = 0;
            Refresh();
        }

        public static void Down(int k) { if (pts[k] > 0) pts[k]--; Refresh(); }

        static void Refresh()
        {
            if (!view) return;
            view.SetText(P + "FREE_POINT", Free.ToString());
            for (int i = 0; i < 4; i++) view.SetText(P + "OPTION" + (i + 1), pts[i].ToString());
        }

        /// <summary>OK: 11014 with points left, else 11041, then the server (aero.set) or the offline save.</summary>
        public static void Ok(bool confirmed = false)
        {
            if (editing == null) return;
            if (Free > 0) { LastText = MissionText.Get("11014"); UiPopup.Message(LastText); return; }
            if (!confirmed) { UiPopup.Show(MissionText.Get("11041"), b => { if (b == UiPopup.Yes) Ok(true); }, UiPopup.Yes, UiPopup.No); return; }
            var car = editing;
            var p = PlayerProfile.Current;
            int index = p != null ? p.Cars.IndexOf(car) : -1;
            var values = new int[4]; Array.Copy(pts, values, 4);
            Progress.Op("aero.set", new Dictionary<string, object> { ["car"] = index, ["points"] = new List<object> { values[0], values[1], values[2], values[3] } }, () =>
            {
                car.AeroPoints = values;
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (!r.Ok) return;
                // the server's driver record already carries the points online; offline they were set above
                LastText = MissionText.Get("11064");
                ChatBox.System(LastText);
                if (GameFlow.Instance) GameFlow.Instance.ApplyCarStats();
                Close(true);
            });
        }

        static void Close(bool ok)
        {
            var ui = UiManager.Instance;
            if (view) { if (ui) ui.Close(view); else UnityEngine.Object.Destroy(view.gameObject); }
            view = null; editing = null;
            var d = done; done = null;
            d?.Invoke(ok);
        }
    }
}
