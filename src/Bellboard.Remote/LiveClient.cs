using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Bellboard.Shared;

namespace Bellboard.Remote;

/// <summary>
/// One phone's live connection (/api/live). It has its own pipe session to Bellboard, so its calls and terminal
/// mirrors are independent of other phones; state and notices come from the shared Bridge.
/// </summary>
internal sealed class LiveClient
{
    /// <summary>Unsent text before a mirrored terminal is dropped with "overflow" (the phone re-attaches).</summary>
    private const int MaxQueuedChars = 2 * 1024 * 1024;
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(33);
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly WebSocket _ws;
    private readonly Bridge _bridge;
    private readonly Channel<string> _out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Mirror> _mirrors = new();
    private readonly RateLimiter _limit = new(perSecond: 20, burst: 40);
    private PipeSession? _pipe;
    private int _queued;

    public Device Device { get; }
    public bool Visible { get; private set; } = true;

    private sealed class Mirror
    {
        public required string Pane;
        public string? Sub;
        public readonly StringBuilder Pending = new();
        public long Seq;
        /// <summary>Bellboard's number for the last chunk: chunks must follow on, or the phone resyncs.</summary>
        public long Upstream;
        public bool FlushScheduled;
        public bool Started;
    }

    public LiveClient(WebSocket ws, Bridge bridge, Device device)
    {
        _ws = ws;
        _bridge = bridge;
        Device = device;
    }

    public async Task RunAsync(CancellationToken aborted)
    {
        using var link = CancellationTokenSource.CreateLinkedTokenSource(aborted, _cts.Token);
        var ct = link.Token;
        _pipe = await PipeSession.ConnectAsync(_bridge.PipeName, 5000, ct);
        if (_pipe == null)
        {
            await _ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "Bellboard isn't answering", CancellationToken.None);
            return;
        }
        _pipe.Closed += () => _cts.Cancel();
        _bridge.Add(this);
        _bridge.SetVisible(this, true);
        var writer = Task.Run(() => WriteLoop(ct));
        try { await ReadLoop(ct); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        finally
        {
            _bridge.Remove(this);
            _cts.Cancel();
            _out.Writer.TryComplete();
            _pipe.Dispose();
            try { await writer; } catch { }
            try
            {
                if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                else if (_ws.State == WebSocketState.CloseReceived) await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
        }
    }

    // ---- outgoing ----

    /// <summary>Unsent text before the phone is considered gone (a dead connection that TCP hasn't noticed yet).</summary>
    private const int MaxQueuedTotal = 16 * 1024 * 1024;

    public void Send(JsonObject msg)
    {
        var s = msg.ToJsonString();
        if (Interlocked.Add(ref _queued, s.Length) > MaxQueuedTotal)
        {
            Console.Error.WriteLine($"{Device.Name}: the phone stopped reading; closing its connection");
            _out.Writer.TryComplete();
            _cts.Cancel();
            return;
        }
        _out.Writer.TryWrite(s);
    }

    // Only the newest state matters: one waiting in the queue is replaced, not added to.
    private JsonObject? _pendingState;
    private const string StateMarker = "\u0000state";

    public void SendState(JsonObject state)
    {
        var copy = (JsonObject)state.DeepClone();
        copy["t"] = "state";
        copy["device"] = new JsonObject { ["id"] = Device.Id, ["name"] = Device.Name, ["level"] = Device.Level };
        if (Interlocked.Exchange(ref _pendingState, copy) == null) _out.Writer.TryWrite(StateMarker);
    }

    /// <summary>The device was revoked (or its level changed): tell the phone and close.</summary>
    public void Revoke()
    {
        Send(new JsonObject { ["t"] = "revoked" });
        _out.Writer.TryComplete();
    }

    private async Task WriteLoop(CancellationToken ct)
    {
        try { await Write(ct); }
        catch
        {
            // A send failed: the connection is gone, so stop reading too.
            _cts.Cancel();
            throw;
        }
    }

    private async Task Write(CancellationToken ct)
    {
        while (await _out.Reader.WaitToReadAsync(ct))
        {
            while (_out.Reader.TryRead(out var s))
            {
                if (ReferenceEquals(s, StateMarker))
                {
                    if (Interlocked.Exchange(ref _pendingState, null) is not { } state) continue;
                    state["now"] = DateTimeOffset.Now.ToString("o"); // the state may be cached: the clock must be current
                    s = state.ToJsonString();
                }
                else Interlocked.Add(ref _queued, -s.Length);
                await _ws.SendAsync(Utf8.GetBytes(s), WebSocketMessageType.Text, true, ct);
            }
        }
        // Completed (revoked): close the socket.
        // The reader may be waiting in ReceiveAsync: only send our close frame here, and give the phone a moment
        // to answer it. Cancelling at once would reset the connection and could lose the "revoked" message.
        if (_ws.State == WebSocketState.Open)
            await _ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "revoked", CancellationToken.None);
        _ = Task.Delay(2000).ContinueWith(_ => _cts.Cancel());
    }

