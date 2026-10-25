using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Net
{
    /// <summary>
    /// Online play: connection, account login, the server-held driver (mirrored into PlayerProfile.Current),
    /// city channels, own car state upload, remote players, chat, parties and race messages.
    /// When this isn't active the game runs offline exactly as before.
    /// </summary>
    public sealed class OnlineSession : MonoBehaviour
    {
        public static OnlineSession Instance { get; private set; }
        public static bool Active => Instance && Instance.LoggedIn && Instance.Net && Instance.Net.Connected;

        public NetClient Net { get; private set; }
        public bool LoggedIn { get; private set; }
        public string Username { get; private set; }
        public string ServerName { get; private set; }
        public string Channel { get; private set; }
        public RemotePlayers Remotes { get; private set; }
        public Dictionary<string, object> Party { get; private set; }
        public float PingMs { get; private set; }

        public event Action<string, Dictionary<string, object>> RaceMessage;
        public event Action<string, string, string> ChatReceived;  // scope, from, text

        float stateTimer, pingTimer, saveTimer;
        Vector3 lastSentPos;
        float lastSentAt;
        bool teleportNext = true;

        // login: a newer login or a logout makes older ConnectAndLogin callbacks stale
        int loginGen;
        // city channel: events for a channel are only taken once its channel.joined has arrived
        int enterGen;
        bool enterPending;
        string joinedChannel;
        // party invites: one popup at a time
        NuiView invitePopup;
        bool inviteOpen;

        /// <summary>Which account/server/driver PlayerProfile.Current was last filled from (local progress merges only into the same driver).</summary>
        static string appliedDriverKey;

        public static OnlineSession Ensure()
        {
            if (Instance) return Instance;
            var go = new GameObject("Online Session");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<OnlineSession>();
            Instance.Net = go.AddComponent<NetClient>();
            Instance.Net.Message += Instance.OnMessage;
            Instance.Net.Disconnected += Instance.OnDisconnected;
            Instance.Net.BeforeQuit += Instance.OnBeforeQuit;
            ServerNotices.Hook(Instance.Net);   // notice / time messages, kick texts (HANDLERS.md)
            return Instance;
        }

        // ---------------- server list ----------------
        public struct ServerEntry { public string Name, Url; }

        /// <summary>servers.txt next to the exe (lines "Name|ws://host:port"), "-server ws://..." on the command line, plus localhost.</summary>
        public static List<ServerEntry> Servers()
        {
            var list = new List<ServerEntry>();
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-server", StringComparison.OrdinalIgnoreCase)) list.Add(new ServerEntry { Name = "Command line", Url = args[i + 1] });
            try
            {
                var file = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "servers.txt");
                if (File.Exists(file))
                    foreach (var line in File.ReadAllLines(file))
                    {
                        var l = line.Trim();
                        if (l.Length == 0 || l.StartsWith("#")) continue;
                        var parts = l.Split('|');
                        list.Add(parts.Length >= 2 ? new ServerEntry { Name = parts[0].Trim(), Url = parts[1].Trim() } : new ServerEntry { Name = l, Url = l });
                    }
            }
            catch { }
            if (!list.Any(s => s.Url.Contains("127.0.0.1") || s.Url.Contains("localhost")))
                list.Add(new ServerEntry { Name = "This PC", Url = "ws://127.0.0.1:7860" });
            return list;
        }

        /// <summary>
        /// Connects (if needed) and logs in. done(ok, error, hasDriver) is not called at all when Logout() or a newer
        /// ConnectAndLogin happened in the meantime.
        /// </summary>
        public async void ConnectAndLogin(string url, string user, string pass, bool register, Action<bool, string, bool> done)
        {
            int gen = ++loginGen;
            if (!Net.Connected || Net.Url != url)
            {
                bool ok;
                try { ok = await Net.Connect(url); }
                catch (Exception ex) { Debug.LogException(ex); ok = false; }
                if (gen != loginGen || !this) return;
                if (!ok) { done?.Invoke(false, $"Could not reach the server at {url}.", false); return; }
            }
            Net.Request(register ? "register" : "login", new Dictionary<string, object> { ["protocol"] = NetClient.Protocol, ["user"] = user, ["pass"] = pass }, r =>
            {
                if (gen != loginGen) return;
                if (!r.B("ok")) { done?.Invoke(false, r.S("error", "login failed"), false); return; }
                LoggedIn = true;
                Username = r.S("user", user);
                var driver = r.O("driver");
                if (driver != null) ApplyDriver(driver);
                done?.Invoke(true, null, driver != null);
            });
        }

        public void CreateDriver(string name, int vehicleIndex, int color, int avatar, Action<bool, string> done)
        {
            Net.Request("driver.create", new Dictionary<string, object> { ["name"] = name, ["car"] = vehicleIndex, ["color"] = color, ["avatar"] = avatar }, r =>
            {
                if (r.B("ok")) ApplyDriver(r.O("driver"));
                done(r.B("ok"), r.S("error"));
            });
        }

        /// <summary>
        /// Server driver JSON uses PlayerProfile's field names, so JsonUtility maps it directly. Economy and
        /// everything else is server-authoritative; progress the client keeps between 20 s pushes (quest counters,
        /// distance, best lap) is merged in from the local copy of the same driver instead of being thrown away.
        /// </summary>
        public static void ApplyDriver(Dictionary<string, object> driver)
        {
            if (driver == null) return;
            PlayerProfile p;
            try { p = JsonUtility.FromJson<PlayerProfile>(MiniJson.Write(driver)); }
            catch (Exception ex) { Debug.LogWarning("bad driver from server: " + ex.Message); return; }
            if (p == null) return;
            p.Normalize();
            p.OnlineDriver = true;

            var s = Instance;
            string key = $"{(s && s.Net ? s.Net.Url : "")}|{(s ? s.Username : "")}|{p.Name}";
            var local = PlayerProfile.Current;
            if (local != null && local != p && local.OnlineDriver && key == appliedDriverKey) Merge(local, p);
            else if (p.Cars.Count > 0) p.CurrentCar = Mathf.Clamp(p.CurrentCar, 0, p.Cars.Count - 1);
            appliedDriverKey = key;

            PlayerProfile.Current = p;
            if (GameFlow.Instance) { GameFlow.Instance.ApplyCarStats(); GameFlow.Instance.RefreshHud(); }
        }

        /// <summary>Keeps local not-yet-pushed progress on top of the server copy (server = into).</summary>
        static void Merge(PlayerProfile local, PlayerProfile into)
        {
            // quests: the server's list decides which quests are active; for quests in both keep the further progress
            if (into.ActiveQuests != null && local.ActiveQuests != null)
                foreach (var sq in into.ActiveQuests)
                {
                    if (sq == null) continue;
                    var lq = local.ActiveQuests.Find(x => x != null && x.QuestId == sq.QuestId);
                    if (lq == null) continue;
                    if (lq.Step > sq.Step) { sq.Step = lq.Step; sq.Counter = lq.Counter; }
                    else if (lq.Step == sq.Step) sq.Counter = Math.Max(sq.Counter, lq.Counter);
                }
            into.DistanceKm = Mathf.Max(into.DistanceKm, local.DistanceKm);
            if (local.BestLapSeconds > 0 && (into.BestLapSeconds <= 0 || local.BestLapSeconds < into.BestLapSeconds)) into.BestLapSeconds = local.BestLapSeconds;
            // current car: the server's choice if it points at a real car, otherwise the local one (if valid there)
            int cars = into.Cars != null ? into.Cars.Count : 0;
            if (into.CurrentCar < 0 || into.CurrentCar >= cars)
                into.CurrentCar = local.CurrentCar >= 0 && local.CurrentCar < cars ? local.CurrentCar : 0;
        }

        // ---------------- city ----------------
        public void EnterCity(string city, Action<bool> done = null)
        {
            Remotes?.Clear();
            teleportNext = true;
            Channel = null;
            joinedChannel = null;
            enterPending = true;
            int gen = ++enterGen;
            Net.Request("enter", new Dictionary<string, object> { ["city"] = city }, r =>
            {
                if (gen != enterGen) return;   // left the city or entered another one meanwhile
                enterPending = false;
                bool ok = r.B("ok");
                if (ok)
                {
                    Channel = r.S("channel");
                    // channel.joined normally arrives before this reply; if it was for another channel, wait for ours
                    if (joinedChannel != null && joinedChannel != Channel) { joinedChannel = null; Remotes?.Clear(); }
                }
                else
                {
                    Channel = null;
                    joinedChannel = null;
                    if (r.S("error") != "disconnected") UiPopup.Message("Could not enter the city: " + r.S("error"));
                }
                done?.Invoke(ok);
            });
        }

        /// <summary>
        /// Call when the player leaves the city (GameFlow.ClearCity: travel, character select, logout). Leaves any
        /// online race lobby, stops the car-state upload and drops channel events until the next EnterCity.
        /// </summary>
        public void LeaveCity()
        {
            OnlineRace.LeaveLobby();
            enterGen++;
            enterPending = false;
            Channel = null;
            joinedChannel = null;
            teleportNext = true;
            Remotes?.Clear();
        }

        void Update()
        {
            if (!Active) return;
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (car && Channel != null)
            {
                EnsureRemotes();
                stateTimer -= Time.unscaledDeltaTime;
                if (stateTimer <= 0)
                {
                    stateTimer = 0.1f;
                    var pos = car.transform.position;
                    var rb = car.GetComponent<Rigidbody>();
                    var v = rb ? rb.linearVelocity : Vector3.zero;
                    // a jump bigger than driving could explain (R respawn, race grid, travel) counts as a teleport
                    float elapsed = Mathf.Clamp(Time.realtimeSinceStartup - lastSentAt, 0f, 2f);
                    float limit = 12f + v.magnitude * elapsed * 1.25f;
                    bool teleport = teleportNext || (pos - lastSentPos).sqrMagnitude > limit * limit;
                    teleportNext = false;
                    lastSentPos = pos;
                    lastSentAt = Time.realtimeSinceStartup;
                    Net.Send("state", new Dictionary<string, object>
                    {
                        ["p"] = new object[] { R(pos.x), R(pos.y), R(pos.z) }, ["r"] = R(car.transform.eulerAngles.y),
                        ["v"] = new object[] { R(v.x), R(v.y), R(v.z) }, ["s"] = Mathf.RoundToInt(car.SpeedKmh), ["b"] = car.Boosting, ["teleport"] = teleport,
                    });
                }
            }
            pingTimer -= Time.unscaledDeltaTime;
            if (pingTimer <= 0) { pingTimer = 5f; Net.Send("ping", new Dictionary<string, object> { ["at"] = (double)(Time.realtimeSinceStartup * 1000f) }); }
            // non-economic progress (current car, city, quest counters, distance) syncs every 20 s
            saveTimer -= Time.unscaledDeltaTime;
            if (saveTimer <= 0) { saveTimer = 20f; PushProfileUpdate(); }
        }

        void EnsureRemotes()
        {
            if (!Remotes) Remotes = TryGetComponent(out RemotePlayers rp) ? rp : gameObject.AddComponent<RemotePlayers>();
        }

        static double R(float f) => Math.Round(f, 2);

        static Dictionary<string, object> ProfileUpdateBody(PlayerProfile p)
        {
            var quests = p.ActiveQuests.Where(q => q != null).Select(q => (object)new Dictionary<string, object> { ["QuestId"] = q.QuestId, ["Step"] = q.Step, ["Counter"] = q.Counter }).ToList();
            return new Dictionary<string, object>
            {
                ["update"] = new Dictionary<string, object>
                {
                    ["CurrentCar"] = p.CurrentCar, ["City"] = p.City, ["ActiveQuests"] = quests, ["DistanceKm"] = (double)p.DistanceKm,
                    ["PaintSlotApply"] = p.CurrentCar, ["PaintItem"] = p.Car != null ? p.Car.PaintItem : -1,
                },
            };
        }

        public void PushProfileUpdate(Action done = null)
        {
            var p = PlayerProfile.Current;
            if (p == null || !Active || !p.OnlineDriver) return;
            Net.Request("profile.update", ProfileUpdateBody(p), r => { done?.Invoke(); });
        }

        /// <summary>Best-effort immediate push (logout / quit, right before the socket closes).</summary>
        void PushProfileNow()
        {
            var p = PlayerProfile.Current;
            if (p == null || !p.OnlineDriver || !LoggedIn || !Net || !Net.Connected) return;
            Net.SendNow("profile.update", ProfileUpdateBody(p));
        }

        void OnBeforeQuit() => PushProfileNow();

        /// <summary>Asks the server to make the next car-state upload count as a teleport (respawn, race grid).</summary>
        public void MarkTeleport() => teleportNext = true;

        // ---------------- messages ----------------
        /// <summary>Channel events only count once channel.joined for the current channel has arrived.</summary>
        bool InChannel => joinedChannel != null;

        void OnMessage(string t, Dictionary<string, object> m)
        {
            switch (t)
            {
                case "welcome": ServerName = m.S("server"); break;
                case "pong": PingMs = Time.realtimeSinceStartup * 1000f - (float)m.N("at"); break;
                case "channel.joined":
                    // stale: we left the city (or logged out) after asking to enter
                    if (!enterPending && Channel == null) break;
                    EnsureRemotes();
                    Remotes.Clear();
                    joinedChannel = m.S("channel");
                    Channel = joinedChannel;
                    foreach (var o in m.L("players") ?? new List<object>()) Remotes.Add(o as Dictionary<string, object>);
                    ChatReceived?.Invoke("system", "", $"Joined channel {joinedChannel}. {Remotes.Count} other driver(s) here.");
                    break;
                case "player.joined":
                {
                    if (!InChannel) break;
                    var pl = m.O("player");
                    if (pl == null) break;
                    Remotes?.Add(pl);
                    ChatReceived?.Invoke("system", "", $"{pl.S("name")} entered the channel.");
                    break;
                }
                case "player.left": if (InChannel) Remotes?.Remove(m.I("id")); break;
                case "player.look":
                {
                    var pl = m.O("player");
                    if (InChannel && Remotes && pl != null) { Remotes.Remove(pl.I("id")); Remotes.Add(pl); }
                    break;
                }
                case "s": if (InChannel) Remotes?.ApplySnapshot(m.L("u")); break;
                case "chat": ChatReceived?.Invoke(m.S("scope"), m.S("from"), m.S("to") != null ? $"(to {m.S("to")}) {m.S("text")}" : m.S("text")); break;
                case "party.invite": if (!Social.SocialNet.Active) OnPartyInvite(m.S("from")); break;   // the social layer shows PARTY 420000
                case "party.update":
                    Party = m.O("party");
                    ChatReceived?.Invoke("system", "", Party == null ? "You left the party." : "Party: " + string.Join(", ", (Party.L("members") ?? new List<object>()).Select(x => (x as Dictionary<string, object>).S("name"))));
                    break;
                case "driver": ApplyDriver(m.O("driver")); break;
                case "kicked": ServerNotices.Kicked(m.S("reason")); break;   // op 0x09c texts
                default:
                    if (t != null && t.StartsWith("race.")) RaceMessage?.Invoke(t, m);
                    break;
            }
        }

        /// <summary>At most one invite popup: further invites while it is open only show up in the chat.</summary>
        void OnPartyInvite(string from)
        {
            if (inviteOpen && invitePopup)
            {
                ChatReceived?.Invoke("system", "", $"{from} also invited you to a party (answer the open invite first).");
                return;
            }
            inviteOpen = true;
            invitePopup = UiPopup.Show($"{from} invites you to a party.", id =>
            {
                inviteOpen = false;
                invitePopup = null;
                if (id == UiPopup.Yes && Active) Net.Request("party.accept", null, r => { if (!r.B("ok")) UiPopup.Message(r.S("error")); });
            }, UiPopup.Yes, UiPopup.No);
            if (!invitePopup) inviteOpen = false;
        }

        void OnDisconnected(string reason)
        {
            bool was = LoggedIn;
            OnlineRace.Reset();
            LoggedIn = false;
            ResetChannel();
            if (was && !ServerNotices.ConsumeKick()) UiPopup.Message($"Lost connection to the server ({reason}).\nYou can keep driving, but nothing is saved until you reconnect (Esc > Character select).");
        }

        void ResetChannel()
        {
            enterGen++;
            enterPending = false;
            Channel = null;
            joinedChannel = null;
            Party = null;
            Remotes?.Clear();
        }

        /// <summary>Saves what can be saved, leaves any race lobby and always closes the socket (also mid-login).</summary>
        public void Logout()
        {
            loginGen++;   // a login still in flight must not report back
            if (LoggedIn && Net && Net.Connected)
            {
                PushProfileNow();
                OnlineRace.LeaveLobby(true);
            }
            OnlineRace.Reset();
            LoggedIn = false;
            ResetChannel();
            if (Net) Net.Close();
        }

        // ---------------- chat helpers ----------------
        /// <summary>Plain text goes to the channel; "/w name text", "/p text", "/invite name", "/accept", "/leave" and "/online" are commands.</summary>
        public void SubmitChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !Active) return;
            text = text.Trim();
            if (Social.SocialChat.TryHandle(text)) return;   // "*Name", "&", "@", /party, /crew, /friend, /note, /block (SOCIAL.md)
            if (text.StartsWith("/w ") || text.StartsWith("/whisper "))
            {
                var parts = text.Split(new[] { ' ' }, 3);
                if (parts.Length == 3) Net.Send("chat", new Dictionary<string, object> { ["scope"] = "whisper", ["to"] = parts[1], ["text"] = parts[2] });
            }
            else if (text.StartsWith("/p ")) Net.Send("chat", new Dictionary<string, object> { ["scope"] = "party", ["text"] = text.Substring(3) });
            else if (text.StartsWith("/trade ")) Game.TradeWindow.Request(text.Substring(7).Trim());
            else if (text.StartsWith("/invite ")) Net.Request("party.invite", new Dictionary<string, object> { ["name"] = text.Substring(8).Trim() }, r => ChatReceived?.Invoke("system", "", r.B("ok") ? "Invite sent." : r.S("error")));
            else if (text == "/accept") Net.Request("party.accept", null, r => { if (!r.B("ok")) ChatReceived?.Invoke("system", "", r.S("error")); });
            else if (text == "/leave") Net.Request("party.leave", null, null);
            else if (text == "/online")
                Net.Request("online", null, r => ChatReceived?.Invoke("system", "", r.B("ok") ? "Online: " + string.Join(", ", (r.L("players") ?? new List<object>()).Select(x => (x as Dictionary<string, object>).S("name"))) : r.S("error")));
            else if (text == "/ping") ChatReceived?.Invoke("system", "", $"Ping {PingMs:F0} ms");
            else Net.Send("chat", new Dictionary<string, object> { ["scope"] = "channel", ["text"] = text });
        }
    }
}
