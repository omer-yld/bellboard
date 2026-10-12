using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Bellboard.Core;
using Bellboard.Services;

namespace Bellboard.Panes;

/// <summary>
/// A real Chromium (WebView2) pane next to the terminals, scriptable through the pipe API via the
/// Chrome DevTools Protocol: snapshot, click, type, eval, console/network capture, screenshots.
/// </summary>
public sealed class BrowserPane : PaneBase
{
    public override string Kind => "browser";
    public string? InitialUrl { get; init; }
    public string Url => Web.CoreWebView2?.Source ?? InitialUrl ?? "";
    public bool OnStartPage => Url.StartsWith(WebEnv.StartPageUrl, StringComparison.OrdinalIgnoreCase);

    private readonly TextBox _address;
    private readonly List<JsonObject> _console = new();
    private readonly List<JsonObject> _network = new();
    private readonly Dictionary<string, string> _requestUrls = new();

    private const string PageScript = """
        (() => {
          const post = (m) => { try { window.chrome.webview.postMessage(m); } catch (e) {} };
          window.addEventListener('focus', () => post({ t: 'focus' }), true);
          document.addEventListener('pointerdown', () => post({ t: 'focus' }), true);
        })();
        """;

    public BrowserPane(MuxController mux, Workspace ws, string id) : base(mux, ws, id)
    {
        var bar = new DockPanel { Background = (Brush)Application.Current.Resources["PaneHeaderBg"], Height = 34, LastChildFill = true };
        bar.Children.Add(NavButton("\uE72B", "Back (Alt+Left)", () => Web.CoreWebView2?.GoBack()));
        bar.Children.Add(NavButton("\uE72A", "Forward (Alt+Right)", () => Web.CoreWebView2?.GoForward()));
        bar.Children.Add(NavButton("\uE72C", "Reload (F5)", () => Web.CoreWebView2?.Reload()));
        bar.Children.Add(NavButton("\uE80F", "Start page", () => { Navigate(""); FocusAddress(); }));
        var devtools = NavButton("\uEC7A", "DevTools (F12)", () => Web.CoreWebView2?.OpenDevToolsWindow());
        DockPanel.SetDock(devtools, Dock.Right);
        bar.Children.Add(devtools);
        _address = new TextBox
        {
            Style = (Style)Application.Current.Resources["AddressBox"],
            Tag = "Search or type a URL",
        };
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Navigate(_address.Text); FocusContent(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _address.Text = DisplayUrl; FocusContent(); e.Handled = true; }
        };
        _address.GotKeyboardFocus += (_, _) => { _address.SelectAll(); Mux.OnPaneFocused(this); };
        bar.Children.Add(_address);

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(Web);
        Host.Child = dock;
    }

    private static Button NavButton(string glyph, string tip, Action onClick)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)Application.Current.Resources["NavButton"] };
        DockPanel.SetDock(b, Dock.Left);
        b.Click += (_, _) => onClick();
        return b;
    }

    public override async Task InitializeAsync()
    {
        await Web.EnsureCoreWebView2Async(WebEnv.Env);
        if (Disposed) return;
        var core = Web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        // Serves the start page; the folder only holds Bellboard's own static pages.
        core.SetVirtualHostNameToFolderMapping(WebEnv.HostName, WebEnv.WebRoot, CoreWebView2HostResourceAccessKind.Allow);
        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
        core.SourceChanged += (_, _) => { if (!_address.IsKeyboardFocused) _address.Text = DisplayUrl; Workspace.Refresh(); };
        core.DocumentTitleChanged += (_, _) =>
        {
            Title = core.DocumentTitle;
            if (!OnStartPage) Mux.RememberPage(core.Source, core.DocumentTitle);
            Workspace.Refresh();
        };
        // Page content may only report focus; the start page may also ask for its data.
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                switch ((string?)JsonNode.Parse(e.WebMessageAsJson)?["t"])
                {
                    case "focus":
                        Mux.OnPaneFocused(this);
                        break;
                    case "start-ready" when e.Source.StartsWith(WebEnv.StartPageUrl, StringComparison.OrdinalIgnoreCase):
                        SendStartData();
                        break;
                }
            }
            catch { }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript);

        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) => OnConsole(e.ParameterObjectAsJson);
        core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) => OnException(e.ParameterObjectAsJson);
        core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += (_, e) => OnRequest(e.ParameterObjectAsJson);
        core.GetDevToolsProtocolEventReceiver("Network.responseReceived").DevToolsProtocolEventReceived += (_, e) => OnResponse(e.ParameterObjectAsJson);
        core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_, e) => OnFailed(e.ParameterObjectAsJson);

        core.NavigationCompleted += (_, e) =>
        {
            _nav?.TrySetResult(e.IsSuccess);
            if (e.IsSuccess && !OnStartPage) Mux.RememberPage(core.Source, core.DocumentTitle);
        };
        _nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Navigate(InitialUrl ?? Mux.Config.HomeUrl ?? "");
        _ready.TrySetResult();
    }

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _nav;

    public async Task<bool> NavigateAndWaitAsync(string input, int timeoutMs)
    {
        await _ready.Task;
        _nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Navigate(input);
        return await WaitLoadedAsync(timeoutMs);
    }

    public async Task<bool> WaitLoadedAsync(int timeoutMs)
    {
        if (await Task.WhenAny(_ready.Task, Task.Delay(timeoutMs)) != _ready.Task) return false;
        var nav = _nav;
        if (nav == null) return true;
        return await Task.WhenAny(nav.Task, Task.Delay(timeoutMs)) == nav.Task && nav.Task.Result;
    }

    public async Task GoAsync(string action, int timeoutMs)
    {
        await _ready.Task;
        _nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        switch (action)
        {
            case "back": if (Core.CanGoBack) Core.GoBack(); else _nav.TrySetResult(true); break;
            case "forward": if (Core.CanGoForward) Core.GoForward(); else _nav.TrySetResult(true); break;
            default: Core.Reload(); break;
        }
        await WaitLoadedAsync(timeoutMs);
    }

    /// <summary>Navigate to a URL, a search, or (empty input) the start page.</summary>
    public void Navigate(string input)
    {
        var url = NormalizeUrl(input, Mux.Config.SearchUrl);
        _address.Text = url == WebEnv.StartPageUrl ? "" : url;
        try { Web.CoreWebView2?.Navigate(url); }
        catch (ArgumentException) { Web.CoreWebView2?.Navigate(UrlTools.Search(Mux.Config.SearchUrl, input)); }
    }

    private string DisplayUrl => OnStartPage ? "" : Url;

    public void FocusAddress()
    {
        _address.Focus();
        Keyboard.Focus(_address);
        _address.SelectAll();
    }

    /// <summary>Data for the start page: this workspace's dev-server ports and recent pages.</summary>
    private void SendStartData()
    {
        var ports = Workspace.Ports.Select(p => (JsonNode)new JsonObject { ["port"] = p, ["url"] = $"http://localhost:{p}" }).ToArray();
        var recent = Mux.BrowserHistory.Take(8).Select(h => (JsonNode)new JsonObject { ["url"] = h.Url, ["title"] = h.Title }).ToArray();
        var msg = new JsonObject
        {
            ["t"] = "start",
            ["workspace"] = Workspace.Name,
            ["ports"] = new JsonArray(ports),
            ["recent"] = new JsonArray(recent),
            ["search"] = Mux.Config.SearchUrl,
        };
        try { Web.CoreWebView2?.PostWebMessageAsJson(msg.ToJsonString()); } catch { }
    }

    public static string NormalizeUrl(string input, string searchUrl = UrlTools.DefaultSearch) =>
        UrlTools.Normalize(input, WebEnv.StartPageUrl, searchUrl);

    // ---- CDP event capture ----

    private void OnConsole(string json)
    {
        var p = JsonNode.Parse(json)!;
        var args = p["args"]?.AsArray().Select(a => a?["value"]?.ToJsonString().Trim('"') ?? (string?)a?["description"] ?? (string?)a?["type"] ?? "").ToArray() ?? [];
        Add(_console, new JsonObject { ["level"] = (string?)p["type"], ["text"] = string.Join(" ", args), ["at"] = DateTime.Now.ToString("HH:mm:ss.fff") });
    }

    private void OnException(string json)
    {
        var d = JsonNode.Parse(json)?["exceptionDetails"];
        var text = (string?)d?["exception"]?["description"] ?? (string?)d?["text"] ?? "exception";
        Add(_console, new JsonObject { ["level"] = "exception", ["text"] = text, ["at"] = DateTime.Now.ToString("HH:mm:ss.fff") });
    }

    private void OnRequest(string json)
    {
        var p = JsonNode.Parse(json)!;
        if ((string?)p["requestId"] is { } id && (string?)p["request"]?["url"] is { } url)
        {
            _requestUrls[id] = $"{(string?)p["request"]?["method"]} {url}";
            if (_requestUrls.Count > 2000) _requestUrls.Clear();
        }
    }

    private void OnResponse(string json)
    {
        var p = JsonNode.Parse(json)!;
        var r = p["response"];
        Add(_network, new JsonObject
        {
            ["status"] = (int?)r?["status"],
            ["url"] = (string?)r?["url"],
            ["type"] = (string?)p["type"],
            ["mime"] = (string?)r?["mimeType"],
            ["at"] = DateTime.Now.ToString("HH:mm:ss.fff"),
        });
    }

    private void OnFailed(string json)
    {
        var p = JsonNode.Parse(json)!;
        var id = (string?)p["requestId"] ?? "";
        _requestUrls.TryGetValue(id, out var url);
        Add(_network, new JsonObject { ["status"] = 0, ["url"] = url, ["error"] = (string?)p["errorText"], ["at"] = DateTime.Now.ToString("HH:mm:ss.fff") });
    }

    private static void Add(List<JsonObject> list, JsonObject item)
    {
        list.Add(item);
        if (list.Count > 500) list.RemoveRange(0, list.Count - 500);
    }

    public JsonArray ConsoleLog(int last, bool clear) => Take(_console, last, clear);
    public JsonArray NetworkLog(int last, bool clear) => Take(_network, last, clear);

    private static JsonArray Take(List<JsonObject> list, int last, bool clear)
    {
        var arr = new JsonArray(list.Skip(Math.Max(0, list.Count - last)).Select(o => (JsonNode)o.DeepClone()).ToArray());
        if (clear) list.Clear();
        return arr;
    }

    // ---- Automation ----

    private CoreWebView2 Core => Web.CoreWebView2 ?? throw new InvalidOperationException("browser pane is not ready");

    public async Task<JsonNode?> Cdp(string method, JsonObject? args = null)
    {
        var json = await Core.CallDevToolsProtocolMethodAsync(method, (args ?? new JsonObject()).ToJsonString());
        return JsonNode.Parse(json);
    }

    /// <summary>Runtime.evaluate with promise support; returns the JSON value.</summary>
    public async Task<JsonNode?> EvalAsync(string expression)
    {
        var r = await Cdp("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["awaitPromise"] = true,
            ["returnByValue"] = true,
            ["userGesture"] = true,
        });
        if (r?["exceptionDetails"] is { } ex)
            throw new InvalidOperationException((string?)ex["exception"]?["description"] ?? (string?)ex["text"] ?? "evaluation failed");
        return r?["result"]?["value"]?.DeepClone();
    }

    public async Task<string> SnapshotAsync()
    {
        var v = await EvalAsync(Automation.SnapshotScript);
        return (string?)v ?? "";
    }

    private async Task<(double X, double Y)> LocateAsync(string target)
    {
        var v = await EvalAsync(Automation.LocateScript(target));
        if (v is not JsonObject o) throw new InvalidOperationException($"no element matches '{target}' (run `bell browser snapshot` for refs)");
        return ((double)o["x"]!, (double)o["y"]!);
    }

    public async Task ClickAsync(string target)
    {
        var (x, y) = await LocateAsync(target);
        await Cdp("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y });
        await Cdp("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mousePressed", ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 });
        await Cdp("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseReleased", ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 });
    }

    public async Task TypeAsync(string? target, string text, bool clear, bool submit)
    {
        if (target != null)
        {
            var ok = await EvalAsync(Automation.FocusScript(target, clear));
            if (ok?.GetValue<bool>() != true) throw new InvalidOperationException($"no element matches '{target}'");
        }
        if (text.Length > 0) await Cdp("Input.insertText", new JsonObject { ["text"] = text });
        if (submit) await PressAsync("Enter");
    }

    public async Task PressAsync(string key)
    {
        var (k, code, vk, text) = Automation.Key(key);
        var down = new JsonObject { ["type"] = text != null ? "keyDown" : "rawKeyDown", ["key"] = k, ["code"] = code, ["windowsVirtualKeyCode"] = vk };
        if (text != null) down["text"] = text;
        await Cdp("Input.dispatchKeyEvent", down);
        await Cdp("Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = k, ["code"] = code, ["windowsVirtualKeyCode"] = vk });
    }

    public async Task<string> ScreenshotBase64Async(bool fullPage)
    {
        var args = new JsonObject { ["format"] = "png" };
        if (fullPage) args["captureBeyondViewport"] = true;
        var r = await Cdp("Page.captureScreenshot", args);
        return (string?)r?["data"] ?? throw new InvalidOperationException("screenshot failed");
    }

    public async Task<bool> WaitAsync(string? selector, string? text, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var check = Automation.WaitCheckScript(selector, text);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((await EvalAsync(check))?.GetValue<bool>() == true) return true;
            }
            catch { }
            await Task.Delay(200);
        }
        return false;
    }

    public override void FocusContent()
    {
        base.FocusContent();
    }

    public override Dictionary<string, object?> Describe()
    {
        var d = base.Describe();
        d["url"] = Url;
        return d;
    }
}

