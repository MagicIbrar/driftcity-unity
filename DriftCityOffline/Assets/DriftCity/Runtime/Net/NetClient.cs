using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DriftCity.Net
{
    /// <summary>
    /// WebSocket connection to the Drift City Offline server. Receives and sends on background tasks and hands
    /// messages to the main thread in Update; requests carry a "rid" so replies reach their callback.
    /// Every request callback runs exactly once, on the main thread: with the reply, or with
    /// {ok:false, error:...} on disconnect ("disconnected") or when no reply came in time ("timeout").
    /// </summary>
    public sealed class NetClient : MonoBehaviour
    {
        public const int Protocol = 1;
        public const float RequestTimeout = 15f;
        const int MaxQueuedRequests = 2000;
        const int MaxMessageBytes = 16 * 1024 * 1024;

        public bool Connected { get { var c = conn; return c != null && c.Ready && c.Ws.State == WebSocketState.Open; } }
        public bool Connecting => connecting != null && !connecting.IsCompleted;
        public string Url { get; private set; }
        public event Action<string, Dictionary<string, object>> Message;   // type, message
        public event Action<string> Disconnected;
        /// <summary>Raised on application quit while the connection is still open (last chance for a SendNow).</summary>
        public event Action BeforeQuit;

        /// <summary>One socket and everything that belongs to it, so a stale task never touches a newer connection.</summary>
        sealed class Conn
        {
            public ClientWebSocket Ws;
            public CancellationTokenSource Cts;
            public int Gen;
            public volatile bool Ready;
            public volatile string CloseReason;
            public readonly ConcurrentQueue<string> Outbox = new ConcurrentQueue<string>();
            public string LatestState;   // newest car-state upload only (older ones are dropped, requests are kept)
            public readonly SemaphoreSlim Signal = new SemaphoreSlim(0);
            public readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
        }

        sealed class Pending
        {
            public Action<Dictionary<string, object>> Callback;
            public string Type;
            public float Deadline;
        }

        volatile Conn conn;
        int generation;
        Task<bool> connecting;
        string connectingUrl;
        readonly ConcurrentQueue<(int gen, string text)> inbox = new ConcurrentQueue<(int, string)>();
        readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        int nextRid = 1;
        float timeoutCheck;
        bool quitting;

        /// <summary>Connects (closing any previous connection). Calling it again while connecting to the same url
        /// returns the attempt in progress instead of starting (and killing) another one.</summary>
        public Task<bool> Connect(string url, int timeoutMs = 6000)
        {
            if (connecting != null && !connecting.IsCompleted && connectingUrl == url) return connecting;
            connectingUrl = url;
            connecting = ConnectInternal(url, timeoutMs);
            return connecting;
        }

        async Task<bool> ConnectInternal(string url, int timeoutMs)
        {
            Url = url;
            Close();
            var c = new Conn { Ws = new ClientWebSocket(), Cts = new CancellationTokenSource(), Gen = generation };
            conn = c;
            try
            {
                var connect = c.Ws.ConnectAsync(new Uri(url), c.Cts.Token);
                var winner = await Task.WhenAny(connect, Task.Delay(timeoutMs));
                if (winner != connect)
                {
                    Observe(connect);
                    if (conn == c) Close(); else Shutdown(c);
                    return false;
                }
                await connect;
            }
            catch (Exception ex)
            {
                if (conn == c) { Debug.LogWarning($"connect {url}: {ex.Message}"); Close(); }
                else Shutdown(c);
                return false;
            }
            // a newer Connect/Close happened while this one was waiting: this socket is not ours any more
            if (conn != c) { Shutdown(c); return false; }
            if (c.Ws.State != WebSocketState.Open) { Close(); return false; }
            c.Ready = true;
            var ct = c.Cts.Token;
            _ = Task.Run(() => ReceiveLoop(c, ct));
            _ = Task.Run(() => SendLoop(c, ct));
            return true;
        }

        static void Observe(Task t) => t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

        async Task ReceiveLoop(Conn c, CancellationToken ct)
        {
            var ws = c.Ws;
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) { c.CloseReason = string.IsNullOrEmpty(ws.CloseStatusDescription) ? "closed by server" : ws.CloseStatusDescription; break; }
                    // accumulate raw bytes and decode once: a multi-byte UTF-8 character can straddle two chunks
                    message.Write(buffer, 0, r.Count);
                    if (message.Length > MaxMessageBytes) { c.CloseReason = "message too large"; break; }
                    if (r.EndOfMessage)
                    {
                        inbox.Enqueue((c.Gen, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)));
                        message.SetLength(0);
                    }
                }
            }
            catch (Exception ex) { if (!ct.IsCancellationRequested && c.CloseReason == null) c.CloseReason = ex.Message; }
            if (c.CloseReason == null && !ct.IsCancellationRequested) c.CloseReason = "connection lost";
        }

        async Task SendLoop(Conn c, CancellationToken ct)
        {
            var ws = c.Ws;
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    if (!c.Outbox.TryDequeue(out var text)) text = Interlocked.Exchange(ref c.LatestState, null);
                    if (text == null) { await c.Signal.WaitAsync(50, ct).ConfigureAwait(false); continue; }
                    await c.SendLock.WaitAsync(ct).ConfigureAwait(false);
                    try { await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
                    finally { c.SendLock.Release(); }
                }
            }
            catch (Exception ex)
            {
                // a failed send means the connection is gone: Update closes it, fails pending requests and fires Disconnected
                if (!ct.IsCancellationRequested && c.CloseReason == null) c.CloseReason = "send failed: " + ex.Message;
            }
        }

        void Update()
        {
            int budget = 400;
            while (budget-- > 0 && inbox.TryDequeue(out var item))
            {
                if (item.gen != generation) continue;   // left over from an older connection
                Dictionary<string, object> m;
                try { m = MiniJson.ParseObject(item.text); }
                catch (Exception ex) { Debug.LogWarning("bad message from server: " + ex.Message); continue; }
                if (m == null) continue;
                string t = m.S("t");
                if (t == "reply")
                {
                    int rid = m.I("rid");
                    if (pending.TryGetValue(rid, out var p)) { pending.Remove(rid); Invoke(p.Callback, m); }
                    continue;   // unknown rid: that request already timed out or was failed
                }
                if (t == "error") { HandleError(m); continue; }
                try { Message?.Invoke(t, m); } catch (Exception ex) { Debug.LogException(ex); }
            }

            timeoutCheck -= Time.unscaledDeltaTime;
            if (timeoutCheck <= 0 && pending.Count > 0)
            {
                timeoutCheck = 0.5f;
                float now = Time.realtimeSinceStartup;
                List<int> expired = null;
                foreach (var kv in pending) if (kv.Value.Deadline <= now) (expired ??= new List<int>()).Add(kv.Key);
                if (expired != null)
                    foreach (var rid in expired)
                    {
                        if (!pending.TryGetValue(rid, out var p)) continue;
                        pending.Remove(rid);
                        Debug.LogWarning($"request '{p.Type}' (rid {rid}) got no reply within {RequestTimeout:F0} s");
                        Invoke(p.Callback, Fail("timeout"));
                    }
            }

            var c = conn;
            var reason = c != null ? c.CloseReason : null;
            if (reason != null)
            {
                Close();
                try { Disconnected?.Invoke(reason); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        /// <summary>Server error {t:'error', for:type, error, rid?}: fails the matching request, otherwise it is only logged.</summary>
        void HandleError(Dictionary<string, object> m)
        {
            string err = m.S("error") ?? m.S("message") ?? "server error";
            string forType = m.S("for");
            int rid = m.I("rid");
            int match = 0;
            if (rid != 0) { if (pending.ContainsKey(rid)) match = rid; }
            else if (forType != null)
                // no rid: fail the oldest pending request of that type, if there is one
                foreach (var kv in pending) if (kv.Value.Type == forType && (match == 0 || kv.Key < match)) match = kv.Key;
            Debug.LogWarning($"server error for '{forType ?? "?"}'{(rid != 0 ? $" (rid {rid})" : "")}: {err}");
            if (match != 0 && pending.TryGetValue(match, out var p))
            {
                pending.Remove(match);
                Invoke(p.Callback, new Dictionary<string, object>(m) { ["ok"] = false, ["error"] = err });
            }
            else
            {
                try { Message?.Invoke("error", m); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        static Dictionary<string, object> Fail(string error) => new Dictionary<string, object> { ["ok"] = false, ["error"] = error };

        static void Invoke(Action<Dictionary<string, object>> cb, Dictionary<string, object> m)
        {
            if (cb == null) return;
            try { cb(m); } catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>Fire-and-forget message. Car-state uploads ("state") keep only the newest one queued.</summary>
        public void Send(string type, Dictionary<string, object> body = null)
        {
            var c = conn;
            if (c == null || !Connected) return;
            body = body ?? new Dictionary<string, object>();
            body["t"] = type;
            var text = MiniJson.Write(body);
            if (type == "state") Interlocked.Exchange(ref c.LatestState, text);
            else
            {
                c.Outbox.Enqueue(text);
                // the socket isn't draining: treat it as dead rather than queueing forever
                if (c.Outbox.Count > MaxQueuedRequests && c.CloseReason == null) c.CloseReason = "send queue overflow";
            }
            c.Signal.Release();
        }

        /// <summary>Request with a reply callback (always called exactly once, on the main thread).</summary>
        public void Request(string type, Dictionary<string, object> body, Action<Dictionary<string, object>> reply)
        {
            if (!Connected) { Invoke(reply, Fail("not connected")); return; }
            body = body ?? new Dictionary<string, object>();
            int rid = nextRid++;
            if (nextRid >= int.MaxValue - 1) nextRid = 1;
            pending[rid] = new Pending { Callback = reply, Type = type, Deadline = Time.realtimeSinceStartup + RequestTimeout };
            body["rid"] = rid;
            Send(type, body);
        }

        /// <summary>
        /// Sends one message right away, blocking for at most timeoutMs (logout / quit, where the send queue is torn
        /// down before it would get the message out). Best effort; returns whether it was handed to the socket.
        /// </summary>
        public bool SendNow(string type, Dictionary<string, object> body, int timeoutMs = 800)
        {
            var c = conn;
            if (c == null || !Connected) return false;
            body = body ?? new Dictionary<string, object>();
            body["t"] = type;
            var bytes = Encoding.UTF8.GetBytes(MiniJson.Write(body));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool locked = false;
            try
            {
                // the send loop runs on the thread pool, so waiting here can't deadlock on the main thread
                locked = c.SendLock.Wait(timeoutMs);
                if (!locked) return false;
                int left = Math.Max(50, timeoutMs - (int)sw.ElapsedMilliseconds);
                return c.Ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(left);
            }
            catch (Exception ex) { Debug.LogWarning($"send {type}: {ex.Message}"); return false; }
            finally { if (locked) c.SendLock.Release(); }
        }

        /// <summary>Closes the connection. Every pending request callback is called with {ok:false, error:"disconnected"}.</summary>
        public void Close()
        {
            var c = conn;
            conn = null;
            generation++;   // drops anything the old connection still has in (or is about to put in) the inbox
            while (inbox.TryDequeue(out _)) { }
            if (c != null) Shutdown(c);
            FailPending("disconnected");
        }

        void FailPending(string error)
        {
            if (pending.Count == 0) return;
            var callbacks = new List<Pending>(pending.Values);
            pending.Clear();
            if (quitting) return;   // the app is shutting down: nobody is waiting any more
            foreach (var p in callbacks) Invoke(p.Callback, Fail(error));
        }

        /// <summary>Sends a close frame (bounded wait, off the main thread), then stops the loops and disposes the socket.</summary>
        static void Shutdown(Conn c)
        {
            c.Ready = false;
            Task closing = null;
            try { if (c.Ws.State == WebSocketState.Open) closing = c.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
            void Finish()
            {
                try { c.Cts.Cancel(); } catch { }
                try { c.Ws.Dispose(); } catch { }
            }
            if (closing == null) { Finish(); return; }
            Observe(closing);
            Task.WhenAny(closing, Task.Delay(1000)).ContinueWith(_ => Finish(), TaskScheduler.Default);
        }

        void OnApplicationQuit()
        {
            try { if (Connected) BeforeQuit?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
            quitting = true;
            Close();
        }
        void OnDestroy() { quitting = true; Close(); }
    }
}
