using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Bellboard.Services;

/// <summary>One shared WebView2 environment (one browser process) for every terminal and browser pane.</summary>
public static class WebEnv
{
    // A reserved .example name: ".local" triggers an mDNS lookup that stalls every page load by ~2 s.
    public const string HostName = "bellboard.example";
    public static CoreWebView2Environment Env { get; private set; } = null!;
    /// <summary>Completes when the environment exists; started from the window constructor.</summary>
    public static Task Ready { get; private set; } = Task.CompletedTask;
    public static string StartPageUrl => $"https://{HostName}/start.html";

    public static Task Start() => Ready = InitAsync();
    public static string WebRoot => Path.Combine(AppContext.BaseDirectory, "web");
    public static string BrowserVersion => Env?.BrowserVersionString ?? "?";

    public static async Task InitAsync()
    {
        var dataDir = Path.Combine(Log.Dir, "WebView2");
        var options = new CoreWebView2EnvironmentOptions
        {
            // Keep GPU rasterization for the WebGL terminal renderer; quiet background throttling for hidden panes.
            AdditionalBrowserArguments = "--enable-gpu-rasterization --disable-background-timer-throttling --disable-renderer-backgrounding",
        };
        Env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
        Log.Info($"WebView2 runtime {Env.BrowserVersionString}");
    }
}

internal static class AppIcon
{
    public static System.Drawing.Icon Create()
    {
        try
        {
            if (Environment.ProcessPath is { } p && System.Drawing.Icon.ExtractAssociatedIcon(p) is { } icon) return icon;
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }
}
