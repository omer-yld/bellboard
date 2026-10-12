using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Bellboard.Shared;

/// <summary>Constants shared by the app (server) and the CLI (client).</summary>
public static class BellboardProtocol
{
    public const string Version = "0.4.0";
    /// <summary>Where `bell update` looks when config.json sets no "updateFeed": the latest GitHub release's latest.json.</summary>
    public const string DefaultUpdateFeed = "https://github.com/omer-yld/bellboard/releases/latest/download/latest.json";
    public const string EnvPipe = "BELLBOARD_PIPE";
    public const string EnvPane = "BELLBOARD_PANE_ID";
    public const string EnvWorkspace = "BELLBOARD_WORKSPACE_ID";
    public const string EnvHome = "BELLBOARD_HOME";
    public const string EnvPrefix = "BELLBOARD_";
    /// <summary>Bellboard's earlier name was wmux: its WMUX_* variables are still read (a pane of an older version).</summary>
    public const string LegacyEnvPrefix = "WMUX_";

    /// <summary>A BELLBOARD_* variable, falling back to its pre-rename WMUX_* name.</summary>
    public static string? Env(string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) return v;
        return name.StartsWith(EnvPrefix, StringComparison.Ordinal)
               && Environment.GetEnvironmentVariable(LegacyEnvPrefix + name[EnvPrefix.Length..]) is { Length: > 0 } old
            ? old
            : null;
    }

    public static string DefaultPipeName()
    {
        var user = new string(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
        return "bellboard-" + user;
    }

    /// <summary>
    /// The pipe a command talks to. Panes export BELLBOARD_PIPE so a CLI started inside Bellboard always reaches
    /// the instance that owns it; inside a pane of an older wmux it reaches that wmux.
    /// </summary>
    public static string ResolvePipeName() => Env(EnvPipe) ?? DefaultPipeName();

    /// <summary>Bellboard's own pipe: BELLBOARD_PIPE only, so starting Bellboard from a wmux pane doesn't attach to wmux.</summary>
    public static string ServerPipeName() => Own(EnvPipe) ?? DefaultPipeName();

    /// <summary>%LOCALAPPDATA%\Bellboard (log, session, WebView2 data), or BELLBOARD_HOME for an isolated instance.</summary>
    public static string LocalDataDir => Own(EnvHome) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bellboard");

    /// <summary>%APPDATA%\Bellboard (config.json), or BELLBOARD_HOME for an isolated instance.</summary>
    public static string RoamingDataDir => Own(EnvHome) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bellboard");

    private static string? Own(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

public sealed class BellboardException(string message) : Exception(message);

/// <summary>
/// Newline-delimited JSON client for the Bellboard named pipe.
/// Request:  {"id":1,"method":"pane.split","params":{...}}
/// Response: {"id":1,"ok":true,"result":...} | {"id":1,"ok":false,"error":"..."}
/// </summary>
public sealed class PipeClient : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly NamedPipeClientStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private int _nextId;

    private PipeClient(NamedPipeClientStream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Utf8);
        _writer = new StreamWriter(stream, Utf8) { AutoFlush = true, NewLine = "\n" };
    }

    public static PipeClient? TryConnect(string pipeName, int timeoutMs)
    {
        // CurrentUserOnly makes the client verify the server is owned by the same user (no pipe squatting).
        var s = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            s.Connect(timeoutMs);
            return new PipeClient(s);
        }
        catch
        {
            s.Dispose();
            return null;
        }
    }

    public JsonNode? Call(string method, JsonObject? parameters = null, int timeoutMs = 60_000)
    {
        var req = new JsonObject
        {
            ["id"] = ++_nextId,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };
        _writer.WriteLine(req.ToJsonString());
        var readTask = _reader.ReadLineAsync();
        if (!readTask.Wait(timeoutMs))
            throw new BellboardException($"timed out waiting for '{method}'");
        var line = readTask.Result ?? throw new BellboardException("Bellboard closed the connection");
        var resp = JsonNode.Parse(line) as JsonObject ?? throw new BellboardException("malformed response");
        if (resp["ok"]?.GetValue<bool>() != true)
            throw new BellboardException((string?)resp["error"] ?? "unknown error");
        return resp["result"];
    }

    public void Dispose()
    {
        _writer.Dispose();
        _reader.Dispose();
        _stream.Dispose();
    }
}
