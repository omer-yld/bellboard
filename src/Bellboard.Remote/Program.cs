using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Bellboard.Remote.Push;
using Bellboard.Shared;

namespace Bellboard.Remote;

/// <summary>
/// bellboard-remote.exe: the HTTPS/WebSocket server behind Bellboard's phone view. Started by Bellboard while
/// remote access is on; talks to it over the named pipe; exits when Bellboard does. See PROTOCOL.md.
/// Exit codes: 0 Bellboard went away, 2 couldn't reach Bellboard, 3 the port is in use.
/// </summary>
internal static class Program
{
    internal const string Cookie = "bb_device";

    public static async Task<int> Main()
    {
        var pipeName = BellboardProtocol.ResolvePipeName();
        WatchParent();

        PipeSession? pipe = null;
        for (int i = 0; i < 20 && pipe == null; i++)
        {
            pipe = await PipeSession.ConnectAsync(pipeName, 1000);
            if (pipe == null) await Task.Delay(500);
        }
        if (pipe == null)
        {
            Console.Error.WriteLine($@"can't reach Bellboard on \\.\pipe\{pipeName}");
            return 2;
        }
        var hello = await pipe.CallAsync("remote.hello", new JsonObject { ["pid"] = Environment.ProcessId }) as JsonObject
                    ?? throw new InvalidOperationException("remote.hello returned nothing");
        var config = Config.From(hello);
        var bridge = new Bridge(pipe, pipeName, config);
        await bridge.StartAsync();

        var stop = new CancellationTokenSource();
        pipe.Closed += () => stop.Cancel();

        WebApplication app;
        try
        {
            app = Build(bridge, config);
            await app.StartAsync(stop.Token);
        }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            Console.Error.WriteLine($"port {config.Port} is in use");
            return 3;
        }
        Console.WriteLine($"listening on {(config.CertPfx != null ? "https" : "http")}://{config.Bind}:{config.Port} (hosts: {string.Join(", ", config.Hosts)})");
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        try { await app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
        return 0;
    }