internal static class Automation
{
    private static string Js(string s) => JsonSerializer.Serialize(s);

    /// <summary>Compact, agent-friendly view of the page: interactive elements and headings with stable refs.</summary>
    public const string SnapshotScript = """
        (() => {
          const ROLES = new Set(['button','link','textbox','searchbox','combobox','checkbox','radio','switch','tab','menuitem','option','slider','spinbutton','heading','listbox','img']);
          const clip = (s, n) => (s || '').replace(/\s+/g, ' ').trim().slice(0, n);
          const visible = (el) => {
            const r = el.getBoundingClientRect();
            if (r.width === 0 && r.height === 0) return false;
            const s = getComputedStyle(el);
            return s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
          };
          const labelOf = (el) => {
            if (el.id) { const l = document.querySelector('label[for="' + CSS.escape(el.id) + '"]'); if (l) return l.innerText; }
            const p = el.closest('label'); return p ? p.innerText : '';
          };
          const roleOf = (el) => {
            const r = el.getAttribute('role'); if (r) return r;
            const t = el.tagName.toLowerCase();
            if (t === 'a') return el.hasAttribute('href') ? 'link' : null;
            if (t === 'button' || t === 'summary') return 'button';
            if (t === 'select') return 'combobox';
            if (t === 'textarea') return 'textbox';
            if (t === 'img') return el.getAttribute('alt') ? 'img' : null;
            if (/^h[1-6]$/.test(t)) return 'heading';
            if (t === 'input') {
              const ty = (el.type || 'text').toLowerCase();
              if (ty === 'hidden') return null;
              return ({checkbox:'checkbox', radio:'radio', submit:'button', button:'button', reset:'button', image:'button', range:'slider', search:'searchbox', number:'spinbutton'})[ty] || 'textbox';
            }
            if (el.isContentEditable) return 'textbox';
            if (el.hasAttribute('onclick')) return 'button';
            return null;
          };
          const nameOf = (el) => clip(el.getAttribute('aria-label') || labelOf(el) || el.innerText || el.getAttribute('placeholder') ||
            el.getAttribute('title') || el.getAttribute('alt') || (el.type === 'submit' ? el.value : '') || el.getAttribute('name'), 80);
          window.__bellboardRef = window.__bellboardRef || 0;
          const out = [];
          for (const el of document.querySelectorAll('a[href],button,input,select,textarea,summary,[role],[contenteditable],[onclick],h1,h2,h3,h4,h5,h6,img[alt]')) {
            const role = roleOf(el);
            if (!role || !ROLES.has(role) || !visible(el)) continue;
            let ref = el.getAttribute('data-bb-ref');
            if (!ref) { ref = 'e' + (++window.__bellboardRef); el.setAttribute('data-bb-ref', ref); }
            let line = '- ' + role + ' "' + nameOf(el).replace(/"/g, "'") + '" [ref=' + ref + ']';
            if (role === 'heading') line += ' [level=' + (el.getAttribute('aria-level') || el.tagName[1] || '') + ']';
            if (role === 'link') { const h = el.getAttribute('href'); if (h && !h.startsWith('javascript:')) line += ' -> ' + h.slice(0, 120); }
            if (['textbox','searchbox','combobox','spinbutton'].includes(role)) line += ' value="' + clip(el.value ?? el.innerText, 60) + '"';
            if ((role === 'checkbox' || role === 'radio' || role === 'switch') && (el.checked || el.getAttribute('aria-checked') === 'true')) line += ' [checked]';
            if (el.disabled || el.getAttribute('aria-disabled') === 'true') line += ' [disabled]';
            out.push(line);
            if (out.length >= 500) { out.push('- … truncated'); break; }
          }
          return 'page: ' + document.title + '\nurl: ' + location.href + '\n' + out.join('\n');
        })()
        """;