    // ---- incoming ----

    private async Task ReadLoop(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var msg = new System.IO.MemoryStream();
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await _ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) break;
            msg.Write(buffer, 0, r.Count);
            if (msg.Length > 256 * 1024) throw new WebSocketException("message too large");
            if (!r.EndOfMessage) continue;
            var text = Utf8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
            msg.SetLength(0);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            JsonObject m;
            try { m = JsonNode.Parse(text) as JsonObject ?? throw new FormatException(); }
            catch { Send(new JsonObject { ["t"] = "error", ["error"] = "malformed message" }); continue; }
            await HandleAsync(m);
        }
    }

    private async Task HandleAsync(JsonObject m)
    {
        switch ((string?)m["t"])
        {
            case "hello":
                if (_bridge.State is { } state) SendState(state);
                else if (await Call("state.get", new JsonObject()) is JsonObject fresh) SendState(fresh);
                break;
            case "ping":
                Send(new JsonObject { ["t"] = "pong" });
                break;
            case "visible":
                Visible = (bool?)m["on"] ?? true;
                _bridge.SetVisible(this, Visible);
                break;
            case "call":
                // Calls run side by side (agent.new can take seconds); attach/detach stay in order.
                _ = HandleCall(m);
                break;
            case "attach":
                await Attach((string?)m["pane"], (int?)m["scrollback"] ?? 2000);
                break;
            case "detach":
                await Detach((string?)m["pane"]);
                break;
            default:
                Send(new JsonObject { ["t"] = "error", ["error"] = $"unknown message '{(string?)m["t"]}'" });
                break;
        }
    }

    private async Task HandleCall(JsonObject m)
    {
        var id = m["id"]?.DeepClone();
        var method = (string?)m["method"] ?? "";
        var p = m["params"] as JsonObject ?? new JsonObject();
        var reply = new JsonObject { ["t"] = "result", ["id"] = id };
        if (!_limit.TryTake())
        {
            reply["ok"] = false;
            reply["error"] = "slow down";
            Send(reply);
            return;
        }
        if (!RemotePolicy.Allows(Device.Level, method) || method is "terminal.attach" or "state.get")
        {
            reply["ok"] = false;
            reply["error"] = RemotePolicy.Required(method) is null || method is "terminal.attach" or "state.get" ? $"'{method}' isn't available remotely" : RemotePolicy.Refusal(method);
            Send(reply);
            return;
        }
        try
        {
            reply["result"] = await Call(method, (JsonObject)p.DeepClone());
            reply["ok"] = true;
        }
        catch (Exception ex)
        {
            reply["ok"] = false;
            reply["error"] = ex.Message;
        }
        Send(reply);
    }

    private Task<JsonNode?> Call(string method, JsonObject p)
    {
        p["remoteDevice"] = Device.Id;
        return _pipe!.CallAsync(method, p, 60_000);
    }

    // ---- terminal mirrors ----

    private async Task Attach(string? pane, int scrollback)
    {
        if (pane == null) return;
        await Detach(pane);
        var mirror = new Mirror { Pane = pane };
        lock (_mirrors) _mirrors[pane] = mirror;
        try
        {
            var p = new JsonObject { ["pane"] = pane, ["scrollback"] = Math.Clamp(scrollback, 0, 10000), ["remoteDevice"] = Device.Id };
            var result = await _pipe!.SubscribeAsync("terminal.attach", p, (ev, data) => OnMirrorEvent(mirror, ev, data));
            mirror.Sub = (string?)result["sub"];
        }
        catch (Exception ex)
        {
            lock (_mirrors) _mirrors.Remove(pane);
            Send(new JsonObject { ["t"] = "error", ["pane"] = pane, ["error"] = ex.Message });
        }
    }

    /// <summary>Forgets this mirror, unless the pane has been attached again since (a newer mirror).</summary>
    private void RemoveMirror(Mirror m)
    {
        lock (_mirrors)
            if (_mirrors.TryGetValue(m.Pane, out var current) && current == m) _mirrors.Remove(m.Pane);
    }

    /// <param name="only">Detach only if this is still the pane's mirror.</param>
    private async Task Detach(string? pane, Mirror? only = null)
    {
        if (pane == null) return;
        Mirror? m;
        lock (_mirrors)
        {
            if (!_mirrors.TryGetValue(pane, out m) || (only != null && m != only)) return;
            _mirrors.Remove(pane);
        }
        if (m.Sub != null) await _pipe!.UnsubscribeAsync(m.Sub);
    }

    /// <summary>On the pipe's reader thread: queue, coalescing output to ~30 frames a second.</summary>
    private void OnMirrorEvent(Mirror m, string ev, JsonNode? data)
    {
        lock (m)
        {
            switch (ev)
            {
                case "snapshot":
                    m.Seq = 0;
                    m.Upstream = (long?)data?["seq"] ?? 0;
                    m.Pending.Clear();
                    m.Started = true;
                    Send(new JsonObject
                    {
                        ["t"] = "snapshot", ["pane"] = m.Pane, ["data"] = (string?)data?["data"] ?? "",
                        ["cols"] = (int?)data?["cols"] ?? 80, ["rows"] = (int?)data?["rows"] ?? 24, ["seq"] = 0,
                    });
                    break;
                case "output":
                    if (!m.Started) break;
                    if (Volatile.Read(ref _queued) > MaxQueuedChars)
                    {
                        // The phone can't keep up: drop this mirror; it re-attaches for a fresh snapshot.
                        m.Started = false;
                        m.Pending.Clear();
                        Send(new JsonObject { ["t"] = "overflow", ["pane"] = m.Pane });
                        _ = Detach(m.Pane, m);
                        break;
                    }
                    var upstream = (long?)data?["seq"] ?? m.Upstream + 1;
                    if (upstream != m.Upstream + 1)
                    {
                        // A chunk went missing: a fresh snapshot is the only way to a correct screen.
                        m.Started = false;
                        m.Pending.Clear();
                        Send(new JsonObject { ["t"] = "overflow", ["pane"] = m.Pane });
                        _ = Detach(m.Pane, m);
                        break;
                    }
                    m.Upstream = upstream;
                    m.Pending.Append((string?)data?["d"]);
                    if (!m.FlushScheduled)
                    {
                        m.FlushScheduled = true;
                        _ = Task.Delay(Coalesce).ContinueWith(_ => Flush(m));
                    }
                    break;
                case "resize":
                    FlushLocked(m);
                    Send(new JsonObject { ["t"] = "resize", ["pane"] = m.Pane, ["cols"] = (int?)data?["cols"] ?? 80, ["rows"] = (int?)data?["rows"] ?? 24 });
                    break;
                case "exit":
                    FlushLocked(m);
                    Send(new JsonObject { ["t"] = "exit", ["pane"] = m.Pane });
                    RemoveMirror(m);
                    break;
                case "overflow":
                    m.Started = false;
                    Send(new JsonObject { ["t"] = "overflow", ["pane"] = m.Pane });
                    RemoveMirror(m);
                    break;
            }
        }
    }

    private void Flush(Mirror m)
    {
        lock (m) FlushLocked(m);
    }

    private void FlushLocked(Mirror m)
    {
        m.FlushScheduled = false;
        if (m.Pending.Length == 0 || !m.Started) return;
        Send(new JsonObject { ["t"] = "out", ["pane"] = m.Pane, ["d"] = m.Pending.ToString(), ["seq"] = ++m.Seq });
        m.Pending.Clear();
    }
}

