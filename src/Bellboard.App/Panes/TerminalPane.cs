using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Bellboard.Core;
using Bellboard.Services;
using Bellboard.Shared;
using Bellboard.Terminal;

namespace Bellboard.Panes;

/// <param name="Since">When the agent entered this status (for "needs input · 3m").</param>
/// <param name="Message">Latest thing the agent said or was asked (permission prompt, task, …).</param>
public sealed record AgentInfo(string Kind, string? SessionId, string Status, DateTime Since = default, string? Message = null);

/// <summary>
/// A shell in a pseudoconsole, rendered by xterm.js (WebGL) inside WebView2.
/// Output: ConPTY → reader thread → OscScanner + batched PostWebMessage → xterm.write.
/// Input:  xterm onData → WebMessage → ConPTY input pipe.
/// </summary>
public sealed class TerminalPane : PaneBase
{
    private static readonly JsonSerializerOptions JsonOut = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const int ChunkChars = 32 * 1024;

    public override string Kind => "terminal";
    public int? ShellPid => _pty?.ProcessId;
    public string? Backend => _pty?.Backend;
    public AgentInfo? Agent { get; set; }
    /// <summary>Subagents/teammates the agent in this pane is running (from SubagentStart/Stop hooks): id → type.</summary>
    public Dictionary<string, (string Type, DateTime Started)> Subagents { get; } = new();
    public (int State, int Percent)? Progress { get; private set; }
    public string? Renderer { get; private set; }

    // Set before InitializeAsync.
    public string? StartCwd { get; init; }
    public string? StartCommand { get; init; }
    /// <summary>Extra environment for this pane's shell (e.g. BELLBOARD_AGENT_TASK for agent workspaces).</summary>
    public Dictionary<string, string>? ExtraEnv { get; init; }
    /// <summary>Per-pane shell (alias like "wsl", "bash", "cmd" or a command line); null = config default.</summary>
    public string? Shell { get; init; }
    public string? RestoreScrollback { get; set; }
    public AgentInfo? RestoreAgent { get; set; }

    /// <summary>
    /// A feed: a read-only pane that shows what another program reports (AI Buddy's coding-agent runs), written through
    /// `feed.write`. It has no shell, takes no typing, and isn't kept in the session.
    /// </summary>
    public bool Feed { get; init; }
    /// <summary>A feed's badge text (≤ 4 letters) and the label in its header.</summary>
    public string? FeedBadge { get; set; }
    public string? FeedLabel { get; set; }

    /// <summary>Agent session to persist: the live one from hooks, or a restored one whose resume hasn't been sent yet.</summary>
    public AgentInfo? SessionAgent => Agent ?? (_resumeSent ? null : RestoreAgent);

    private PtySession? _pty;
    private readonly OscScanner _osc = new();
    private readonly object _outLock = new();
    private readonly StringBuilder _outBuf = new();
    private readonly List<string> _preReady = new();
    private bool _flushScheduled, _pageReady, _exited, _sawPrompt, _resumeSent, _resumePending, _sawOutput, _sawPromptRaw;
    public ShellKind ShellKind { get; private set; }
    private string? _pendingInput;
    private DateTime _startedAt;
    private int _cols = 80, _rows = 24;
    private bool _warm;

    // Startup timeline (ms since the pane was created), logged once the first prompt appears.
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _timeline = new();
    private bool _timelineLogged;

    private static bool _shellPoolPrimed;
    // Cell size in DIPs, learned from the first rendered terminal; used to size new shells before xterm exists.
    private static double _cellW, _cellH;

    public static string PageUrl => $"https://{WebEnv.HostName}/terminal.html";

    public TerminalPane(MuxController mux, Workspace ws, string id, WebView2? warmView = null) : base(mux, ws, id, warmView)
    {
        _warm = warmView?.CoreWebView2 != null;
        Host.Child = Web;
        _osc.Title += t => Ui(() => { Title = t; Workspace.Refresh(); });
        _osc.Cwd += p =>
        {
            if (!_sawPromptRaw) { _sawPromptRaw = true; var at = _clock.ElapsedMilliseconds; Ui(() => Mark($"prompt-emitted@{at}")); }
            Ui(() => OnCwd(p));
        };
        _osc.Notify += (t, b) => Ui(() => Mux.Notifications.Post(this, t, b, "osc"));
        _osc.Progress += (s, p) => Ui(() => { Progress = s == 0 ? null : (s, p); Workspace.Refresh(); });
        _osc.Bell += () => Ui(OnBell);
    }