    private const string FindFn = """
        const __find = (t) => {
          const m = /^@?(e\d+)$/.exec(t);
          if (m) return document.querySelector('[data-bb-ref="' + m[1] + '"]');
          if (t.startsWith('text=')) {
            const want = t.slice(5).toLowerCase();
            const all = [...document.querySelectorAll('a,button,input,select,textarea,summary,label,[role],[onclick]')];
            return all.find(e => (e.innerText || e.value || '').toLowerCase().includes(want)) || null;
          }
          return document.querySelector(t);
        };
        """;

    public static string LocateScript(string target) => $$"""
        (() => { {{FindFn}}
          const el = __find({{Js(target)}});
          if (!el) return null;
          el.scrollIntoView({ block: 'center', inline: 'center' });
          const r = el.getBoundingClientRect();
          return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
        })()
        """;

    public static string FocusScript(string target, bool clear) => $$"""
        (() => { {{FindFn}}
          const el = __find({{Js(target)}});
          if (!el) return false;
          el.scrollIntoView({ block: 'center' });
          el.focus();
          if ({{(clear ? "true" : "false")}}) {
            if ('value' in el) { el.value = ''; el.dispatchEvent(new Event('input', { bubbles: true })); }
            else if (el.isContentEditable) el.textContent = '';
          } else if (typeof el.setSelectionRange === 'function' && typeof el.value === 'string') {
            try { el.setSelectionRange(el.value.length, el.value.length); } catch (e) {}
          }
          return true;
        })()
        """;

