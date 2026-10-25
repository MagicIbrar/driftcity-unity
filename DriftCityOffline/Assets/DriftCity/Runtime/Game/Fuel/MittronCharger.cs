using System;
using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The Mittron filling station (game.nui CHARGER 940000; CHARGER4GATE 950000 is the same dialog in another skin for
    /// the "Gate" servers, net+0x244 &amp; 0x200), opened by a station's MITRON button (0x561e9b). Show 0x550230 / handler
    /// 0x54faf0 / gauge 0x54f9f0 [exact]:
    /// - the litres to add start at 0; 20L / 50L / 100L / 200L add that much, FULL sets the room left, - / + step one
    ///   litre; the amount is clamped to what the tank can still take;
    /// - GAUGE shows fuel + the litres being bought, GAUGE_DISABLE (24 % white) the fuel already in the tank, GAUGETEXT
    ///   "%dL (+%dL) / %dL", LITER the litres, SELL_PRICE their price ("%I64d", "%I64d(%I64d)" while a sale price differs),
    ///   OWN_MITO the driver's Mito, MITRONPRICE "1L / %u Mito", OWNTEAM 10168 "Owned by: %s Crew" / 10169 "Owned by:
    ///   None" (crew-owned stations came from the channel battle, packet 0x2d4: Services/StationOwners.cs);
    /// - CHRAGE buys (0x54f950: nothing with 0 L; else the price goes to the server), ESC / CLOSE close.
    /// The price per litre came from the server (0x54fec0); the remake's is <see cref="MittronFuel.PricePerLitre"/>.
    /// Online the server charges the Mito (fuel.buy); short of Mito only what the Mito covers goes in (10947).
    /// </summary>
    public static class MittronCharger
    {
        public const int Dialog = 940000, GateDialog = 950000;
        /// <summary>Use the CHARGER4GATE skin (the client's "Gate" servers) instead of CHARGER.</summary>
        public static bool GateLayout;

        static NuiView view;
        static string P_;   // control prefix of the open skin
        static long litres;
        static Action onClosed;
        static readonly Dictionary<RawImage, (float width, Rect uv)> bars = new Dictionary<RawImage, (float, Rect)>();
        static UiManager hooked;
        static string hookedPrefix;

        public static bool IsOpen => view;
        public static NuiView View => view;
        public static long Amount => litres;
        static PlayerProfile P => PlayerProfile.Current;

        /// <summary>Opens the filling station for the current car; <paramref name="closed"/> runs when it goes away.</summary>
        public static NuiView Open(Action closed = null)
        {
            var ui = UiManager.Instance;
            if (!ui || P?.Car == null) return null;
            // a mileage-limited rental car is never refilled (10953 / 10958; Services/RentalCars.cs)
            var rental = Services.RentalCars.RefuelRefusal(P.Car);
            if (rental != null) { UiPopup.Message(rental, () => closed?.Invoke()); return null; }
            Close();
            P_ = GateLayout ? "ID_CHARGER4GATE_" : "ID_CHARGER_";
            view = ui.Open(GateLayout ? GateDialog : Dialog);
            if (!view) return null;
            onClosed = closed;
            litres = 0;
            bars.Clear();
            Hook(ui);
            view.SetVisible(P_ + "OWNTEAMMARK", false);
            view.SetText(P_ + "OWNTEAM", MissionText.Get("10169"));
            // the crew that owns this channel's station (op 0x2d4 -> 0x54fec0; Services/StationOwners.cs)
            Services.StationOwners.Fill(view, P_);
            view.SetText(P_ + "MITRONPRICE", $"1L / {MittronFuel.PricePerLitre} Mito");
            if (GateLayout)
            {
                // no sale in the remake: the sale price shows the list price and the percent 0 %
                view.SetText(P_ + "SALEPERCENT", "0%");
                view.SetVisible(P_ + "SALEINFO", false);
            }
            AudioLibrary.PlaySfx("sfx/SID_CHARGINGSTATION_PREGAGE", AudioLibrary.UiVolume);
            Refresh();
            return view;
        }

        public static void Close()
        {
            if (view) UiManager.Instance?.Close(view);
            view = null;
            var cb = onClosed; onClosed = null;
            cb?.Invoke();
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui && hookedPrefix == P_) return;
            hooked = ui; hookedPrefix = P_;
            ui.On(P_ + "20L", _ => Step(20));
            ui.On(P_ + "50L", _ => Step(50));
            ui.On(P_ + "100L", _ => Step(100));
            ui.On(P_ + "200L", _ => Step(200));
            ui.On(P_ + "FULL", _ => { litres = (long)Mathf.Floor(MittronFuel.Room); Refresh(); });
            ui.On(P_ + "PLUS", _ => Step(1));
            ui.On(P_ + "MINUS", _ => Step(-1));
            ui.On(P_ + "CHRAGE", _ => Buy());
            ui.On(P_ + "ESC", _ => Close());
            ui.On(P_ + "CLOSE", _ => Close());
        }

        /// <summary>Adds (or removes) litres to the amount, clamped to [0, room left].</summary>
        public static void Step(long n)
        {
            litres += n;
            Refresh();
        }

        public static void SetAmount(long n) { litres = n; Refresh(); }

        public static long Price(long l) => l * MittronFuel.PricePerLitre;

        static void Refresh()
        {
            if (!view) return;
            long room = (long)Mathf.Floor(MittronFuel.Room);
            litres = Math.Max(0, Math.Min(litres, room));
            float cap = MittronFuel.Capacity, fuel = MittronFuel.Litres;
            Bar(view.Control(P_ + "GAUGE"), cap > 0 ? (fuel + litres) / cap : 0f);
            Bar(view.Control(P_ + "GAUGE_DISABLE"), cap > 0 ? fuel / cap : 0f);
            view.SetText(P_ + "GAUGETEXT", $"{(int)fuel}L (+{litres}L) / {(int)cap}L");
            view.SetText(P_ + "LITER", litres.ToString());
            view.SetText(P_ + "SELL_PRICE", Price(litres).ToString(GateLayout ? "N0" : "0"));
            if (GateLayout) view.SetText(P_ + "SALE_PRICE", Price(litres).ToString("N0"));
            view.SetText(P_ + "OWN_MITO", GateLayout ? (P?.Mito ?? 0).ToString("N0") : (P?.Mito ?? 0).ToString());
            // 0x54f900: CHRAGE is enabled only with an amount to buy
            view.SetEnabled(P_ + "CHRAGE", litres > 0);
        }

        /// <summary>Clips a gauge image to <paramref name="f"/> of its width (the client sets the element width, 0x54f9f0).</summary>
        static void Bar(NuiControlView c, float f)
        {
            var img = c ? c.GetComponentInChildren<RawImage>(true) : null;
            if (!img) return;
            if (!bars.TryGetValue(img, out var full)) bars[img] = full = (img.rectTransform.sizeDelta.x, img.uvRect);
            f = Mathf.Clamp01(f);
            var rt = img.rectTransform;
            rt.sizeDelta = new Vector2(full.width * f, rt.sizeDelta.y);
            img.uvRect = new Rect(full.uv.x, full.uv.y, full.uv.width * f, full.uv.height);
            img.enabled = f > 0f;
        }

        /// <summary>CHRAGE: buys the litres for Mito (the server charges online).</summary>
        public static void Buy(Action<bool> done = null)
        {
            if (!view || P == null) { done?.Invoke(false); return; }
            long want = litres;
            if (want <= 0) { done?.Invoke(false); return; }   // 0x54f950: 10502 (an empty text in the English client)
            // short of Mito: only what the Mito covers (10947 "Only %d Mito amount of Mittron refueled due to insufficient Mito.")
            long afford = MittronFuel.PricePerLitre > 0 ? P.Mito / MittronFuel.PricePerLitre : want;
            long buy = Math.Min(want, afford);
            if (buy <= 0) { UiPopup.Message(MissionText.Get("10947", 0)); done?.Invoke(false); return; }
            long cost = Price(buy);
            AudioLibrary.PlaySfx("sfx/SID_CHARGINGSTATION_GAGE", AudioLibrary.UiVolume);
            // online station.fill: the server charges and pays the owner crew's fee pool (src/stations.js)
            Progress.Op(Services.StationOwners.FillOp, new Dictionary<string, object> { ["litres"] = buy, ["price"] = MittronFuel.PricePerLitre }, () =>
            {
                if (P.Mito < cost) return new Progress.Result { Error = MissionText.Get("10947", 0) };
                P.Mito -= cost;
                return new Progress.Result { Ok = true, Mito = -cost };
            }, r =>
            {
                if (r.Ok)
                {
                    MittronFuel.Add(buy);
                    AudioLibrary.PlaySfx("sfx/SID_CHARGINGSTATION_FINISH", AudioLibrary.UiVolume);
                    if (buy < want) UiPopup.Message(MissionText.Get("10947", (int)cost));
                    litres = 0;
                }
                Refresh();
                done?.Invoke(r.Ok);
            });
        }
    }
}
