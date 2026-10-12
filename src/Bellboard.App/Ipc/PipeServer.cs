using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
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
        using (stream)
        using (var reader = new StreamReader(stream, Utf8))
        using (var writer = new StreamWriter(stream, Utf8) { AutoFlush = true, NewLine = "\n" })
        {
            try
            {
                while (stream.IsConnected && !_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    var response = await Handle(line);
                    await writer.WriteLineAsync(response.ToJsonString());
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { Log.Error("pipe session", ex); }
        }
    }

    private async Task<JsonObject> Handle(string line)
    {
        JsonNode? id = null;
        try
        {
            var req = JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException("request must be a JSON object");
            id = req["id"]?.DeepClone();
            var method = (string?)req["method"] ?? throw new InvalidOperationException("missing method");
            var p = req["params"] as JsonObject ?? new JsonObject();
            var result = await _ui.InvokeAsync(() => _router.HandleAsync(method, p)).Task.Unwrap();
            return new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = result };
        }
        catch (Exception ex)
        {
            var msg = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException.Message : ex.Message;
            return new JsonObject { ["id"] = id?.DeepClone(), ["ok"] = false, ["error"] = msg };
        }
    }

    public void Dispose() => _cts.Cancel();
}
