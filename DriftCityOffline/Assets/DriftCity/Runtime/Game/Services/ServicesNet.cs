using System;
using System.Collections.Generic;
using DriftCity.Net;
using UnityEngine;

namespace DriftCity.Game.Services
{
    /// <summary>
    /// The original server's remaining systems (DriftCity_RE/SERVICES.md; server src/services.js): requests, the pushes
    /// (pchan, rental.returned) and the server switches. The switches were feature bits of the client's server (net+0x24c,
    /// +0x258, +0x290, +0x294, op 0x097): the remake asks the server's <c>features</c> once per session; offline the
    /// remake's own defaults apply (<see cref="ServerSwitches"/>).
    /// </summary>
    public static class ServicesNet
    {
        static NetClient hooked;
        public static bool Online => OnlineSession.Active;

        /// <summary>A request to the server; offline the reply is { ok: false, error: "offline" }. A driver in the reply is applied.</summary>
        public static void Request(string type, Dictionary<string, object> body, Action<Dictionary<string, object>> reply)
        {
            if (!Online) { reply?.Invoke(new Dictionary<string, object> { ["ok"] = false, ["error"] = "offline" }); return; }
            Hook();
            OnlineSession.Instance.Net.Request(type, body ?? new Dictionary<string, object>(), r =>
            {
                if (r != null && r.O("driver") != null) OnlineSession.ApplyDriver(r.O("driver"));
                reply?.Invoke(r);
            });
        }

        /// <summary>The reply's text: its globaltext key when it has one, else its error.</summary>
        public static string Text(Dictionary<string, object> r, string fallback = "The server refused that.")
        {
            if (r == null) return "No answer from the server.";
            string key = r.S("key");
            if (!string.IsNullOrEmpty(key) && key.Length == 5 && char.IsDigit(key[0]))
            {
                string t = GameTables.Text(key);
                if (!string.IsNullOrEmpty(t) && t != key) return MissionText.Printf(t, r.S("name") ?? "");
            }
            return r.S("error") ?? r.S("text") ?? fallback;
        }

        /// <summary>Listens to the session's pushes (once per connection).</summary>
        public static void Hook()
        {
            var s = OnlineSession.Instance;
            if (!s || !s.Net || hooked == s.Net) return;
            if (hooked) hooked.Message -= OnMessage;
            hooked = s.Net;
            hooked.Message += OnMessage;
            ServerSwitches.Invalidate();
        }

        static void OnMessage(string t, Dictionary<string, object> m)
        {
            try
            {
                if (t == "pchan") PrivateChannel.OnPush(m);
                else if (t == "rental.returned") RentalCars.OnReturned(m.S("name"), m.B("current"));
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }

    /// <summary>
    /// The server switches (server src/switches.js): private chat, rental cars, the station's INSTANT button, the event
    /// delivery pair, recommend-a-driver, the personal shop. Offline: private chat, rentals of other drivers, recommend and
    /// the personal shop need a server (off); INSTANT is on; the event delivery pair runs with the delivery event seasons
    /// (Valentine / harvest / Halloween, the stations' QSEVENT badges).
    /// </summary>
    public static class ServerSwitches
    {
        static Dictionary<string, object> features;
        static bool asking;
        static float askedAt = -999f;

        public static void Invalidate() { features = null; askedAt = -999f; }

        /// <summary>Asks the server again (e.g. after connecting); <paramref name="done"/> runs when the answer is in.</summary>
        public static void Refresh(Action done = null)
        {
            if (!ServicesNet.Online) { done?.Invoke(); return; }
            asking = true; askedAt = Time.unscaledTime;
            ServicesNet.Request("features", null, r =>
            {
                asking = false;
                if (r != null && r.B("ok")) features = r.O("features");
                done?.Invoke();
            });
        }

        static bool Online(string name, bool offline)
        {
            if (!ServicesNet.Online) return offline;
            if (features == null && !asking && Time.unscaledTime - askedAt > 5f) Refresh();
            return features != null && features.B(name);
        }

        static bool DeliverySeason => SeasonEvents.Is(EventSeason.Valentine) || SeasonEvents.Is(EventSeason.Harvest) || SeasonEvents.Is(EventSeason.Halloween);

        public static bool PrivateChat => Online("privateChat", false);
        public static bool Rentals => Online("rentals", true);
        public static bool Instant => Online("instant", true);
        public static bool EventDelivery => Online("eventDelivery", DeliverySeason);
        public static bool Recommend => Online("recommend", false);
        public static bool PersonalShop => Online("personalShop", true);
        /// <summary>The switches are known (online: the server answered).</summary>
        public static bool Known => !ServicesNet.Online || features != null;

        /// <summary>Tests: sets a switch for this session (online until the next refresh).</summary>
        public static void Override(string name, bool on)
        {
            features ??= new Dictionary<string, object>();
            features[name] = on;
        }
    }

    /// <summary>
    /// Runs the services' timers: the rental expiry (offline; online the server pushes rental.returned), a mileage-limited
    /// rental going back when its tank runs dry, and the recommend prompt on the first city of a session.
    /// </summary>
    public sealed class ServicesHost : MonoBehaviour
    {
        public static ServicesHost Instance { get; private set; }
        float nextCheck;
        CitySession lastCity;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static ServicesHost Ensure()
        {
            if (Instance) return Instance;
            var go = new GameObject("Services");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<ServicesHost>();
            return Instance;
        }

        void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            try
            {
                if (ServicesNet.Online) ServicesNet.Hook();
                RentalCars.Tick();
                var city = CitySession.Current;
                if (city && city != lastCity) Recommend.OnCityEntered();
                lastCity = city;
                Recommend.Tick();
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }
}