    /// <summary>
    /// The shell starts right away, sized from the pane's layout, in parallel with the renderer.
    /// Its output is buffered until xterm.js is ready, then the size is corrected.
    /// </summary>
    public override async Task InitializeAsync()
    {
        (_cols, _rows) = EstimateSize();
        if (!Feed)
        {
            StartShell();
            Mark(Prewarmed != null ? "shell (pre-started)" : "shell");
        }
        else PostOutput("\x1b[?25l"); // no cursor: nothing types here
        Workspace.Refresh();

        if (_warm)
        {
            Attach(Web.CoreWebView2);
            Mark("view (warm)");
            PostConfig();
            return;
        }
        await WebEnv.Ready;
        await Web.EnsureCoreWebView2Async(WebEnv.Env);
        if (Disposed) return;
        ConfigureCore(Web.CoreWebView2);
        Attach(Web.CoreWebView2);
        Mark("view");
        Web.CoreWebView2.Navigate(PageUrl);
    }

    /// <summary>Settings shared by pane-owned and pre-warmed terminal views.</summary>
    public static void ConfigureCore(CoreWebView2 core)
    {
        core.SetVirtualHostNameToFolderMapping(WebEnv.HostName, WebEnv.WebRoot, CoreWebView2HostResourceAccessKind.Allow);
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreDevToolsEnabled = App.DevMode;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{WebEnv.HostName}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
    }

