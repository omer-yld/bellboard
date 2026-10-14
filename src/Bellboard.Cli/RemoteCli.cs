using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bellboard.Shared;

namespace Bellboard.Cli;

/// <summary>`bell remote …` (the phone view), `bell events` and `bell attach`.</summary>
internal static class RemoteCli
{
    public const string Usage = """
        usage: bell remote [status]                          is it on, the address, connected devices
               bell remote enable [--mode tailscale|local|lan] [--port N] [--url URL]
               bell remote disable
               bell remote tailscale [off]                   publish it on your tailnet with `tailscale serve`
               bell remote pair                              a QR code and one-time code for a phone (2 minutes)
               bell remote devices · revoke <id> · level <id> view|respond|full · rename <id> <name>
               bell remote set [--answer-prompts away|always|off] [--push on|off] [--push-text on|off] [--away-minutes N]
        """;

    public static int Remote(PipeClient client, Args a, JsonObject p)
    {
        var sub = a.Pos(0) ?? "status";
        switch (sub)
        {
            case "status":
                return Status(client.Call("remote.status", p), a);
            case "enable" or "disable":
                if (a.Opt("mode") is { } mode) p["mode"] = mode;
                if (a.Opt("port") is { } port) p["port"] = int.TryParse(port, out var n) ? n : throw new BellboardException("--port must be a number");
                if (a.Opt("url") is { } url) p["url"] = url;
                return Status(client.Call("remote." + sub, p), a);
            case "tailscale":
            {
                if (a.Pos(1) == "off") p["off"] = true;
                Console.Error.WriteLine(a.Pos(1) == "off" ? "Stopping tailscale serve…" : "Setting up tailscale serve…");
                var r = client.Call("remote.tailscale", p, 60_000);
                if (a.Has("json")) return Json(r);
                Console.WriteLine((bool?)r?["served"] == true
                    ? $"Bellboard Remote is on your tailnet: {r?["url"]}\nPair a phone with `bell remote pair`."
                    : "tailscale serve stopped.");
                return 0;
            }
            case "pair":
            {
                var r = client.Call("remote.pair-code", p);
                if (a.Has("json")) return Json(r);
                Console.WriteLine();
                foreach (var line in ((string?)r?["qr"] ?? "").Split('\n')) Console.WriteLine("  " + line);
                Console.WriteLine($"  Scan it with the phone's camera, or open {r?["url"]}");
                Console.WriteLine($"  and enter the code  {r?["code"]}");
                Console.WriteLine("  The code works once, for 2 minutes. New phones can answer agents (level: respond);");
                Console.WriteLine("  change it with `bell remote level <id> view|respond|full`.");
                return 0;
            }
            case "devices":
            {
                var r = client.Call("remote.devices", p)!.AsArray();
                if (a.Has("json")) return Json(r);
                if (r.Count == 0) Console.WriteLine("no paired devices (bell remote pair)");
                foreach (var d in r)
                {
                    var seen = DateTime.TryParse((string?)d?["lastSeen"], out var t) ? Ago(t) : "never";
                    var flags = ((bool?)d?["connected"] == true ? " · connected" : "") + ((bool?)d?["push"] == true ? " · push" : "");
                    Console.WriteLine($"{d?["id"],-10} {d?["name"],-24} {d?["level"],-8} seen {seen}{(d?["lastIp"] is { } ip ? " from " + ip : "")}{flags}");
                }
                return 0;
            }
            case "revoke":
                p["device"] = a.Pos(1) ?? throw new BellboardException("usage: bell remote revoke <device id> (see `bell remote devices`)");
                client.Call("remote.revoke", p);
                Console.WriteLine($"revoked {a.Pos(1)}");
                return 0;
            case "level":
                p["device"] = a.Pos(1) ?? throw new BellboardException("usage: bell remote level <device id> view|respond|full");
                p["level"] = a.Pos(2) ?? throw new BellboardException("usage: bell remote level <device id> view|respond|full");
                client.Call("remote.level", p);
                Console.WriteLine($"{a.Pos(1)} → {a.Pos(2)} (it reconnects with the new level)");
                return 0;
            case "rename":
                p["device"] = a.Pos(1) ?? throw new BellboardException("usage: bell remote rename <device id> <name>");
                p["name"] = string.Join(" ", a.Positional.Skip(2));
                client.Call("remote.rename", p);
                return 0;
            case "set":
                if (a.Opt("answer-prompts") is { } ap) p["answerPrompts"] = ap;
                if (a.Opt("push") is { } push) p["push"] = OnOff(push, "--push");
                if (a.Opt("push-text") is { } pt) p["pushText"] = OnOff(pt, "--push-text");
                if (a.Opt("away-minutes") is { } am) p["awayMinutes"] = int.TryParse(am, out var m) ? m : throw new BellboardException("--away-minutes must be a number");
                return Status(client.Call("remote.set", p), a);
            case "url":
                Console.WriteLine((string?)client.Call("remote.status", p)?["url"]);
                return 0;
            default:
                throw new BellboardException(Usage);
        }
    }

