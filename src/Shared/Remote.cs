using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Bellboard.Shared;

/// <summary>
/// What a paired phone (or browser) may do: view &lt; respond &lt; full. Checked by the remote sidecar before it
/// forwards a call and again by Bellboard when the call arrives.
/// </summary>
public static class RemotePolicy
{
    public const string View = "view", Respond = "respond", Full = "full";

    public static int Rank(string? level) => level switch { View => 0, Respond => 1, Full => 2, _ => -1 };

    public static bool IsLevel(string? level) => Rank(level) >= 0;

    /// <summary>The level a method needs, or null when it can never be called remotely.</summary>
    public static string? Required(string method) => method switch
    {
        "workspace.list" or "agent.inbox" or "agent.list" or "agent.info" or "notification.list" or "sidebar.state"
            or "log.list" or "terminal.read" or "agent.prompt" or "state.get" or "terminal.attach" => View,
        // terminal.send / send-key: respond only for agent panes; Bellboard checks the pane.
        "agent.answer" or "terminal.send" or "terminal.send-key" or "notification.read" or "notification.clear"
            or "notification.dnd" or "workspace.mute" => Respond,
        "agent.recent-repos" or "agent.new" or "agent.finish" or "terminal.fit" => Full,
        _ => null,
    };

    public static bool Allows(string? level, string method) =>
        Required(method) is { } need && Rank(level) >= Rank(need);

    /// <summary>The error for a refused call (shown on the phone).</summary>
    public static string Refusal(string method) =>
        Required(method) is { } need ? $"needs {need} access" : $"'{method}' isn't available remotely";

    /// <summary>Text a respond-level device may type in one call.</summary>
    public const int MaxSendChars = 4096;
}

/// <summary>
/// A multiplexed client for the Bellboard pipe: concurrent calls, and subscriptions whose events arrive as
/// <c>{"sub":"s1","event":"…","data":…}</c> lines between responses. Used by the remote sidecar and by
/// `bell events` / `bell attach`.
/// </summary>
public sealed class PipeSession : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly NamedPipeClientStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _calls = new();
    private readonly Dictionary<string, Action<string, JsonNode?>> _subs = new();
    // Events can arrive before the response that names their subscription: hold them until it's registered, but
    // only while a subscribe call is in flight (otherwise they belong to a subscription that has ended).
    private readonly Dictionary<string, List<(string Event, JsonNode? Data)>> _early = new();
    private int _subscribing;
    private int _nextId;

    /// <summary>Raised once when the pipe closes (Bellboard quit or restarted).</summary>
    public event Action? Closed;
    public bool IsClosed { get; private set; }

    private PipeSession(NamedPipeClientStream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Utf8);
        _writer = new StreamWriter(stream, Utf8) { AutoFlush = true, NewLine = "\n" };
        _ = Task.Run(ReadLoop);
    }

    public static async Task<PipeSession?> ConnectAsync(string pipeName, int timeoutMs, CancellationToken ct = default)
    {
        var s = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        try
        {
            await s.ConnectAsync(timeoutMs, ct);
            return new PipeSession(s);
        }
        catch
        {
            await s.DisposeAsync();
            return null;
        }
    }

    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null, int timeoutMs = 60_000, CancellationToken ct = default)
    {
        if (IsClosed) throw new BellboardException("Bellboard closed the connection");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = tcs;
        var req = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        try
        {
            await _writeLock.WaitAsync(ct);
            try { await _writer.WriteLineAsync(req.ToJsonString()); }
            finally { _writeLock.Release(); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _calls.TryRemove(id, out _);
            OnClosed();
            throw new BellboardException("Bellboard closed the connection");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        using (timeout.Token.Register(() =>
               {
                   if (_calls.TryRemove(id, out var t))
                       t.TrySetException(ct.IsCancellationRequested ? new OperationCanceledException(ct) : new BellboardException($"timed out waiting for '{method}'"));
               }))
        {
            return await tcs.Task;
        }
    }

    /// <summary>
    /// Calls a subscribing method (events.subscribe, terminal.attach…) and routes its events to
    /// <paramref name="onEvent"/> (on the pipe's reader thread: keep it short). Returns the method's result,
    /// whose "sub" names the subscription.
    /// </summary>
    public async Task<JsonObject> SubscribeAsync(string method, JsonObject parameters, Action<string, JsonNode?> onEvent, int timeoutMs = 30_000)
    {
        lock (_subs) _subscribing++;
        JsonObject result;
        try
        {
            result = await CallAsync(method, parameters, timeoutMs) as JsonObject
                     ?? throw new BellboardException($"'{method}' didn't start a subscription");
        }
        catch
        {
            lock (_subs) { if (--_subscribing == 0) _early.Clear(); }
            throw;
        }
        var sub = (string?)result["sub"];
        lock (_subs)
        {
            if (sub != null)
            {
                // Under the lock, like the reader's own deliveries: held events come first, in order.
                if (_early.Remove(sub, out var early))
                    foreach (var (ev, data) in early)
                    {
                        try { onEvent(ev, data); } catch { }
                    }
                _subs[sub] = onEvent;
            }
            if (--_subscribing == 0) _early.Clear();
        }
        return sub != null ? result : throw new BellboardException($"'{method}' didn't start a subscription");
    }

    public async Task UnsubscribeAsync(string sub)
    {
        lock (_subs) _subs.Remove(sub);
        try { await CallAsync("unsubscribe", new JsonObject { ["sub"] = sub }, 5000); } catch { }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync();
                if (line == null) break;
                if (line.Length == 0) continue;
                JsonObject msg;
                try { msg = JsonNode.Parse(line) as JsonObject ?? throw new FormatException(); }
                catch { continue; }
                if ((string?)msg["sub"] is { } sub)
                {
                    var ev = (string?)msg["event"] ?? "";
                    var data = msg["data"];
                    msg.Remove("data");
                    // Delivered under the lock so held "early" events and live ones never interleave.
                    lock (_subs)
                    {
                        if (!_subs.TryGetValue(sub, out var handler))
                        {
                            if (_subscribing == 0) continue; // an ended subscription's stragglers
                            if (!_early.TryGetValue(sub, out var list)) _early[sub] = list = new();
                            if (list.Count < 10_000) list.Add((ev, data));
                            continue;
                        }
                        if (ev == "overflow") _subs.Remove(sub);
                        try { handler(ev, data); } catch { }
                    }
                    continue;
                }
                var id = (int?)msg["id"] ?? 0;
                if (!_calls.TryRemove(id, out var tcs)) continue;
                if ((bool?)msg["ok"] == true) tcs.TrySetResult(msg["result"]?.DeepClone());
                else tcs.TrySetException(new BellboardException((string?)msg["error"] ?? "unknown error"));
            }
        }
        catch { }
        OnClosed();
    }

    private void OnClosed()
    {
        if (IsClosed) return;
        IsClosed = true;
        foreach (var kv in _calls)
            if (_calls.TryRemove(kv.Key, out var t)) t.TrySetException(new BellboardException("Bellboard closed the connection"));
        Closed?.Invoke();
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
        OnClosed();
    }
}