    private void Attach(CoreWebView2 core)
    {
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, e) => Log.Error($"terminal {Id} webview process failed: {e.ProcessFailedKind}");
    }

    /// <summary>Terminal size for an area of w×h DIPs, using cell metrics learned from rendered panes.</summary>
    public static (int Cols, int Rows) SizeFor(double w, double h, double fontSize, double lineHeight)
    {
        if (w < 60 || h < 60) return (120, 30);
        double cw = _cellW > 0 ? _cellW : fontSize * 0.602;
        double ch = _cellH > 0 ? _cellH : Math.Ceiling(fontSize * lineHeight * 1.2);
        return ((int)Math.Clamp((w - 4 - 14 - 8) / cw, 20, 1000), (int)Math.Clamp((h - 4 - 10) / ch, 5, 500));
    }

    private (int Cols, int Rows) EstimateSize()
    {
        double w = Host.ActualWidth, h = Host.ActualHeight;
        if (w < 60 || h < 60)
        {
            Host.UpdateLayout();
            w = Host.ActualWidth;
            h = Host.ActualHeight;
        }
        if (w < 60 || h < 60)
        {
            // Not laid out yet (e.g. a workspace that isn't visible): use the content area.
            w = Mux.Window.ContentHost.ActualWidth;
            h = Mux.Window.ContentHost.ActualHeight;
        }
        if (w < 60 || h < 60) return (120, 30);
        double fs = Mux.Config.FontSize;
        double cw = _cellW > 0 ? _cellW : fs * 0.602;
        double ch = _cellH > 0 ? _cellH : Math.Ceiling(fs * Mux.Config.LineHeight * 1.2);
        // Ring border (2+2), page insets (10+4 / 6+4), scrollbar (8).
        return ((int)Math.Clamp((w - 4 - 14 - 8) / cw, 20, 1000), (int)Math.Clamp((h - 4 - 10) / ch, 5, 500));
    }

    private void Mark(string what)
    {
        if (_timelineLogged) return;
        _timeline.Add($"{what} {_clock.ElapsedMilliseconds}ms");
    }

    private void LogTimeline()
    {
        if (_timelineLogged) return;
        Mark("prompt");
        _timelineLogged = true;
        Log.Info($"pane {Id} startup: {string.Join(" → ", _timeline)}");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith($"https://{WebEnv.HostName}/", StringComparison.OrdinalIgnoreCase)) return;
        JsonObject m;
        try { m = JsonNode.Parse(e.WebMessageAsJson)!.AsObject(); } catch { return; }
        switch ((string?)m["t"])
        {
            case "hello":
                Mark("page");
                PostConfig();
                break;
            case "ready":
                Renderer = (string?)m["renderer"];
                if (m["perf"] is JsonObject perf) _timeline.Add("[page " + perf.ToJsonString() + "]");
                OnReady((int?)m["cols"] ?? 80, (int?)m["rows"] ?? 24);
                break;
            case "i":
                if (Feed) return; // nothing to type into
                if (_exited)
                {
                    // A pinned pane keeps its place: a key starts a new shell instead of closing it.
                    if (MuxController.PinnedReason(this) != null) Mux.RestartTerminal(this);
                    else Mux.ClosePane(this);
                    return;
                }
                var d = (string?)m["d"] ?? "";
                var bin = m["bin"]?.GetValue<bool>() == true;
                // Typing at the desk takes the terminal back from a phone that resized it.
                if (_fit != null) EndFit();
                if (bin) _pty?.WriteBinary(d); else _pty?.Write(d);
                // Broadcast: the same keystrokes go to every other terminal in the workspace.
                if (Workspace.Broadcast)
                    foreach (var other in Workspace.Panes.OfType<TerminalPane>().Where(t => t != this))
                        other.Echo(d, bin);
                break;
            case "r":
                _cols = (int?)m["cols"] ?? _cols;
                _rows = (int?)m["rows"] ?? _rows;
                if (_fit == null)
                {
                    _pty?.Resize(_cols, _rows);
                    MirrorResize();
                }
                break;
            case "snap":
                if ((int?)m["id"] is { } snapId && _snapshots.Remove(snapId, out var waiting))
                    waiting.TrySetResult(((string?)m["data"] ?? "", (int?)m["cols"] ?? _cols, (int?)m["rows"] ?? _rows));
                break;
            case "focus":
                Mux.OnPaneFocused(this);
                break;
            case "key":
                Mux.Window.HandleWebShortcut(m);
                break;
            case "copy":
                try { Clipboard.SetText((string?)m["d"] ?? ""); } catch (Exception ex) { Log.Error("clipboard", ex); }
                break;
            case "paste-request":
                try
                {
                    if (Clipboard.ContainsText()) Post(new { t = "paste", d = Clipboard.GetText() });
                    else if (Clipboard.ContainsFileDropList())
                    {
                        var files = Clipboard.GetFileDropList();
                        var parts = new List<string>();
                        foreach (var f in files) if (f != null) parts.Add(f.Contains(' ') ? $"\"{f}\"" : f);
                        Post(new { t = "paste", d = string.Join(" ", parts) });
                    }
                }
                catch (Exception ex) { Log.Error("clipboard", ex); }
                break;
            case "link":
                Mux.OpenLink(this, (string?)m["url"] ?? "", m["external"]?.GetValue<bool>() == true);
                break;
        }
    }

    /// <summary>Push font/theme changes to the live terminal.</summary>
    public void ApplyConfig()
    {
        Web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(Mux.Config.Theme["background"]);
        if (_pageReady) PostConfig();
    }

    private void PostConfig()
    {
        var c = Mux.Config;
        Post(new
        {
            t = "config",
            fontFamily = Mux.FontStack,
            fontSize = c.FontSize,
            lineHeight = c.LineHeight,
            cursorStyle = c.CursorStyle,
            cursorBlink = c.CursorBlink,
            scrollback = c.Scrollback,
            theme = c.Theme,
            buildNumber = Environment.OSVersion.Version.Build,
        });
    }

    // ---- Unloading idle panes (renderer freed, shell keeps running) ----

    public bool Unloaded { get; private set; }
    private string? _reloadSnapshot;
    private int _preReadyChars;
    private bool _preReadyTrimmed;
    private const int MaxHiddenOutput = 4 * 1024 * 1024;

    /// <summary>Snapshot the screen and free the WebView2 renderer; output is buffered until ReloadAsync.</summary>
    public async Task<bool> UnloadAsync()
    {
        if (Unloaded || !_pageReady || Disposed || Web.CoreWebView2 == null) return false;
        string? snapshot;
        try
        {
            var json = await Web.CoreWebView2.ExecuteScriptAsync("window.bellboard ? bellboard.serialize(5000, true) : null");
            snapshot = JsonSerializer.Deserialize<string?>(json);
        }
        catch { return false; }
        if (snapshot == null || Disposed) return false;
        Unloaded = true;
        _pageReady = false;
        _reloadSnapshot = snapshot;
        var old = Web;
        Host.Child = null;
        try { old.Dispose(); } catch { }
        Web = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(Mux.Config.Theme["background"]) };
        Log.Info($"pane {Id}: unloaded its renderer (idle workspace)");
        return true;
    }

    /// <summary>Bring the renderer back and replay the snapshot plus everything the shell printed meanwhile.</summary>
    public async Task ReloadAsync()
    {
        if (!Unloaded || Disposed) return;
        Unloaded = false;
        var warm = Mux.TerminalPool.Take();
        if (warm != null) { try { Web.Dispose(); } catch { } Web = warm; }
        Host.Child = Web;
        _warm = Web.CoreWebView2 != null;
        if (_warm)
        {
            Attach(Web.CoreWebView2!);
            PostConfig();
        }
        else
        {
            await WebEnv.Ready;
            await Web.EnsureCoreWebView2Async(WebEnv.Env);
            if (Disposed) return;
            ConfigureCore(Web.CoreWebView2!);
            Attach(Web.CoreWebView2!);
            Web.CoreWebView2!.Navigate(PageUrl);
        }
        Log.Info($"pane {Id}: reloaded");
    }

    /// <summary>For API calls that need the live terminal (read, screenshot): reload and wait briefly.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (!Unloaded && _pageReady) return;
        if (Unloaded) await ReloadAsync();
        for (int i = 0; i < 60 && !_pageReady && !Disposed; i++) await Task.Delay(50);
    }

    private bool? _dimmed;

    /// <summary>Fades the terminal while another pane of the split has focus (the page forgets it on reload).</summary>
    public void SetDimmed(bool on)
    {
        if (_dimmed == on || Web?.CoreWebView2 == null) return;
        _dimmed = on;
        _ = Web.CoreWebView2.ExecuteScriptAsync($"window.bellboard && bellboard.setDim({(on ? "true" : "false")})");
    }

    private void OnReady(int cols, int rows)
    {
        _dimmed = null;
        Mark("xterm");
        if (_pageReady)
        {
            // The page reloaded (e.g. renderer crash): keep the shell, just resync size.
            _cols = cols;
            _rows = rows;
            if (_fit == null) _pty?.Resize(cols, rows);
            MirrorResize();
            return;
        }
        _pageReady = true;
        if (Host.ActualWidth > 60 && cols > 0 && rows > 0)
        {
            _cellW = (Host.ActualWidth - 4 - 14 - 8) / cols;
            _cellH = (Host.ActualHeight - 4 - 10) / rows;
        }
        if (cols != _cols || rows != _rows)
        {
            _cols = cols;
            _rows = rows;
            if (_fit == null) _pty?.Resize(cols, rows);
            MirrorResize();
        }
        Mux.TerminalPool.Warm();
        if (!_shellPoolPrimed)
        {
            // Once the first terminal is up, start a shell for the next workspace in the background.
            _shellPoolPrimed = true;
            Mux.Shells.Refill();
        }
        if (_reloadSnapshot != null)
        {
            // Back from being unloaded: the screen as it was, then what happened while hidden.
            PostOutput(_reloadSnapshot);
            if (_preReadyTrimmed) PostOutput("\r\n\x1b[2m… older output was trimmed while this workspace was idle …\x1b[0m\r\n");
            _reloadSnapshot = null;
            _preReadyTrimmed = false;
        }
        else if (RestoreScrollback is { Length: > 0 } sb)
        {
            // ConPTY paints its first frame from the top of the viewport, so scroll the restored
            // history out of view first and home the cursor; the history stays in scrollback.
            var clear = new StringBuilder("\r\n");
            for (int i = 0; i < rows; i++) clear.Append("\r\n");
            PostOutput(sb + "\x1b[0m\r\n\x1b[2m── session restored " + DateTime.Now.ToString("g") + " ──\x1b[0m" + clear + "\x1b[H");
            RestoreScrollback = null;
        }
        foreach (var chunk in _preReady) PostOutput(chunk);
        _preReady.Clear();
        _preReadyChars = 0;
        if (_sawPrompt) LogTimeline(); // the shell beat the renderer; its prompt is visible now
        if (Mux.Selected == Workspace && Workspace.FocusedPane == this) FocusContent();
    }

    /// <summary>A shell started ahead of time by the ShellPool, adopted instead of launching one.</summary>
    public PrewarmedShell? Prewarmed { get; init; }

    /// <summary>Command line, environment and process folder for a pane's shell (shared with the ShellPool).</summary>
    public static (ShellSpec Shell, Dictionary<string, string> Env, string ProcessCwd) PrepareLaunch(
        MuxController mux, string paneId, string workspaceId, string? shellChoice, string? command, string cwd, Dictionary<string, string>? extraEnv)
    {
        var zsh = ShellResolver.ExpandAlias(shellChoice)?.Contains("wsl", StringComparison.OrdinalIgnoreCase) == true ? ShellIntegration.EnsureZsh() : null;
        var shell = ShellResolver.Resolve(shellChoice, command, cwd, zsh);
        var env = ShellResolver.BaseEnvironment();
        env[BellboardProtocol.EnvPane] = paneId;
        env[BellboardProtocol.EnvWorkspace] = workspaceId;
        env[BellboardProtocol.EnvPipe] = mux.PipeName;
        env["BELLBOARD"] = "1";
        env["TERM_PROGRAM"] = "bellboard";
        env["TERM_PROGRAM_VERSION"] = BellboardProtocol.Version;
        env["COLORTERM"] = "truecolor";
        // Put the Bellboard CLI on PATH inside every pane; the wsl folder makes `bell` work in WSL shells too.
        var home = AppContext.BaseDirectory.TrimEnd('\\');
        env["PATH"] = home + ";" + Path.Combine(home, "wsl") + ";" + (env.TryGetValue("PATH", out var path) ? path : "");
        foreach (var kv in shell.Env) env[kv.Key] = kv.Value;
        if (extraEnv != null) foreach (var kv in extraEnv) env[kv.Key] = kv.Value;
        // WSL shells get WSLENV from ShellResolver; for the rest, a `wsl` typed at the prompt still sees this pane.
        if (!shell.Env.ContainsKey("WSLENV"))
            env["WSLENV"] = env.TryGetValue("WSLENV", out var inherited) && inherited.Length > 0
                ? inherited.TrimEnd(':') + ":" + ShellResolver.WslPaneVariables
                : ShellResolver.WslPaneVariables;
        // WSL needs a Linux cwd; wsl.exe gets it via --cd, so start the Windows process somewhere neutral.
        var processCwd = shell.Kind == ShellKind.Wsl && ShellResolver.ParseWslPath(cwd) != null
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : cwd;
        return (shell, env, processCwd);
    }

    private void StartShell()
    {
        var cwd = StartCwd is { } sc && Directory.Exists(sc) ? sc : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Cwd = cwd;
        GitBranch = GitInfo.Branch(cwd);
        ShellSpec shell;
        if (Prewarmed is { } pre)
        {
            shell = pre.Spec;
            _pty = pre.Pty;
            _startedAt = pre.Started;
            (_cols, _rows) = (pre.Cols, pre.Rows);
            pre.Adopt(OnPtyOutput, code => Ui(() => OnExited(code)));
            Log.Info($"pane {Id}: adopted pre-started shell pid {_pty.ProcessId} ({(int)(DateTime.UtcNow - pre.Started).TotalMilliseconds} ms old)");
        }
        else
        {
            var (spec, env, processCwd) = PrepareLaunch(Mux, Id, Workspace.Id, Shell ?? Mux.Config.Shell, StartCommand, cwd, ExtraEnv);
            shell = spec;
            try
            {
                _pty = PtySession.Start(shell.CommandLine, processCwd, env, (short)_cols, (short)_rows, Mux.Config.Conpty != "system");
            }
            catch (Exception ex)
            {
                Log.Error($"pane {Id}: failed to start shell", ex);
                PostOutput($"\x1b[31mbellboard: could not start shell: {ex.Message}\x1b[0m\r\n\x1b[2mSet \"shell\" in {BellboardConfig.FilePath}\x1b[0m\r\n");
                _exited = true;
                return;
            }
            _startedAt = DateTime.UtcNow;
            _pty.Output += OnPtyOutput;
            _pty.Exited += code => Ui(() => OnExited(code));
            Log.Info($"pane {Id}: pid {_pty.ProcessId} via {_pty.Backend}: {shell.Exe}");
        }

        ShellKind = shell.Kind;
        // Typed once the shell has drawn its first prompt (or after a short timeout).
        if (shell.TypedCommand is { } typed) _pendingInput = typed + "\r";
        if (RestoreAgent is { } ra && Mux.Config.RestoreAgents && AgentManager.ResumeCommand(ra.Kind, ra.SessionId) is { } resume)
        {
            _pendingInput += resume + "\r";
            _resumePending = true;
        }
        if (_pendingInput != null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) => { timer.Stop(); SendPendingInput(); };
            timer.Start();
        }
        Mux.Monitors?.Poke();
    }

    private void SendPendingInput()
    {
        if (_pendingInput is { } input)
        {
            _pendingInput = null;
            if (_resumePending) _resumeSent = true;
            _pty?.Write(input);
        }
    }

    private void OnPtyOutput(string text)
    {
        if (!_sawOutput)
        {
            _sawOutput = true;
            Ui(() => Mark("first output"));
        }
        _osc.Feed(text);
        lock (_outLock)
        {
            _outBuf.Append(text);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        Web.Dispatcher.InvokeAsync(Flush, DispatcherPriority.Normal);
    }

    private void Flush()
    {
        string s;
        lock (_outLock)
        {
            s = _outBuf.ToString();
            _outBuf.Clear();
            _flushScheduled = false;
        }
        Emit(s);
    }

    private void PostOutput(string s)
    {
        if (Disposed || s.Length == 0) return;
        if (!_pageReady)
        {
            _preReady.Add(s);
            _preReadyChars += s.Length;
            // Hidden for a long time with a chatty process: keep the newest output only.
            while (_preReadyChars > MaxHiddenOutput && _preReady.Count > 1)
            {
                _preReadyChars -= _preReady[0].Length;
                _preReady.RemoveAt(0);
                _preReadyTrimmed = true;
            }
            return;
        }
        for (int i = 0; i < s.Length;)
        {
            int len = Math.Min(ChunkChars, s.Length - i);
            if (i + len < s.Length && char.IsHighSurrogate(s[i + len - 1])) len--;
            Post(new { t = "o", d = s.Substring(i, len) });
            i += len;
        }
    }

    private void Post(object message)
    {
        if (Disposed || Web.CoreWebView2 == null) return;
        try { Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOut)); }
        catch (Exception ex) { Log.Error($"pane {Id}: post failed", ex); }
    }

    private void OnCwd(string path)
    {
        Cwd = path;
        GitBranch = GitInfo.Branch(path);
        Mux.RememberDirectory(path);
        Workspace.Refresh();
        if (!_sawPrompt)
        {
            _sawPrompt = true;
            if (_pageReady) LogTimeline();
            SendPendingInput();
        }
    }

    private void OnBell()
    {
        if (!Mux.Config.BellIsAttention) return;
        bool looking = Mux.Window.IsActive && Mux.Selected == Workspace && Workspace.FocusedPane == this;
        if (!looking) Attention = true;
    }

    private void OnExited(int code)
    {
        if (Disposed) return;
        _exited = true;
        Agent = null;
        Workspace.Refresh();
        bool quick = DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(2);
        bool pinned = MuxController.PinnedReason(this) != null;
        if (code == 0 && !quick && !pinned)
        {
            Mux.ClosePane(this);
            return;
        }
        Emit($"\r\n\x1b[0m\x1b[2m[process exited with code {code} — press any key to {(pinned ? "start a new shell" : "close")}]\x1b[0m\r\n");
    }

    private void Echo(string data, bool binary)
    {
        if (_exited || _pty == null) return;
        if (binary) _pty.WriteBinary(data); else _pty.Write(data);
    }

    // ---- API surface used by the CLI ----

    public void SendText(string text)
    {
        if (Feed) throw new InvalidOperationException($"{Id} is a feed: it shows what {FeedLabel ?? "another program"} reports and has no shell to type into");
        if (_pty == null) throw new InvalidOperationException("shell is not running yet");
        _pty.Write(text);
    }

    /// <summary>Adds text to a feed (line breaks become terminal line breaks).</summary>
    public void WriteFeed(string text)
    {
        if (!Feed) throw new InvalidOperationException($"{Id} is a terminal, not a feed");
        Emit(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    public async Task<string> ReadAsync(int lines)
    {
        await EnsureLoadedAsync();
        var json = await Web.CoreWebView2.ExecuteScriptAsync($"window.bellboard ? bellboard.read({lines}) : ''");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    public async Task<string?> SerializeAsync(int rows)
    {
        if (Unloaded && _reloadSnapshot != null) return _reloadSnapshot + string.Concat(_preReady);
        if (!_pageReady || Web.CoreWebView2 == null) return null;
        try
        {
            var json = await Web.CoreWebView2.ExecuteScriptAsync($"window.bellboard ? bellboard.serialize({rows}) : null");
            return JsonSerializer.Deserialize<string?>(json);
        }
        catch { return null; }
    }

    public override void FocusContent()
    {
        base.FocusContent();
        Post(new { t = "focus" });
    }

    public void OpenFind()
    {
        base.FocusContent();
        Post(new { t = "find" });
    }

    public override Dictionary<string, object?> Describe()
    {
        var d = base.Describe();
        d["pid"] = ShellPid;
        d["branch"] = GitBranch;
        d["ports"] = Ports;
        d["agent"] = Agent == null ? null : new Dictionary<string, object?> { ["kind"] = Agent.Kind, ["status"] = Agent.Status, ["sessionId"] = Agent.SessionId };
        d["conpty"] = Backend;
        d["shell"] = Feed ? "feed" : ShellKind.ToString().ToLowerInvariant();
        d["renderer"] = Renderer;
        d["size"] = $"{_cols}x{_rows}";
        d["unloaded"] = Unloaded;
        d["feed"] = Feed;
        d["remote"] = Remote;
        return d;
    }

    private void Ui(Action a)
    {
        if (!Disposed) Web.Dispatcher.InvokeAsync(() => { if (!Disposed) a(); });
    }

    public override void Dispose()
    {
        if (Disposed) return;
        Mux.Prompts.CancelFor(this);
        foreach (var m in _mirrors.ToList()) m.Send("exit", new JsonObject());
        _mirrors.Clear();
        foreach (var w in _snapshots.Values) w.TrySetCanceled();
        _snapshots.Clear();
        _pty?.Dispose();
        base.Dispose();
    }

    // ---- Remote mirroring (terminal.attach): a phone shows this terminal live ----

    /// <summary>Numbers every chunk of output, so a mirror can join between a snapshot and the live stream.</summary>
    private long _seq;
    private readonly List<TerminalMirror> _mirrors = new();
    private readonly Dictionary<int, TaskCompletionSource<(string Data, int Cols, int Rows)>> _snapshots = new();
    private int _nextSnap;
    /// <summary>"Phone fit": the shell is sized for a phone while nobody types at the desk.</summary>
    private (int Cols, int Rows)? _fit;

    public string SizeText => _fit is { } f ? $"{f.Cols}x{f.Rows}" : $"{_cols}x{_rows}";
    public bool Fitted => _fit != null;
    public int MirrorCount => _mirrors.Count;

    /// <summary>Output as it happens: to xterm.js, and numbered to any mirrors.</summary>
    private void Emit(string s)
    {
        if (s.Length == 0) return;
        PostOutput(s);
        _seq++;
        if (_mirrors.Count == 0) return;
        foreach (var m in _mirrors.ToList()) m.Output(_seq, s);
    }

    /// <summary>Starts mirroring: a snapshot of the screen and scrollback, then every chunk after it, in order.</summary>
    public async Task AttachMirrorAsync(TerminalMirror mirror, int scrollback)
    {
        _mirrors.Add(mirror);
        var (data, seq, cols, rows) = await SnapshotAsync(Math.Clamp(scrollback, 0, 20000));
        if (!_mirrors.Contains(mirror)) return; // detached meanwhile
        if (_fit is { } f) (cols, rows) = f;
        mirror.Started(data, seq, cols, rows);
    }

    public void DetachMirror(TerminalMirror mirror)
    {
        _mirrors.Remove(mirror);
        if (_mirrors.Count == 0 && _fit != null) EndFit();
    }

    private async Task<(string Data, long Seq, int Cols, int Rows)> SnapshotAsync(int scrollback)
    {
        if (_pageReady && !Unloaded && Web.CoreWebView2 != null)
        {
            // Asked on the same channel as the output, so the page answers after every chunk posted before it.
            var id = ++_nextSnap;
            var wait = new TaskCompletionSource<(string, int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _snapshots[id] = wait;
            var seq = _seq;
            Post(new { t = "snap", id, rows = scrollback });
            var done = await Task.WhenAny(wait.Task, Task.Delay(5000));
            if (done == wait.Task && wait.Task.IsCompletedSuccessfully)
            {
                var (data, cols, rows) = wait.Task.Result;
                return (data, seq, cols, rows);
            }
            _snapshots.Remove(id);
            if (_pageReady && !Unloaded) return ("", _seq, _cols, _rows);
        }
        // No live renderer: what it will show when it comes back.
        var text = (_reloadSnapshot ?? RestoreScrollback ?? "") + string.Concat(_preReady);
        return (text, _seq, _cols, _rows);
    }

    private void MirrorResize()
    {
        if (_mirrors.Count == 0) return;
        var (cols, rows) = _fit ?? (_cols, _rows);
        foreach (var m in _mirrors.ToList()) m.Send("resize", new JsonObject { ["cols"] = cols, ["rows"] = rows });
    }

    /// <summary>Phone fit: size the shell for a phone. Ends when someone types at the desk or the phone leaves.</summary>
    public void Fit(int cols, int rows)
    {
        if (Feed) throw new InvalidOperationException($"{Id} is a feed");
        _fit = (Math.Clamp(cols, 20, 500), Math.Clamp(rows, 5, 300));
        _pty?.Resize(_fit.Value.Cols, _fit.Value.Rows);
        MirrorResize();
        Log.Info($"pane {Id}: sized {_fit.Value.Cols}x{_fit.Value.Rows} for a phone");
    }

    public void EndFit()
    {
        if (_fit == null) return;
        _fit = null;
        _pty?.Resize(_cols, _rows);
        MirrorResize();
        Log.Info($"pane {Id}: back to the desktop size {_cols}x{_rows}");
    }

    /// <summary>The SSH host this pane connects to, when it was opened as an SSH shell.</summary>
    public string? Remote => ShellResolver.SshDestination(Shell);

    public override string TabLabel => DisplayTitle == null && Remote is { } host ? host : base.TabLabel;

    /// <summary>
    /// The shell a tab, split or workspace opened from this pane should run: wsl or ssh if the user started one at
    /// this pane's prompt, otherwise the shell this pane was opened with (null = the default shell).
    /// </summary>
    public string? ShellForNewPane()
    {
        if (_pty is { } pty)
        {
            try
            {
                // Only direct children of the shell: git's own ssh.exe (child of git.exe) doesn't count.
                foreach (var child in Win32.Processes().Where(p => p.ParentPid == pty.ProcessId).OrderByDescending(p => p.Pid))
                    if (ShellResolver.NestedShellAlias(child.Exe, Win32.CommandLine(child.Pid)) is { } nested) return nested;
            }
            catch (Exception ex)
            {
                Log.Error("looking for a nested shell", ex);
            }
        }
        return Shell;
    }

    /// <summary>Close the shell and wait for it (used at exit so no shell outlives Bellboard).</summary>
    public Task CloseShellAsync() => _pty?.CloseAsync() ?? Task.CompletedTask;
}

/// <summary>
/// One viewer of a terminal (a phone, `bell attach`). Output that arrives before its snapshot is held, then
/// only chunks newer than the snapshot are sent, so nothing is missed or shown twice.
/// </summary>
public sealed class TerminalMirror
{
    private readonly Action<string, JsonNode?> _send;
    private List<(long Seq, string Text)>? _early = new();
    private long _since;

    public TerminalMirror(Action<string, JsonNode?> send) => _send = send;

    public void Send(string ev, JsonNode? data) => _send(ev, data);

    internal void Output(long seq, string text)
    {
        if (_early != null)
        {
            _early.Add((seq, text));
            return;
        }
        if (seq > _since) _send("output", new JsonObject { ["seq"] = seq, ["d"] = text });
    }

    internal void Started(string data, long seq, int cols, int rows)
    {
        _since = seq;
        _send("snapshot", new JsonObject { ["data"] = data, ["seq"] = seq, ["cols"] = cols, ["rows"] = rows });
        var early = _early!;
        _early = null;
        foreach (var (s, t) in early) Output(s, t);
    }
}
