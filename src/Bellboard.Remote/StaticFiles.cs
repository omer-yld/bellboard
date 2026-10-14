using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Bellboard.Remote;

/// <summary>The phone app (wwwroot) and xterm.js, embedded in the exe and served with strict headers.</summary>
internal static class StaticFiles
{
    private sealed record Asset(byte[] Bytes, string Type, string ETag);

    private static readonly Dictionary<string, Asset> Assets = Load();

    private static Dictionary<string, Asset> Load()
    {
        var map = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("wwwroot/", StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            var path = "/" + name["wwwroot/".Length..].Replace('\\', '/');
            map[path] = new Asset(bytes, ContentType(path), "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"");
        }
        return map;
    }

    public static bool Has(string path) => Assets.ContainsKey(path);

    public static IEnumerable<string> Paths => Assets.Keys;

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".webmanifest" => "application/manifest+json",
        ".json" => "application/json",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream",
    };

    /// <summary>Serves GET/HEAD for an embedded file; "/" is index.html. Returns false if there's no such file.</summary>
    public static async Task<bool> TryServeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path == "/" || path == "/index.html") path = "/index.html";
        if (!Assets.TryGetValue(path, out var a)) return false;
        var res = ctx.Response;
        res.ContentType = a.Type;
        res.Headers.ETag = a.ETag;
        // The service worker must always be re-checked; everything else revalidates cheaply with the ETag.
        res.Headers.CacheControl = path == "/sw.js" ? "no-cache, no-store" : "no-cache";
        if (path == "/sw.js") res.Headers["Service-Worker-Allowed"] = "/";
        if (ctx.Request.Headers.IfNoneMatch == a.ETag)
        {
            res.StatusCode = StatusCodes.Status304NotModified;
            return true;
        }
        res.ContentLength = a.Bytes.Length;
        if (!HttpMethods.IsHead(ctx.Request.Method)) await res.Body.WriteAsync(a.Bytes);
        return true;
    }
}
