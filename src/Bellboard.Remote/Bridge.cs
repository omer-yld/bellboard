using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bellboard.Remote.Push;
using Bellboard.Shared;

namespace Bellboard.Remote;

/// <summary>A paired device, as Bellboard reported it when its token was checked.</summary>
internal sealed record Device(string Id, string Name, string Level);

/// <summary>
/// The sidecar's link to Bellboard: one pipe session for auth, pairing and the event stream (state, notices,
/// permission requests, revocations), fanned out to every connected phone, plus Web Push for phones that
/// aren't looking.
/// </summary>
internal sealed class Bridge
{
    private readonly PipeSession _pipe;
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<string, (Device? Device, DateTime At)> _auth = new();
    private readonly ConcurrentDictionary<LiveClient, byte> _clients = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastVisible = new();
    // Panes with a permission request open: their Notification-hook notices don't need a second push.
    private readonly ConcurrentDictionary<string, string> _pendingPrompts = new();
    private readonly WebPushSender? _push;
    private JsonObject? _state;
    private string _lastConnected = "";

    public Config Config { get; }

    public Bridge(PipeSession pipe, string pipeName, Config config)
    {
        _pipe = pipe;
        _pipeName = pipeName;
        Config = config;
        if (config.Push && config.VapidPublic != null && config.VapidPrivate != null)
            _push = new WebPushSender(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, new VapidKeys(config.VapidPublic, config.VapidPrivate), config.Subject);
    }

    public string PipeName => _pipeName;

    public async Task StartAsync()
    {
        await _pipe.SubscribeAsync("events.subscribe", new JsonObject { ["topics"] = new JsonArray("state", "notice", "prompt", "remote") }, OnEvent);
        _state = await _pipe.CallAsync("state.get") as JsonObject;
    }

    public Task<JsonNode?> CallAsync(string method, JsonObject p, int timeoutMs = 30_000) => _pipe.CallAsync(method, p, timeoutMs);

    // ---- auth ----

    /// <summary>Bumped on every revocation, so an auth answer that was in flight meanwhile isn't cached.</summary>
    private int _generation;

    /// <summary>A recently confirmed token without asking Bellboard (null if it must be checked).</summary>
    public Device? Cached(string? token) =>
        token != null && _auth.TryGetValue(token, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30) ? hit.Device : null;

