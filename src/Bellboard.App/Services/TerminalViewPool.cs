using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Bellboard.Panes;

namespace Bellboard.Services;

/// <summary>
/// Keeps one terminal WebView2 created, configured and with terminal.html already loaded,
/// parked in a collapsed panel. A new pane adopts it and only has to send its config, so it
/// appears immediately instead of after a webview + page load (~0.5–2 s).
/// </summary>
public sealed class TerminalViewPool
{
    private readonly Panel _parking;
    private readonly Func<System.Drawing.Color> _background;
    private WebView2? _spare;
    private bool _warming;

    public TerminalViewPool(Panel parking, Func<System.Drawing.Color> background)
    {
        _parking = parking;
        _background = background;
    }

    /// <summary>Start preparing a spare view if there isn't one (idempotent).</summary>
    public void Warm()
    {
        if (_spare != null || _warming) return;
        _warming = true;
        _ = WarmAsync();
    }

    private async Task WarmAsync()
    {
        WebView2? view = null;
        try
        {
            await WebEnv.Ready;
            view = new WebView2 { DefaultBackgroundColor = _background() };
            _parking.Children.Add(view);
            await view.EnsureCoreWebView2Async(WebEnv.Env);
            var core = view.CoreWebView2;
            TerminalPane.ConfigureCore(core);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnMessage(object? s, CoreWebView2WebMessageReceivedEventArgs e)
            {
                try
                {
                    if ((string?)JsonNode.Parse(e.WebMessageAsJson)?["t"] == "hello") loaded.TrySetResult();
                }
                catch { }
            }
            core.WebMessageReceived += OnMessage;
            core.Navigate(TerminalPane.PageUrl);
            var done = await Task.WhenAny(loaded.Task, Task.Delay(15000));
            core.WebMessageReceived -= OnMessage;
            if (done != loaded.Task) throw new TimeoutException("terminal page did not load");
            _spare = view;
        }
        catch (Exception ex)
        {
            Log.Error("warming a terminal view failed", ex);
            if (view != null)
            {
                _parking.Children.Remove(view);
                view.Dispose();
            }
        }
        finally
        {
            _warming = false;
        }
    }

    /// <summary>Hands out the ready view (or null) and starts warming the next one.</summary>
    public WebView2? Take()
    {
        var view = _spare;
        _spare = null;
        if (view != null) _parking.Children.Remove(view);
        _parking.Dispatcher.InvokeAsync(Warm, DispatcherPriority.ApplicationIdle);
        return view;
    }

    public void Dispose()
    {
        _spare?.Dispose();
        _spare = null;
    }
}