    private static bool IsAddressInUse(Exception ex) =>
        ex is IOException { InnerException: SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } }
        || ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
        || ex.GetType().Name == "AddressInUseException"
        || (ex.InnerException != null && IsAddressInUse(ex.InnerException));

    /// <summary>Exit with Bellboard even if it crashed without closing the pipe.</summary>
    private static void WatchParent()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("BELLBOARD_PARENT_PID"), out var pid)) return;
        try
        {
            var parent = Process.GetProcessById(pid);
            _ = Task.Run(async () =>
            {
                await parent.WaitForExitAsync();
                Environment.Exit(0);
            });
        }
        catch { Environment.Exit(0); }
    }

    internal static WebApplication Build(Bridge bridge, Config config)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning)
            // A port in use is reported in one line by Main (exit code 3), not as a stack trace.
            .AddFilter("Microsoft.Extensions.Hosting", LogLevel.None);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 256 * 1024;
            var ip = IPAddress.Parse(config.Bind);
            if (config.CertPfx != null)
            {
                var cert = X509CertificateLoader.LoadPkcs12(config.CertPfx, null);
                k.Listen(ip, config.Port, o => o.UseHttps(cert));
            }
            else k.Listen(ip, config.Port);
        });
        var app = builder.Build();
        // Pings every 30 s; a phone that doesn't answer within 30 s more is dropped (a dead mobile connection).
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
        var pairLimits = new LimiterMap(perSecond: 0.2, burst: 5);

        app.Use(async (ctx, next) =>
        {
            var req = ctx.Request;
            var res = ctx.Response;
            // Only names Bellboard listed (localhost, the Tailscale name, this PC on the LAN): stops DNS rebinding.
            if (!HostAllowed(req.Host.Host, config))
            {
                res.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["X-Frame-Options"] = "DENY";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            res.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; " +
                "worker-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'; object-src 'none'";
            if (IsHttps(ctx)) res.Headers["Strict-Transport-Security"] = "max-age=31536000";
            if (req.Path.StartsWithSegments("/api"))
            {
                res.Headers.CacheControl = "no-store";
                // Changes (and the socket) only from this page itself: a page on another site can't use the cookie.
                bool changes = !HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method) || ctx.WebSockets.IsWebSocketRequest;
                if (changes && !OriginAllowed(req.Headers.Origin.ToString(), config))
                {
                    res.StatusCode = StatusCodes.Status403Forbidden;
                    await res.WriteAsJsonAsync(new { error = "cross-site request refused" });
                    return;
                }
            }
            await next();
        });

        app.MapGet("/api/health", async ctx =>
        {
            var device = await DeviceOf(ctx, bridge);
            await ctx.Response.WriteAsJsonAsync(new { ok = true, version = config.Version, paired = device != null });
        });

        app.MapPost("/api/pair", async ctx =>
        {
            var ip = ClientIp(ctx);
            if (!pairLimits.TryTake(ip))
            {
                await Error(ctx, 429, "Too many tries. Wait a minute.");
                return;
            }
            var body = await ReadJson(ctx);
            var r = await bridge.CallAsync("remote.pair", new JsonObject
            {
                ["code"] = (string?)body?["code"], ["name"] = (string?)body?["name"], ["ip"] = ip,
            }) as JsonObject;
            if (r == null || (bool?)r["ok"] != true)
            {
                await Error(ctx, (int?)r?["status"] ?? 400, (string?)r?["error"] ?? "pairing failed");
                return;
            }
            ctx.Response.Cookies.Append(Cookie, (string)r["token"]!, new CookieOptions
            {
                HttpOnly = true,
                Secure = IsHttps(ctx),
                SameSite = SameSiteMode.Strict,
                Path = "/api",
                MaxAge = TimeSpan.FromDays(400),
                IsEssential = true,
            });
            await ctx.Response.WriteAsJsonAsync(new JsonObject { ["device"] = r["device"]!.DeepClone() });
        });

        app.MapPost("/api/logout", async ctx =>
        {
            if (await Require(ctx, bridge) is not { } device) return;
            await bridge.CallAsync("remote.logout", new JsonObject { ["device"] = device.Id });
            ctx.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/api", Secure = IsHttps(ctx), SameSite = SameSiteMode.Strict, HttpOnly = true });
            await ctx.Response.WriteAsJsonAsync(new { ok = true });
        });

        app.MapGet("/api/state", async ctx =>
        {
            if (await Require(ctx, bridge) is not { } device) return;
            var state = bridge.State is { } s ? (JsonObject)s.DeepClone() : await bridge.CallAsync("state.get", new JsonObject()) as JsonObject ?? new JsonObject();
            state["device"] = DeviceJson(device);
            state["now"] = DateTimeOffset.Now.ToString("o");
            await WriteJson(ctx, state);
        });

        var callLimits = new LimiterMap(perSecond: 20, burst: 40);
        app.MapPost("/api/call", async ctx =>
        {
            if (await Require(ctx, bridge) is not { } device) return;
            if (!callLimits.TryTake(device.Id))
            {
                await Error(ctx, 429, "slow down");
                return;
            }
            var body = await ReadJson(ctx);
            var method = (string?)body?["method"] ?? "";
            if (!RemotePolicy.Allows(device.Level, method) || method is "terminal.attach")
            {
                await Error(ctx, 403, RemotePolicy.Required(method) == null || method == "terminal.attach" ? $"'{method}' isn't available remotely" : RemotePolicy.Refusal(method));
                return;
            }
            var p = body?["params"] as JsonObject is { } given ? (JsonObject)given.DeepClone() : new JsonObject();
            p["remoteDevice"] = device.Id;
            try { await WriteJson(ctx, await bridge.CallAsync(method, p, 60_000)); }
            catch (BellboardException ex) { await Error(ctx, 400, ex.Message); }
        });

        app.MapGet("/api/push/key", async ctx =>
        {
            if (await Require(ctx, bridge) is null) return;
            await WriteJson(ctx, new JsonObject { ["publicKey"] = config.Push ? config.VapidPublic : null });
        });

        app.MapPost("/api/push/subscribe", async ctx =>
        {
            if (await Require(ctx, bridge) is not { } device) return;
            var sub = PushSubscription.FromJson(await ReadJson(ctx));
            if (sub == null || !PushServiceAllowed(sub.Endpoint))
            {
                await Error(ctx, 400, sub == null ? "not a push subscription" : "unknown push service");
                return;
            }
            await bridge.CallAsync("remote.push.subscribe", new JsonObject { ["device"] = device.Id, ["subscription"] = sub.ToJson() });
            await ctx.Response.WriteAsJsonAsync(new { ok = true });
        });

        app.MapDelete("/api/push/subscribe", async ctx =>
        {
            if (await Require(ctx, bridge) is not { } device) return;
            await bridge.CallAsync("remote.push.unsubscribe", new JsonObject { ["device"] = device.Id });
            await ctx.Response.WriteAsJsonAsync(new { ok = true });
        });

        app.Map("/api/live", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                await Error(ctx, 400, "expected a WebSocket");
                return;
            }
            // Asked fresh: a socket outlives the auth cache, so it must not start from a revoked device's entry.
            if (await Require(ctx, bridge, fresh: true) is not { } device) return;
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = true });
            await new LiveClient(ws, bridge, device).RunAsync(ctx.RequestAborted);
        });

        app.Map("/api/{**rest}", async ctx => await Error(ctx, 404, "no such API"));

        // "{**path}": the default fallback pattern skips paths with a file extension (app.js, xterm.css…).
        app.MapFallback("{**path}", async ctx =>
        {
            if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) && await StaticFiles.TryServeAsync(ctx)) return;
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        });
        return app;
    }

    // ---- helpers ----

    /// <summary>
    /// Browsers' push services (Chrome/Android: FCM; Firefox: Mozilla; Safari/iOS: Apple; Edge: WNS). Anything else
    /// is refused, so a device can't make the sidecar POST to addresses on your network.
    /// </summary>
    internal static bool PushServiceAllowed(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort) return false;
        var host = uri.Host.ToLowerInvariant();
        return host is "fcm.googleapis.com" or "android.googleapis.com" or "updates.push.services.mozilla.com"
               || host.EndsWith(".push.apple.com") || host.EndsWith(".notify.windows.com") || host.EndsWith(".push.services.mozilla.com");
    }

    internal static bool HostAllowed(string host, Config config)
    {
        var h = host.Trim('[', ']').ToLowerInvariant();
        return h.Length > 0 && (config.Hosts.Contains(h) || config.Hosts.Contains("[" + h + "]"));
    }

    /// <summary>
    /// The exact origin (scheme, host and port) must be one Bellboard listed: another port on the same host is
    /// "same-site" to the browser, so the SameSite cookie alone wouldn't stop it.
    /// </summary>
    internal static bool OriginAllowed(string origin, Config config) =>
        NormalizeOrigin(origin) is { } o && config.Origins.Contains(o);

    /// <summary>"HTTPS://PC.ts.net:443" → "https://pc.ts.net"; null if it isn't an http(s) origin.</summary>
    internal static string? NormalizeOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.Query.Length > 0) return null;
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    private static bool IsHttps(HttpContext ctx) =>
        ctx.Request.IsHttps
        || (IsLoopback(ctx) && string.Equals(ctx.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase))
        || ctx.Request.Headers.Origin.ToString().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopback(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } a && IPAddress.IsLoopback(a);

    /// <summary>The phone's address: from Tailscale Serve's X-Forwarded-For when the request came through it.</summary>
    private static string ClientIp(HttpContext ctx)
    {
        // The proxy appends the address it saw: take the last hop, not one the client could have written.
        if (IsLoopback(ctx) && ctx.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } fwd)
            return fwd.Split(',')[^1].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    }

    private static readonly LimiterMap AuthLimits = new(perSecond: 1, burst: 10);

    private static async Task<Device?> DeviceOf(HttpContext ctx, Bridge bridge, bool fresh = false)
    {
        var token = ctx.Request.Cookies[Cookie];
        if (string.IsNullOrEmpty(token)) return null;
        if (!fresh && bridge.Cached(token) is { } known) return known;
        // Each unknown token costs a trip to Bellboard: limit how often one address can make us check.
        if (!AuthLimits.TryTake(ClientIp(ctx))) return null;
        try { return await bridge.AuthenticateAsync(token, ClientIp(ctx), fresh); }
        catch (Exception ex) when (ex is BellboardException or OperationCanceledException)
        {
            Console.Error.WriteLine("auth: " + ex.Message);
            return null;
        }
    }

    private static async Task<Device?> Require(HttpContext ctx, Bridge bridge, bool fresh = false)
    {
        var d = await DeviceOf(ctx, bridge, fresh);
        if (d == null) await Error(ctx, 401, "not paired");
        return d;
    }

    private static JsonObject DeviceJson(Device d) => new() { ["id"] = d.Id, ["name"] = d.Name, ["level"] = d.Level };

    private static async Task<JsonNode?> ReadJson(HttpContext ctx)
    {
        try { return await JsonNode.ParseAsync(ctx.Request.Body); }
        catch { return null; }
    }

    private static async Task WriteJson(HttpContext ctx, JsonNode? node)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(node?.ToJsonString() ?? "null");
    }

    private static async Task Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        await WriteJson(ctx, new JsonObject { ["error"] = message });
    }
}
