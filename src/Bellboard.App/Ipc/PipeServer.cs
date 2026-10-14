using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;
using Bellboard.Services;

namespace Bellboard.Ipc;

/// <summary>
/// \\.\pipe\bellboard-&lt;user&gt; — newline-delimited JSON requests, restricted to the current user
/// (PipeOptions.CurrentUserOnly), since a client can type into any shell.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string _name;
    private readonly CommandRouter _router;
    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _cts = new();

    public PipeServer(string name, CommandRouter router, Dispatcher ui)
    {
        _name = name;
        _router = router;
        _ui = ui;
    }

    public void Start() => _ = Task.Run(AcceptLoop);

    private async Task AcceptLoop()
    {
        Log.Info($@"listening on \\.\pipe\{_name}");
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error("pipe accept failed", ex);
                await Task.Delay(500);
                continue;
            }
            _ = Task.Run(() => Serve(server));
        }
    }

    private async Task Serve(NamedPipeServerStream stream)
    {
        using var conn = new PipeConnection(ClientPid(stream));
        using (stream)
        using (var reader = new StreamReader(stream, Utf8))
        using (var writer = new StreamWriter(stream, Utf8) { AutoFlush = false, NewLine = "\n" })
        {
            var pump = Task.Run(() => conn.PumpAsync(writer, _cts.Token));
            try
            {
                while (stream.IsConnected && !_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    // Side by side: a slow call (agent finish, a permission request waiting for the phone) mustn't
                    // hold up the next one. Responses carry the request's id, so clients match them up.
                    _ = Task.Run(async () => conn.Send((await Handle(line, conn)).ToJsonString()));
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { Log.Error("pipe session", ex); }
            finally
            {
                // Subscriptions belong to the UI thread's objects (panes, the event hub): end them there.
                await _ui.InvokeAsync(conn.EndSubscriptions);
                conn.Complete();
                try { await pump; } catch { }
            }
        }
    }

    private async Task<JsonObject> Handle(string line, PipeConnection conn)
    {
        JsonNode? id = null;
        try
        {
            var req = JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException("request must be a JSON object");
            id = req["id"]?.DeepClone();
            var method = (string?)req["method"] ?? throw new InvalidOperationException("missing method");
            var p = req["params"] as JsonObject ?? new JsonObject();
            var result = await _ui.InvokeAsync(() => _router.HandleAsync(method, p, conn)).Task.Unwrap();
            return new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = result };
        }
        catch (Exception ex)
        {
            var msg = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException.Message : ex.Message;
            return new JsonObject { ["id"] = id?.DeepClone(), ["ok"] = false, ["error"] = msg };
        }
    }

    private static int ClientPid(NamedPipeServerStream s)
    {
        try { return GetNamedPipeClientProcessId(s.SafePipeHandle, out var pid) ? (int)pid : 0; }
        catch { return 0; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    public void Dispose() => _cts.Cancel();
}

/// <summary>
/// One client of the pipe. Responses and pushed events share its stream, so everything goes through one
/// queue written by one pump. Pushed events are bounded: a client that stops reading loses its
/// subscriptions (it gets an "overflow" event and can subscribe again) instead of growing memory.
/// </summary>
public sealed class PipeConnection : IDisposable
{
    /// <summary>Queued but unwritten event text before a subscription is dropped (~8 MB of UTF-16).</summary>
    internal const int MaxQueuedChars = 4 * 1024 * 1024;

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Action> _subs = new();
    private int _queuedChars;
    private int _nextSub;

    public PipeConnection(int clientPid) => ClientPid = clientPid;

    /// <summary>The client's process id (0 if unknown): tells the remote sidecar apart from other clients.</summary>
    public int ClientPid { get; }
    public bool Closed { get; private set; }

    /// <summary>Registers a subscription; <paramref name="end"/> runs (on the UI thread) when it ends.</summary>
    public string AddSubscription(Action end)
    {
        var id = "s" + Interlocked.Increment(ref _nextSub);
        lock (_subs) _subs[id] = end;
        return id;
    }

    public bool EndSubscription(string id)
    {
        Action? end;
        lock (_subs)
        {
            if (!_subs.Remove(id, out end)) return false;
        }
        end();
        return true;
    }

    public void EndSubscriptions()
    {
        List<Action> ends;
        lock (_subs)
        {
            ends = new List<Action>(_subs.Values);
            _subs.Clear();
        }
        foreach (var end in ends)
        {
            try { end(); } catch (Exception ex) { Log.Error("ending a pipe subscription", ex); }
        }
    }

    /// <summary>A response line: always queued.</summary>
    public void Send(string line) => _queue.Writer.TryWrite(line);

    /// <summary>
    /// An event for subscription <paramref name="sub"/>. Returns false (and ends that subscription with an
    /// "overflow" event) when the client has fallen too far behind.
    /// </summary>
    public bool Push(string sub, string ev, JsonNode? data)
    {
        if (Closed) return false;
        var line = new JsonObject { ["sub"] = sub, ["event"] = ev, ["data"] = data }.ToJsonString();
        if (Interlocked.Add(ref _queuedChars, line.Length) > MaxQueuedChars)
        {
            Interlocked.Add(ref _queuedChars, -line.Length);
            Send(new JsonObject { ["sub"] = sub, ["event"] = "overflow" }.ToJsonString());
            EndSubscription(sub);
            return false;
        }
        _queue.Writer.TryWrite(line);
        return true;
    }

    internal async Task PumpAsync(TextWriter writer, CancellationToken ct)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var line))
                {
                    if (line.StartsWith("{\"sub\":", StringComparison.Ordinal)) Interlocked.Add(ref _queuedChars, -line.Length);
                    await writer.WriteAsync(line);
                    await writer.WriteAsync('\n');
                }
                await writer.FlushAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Closed = true;
        }
    }

    public void Complete()
    {
        Closed = true;
        _queue.Writer.TryComplete();
    }

    public void Dispose() => Complete();
}