    private static bool OnOff(string v, string flag) => v.ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "1" => true,
        "off" or "false" or "no" or "0" => false,
        _ => throw new BellboardException($"{flag} takes on or off"),
    };

    private static int Status(JsonNode? r, Args a)
    {
        if (a.Has("json")) return Json(r);
        if ((bool?)r?["enabled"] != true)
        {
            Console.WriteLine("Bellboard Remote is off. Turn it on with `bell remote tailscale` (recommended) or `bell remote enable`.");
            return 0;
        }
        var connected = (r?["connected"] as JsonArray)?.Select(x => (string?)x).ToList() ?? new();
        Console.WriteLine($"Bellboard Remote is on ({r?["mode"]}, port {r?["port"]}){((bool?)r?["running"] == true ? "" : " but not running")}");
        if ((string?)r?["problem"] is { } problem) Console.WriteLine($"  problem: {problem}");
        Console.WriteLine($"  address: {r?["url"]}");
        if ((string?)r?["fingerprint"] is { } fp) Console.WriteLine($"  certificate: {fp}");
        Console.WriteLine($"  devices: {r?["devices"]} paired{(connected.Count > 0 ? ", connected now: " + string.Join(", ", connected) : "")}");
        Console.WriteLine($"  permission requests go to the phone: {r?["answerPrompts"]}{((bool?)r?["away"] == true ? " (you're away)" : "")}");
        Console.WriteLine($"  push: {((bool?)r?["push"] == true ? "on" : "off")}{((bool?)r?["pushText"] == true ? ", with message text" : "")}");
        return 0;
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalSeconds < 60 ? "just now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m ago" : d.TotalHours < 48 ? $"{(int)d.TotalHours}h ago" : $"{(int)d.TotalDays}d ago";
    }

    private static int Json(JsonNode? r)
    {
        Console.WriteLine(r?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null");
        return 0;
    }

    // ---- streams ----

    /// <summary>`bell events [--topics state,notice,prompt]`: one JSON line per event until Ctrl+C.</summary>
    public static int Events(Args a)
    {
        var topics = (a.Opt("topics") ?? "notice,prompt").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Stream(async (session, done) =>
        {
            await session.SubscribeAsync("events.subscribe", new JsonObject { ["topics"] = new JsonArray(topics.Select(t => (JsonNode)t).ToArray()) },
                (ev, data) =>
                {
                    if (ev == "overflow") { Console.Error.WriteLine("bell: fell behind; some events were dropped"); done(); return; }
                    Console.WriteLine(new JsonObject { ["event"] = ev, ["data"] = data?.DeepClone() }.ToJsonString());
                });
        });
    }

    /// <summary>`bell attach [--pane P]`: mirror a pane's output here, read-only, until Ctrl+C.</summary>
    public static int Attach(Args a)
    {
        var pane = a.Opt("pane") ?? a.Pos(0) ?? BellboardProtocol.Env(BellboardProtocol.EnvPane)
                   ?? throw new BellboardException("usage: bell attach <pane> (see `bell list`)");
        var stdout = Console.OpenStandardOutput();
        void Write(string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            stdout.Write(b, 0, b.Length);
            stdout.Flush();
        }
        return Stream(async (session, done) =>
        {
            await session.SubscribeAsync("terminal.attach", new JsonObject { ["pane"] = pane, ["scrollback"] = int.TryParse(a.Opt("lines"), out var n) ? n : 200 },
                (ev, data) =>
                {
                    switch (ev)
                    {
                        case "snapshot":
                            Write("\x1b[0m\x1b[2J\x1b[H" + (string?)data?["data"]);
                            break;
                        case "output":
                            Write((string?)data?["d"] ?? "");
                            break;
                        case "exit":
                            Write("\r\n\x1b[0m[the pane closed]\r\n");
                            done();
                            break;
                        case "overflow":
                            Write("\r\n\x1b[0m[fell behind; run bell attach again]\r\n");
                            done();
                            break;
                    }
                });
        });
    }

    private static int Stream(Func<PipeSession, Action, Task> start)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var session = PipeSession.ConnectAsync(BellboardProtocol.ResolvePipeName(), 3000).GetAwaiter().GetResult()
                      ?? throw new BellboardException("Bellboard isn't running");
        using (session)
        {
            session.Closed += () => stop.Cancel();
            start(session, () => stop.Cancel()).GetAwaiter().GetResult();
            try { Task.Delay(Timeout.Infinite, stop.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
        return 0;
    }
}