/// <summary>A token bucket: <c>perSecond</c> calls a second on average, bursts up to <c>burst</c>.</summary>
internal sealed class RateLimiter
{
    private readonly double _perSecond, _burst;
    private double _tokens;
    private DateTime _at = DateTime.UtcNow;
    private readonly object _gate = new();

    public RateLimiter(double perSecond, double burst)
    {
        _perSecond = perSecond;
        _burst = _tokens = burst;
    }

    public bool TryTake()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _tokens = Math.Min(_burst, _tokens + (now - _at).TotalSeconds * _perSecond);
            _at = now;
            if (_tokens < 1) return false;
            _tokens--;
            return true;
        }
    }
}

/// <summary>Rate limiters by key (an address, a device), forgetting idle keys so the map can't grow forever.</summary>
internal sealed class LimiterMap(double perSecond, double burst)
{
    private const int Max = 4096;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (RateLimiter Limiter, DateTime Used)> _map = new();

    public bool TryTake(string key)
    {
        if (_map.Count > Max)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
            foreach (var kv in _map)
                if (kv.Value.Used < cutoff) _map.TryRemove(kv.Key, out _);
            // Still full of active keys: refuse newcomers rather than grow.
            if (_map.Count > Max && !_map.ContainsKey(key)) return false;
        }
        var entry = _map.AddOrUpdate(key, _ => (new RateLimiter(perSecond, burst), DateTime.UtcNow), (_, e) => (e.Limiter, DateTime.UtcNow));
        return entry.Limiter.TryTake();
    }
}
