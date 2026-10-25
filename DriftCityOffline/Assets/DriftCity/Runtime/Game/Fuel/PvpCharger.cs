using System;
using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Battle Zone's fuel offer (game.nui CHARGERPVP 960000, kept by the guide object 0x5c3030; show 0x5c2c30, handler
    /// 0x5c2f00, spend 0x5c2a10) [exact]:
    /// - the PvP stages (0x71e0b3, 0x71ef4f, 0x726697, 0x72d92d) open it when the tank is at 20 % or less (car +0xa00 &lt;=
    ///   0.2), the driver did not close it before (+0xb8) and the car is not a kind-2 car;
    /// - it counts the Mittron Fuel stacks in the bag: COUNT_S 10463 "Small X %u" (i_n_00004, 2 L), COUNT_L 10464 "Large X
    ///   %u" (i_n_00005, 10 L), COUNT_XL 10465 "Super large X %u" (i_n_00006, 300 L), COUNT_XXL 10466 "Super X %u" (always 0);
    /// - with none of them it stays shut and shows the FUELWARNING guide strip instead;
    /// - USE spends items for the litres the tank lacks (capacity - fuel), smallest first: of a kind it takes
    ///   need / litres + 1 when that many are there and stops, else all of that kind and goes on with what is still
    ///   missing (a bigger kind only while its litres fit what is missing); ESC closes it for the rest of the session.
    /// Online the server spends the items (fuel.use); the litres go into the remake's tank (<see cref="MittronFuel"/>).
    /// </summary>
    public static class PvpCharger
    {
        public const int Dialog = 960000;
        static NuiView view;
        static UiManager hooked;

        /// <summary>ESC was pressed: no more offers this session (the client's +0xb8).</summary>
        public static bool Declined;
        public static bool IsOpen => view;
        public static NuiView View => view;
        static PlayerProfile P => PlayerProfile.Current;

        /// <summary>The PvP stages' check: opens the offer when the tank is low (hook for the Battle Zone screens).</summary>
        public static bool OfferIfLow()
        {
            if (!MittronFuel.Enabled || Declined || !MittronFuel.Low) return false;
            return Open();
        }

        /// <summary>Stacks of each charger kind in the bag (S, L, XL).</summary>
        public static int[] Counts()
        {
            var r = new int[MittronFuel.FuelItems.Length];
            for (int i = 0; i < r.Length; i++) r[i] = P?.Count(MittronFuel.FuelItems[i].id) ?? 0;
            return r;
        }

        /// <summary>Opens the offer; with no fuel items it flashes FUELWARNING instead and returns false.</summary>
        public static bool Open()
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) return false;
            var n = Counts();
            if (n[0] + n[1] + n[2] < 1) { GuideText.Flash(GuideKind.FuelWarning, 4f); return false; }
            Close();
            view = ui.Open(Dialog);
            if (!view) return false;
            if (hooked != ui)
            {
                hooked = ui;
                ui.On("ID_CHARGERPVP_USE", _ => Use());
                ui.On("ID_CHARGERPVP_ESC", _ => { Declined = true; Close(); });
            }
            view.SetText("ID_CHARGERPVP_COUNT_S", MissionText.Get("10463", n[0]));
            view.SetText("ID_CHARGERPVP_COUNT_L", MissionText.Get("10464", n[1]));
            view.SetText("ID_CHARGERPVP_COUNT_XL", MissionText.Get("10465", n[2]));
            view.SetText("ID_CHARGERPVP_COUNT_XXL", MissionText.Get("10466", 0));
            return true;
        }

        public static void Close()
        {
            if (view) UiManager.Instance?.Close(view);
            view = null;
        }

        /// <summary>The client's spend rule (0x5c2a10): items of each kind to use for <paramref name="need"/> litres.</summary>
        public static int[] Plan(int need, int[] counts)
        {
            var use = new int[counts.Length];
            int left = need;
            for (int i = 0; i < counts.Length; i++)
            {
                int have = counts[i], per = MittronFuel.FuelItems[i].litres;
                if (have == 0 || !(left == need || per <= left)) continue;
                int n = left / per + 1;
                if (n <= have) { use[i] = n; break; }
                use[i] = have;
                left -= per * have;
            }
            return use;
        }

        /// <summary>USE: spends fuel items for the litres the tank lacks.</summary>
        public static void Use(Action<bool> done = null)
        {
            if (P == null) { done?.Invoke(false); return; }
            int need = (int)MittronFuel.Room;   // 0x5c2fdf: ftol(capacity - fuel)
            var use = Plan(need, Counts());
            var items = new Dictionary<string, object>();
            float litres = 0f;
            for (int i = 0; i < use.Length; i++)
                if (use[i] > 0) { items[MittronFuel.FuelItems[i].id] = use[i]; litres += use[i] * MittronFuel.FuelItems[i].litres; }
            Close();
            if (items.Count == 0 || need <= 0) { done?.Invoke(false); return; }
            Progress.Op("fuel.use", new Dictionary<string, object> { ["items"] = items }, () =>
            {
                foreach (var kv in items) if (P.Count(kv.Key) < (int)kv.Value) return new Progress.Result { Error = "You don't have that." };
                foreach (var kv in items) P.Give(kv.Key, -(int)kv.Value);
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (r.Ok)
                {
                    MittronFuel.Add(litres);
                    AudioLibrary.PlaySfx("sfx/SID_CHARGINGSTATION_FINISH", AudioLibrary.UiVolume);
                    OriginalMenus.RefreshInventoryIfOpen();
                }
                done?.Invoke(r.Ok);
            });
        }
    }
}