    /// <param name="fresh">Ask Bellboard even if cached (opening a live socket).</param>
    public async Task<Device?> AuthenticateAsync(string? token, string? ip, bool fresh = false)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        if (!fresh && Cached(token) is { } hit) return hit;
        var generation = Volatile.Read(ref _generation);
        if (await _pipe.CallAsync("remote.auth", new JsonObject { ["token"] = token, ["ip"] = ip }, 10_000) is not JsonObject d) return null;
        var device = new Device((string)d["id"]!, (string?)d["name"] ?? "Phone", (string?)d["level"] ?? RemotePolicy.View);
        // Only successes are cached (strangers can't grow it), and not across a revocation.
        if (generation == Volatile.Read(ref _generation)) _auth[token] = (device, DateTime.UtcNow);
        return device;
    }

    private void Forget(string deviceId)
    {
        Interlocked.Increment(ref _generation);
        foreach (var kv in _auth.Where(kv => kv.Value.Device?.Id == deviceId).ToList()) _auth.TryRemove(kv.Key, out _);
        var sockets = _clients.Keys.Where(c => c.Device.Id == deviceId).ToList();
        Console.WriteLine($"device {deviceId} revoked or changed: closing {sockets.Count} socket(s)");
        foreach (var c in sockets) c.Revoke();
    }

    // ---- clients ----

    public JsonObject? State => _state;

    public void Add(LiveClient c)
    {
        _clients[c] = 0;
        ReportConnected();
    }

    public void Remove(LiveClient c)
    {
        _clients.TryRemove(c, out _);
        ReportConnected();
    }

    public void SetVisible(LiveClient c, bool on)
    {
        if (on || c.Visible) _lastVisible[c.Device.Id] = DateTime.UtcNow;
    }

    private void ReportConnected()
    {
        var ids = _clients.Keys.Select(c => c.Device.Id).Distinct().OrderBy(x => x).ToList();
        var key = string.Join(",", ids);
        if (key == _lastConnected) return;
        _lastConnected = key;
        _ = SafeCall("remote.connected", new JsonObject { ["devices"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()) });
    }

    private async Task SafeCall(string method, JsonObject p)
    {
        try { await _pipe.CallAsync(method, p, 10_000); }
        catch (Exception ex) { Console.Error.WriteLine($"{method}: {ex.Message}"); }
    }

    // ---- events from Bellboard ----

    private void OnEvent(string ev, JsonNode? data)
    {
        switch (ev)
        {
            case "state" when data is JsonObject state:
                _state = state;
                foreach (var c in _clients.Keys) c.SendState(state);
                break;
            case "notice" when data is JsonObject notice:
                foreach (var c in _clients.Keys) c.Send(new JsonObject { ["t"] = "notice", ["notice"] = notice.DeepClone() });
                if ((bool?)notice["quiet"] != true && (string?)notice["pane"] is { } pane && !_pendingPrompts.ContainsKey(pane))
                    _ = PushAsync(NoticePayload(notice));
                break;
            case "pending" when data is JsonObject pending:
                if ((string?)pending["pane"] is { } pp && (string?)pending["prompt"]?["id"] is { } qid) _pendingPrompts[pp] = qid;
                _ = PushAsync(PromptPayload(pending));
                break;
            case "resolved" when data is JsonObject resolved:
                if ((string?)resolved["pane"] is { } rp) _pendingPrompts.TryRemove(rp, out _);
                break;
            case "revoked" when data is JsonObject r && (string?)r["device"] is { } id:
                Forget(id);
                break;
            case "overflow":
                // Fell behind on the event stream: subscribe again and resend a full state.
                _ = Task.Run(StartAsync);
                break;
        }
    }

    // ---- push ----

    private JsonObject NoticePayload(JsonObject n)
    {
        var where = (string?)n["workspaceName"];
        var title = (string?)n["title"] ?? "Bellboard";
        var body = Config.PushText ? (string?)n["body"] ?? "" : (string?)n["agent"] is { } agent ? $"{agent} needs your attention" : "Something needs your attention";
        return new JsonObject
        {
            ["title"] = where != null ? $"{title} · {where}" : title,
            ["body"] = body,
            ["pane"] = n["pane"]?.DeepClone(),
            ["workspace"] = n["workspace"]?.DeepClone(),
            ["kind"] = "notice",
            ["tag"] = n["pane"]?.DeepClone(),
            ["url"] = $"/#/pane/{(string?)n["pane"]}",
        };
    }

    private JsonObject PromptPayload(JsonObject p)
    {
        var agent = (string?)p["agent"] ?? "Claude Code";
        var q = p["prompt"] as JsonObject;
        var tool = (string?)q?["tool"] ?? "a tool";
        var summary = (string?)q?["summary"];
        var body = Config.PushText && !string.IsNullOrEmpty(summary)
            ? $"{agent} wants to use {tool}: {summary}"
            : $"{agent} is asking for permission to use {tool}";
        return new JsonObject
        {
            ["title"] = $"Permission · {(string?)p["workspaceName"] ?? "Bellboard"}",
            ["body"] = body,
            ["pane"] = p["pane"]?.DeepClone(),
            ["workspace"] = p["workspace"]?.DeepClone(),
            ["kind"] = "prompt",
            ["promptId"] = q?["id"]?.DeepClone(),
            // An Allow button only on a notification that says what it allows.
            ["canAllow"] = Config.PushText && !string.IsNullOrEmpty(summary),
            ["tag"] = p["pane"]?.DeepClone(),
            ["url"] = $"/#/pane/{(string?)p["pane"]}",
        };
    }

    /// <summary>Sends to every device with push on that isn't looking at the app right now.</summary>
    private async Task PushAsync(JsonObject payload)
    {
        if (_push == null) return;
        JsonArray? targets;
        try { targets = await _pipe.CallAsync("remote.push.targets", new JsonObject(), 10_000) as JsonArray; }
        catch { return; }
        if (targets == null) return;
        var json = payload.ToJsonString();
        foreach (var t in targets.OfType<JsonObject>())
        {
            var device = (string?)t["device"];
            if (device == null) continue;
            bool looking = _clients.Keys.Any(c => c.Device.Id == device && c.Visible)
                           || (_lastVisible.TryGetValue(device, out var seen) && DateTime.UtcNow - seen < TimeSpan.FromSeconds(30));
            if (looking) continue;
            var sub = PushSubscription.FromJson(t["subscription"]);
            if (sub == null) continue;
            try
            {
                var r = await _push.SendAsync(sub, json, ttlSeconds: 6 * 3600, urgency: "high", topic: (string?)payload["tag"]);
                if (r.Outcome == PushOutcome.Gone) await SafeCall("remote.push.gone", new JsonObject { ["device"] = device });
                else if (r.Outcome == PushOutcome.Failed) Console.Error.WriteLine($"push to {device} failed: {r.Status} {r.Reason}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"push to {device}: {ex.Message}"); }
        }
    }
}

/// <summary>What Bellboard told the sidecar at startup (remote.hello).</summary>
internal sealed record Config(int Port, string Bind, List<string> Hosts, List<string> Origins, bool Push, bool PushText, string? VapidPublic, string? VapidPrivate,
    string Subject, string Version, byte[]? CertPfx)
{
    public static Config From(JsonObject o) => new(
        (int?)o["port"] ?? 17821,
        (string?)o["bind"] ?? "127.0.0.1",
        (o["hosts"] as JsonArray)?.Select(h => ((string?)h ?? "").ToLowerInvariant()).Where(h => h.Length > 0).ToList() ?? new List<string> { "localhost", "127.0.0.1" },
        (o["origins"] as JsonArray)?.Select(h => Program.NormalizeOrigin((string?)h)).OfType<string>().ToList() ?? new List<string>(),
        (bool?)o["push"] ?? false,
        (bool?)o["pushText"] ?? false,
        (string?)o["vapidPublic"],
        (string?)o["vapidPrivate"],
        (string?)o["subject"] ?? "https://github.com/omer-yld/bellboard",
        (string?)o["version"] ?? "",
        (string?)o["certPfx"] is { } pfx ? Convert.FromBase64String(pfx) : null);
}
