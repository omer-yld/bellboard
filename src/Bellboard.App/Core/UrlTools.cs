using System;
using System.Text.RegularExpressions;

namespace Bellboard.Core;

/// <summary>Address-bar semantics shared by the browser pane and the CLI (pure, unit-tested).</summary>
public static class UrlTools
{
    public const string DefaultSearch = "https://www.google.com/search?q=%s";

    public static string Search(string searchUrl, string query) =>
        searchUrl.Contains("%s") ? searchUrl.Replace("%s", Uri.EscapeDataString(query)) : searchUrl + Uri.EscapeDataString(query);

    /// <summary>
    /// Turns address-bar input into a URL: empty/"start" → the start page, "localhost:3000"/":3000" → http,
    /// "example.com" → https, anything else → a search.
    /// </summary>
    /// <summary>Sessions saved before the rename point at wmux's own pages (https://wmux.example/...); map them to Bellboard's.</summary>
    public static string? MigrateLegacy(string? url, string hostName) =>
        url == null ? null : url.StartsWith("https://wmux.example/", StringComparison.OrdinalIgnoreCase) ? $"https://{hostName}/" + url["https://wmux.example/".Length..] : url;

    public static string Normalize(string input, string startPageUrl, string searchUrl = DefaultSearch)
    {
        var s = input.Trim();
        if (s.Length == 0 || s is "start" or "about:start" or "bellboard:start" or "wmux:start") return startPageUrl;
        if (s.Contains("://") || s.StartsWith("about:") || s.StartsWith("data:") || s.StartsWith("file:")) return s;
        if (Regex.IsMatch(s, @"^:\d+")) return "http://localhost" + s;
        if (s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || s.StartsWith("127.") || s.StartsWith("[::1]") || Regex.IsMatch(s, @"^\d+\.\d+\.\d+\.\d+(:\d+)?"))
            return "http://" + s;
        if (!s.Contains(' ') && Regex.IsMatch(s, @"\.[A-Za-z]{2,}(:\d+)?(/|$)")) return "https://" + s;
        return Search(searchUrl, s);
    }
}