    public static string WaitCheckScript(string? selector, string? text) => $$"""
        (() => {
          const sel = {{Js(selector ?? "")}}, txt = {{Js(text ?? "")}};
          if (sel && !document.querySelector(sel)) return false;
          if (txt && !(document.body && document.body.innerText.includes(txt))) return false;
          return document.readyState !== 'loading';
        })()
        """;

    public static (string Key, string Code, int Vk, string? Text) Key(string name) => name.ToLowerInvariant() switch
    {
        "enter" or "return" => ("Enter", "Enter", 13, "\r"),
        "tab" => ("Tab", "Tab", 9, null),
        "escape" or "esc" => ("Escape", "Escape", 27, null),
        "backspace" => ("Backspace", "Backspace", 8, null),
        "delete" => ("Delete", "Delete", 46, null),
        "space" => (" ", "Space", 32, " "),
        "arrowup" or "up" => ("ArrowUp", "ArrowUp", 38, null),
        "arrowdown" or "down" => ("ArrowDown", "ArrowDown", 40, null),
        "arrowleft" or "left" => ("ArrowLeft", "ArrowLeft", 37, null),
        "arrowright" or "right" => ("ArrowRight", "ArrowRight", 39, null),
        "home" => ("Home", "Home", 36, null),
        "end" => ("End", "End", 35, null),
        "pageup" => ("PageUp", "PageUp", 33, null),
        "pagedown" => ("PageDown", "PageDown", 34, null),
        _ when name.Length == 1 => (name, "Key" + char.ToUpperInvariant(name[0]), char.ToUpperInvariant(name[0]), name),
        _ => throw new InvalidOperationException($"unknown key '{name}'"),
    };
}
