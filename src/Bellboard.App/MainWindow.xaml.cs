using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bellboard.Core;
using Bellboard.Ipc;
using Bellboard.Services;

namespace Bellboard;

public partial class MainWindow : Window
{
    public MuxController Mux { get; }
    public IntPtr Handle { get; private set; }
    /// <summary>Moves tabs and panes by dragging their headers.</summary>
    public PaneDrag Drag => _drag ??= new PaneDrag(this);
    private PaneDrag? _drag;

    private readonly bool _noRestore;
    private PipeServer? _server;
    private DispatcherTimer? _autosave;
    private bool _syncing, _closeReady, _closing, _saving;
    private double _sidebarWidth = 252;

    public MainWindow(BellboardConfig config, string pipeName, bool noRestore)
    {
        InitializeComponent();
        // Creating the WebView2 environment takes a few hundred ms; start it before anything waits on it.
        WebEnv.Start();
        _noRestore = noRestore;
        Mux = new MuxController(this, config, pipeName);
        WorkspaceList.ItemsSource = WorkspaceView(agents: false);
        AgentWorkspaceList.ItemsSource = WorkspaceView(agents: true);
        if (!noRestore && config.RestoreSession && SessionStore.Load() is { } s) SessionStore.ApplyWindow(s, this);
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            // Our own title bar draws the caption; DWM keeps the shadow, rounded corners and a hairline border.
            Win32.StyleTitleBar(Handle, 0x00130F0D, 0x00F0EAE8);
            Win32.SetBorderColor(Handle, 0x002F2622);
            HwndSource.FromHwnd(Handle)?.AddHook(SnapLayoutHook);
        };
        StateChanged += (_, _) => OnWindowStateChanged();
        Loaded += async (_, _) => await StartAsync();
        Activated += (_, _) =>
        {
            if (Mux.Selected?.FocusedPane is { Attention: true } p)
            {
                p.Attention = false;
                Mux.Notifications.MarkRead(p);
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Workspace.Refreshed += _ => UpdateTitle();
        Mux.Workspaces.CollectionChanged += (_, _) => Dispatcher.InvokeAsync(UpdateGroups, DispatcherPriority.Background);
    }

    private async Task StartAsync()
    {
        try
        {
            Mux.Start();
            NoticeList.ItemsSource = Mux.Notifications.Items;

            _server = new PipeServer(Mux.PipeName, new CommandRouter(Mux), Dispatcher);
            _server.Start();

            // Shells start immediately; their views attach as soon as WebView2 is up.
            bool restored = false;
            if (!_noRestore && Mux.Config.RestoreSession && SessionStore.Load() is { } session)
                restored = SessionStore.Restore(Mux, session);
            if (!restored) Mux.NewWorkspace();
            await WebEnv.Ready;

            WatchConfig();
            // Daily update check (only when an update feed is configured).
            var updates = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
            updates.Tick += async (_, _) => await CheckForUpdateAsync();
            updates.Start();
            _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(TimeSpan.FromSeconds(20)); await CheckForUpdateAsync(); });
            var idle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            idle.Tick += async (_, _) => await Mux.UnloadIdleAsync();
            idle.Start();
            _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _autosave.Tick += async (_, _) => await SaveSessionAsync();
            _autosave.Start();
        }
        catch (Exception ex)
        {
            Log.Error("startup failed", ex);
            MessageBox.Show(this,
                "Bellboard could not start: " + ex.Message +
                "\n\nBellboard needs the Microsoft Edge WebView2 Runtime (preinstalled on Windows 11).",
                "Bellboard", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async Task SaveSessionAsync()
    {
        if (_saving || !Mux.Config.RestoreSession) return;
        _saving = true;
        try { await SessionStore.SaveAsync(Mux, this); }
        catch (Exception ex) { Log.Error("session save failed", ex); }
        finally { _saving = false; }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeReady)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _autosave?.Stop();
        while (_saving) await Task.Delay(50);
        await SaveSessionAsync();
        _server?.Dispose();
        Mux.DisposeAll();
        _closeReady = true;
        Log.Info("Bellboard exiting");
        // After the current Close returns: when nothing above had to wait (restoreSession off), we're still inside it,
        // and closing a window that is closing throws (and left Bellboard half-closed).
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    // ---- Shortcuts ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) // address bar / rename box keep their keys
        {
            if (!(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleShortcut(key, Keyboard.Modifiers, "wpf")) e.Handled = true;
    }

    /// <summary>Shortcuts that xterm.js intercepted inside a terminal page.</summary>
    public void HandleWebShortcut(JsonObject m)
    {
        var code = (string?)m["code"] ?? "";
        Key? key = code switch
        {
            _ when code.StartsWith("Key") && code.Length == 4 && Enum.TryParse<Key>(code[3..], out var k) => k,
            _ when code.StartsWith("Digit") && code.Length == 6 => Key.D0 + (code[5] - '0'),
            "ArrowLeft" => Key.Left,
            "ArrowRight" => Key.Right,
            "ArrowUp" => Key.Up,
            "ArrowDown" => Key.Down,
            "Tab" => Key.Tab,
            "Enter" => Key.Enter,
            "Equal" => Key.OemPlus,
            "BracketLeft" => Key.OemOpenBrackets,
            "BracketRight" => Key.OemCloseBrackets,
            "PageUp" => Key.PageUp,
            "Comma" => Key.OemComma,
            "PageDown" => Key.PageDown,
            "Space" => Key.Space,
            _ => null,
        };
        if (key == null) return;
        var mods = ModifierKeys.None;
        if (m["ctrl"]?.GetValue<bool>() == true) mods |= ModifierKeys.Control;
        if (m["shift"]?.GetValue<bool>() == true) mods |= ModifierKeys.Shift;
        if (m["alt"]?.GetValue<bool>() == true) mods |= ModifierKeys.Alt;
        HandleShortcut(key.Value, mods, "web");
    }

    private (Key Key, ModifierKeys Mods, string Source, long At) _lastShortcut;

    public bool HandleShortcut(Key key, ModifierKeys mods, string source = "api")
    {
        // A shortcut can arrive twice for one keypress: WebView2 forwards it to WPF *and* the
        // terminal page reports it. Drop the second copy if it comes from the other path.
        var now = Environment.TickCount64;
        if (_lastShortcut.Key == key && _lastShortcut.Mods == mods && _lastShortcut.Source != source && now - _lastShortcut.At < 250)
        {
            if (App.DevMode) Log.Info($"shortcut {mods}+{key} via {source}: duplicate of {_lastShortcut.Source}, ignored");
            return true;
        }
        if (App.DevMode && mods != ModifierKeys.None) Log.Info($"shortcut {mods}+{key} via {source}");
        var handled = HandleShortcutCore(key, mods);
        if (handled) _lastShortcut = (key, mods, source, now);
        return handled;
    }

    private bool HandleShortcutCore(Key key, ModifierKeys mods)
    {
        var focused = Mux.Selected?.FocusedPane;
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.T: NewWorkspaceHere(); return true;
                case Key.Space: ShowNewShellMenu(focused); return true;
                case Key.W: if (focused != null) Mux.RequestClose(focused); return true;
                case Key.D: if (focused != null) Mux.SplitTerminal(focused, true); return true;
                case Key.E: if (focused != null) Mux.SplitTerminal(focused, false); return true;
                case Key.L: if (focused != null) Mux.OpenBrowser(focused, null); return true;
                case Key.U: Mux.Notifications.JumpToLatestUnread(); return true;
                case Key.I: ToggleNotices(); return true;
                case Key.R: if (Mux.Selected != null) BeginRename(Mux.Selected); return true;
                case Key.B: ToggleSidebar(); return true;
                case Key.Tab: Mux.SelectRelative(-1); return true;
                case Key.F: if (focused is Bellboard.Panes.TerminalPane findIn) findIn.OpenFind(); return true;
                case Key.P: OpenPalette(); return true;
                case Key.N: if (focused != null) Mux.NewTab(focused); return true;
                case Key.OemOpenBrackets: Mux.CycleTab(-1); return true;
                case Key.Z: Mux.ReopenClosed(); return true;
                case Key.A: OpenNewAgent(); return true;
                case Key.PageUp: if (Mux.Selected is { } up) Mux.MoveWorkspace(up, Mux.Workspaces.IndexOf(up) - 1); return true;
                case Key.PageDown: if (Mux.Selected is { } down) Mux.MoveWorkspace(down, Mux.Workspaces.IndexOf(down) + 1); return true;
                case Key.OemCloseBrackets: Mux.CycleTab(1); return true;
                case Key.Enter: Mux.ToggleZoom(); return true;
                case Key.OemPlus: Mux.Equalize(); return true;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt))
        {
            switch (key)
            {
                case Key.Left: Mux.ResizeFocused(-1, 0); return true;
                case Key.Right: Mux.ResizeFocused(1, 0); return true;
                case Key.Up: Mux.ResizeFocused(0, -1); return true;
                case Key.Down: Mux.ResizeFocused(0, 1); return true;
            }
        }
        else if (mods == ModifierKeys.Control)
        {
            if (key == Key.Tab) { Mux.SelectRelative(1); return true; }
            if (key == Key.OemComma) { OpenSettings(); return true; }
            if (key == Key.L && focused is Bellboard.Panes.BrowserPane browser) { browser.FocusAddress(); return true; }
            if (key >= Key.D1 && key <= Key.D9)
            {
                int n = key - Key.D0;
                if (Mux.Workspaces.Count > 0)
                    Mux.Select(Mux.Workspaces[n == 9 ? Mux.Workspaces.Count - 1 : Math.Min(n, Mux.Workspaces.Count) - 1]);
                return true;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            switch (key)
            {
                case Key.Left: Mux.MoveFocus(-1, 0); return true;
                case Key.Right: Mux.MoveFocus(1, 0); return true;
                case Key.Up: Mux.MoveFocus(0, -1); return true;
                case Key.Down: Mux.MoveFocus(0, 1); return true;
            }
        }
        return false;
    }

    // ---- Sidebar ----

    public void SyncSelection(Workspace ws)
    {
        _syncing = true;
        var (list, other) = ws.IsAgent ? (AgentWorkspaceList, WorkspaceList) : (WorkspaceList, AgentWorkspaceList);
        other.SelectedItem = null;
        list.SelectedItem = ws;
        list.ScrollIntoView(ws);
        _syncing = false;
        UpdateTitle();
    }

    private void WorkspaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (sender is ListBox { SelectedItem: Workspace ws }) Mux.Select(ws);
    }

    /// <summary>Workspaces and agents are two lists over one collection (so order and Ctrl+number stay shared).</summary>
    private ListCollectionView WorkspaceView(bool agents)
    {
        var view = new ListCollectionView(Mux.Workspaces) { Filter = o => o is Workspace w && w.IsAgent == agents, IsLiveFiltering = true };
        view.LiveFilteringProperties.Add(nameof(Workspace.IsAgent));
        return view;
    }

    private IEnumerable<ListBox> WorkspaceLists => new[] { WorkspaceList, AgentWorkspaceList };

    private ListBoxItem? ContainerFor(Workspace ws) =>
        WorkspaceLists.Select(l => l.ItemContainerGenerator.ContainerFromItem(ws) as ListBoxItem).FirstOrDefault(i => i != null);

    /// <summary>Re-sorts workspaces whose kind changed, and shows the hint when there are no agents.</summary>
    public void UpdateGroups()
    {
        foreach (var l in WorkspaceLists) (l.ItemsSource as ListCollectionView)?.Refresh();
        AgentsEmpty.Visibility = Mux.Workspaces.Any(w => w.IsAgent) ? Visibility.Collapsed : Visibility.Visible;
        if (Mux.Selected is { } ws) SyncSelection(ws);
    }

    // ---- Drag to reorder workspaces ----

    private Point _dragStart;
    private Workspace? _dragItem;

    private void WorkspaceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition((IInputElement)sender);
        _dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as Workspace;
    }

    private void WorkspaceList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || _dragItem.IsEditing) return;
        var d = e.GetPosition((IInputElement)sender) - _dragStart;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Move);
    }

    private void WorkspaceList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(Workspace)) is not Workspace moving) return;
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as Workspace;
        Mux.MoveWorkspace(moving, target == null ? Mux.Workspaces.Count - 1 : Mux.Workspaces.IndexOf(target));
    }

    private void WorkspaceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: Workspace ws }) BeginRename(ws);
    }

    private void NewWorkspace_Click(object sender, RoutedEventArgs e) => NewWorkspaceHere();

    private void NewWorkspace_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowShellMenu(NewWorkspaceButton, "workspace", NewWorkspaceHere);
    }

    /// <summary>A new workspace in the current folder (per settings), running the focused pane's shell unless told otherwise.</summary>
    private void NewWorkspaceHere(string? shell = null) =>
        Mux.NewWorkspace(cwd: Mux.DefaultNewWorkspaceDir(), shell: shell ?? MuxController.ShellFrom(Mux.Selected?.FocusedPane));

    /// <summary>Ctrl+Shift+Space: pick a shell for a new tab beside the focused pane (or for a new workspace).</summary>
    private void ShowNewShellMenu(Bellboard.Panes.PaneBase? focused)
    {
        if (focused != null)
            ShowShellMenu(focused.Stack.Host, "tab", shell => Mux.NewTab(focused, shell: shell), PlacementMode.Relative);
        else
            ShowShellMenu(NewWorkspaceButton, "workspace", NewWorkspaceHere);
    }

    /// <summary>
    /// The shell menu: installed shells, WSL distros, SSH hosts (~/.ssh/config and recent ones) and "Connect to a
    /// host…". Opened from the tab strip's chevron, a right-click on the sidebar's +, and Ctrl+Shift+Space.
    /// `what` is "tab" or "workspace"; `open` gets the chosen shell's alias.
    /// </summary>
    public void ShowShellMenu(UIElement target, string what, Action<string> open, PlacementMode placement = PlacementMode.Bottom)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        if (placement == PlacementMode.Relative) (menu.HorizontalOffset, menu.VerticalOffset) = (12, 10);
        MenuItem Item(string header, string glyph, Action click, string gesture = "")
        {
            var mi = new MenuItem { Header = header, Tag = glyph, InputGestureText = gesture };
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
            return mi;
        }
        void Label(string text) => menu.Items.Add(new MenuItem { Header = text, Style = (Style)FindResource("MenuLabel") });

        Label($"NEW {what.ToUpperInvariant()} WITH");
        var shells = Bellboard.Terminal.ShellResolver.Available(includeSsh: true);
        var defaultShell = Mux.Config.Shell is { Length: > 0 } configured
            ? configured
            : shells.Select(s => s.Alias).FirstOrDefault(a => a is "pwsh" or "powershell");
        var local = shells.Where(s => !s.Alias.StartsWith("ssh:")).ToList();
        var wslStarted = false;
        foreach (var (alias, label) in local)
        {
            if (alias.StartsWith("wsl") && !wslStarted)
            {
                wslStarted = true;
                if (menu.Items.Count > 1) menu.Items.Add(new Separator());
            }
            Item(label, "", () => open(alias), string.Equals(alias, defaultShell, StringComparison.OrdinalIgnoreCase) ? "default" : "");
        }

        menu.Items.Add(new Separator());
        Label("REMOTE");
        var hosts = shells.Where(s => s.Alias.StartsWith("ssh:")).Select(s => s.Alias[4..]).ToList();
        foreach (var t in Mux.RecentSsh.Where(t => !hosts.Contains(t, StringComparer.OrdinalIgnoreCase)))
            hosts.Add(t);
        var canSsh = Bellboard.Terminal.ShellResolver.SshPath() != null;
        foreach (var host in hosts)
            Item("SSH · " + host, "", () => { Mux.RememberSsh(host); open("ssh:" + host); }).IsEnabled = canSsh;
        var connect = Item("Connect to a host…", "\uE774", () =>
        {
            var dialog = new SshWindow(this, what);
            if (dialog.ShowDialog() == true && dialog.Target is { } target)
            {
                Mux.RememberSsh(target);
                open("ssh:" + target);
            }
        });
        connect.IsEnabled = canSsh;
        connect.ToolTip = canSsh ? "Hosts from ~/.ssh/config are listed above" : "Install the OpenSSH client (Settings › System › Optional features)";
        ToolTipService.SetShowOnDisabled(connect, true);

        menu.Items.Add(new Separator());
        Item("Change the default shell…", "\uE713", () => OpenSettings());
        // Opened from the keyboard: put focus in the menu so the arrow keys and Enter work.
        menu.Opened += (_, _) => menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Focusable && i.IsEnabled)?.Focus();
        menu.IsOpen = true;
    }

    /// <summary>The workspace a menu item acts on: the right-clicked one, else the selected one.</summary>
    private Workspace? MenuTarget(object sender) =>
        (sender as FrameworkElement)?.DataContext as Workspace ?? Mux.Selected;

    private void RenameMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } ws) BeginRename(ws);
    }

    private void CloseWorkspaceMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } ws) Mux.RequestCloseWorkspace(ws, ContainerFor(ws));
    }

    private void PinWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } ws) Mux.SetPinned(ws, !ws.Pinned);
    }

    private Popup? _hint;
    private DispatcherTimer? _hintTimer;

    /// <summary>A short note under an element that goes away by itself (e.g. why a pinned pane didn't close).</summary>
    public void ShowHint(UIElement target, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "\uE718",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12,
            Foreground = (Brush)FindResource("Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 9, 0),
        });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = (Brush)FindResource("FgStrong"), VerticalAlignment = VerticalAlignment.Center });
        _hint ??= new Popup { AllowsTransparency = true, Placement = PlacementMode.Bottom, StaysOpen = true, IsHitTestVisible = false, PopupAnimation = PopupAnimation.Fade };
        _hint.IsOpen = false;
        _hint.Child = new Border
        {
            Margin = new Thickness(0, 0, 14, 14),
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource("Line2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 8, 14, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.5, Color = Colors.Black },
            Child = row,
        };
        TextElement.SetFontFamily(_hint.Child, (FontFamily)FindResource("UiFont"));
        _hint.PlacementTarget = target;
        _hint.HorizontalOffset = 10;
        _hint.VerticalOffset = 4;
        _hint.IsOpen = true;
        _hintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _hintTimer.Stop();
        _hintTimer.Tick -= HideHint;
        _hintTimer.Tick += HideHint;
        _hintTimer.Start();
    }

    private void HideHint(object? sender, EventArgs e)
    {
        _hintTimer?.Stop();
        if (_hint != null) _hint.IsOpen = false;
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender)?.Cwd is { } dir && System.IO.Directory.Exists(dir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender)?.Cwd is { } dir)
        {
            try { Clipboard.SetText(dir); } catch { }
        }
    }

    private void MarkWorkspaceRead_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } ws) Mux.Notifications.MarkRead(ws);
    }

    private void FolderButton_Click(object sender, RoutedEventArgs e) => OpenFolderMenu();

    private void NewAgent_Click(object sender, RoutedEventArgs e) => OpenNewAgent();

    private void FinishAgent_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { IsAgent: true } ws) OpenFinishAgent(ws);
    }

    public void OpenNewAgent(string? task = null)
    {
        var pane = Mux.Selected?.FocusedPane as Bellboard.Panes.TerminalPane;
        var repo = pane?.Cwd ?? Mux.Selected?.Cwd;
        if (Mux.Selected?.AgentTask is { } current) repo = current.Repo; // from an agent workspace, branch off the main repo
        new NewAgentWindow(this, Mux, repo, task, Mux.Selected?.AgentTask == null ? pane?.Remote : null).ShowDialog();
    }

    public void OpenFinishAgent(Workspace ws) => new FinishAgentWindow(this, Mux, ws).ShowDialog();

    public void OpenSettings(string? section = null) => new SettingsWindow(this, Mux, section).ShowDialog();

    private string? _announcedUpdate;

    private async Task CheckForUpdateAsync()
    {
        var feed = Mux.Config.UpdateFeed;
        if (string.IsNullOrWhiteSpace(feed)) return;
        try
        {
            string json;
            if (feed.StartsWith("http://") || feed.StartsWith("https://"))
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Bellboard/" + Bellboard.Shared.BellboardProtocol.Version);
                json = await http.GetStringAsync(feed);
            }
            else json = await System.IO.File.ReadAllTextAsync(feed.StartsWith("file://") ? new Uri(feed).LocalPath : feed);
            var latest = (string?)System.Text.Json.Nodes.JsonNode.Parse(json.TrimStart('\uFEFF'))?["version"];
            if (latest != null && latest != _announcedUpdate && Version.TryParse(latest, out var lv) && Version.TryParse(Bellboard.Shared.BellboardProtocol.Version, out var cv) && lv > cv
                && Mux.Selected?.FocusedPane is { } pane)
            {
                _announcedUpdate = latest;
                Mux.Notifications.Post(pane, "Bellboard update", $"Bellboard {latest} is available (you have {cv}). Run `bell update` to install it.", "Bellboard");
            }
        }
        catch (Exception ex) { Log.Error("update check", ex); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // config.json edited by hand → reload and apply (debounced).
    private System.IO.FileSystemWatcher? _configWatcher;
    private DispatcherTimer? _configDebounce;

    private void WatchConfig()
    {
        try
        {
            System.IO.Directory.CreateDirectory(BellboardConfig.Dir);
            _configDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _configDebounce.Tick += (_, _) =>
            {
                _configDebounce.Stop();
                var fresh = BellboardConfig.Load();
                var json = System.Text.Json.JsonSerializer.Serialize(fresh, BellboardConfig.Json);
                if (json != System.Text.Json.JsonSerializer.Serialize(Mux.Config, BellboardConfig.Json))
                {
                    Mux.ApplyConfig(fresh);
                    Log.Info("config.json changed; reloaded");
                }
            };
            _configWatcher = new System.IO.FileSystemWatcher(BellboardConfig.Dir, "config.json")
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size | System.IO.NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            void Kick(object? s, System.IO.FileSystemEventArgs e) => Dispatcher.InvokeAsync(() => { _configDebounce.Stop(); _configDebounce.Start(); });
            _configWatcher.Changed += Kick;
            _configWatcher.Created += Kick;
            _configWatcher.Renamed += (s, e) => Kick(s, e);
        }
        catch (Exception ex)
        {
            Log.Error("watching config.json", ex);
        }
    }

    private void MuteWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } ws) SetMuted(ws, !ws.Muted);
    }

    public void SetMuted(Workspace ws, bool muted)
    {
        ws.Muted = muted;
        ws.Refresh();
    }

    public void SetDoNotDisturb(bool on)
    {
        BellGlyph.Text = on ? "\uE708" : "\uEA8F";
        BellButton.ToolTip = on ? "Notifications (do not disturb is on)" : "Notifications (Ctrl+Shift+I)";
    }

    /// <summary>"Open a folder as a workspace": pick a folder, home, or a recent folder.</summary>
    public void OpenFolderMenu()
    {
        var menu = new ContextMenu { PlacementTarget = FolderButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, HorizontalOffset = -8 };
        MenuItem Item(string header, string glyph, Action onClick, string? tip = null)
        {
            var mi = new MenuItem { Header = header, Tag = glyph, ToolTip = tip };
            mi.Click += (_, _) => onClick();
            return mi;
        }
        menu.Items.Add(new MenuItem { Header = "NEW WORKSPACE IN", Style = (Style)FindResource("MenuLabel") });
        menu.Items.Add(Item("Choose folder…", "\uE8DA", ChooseFolder));
        menu.Items.Add(Item("Home folder", "\uE80F", () => Mux.NewWorkspace()));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var recent = Mux.RecentDirs
            .Where(d => !string.Equals(d.TrimEnd('\\'), home, StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(d))
            .Take(8).ToList();
        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "RECENT", Style = (Style)FindResource("MenuLabel") });
            foreach (var dir in recent)
                menu.Items.Add(Item(Workspace.ShortPath(dir), "\uE8B7", () => Mux.NewWorkspace(cwd: dir), dir));
        }
        menu.IsOpen = true;
    }

    private void ChooseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Open a folder as a workspace",
            InitialDirectory = Mux.DefaultNewWorkspaceDir() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true) Mux.NewWorkspace(cwd: dlg.FolderName);
    }

    // ---- Command palette ----

    private PaletteWindow? _palette;

    public void OpenPalette(string initialQuery = "")
    {
        if (_palette != null) { _palette.Close(); return; }
        _palette = new PaletteWindow(this, BuildPaletteItems(), initialQuery);
        _palette.Closed += (_, _) =>
        {
            _palette = null;
            if (IsActive) Mux.Selected?.FocusedPane?.FocusContent();
        };
        _palette.Show();
        _palette.Activate();
    }

    private List<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();
        var focused = Mux.Selected?.FocusedPane;
        var ws = Mux.Selected;
        void Act(string title, string glyph, Action run, string? shortcut = null, string? subtitle = null) =>
            items.Add(new PaletteItem { Title = title, Glyph = glyph, Run = run, Shortcut = shortcut, Subtitle = subtitle, Group = "Action" });

        foreach (var w in Mux.Workspaces)
        {
            var target = w;
            items.Add(new PaletteItem
            {
                Title = w.Name + (w == ws ? "  (current)" : ""),
                Subtitle = w.Subtitle,
                Glyph = "\uE8A9",
                Group = "Workspace",
                Shortcut = w.Index is >= 1 and <= 9 ? $"Ctrl+{w.Index}" : null,
                Run = () => Mux.Select(target),
            });
        }

        Act("New workspace", "\uE710", () => NewWorkspaceHere(), "Ctrl+Shift+T", "in the current folder, with the current shell");
        Act("New tab or workspace with another shell…", "\uE756", () => ShowNewShellMenu(focused), "Ctrl+Shift+Space", "PowerShell, cmd, Git Bash, WSL, SSH");
        Act("New workspace in folder…", "\uE8DA", ChooseFolder);
        Act("New agent…", "\uE99A", () => OpenNewAgent(), subtitle: "own git worktree + branch, then starts Claude Code / Codex with the task");
        if (ws?.IsAgent == true) Act("Finish agent…", "\uE99A", () => OpenFinishAgent(ws), subtitle: "merge, pull request, keep, or discard " + ws.AgentTask!.Branch);
        if (focused != null)
        {
            Act("New tab", "\uE710", () => Mux.NewTab(focused), "Ctrl+Shift+N", "terminal tab in this pane");
            Act("New browser tab", "\uE774", () => Mux.NewTab(focused, Mux.CreateBrowser(focused.Workspace, null)));
            if (focused.Stack.Tabs.Count > 1)
            {
                Act("Next tab", "\uE76C", () => Mux.CycleTab(1), "Ctrl+Shift+]");
                Act("Previous tab", "\uE76B", () => Mux.CycleTab(-1), "Ctrl+Shift+[");
            }
            Act("Split right", "\uE8A0", () => Mux.SplitTerminal(focused, true), "Ctrl+Shift+D");
            Act("Split down", "\uE8A0", () => Mux.SplitTerminal(focused, false), "Ctrl+Shift+E");
            Act("Open browser", "\uE774", () => Mux.OpenBrowser(focused, null), "Ctrl+Shift+L");
            if (focused is Bellboard.Panes.TerminalPane term) Act("Find in terminal", "\uE721", term.OpenFind, "Ctrl+Shift+F");
            Act(ws?.ZoomedPane != null ? "Unzoom pane" : "Zoom pane", "\uE740", () => Mux.ToggleZoom(), "Ctrl+Shift+Enter");
            Act("Equalize splits", "\uE8A9", () => Mux.Equalize(), "Ctrl+Shift+=");
            if (focused.Stack.Parent is { } split)
            {
                Act(split.Horizontal ? "Flip split: stack top and bottom" : "Flip split: place side by side", "\uE7AD", () => Mux.FlipSplit(focused),
                    subtitle: "the shells keep running");
                Act(split.Horizontal ? "Swap left and right" : "Swap top and bottom", "\uE8AB", () => Mux.SwapSides(focused));
            }
            if (focused.Stack.Tabs.Count > 1)
                Act("Move tab into its own pane", "\uE8A7", () => Mux.MovePane(focused, focused, DropSide.Right), subtitle: "or drag the tab onto a pane's edge");
            if (MuxController.CanLeave(focused))
            {
                if (ws?.PaneCount > 1) Act("Move pane to a new workspace", "\uE8A7", () => Mux.MoveToWorkspace(focused, null), subtitle: "or drag its header onto the sidebar");
                foreach (var other in Mux.Workspaces.Where(w => w != ws))
                {
                    var dest = other;
                    Act($"Move pane to {dest.Name}", "\uE8A9", () => Mux.MoveToWorkspace(focused, dest), subtitle: "or drag its header onto the workspace");
                }
            }
            if (ws != null)
                Act(ws.Broadcast ? "Stop broadcasting input" : "Broadcast input to all terminals in this workspace", "\uE789",
                    () => Mux.SetBroadcast(ws, !ws.Broadcast));
            var noun = focused.Stack.Tabs.Count > 1 ? "tab" : "pane";
            Act(focused.Pinned ? $"Unpin {noun}" : $"Pin {noun}", focused.Pinned ? "\uE77A" : "\uE718", () => Mux.SetPinned(focused, !focused.Pinned),
                subtitle: focused.Pinned ? null : "keeps it first in its row, always visible, and stops it closing by accident");
            Act($"Close {noun}", "\uE89F", () => Mux.RequestClose(focused), "Ctrl+Shift+W");
        }
        foreach (var (alias, label) in Bellboard.Terminal.ShellResolver.Available(includeSsh: true))
        {
            var shell = alias;
            Act($"New workspace with {label}", "\uE756", () => NewWorkspaceHere(shell));
            if (focused != null) Act($"New tab with {label}", "\uE756", () => Mux.NewTab(focused, shell: shell));
            if (focused != null) Act($"Split right with {label}", "\uE756", () => Mux.SplitTerminal(focused, true, shell: shell));
        }
        if (ws != null)
        {
            Act("Rename workspace", "\uE8AC", () => BeginRename(ws), "Ctrl+Shift+R");
            Act(ws.Pinned ? "Unpin workspace" : "Pin workspace", ws.Pinned ? "\uE77A" : "\uE718", () => Mux.SetPinned(ws, !ws.Pinned),
                subtitle: ws.Pinned ? ws.Name : "keeps it at the top of the sidebar and stops it closing by accident");
            Act("Close workspace", "\uE711", () => Mux.RequestCloseWorkspace(ws));
            if (ws.Cwd is { } dir)
            {
                Act("Open folder in Explorer", "\uE838", () => OpenInExplorer_Click(new FrameworkElement { DataContext = ws }, new RoutedEventArgs()), subtitle: dir);
                Act("Copy folder path", "\uE8C8", () => { try { Clipboard.SetText(dir); } catch { } }, subtitle: dir);
            }
        }
        if (Mux.Closed.Count > 0)
            Act($"Reopen closed {Mux.Closed[^1].Kind}: {Mux.Closed[^1].Label}", "\uE7A7", () => Mux.ReopenClosed(), "Ctrl+Shift+Z");
        if (ws != null && Mux.Workspaces.Count > 1)
        {
            Act("Move workspace up", "\uE74A", () => Mux.MoveWorkspace(ws, Mux.Workspaces.IndexOf(ws) - 1), "Ctrl+Shift+PgUp");
            Act("Move workspace down", "\uE74B", () => Mux.MoveWorkspace(ws, Mux.Workspaces.IndexOf(ws) + 1), "Ctrl+Shift+PgDn");
        }
        Act("Jump to latest unread", "\uEA8F", Mux.Notifications.JumpToLatestUnread, "Ctrl+Shift+U");
        Act("Show inbox (agents and notifications)", "\uEA8F", ToggleNotices, "Ctrl+Shift+I");
        Act("Mark all notifications read", "\uE8FB", Mux.Notifications.MarkAllRead);
        Act(Mux.Notifications.DoNotDisturb ? "Turn off do not disturb" : "Turn on do not disturb", "\uE708",
            () => Mux.Notifications.DoNotDisturb = !Mux.Notifications.DoNotDisturb, subtitle: "no toasts or taskbar flashes; badges still update");
        if (ws != null)
            Act(ws.Muted ? "Unmute this workspace" : "Mute this workspace", "\uE74F", () => SetMuted(ws, !ws.Muted), subtitle: ws.Name);
        Act("Toggle sidebar", "\uE700", ToggleSidebar, "Ctrl+Shift+B");
        Act("Settings", "\uE713", () => OpenSettings(), "Ctrl+,", "font, theme, shell, notifications, session");
        Act("Open settings file", "\uE713", () => OpenPath(BellboardConfig.FilePath), subtitle: BellboardConfig.FilePath);
        Act("Open logs folder", "\uE838", () => OpenPath(Log.Dir), subtitle: Log.Dir);
        Act("Install agent hooks (Claude Code, Codex)", "\uE943", InstallHooks, subtitle: "bell hooks install");
        Act("Quit Bellboard", "\uE7E8", Close);

        foreach (var w in Mux.Workspaces)
            foreach (var pane in w.Panes)
            {
                var target = pane;
                var label = pane is Bellboard.Panes.BrowserPane b ? (b.Title ?? b.Url) : pane.DisplayTitle ?? (pane.Cwd != null ? Workspace.ShortPath(pane.Cwd) : pane.Id);
                items.Add(new PaletteItem
                {
                    Title = $"{label}",
                    Subtitle = $"{pane.Id} · {pane.Kind} in {w.Name}" + (pane is Bellboard.Panes.BrowserPane bb ? " · " + bb.Url : ""),
                    Glyph = pane is Bellboard.Panes.BrowserPane ? "\uE774" : "\uE756",
                    Group = "Pane",
                    Run = () => Mux.Focus(target),
                });
            }

        foreach (var dir in Mux.RecentDirs.Where(System.IO.Directory.Exists).Take(10))
        {
            var d = dir;
            items.Add(new PaletteItem { Title = Workspace.ShortPath(d), Subtitle = d, Glyph = "\uE8B7", Group = "Folder", Run = () => Mux.NewWorkspace(cwd: d) });
        }
        foreach (var (url, title) in Mux.BrowserHistory.Take(10))
        {
            var u = url;
            items.Add(new PaletteItem
            {
                Title = string.IsNullOrWhiteSpace(title) ? url : title,
                Subtitle = url,
                Glyph = "\uE7C3",
                Group = "Page",
                Run = () => { if (Mux.Selected?.FocusedPane is { } near) Mux.OpenBrowser(near, u); },
            });
        }
        return items;
    }

    private static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"open {path}", ex); }
    }

    /// <summary>Runs `bell hooks install` (user-initiated from the palette) and reports the result as a notification.</summary>
    private async void InstallHooks()
    {
        var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "bell.exe");
        var psi = new System.Diagnostics.ProcessStartInfo(exe, "hooks install") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync() + await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (Mux.Selected?.FocusedPane is { } pane) Mux.Notifications.Post(pane, "Agent hooks", output.Trim(), "Bellboard");
        }
        catch (Exception ex) { Log.Error("hooks install", ex); }
    }

    /// <summary>Dev/test hook: open a menu without a mouse (used with `bell screenshot --screen`).</summary>
    public void OpenMenuForTest(string which)
    {
        if (which == "folder")
        {
            OpenFolderMenu();
            return;
        }
        if (which == "sidebar")
        {
            ToggleSidebar();
            return;
        }
        if (which == "inbox")
        {
            ToggleNotices();
            return;
        }
        if (which == "finish")
        {
            // Opens "Finish agent" for the selected agent workspace, or closes the one that's open (it's modal).
            if (OwnedWindows.OfType<FinishAgentWindow>().FirstOrDefault() is { } open) open.Close();
            else if (Mux.Selected is { IsAgent: true } agentWs) Dispatcher.BeginInvoke(() => OpenFinishAgent(agentWs));
            return;
        }
        if (which == "tabs" && Mux.Selected?.FocusedPane is { } tabbed)
        {
            tabbed.Stack.OpenTabList();
            return;
        }
        if (which == "close" && Mux.Selected?.FocusedPane is { } closing)
        {
            Mux.RequestClose(closing);
            return;
        }
        if (which == "pane" && Mux.Selected?.FocusedPane is { } pane)
        {
            pane.Stack.ShowPaneMenu(pane, pane.Stack.Host, PlacementMode.Relative);
            return;
        }
        if (which == "palette")
        {
            Dispatcher.BeginInvoke(() => OpenPalette()); // modal; `bell quit` closes it
            return;
        }
        if (which.StartsWith("settings"))
        {
            var section = which.Contains(':') ? which[(which.IndexOf(':') + 1)..] : null;
            Dispatcher.BeginInvoke(() => OpenSettings(section));
            return;
        }
        if (which == "agent")
        {
            Dispatcher.BeginInvoke(() => OpenNewAgent()); // modal; `bell quit` closes it
            return;
        }
        if (which == "tooltip")
        {
            // A ToolTip object set on the button inherits its font, exactly like a hovered string tooltip.
            if (NewWorkspaceButton.ToolTip is not ToolTip tip) NewWorkspaceButton.ToolTip = tip = new ToolTip { Content = NewWorkspaceButton.ToolTip };
            tip.PlacementTarget = NewWorkspaceButton;
            tip.StaysOpen = true;
            tip.IsOpen = true;
            Log.Info($"dev: tooltip font '{tip.FontFamily.Source}' (button font '{NewWorkspaceButton.FontFamily.Source}')");
            return;
        }
        if (which == "shell")
        {
            ShowNewShellMenu(Mux.Selected?.FocusedPane);
            return;
        }
        if (Mux.Selected is { } ws && ContainerFor(ws) is { } item && item.ContextMenu is { } cm)
        {
            cm.PlacementTarget = item;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
            cm.HorizontalOffset = 90;
            cm.VerticalOffset = 30;
            cm.IsOpen = true;
        }
    }

    private void MarkAllRead_Click(object sender, RoutedEventArgs e) => Mux.Notifications.MarkAllRead();

    public void BeginRename(Workspace ws)
    {
        ws.EditName = ws.Name;
        ws.IsEditing = true;
        ws.Refresh();
        foreach (var l in WorkspaceLists) l.UpdateLayout();
        if (ContainerFor(ws) is { } item && FindChild<TextBox>(item) is { } box)
        {
            Activate();
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Workspace ws) return;
        if (e.Key == Key.Enter) { EndRename(ws, true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndRename(ws, false); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Workspace ws) EndRename(ws, true);
    }

    private void EndRename(Workspace ws, bool commit)
    {
        if (!ws.IsEditing) return;
        ws.IsEditing = false;
        if (commit) ws.CustomName = string.IsNullOrWhiteSpace(ws.EditName) ? null : ws.EditName.Trim();
        ws.Refresh();
        UpdateTitle();
        ws.FocusedPane?.FocusContent();
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    public double SidebarWidth
    {
        get => SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : _sidebarWidth;
        set
        {
            _sidebarWidth = value;
            SidebarColumn.Width = new GridLength(value);
        }
    }

    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SidebarColumn.ActualWidth > 40) _sidebarWidth = SidebarColumn.ActualWidth;
    }

    /// <summary>Whether the sidebar is collapsed (Ctrl+Shift+B or the title-bar button); remembered across restarts.</summary>
    public bool SidebarHidden
    {
        get => SidebarColumn.Width.Value == 0;
        set
        {
            if (value == SidebarHidden) return;
            if (value)
            {
                if (SidebarColumn.ActualWidth > 40) _sidebarWidth = SidebarColumn.ActualWidth;
                SidebarColumn.Width = new GridLength(0);
            }
            else
            {
                SidebarColumn.Width = new GridLength(Math.Max(180, _sidebarWidth));
            }
            SidebarButton.ToolTip = value ? "Show the sidebar (Ctrl+Shift+B)" : "Hide the sidebar (Ctrl+Shift+B)";
        }
    }

    private void ToggleSidebar() => SidebarHidden = !SidebarHidden;

    private void SidebarButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    // ---- Notifications UI ----

    private void BellButton_Click(object sender, RoutedEventArgs e) => ToggleNotices();

    private void OpenNotificationSettings_Click(object sender, RoutedEventArgs e) => OpenPath("ms-settings:notifications");

    private void RefreshAgents()
    {
        var rows = Inbox.Build(Mux);
        AgentList.ItemsSource = rows;
        AgentsSection.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AgentJump_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentRow row)
        {
            NoticePopup.IsOpen = false;
            ActivateFromTray();
            Mux.Focus(row.Pane);
        }
    }

    private void AgentApprove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentRow { Pane: Bellboard.Panes.TerminalPane t })
        {
            try { t.SendText("\r"); } catch (Exception ex) { Log.Error("approve", ex); }
            Mux.Notifications.MarkRead(t);
            RefreshAgents();
        }
    }

    private void ToggleNotices()
    {
        RefreshAgents();
        NoticeEmpty.Visibility = Mux.Notifications.Items.Count == 0 && AgentList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticePopup.IsOpen = !NoticePopup.IsOpen;
        // Keyboard input arrives in an input-synchronous call where WinRT calls fail; check toasts afterwards.
        if (NoticePopup.IsOpen) Dispatcher.InvokeAsync(UpdateToastWarning, DispatcherPriority.Background);
    }

    private void UpdateToastWarning()
    {
        Mux.Notifications.RefreshToastStatus();
        var status = Mux.Notifications.ToastStatus;
        ToastWarning.Visibility = Mux.Config.DesktopNotifications && status != "Enabled" ? Visibility.Visible : Visibility.Collapsed;
        ToastWarningText.Text = status switch
        {
            "DisabledForUser" => "Windows notifications are turned off, so Bellboard can't show toasts. Rings, badges and this list still work.",
            "DisabledForApplication" => "Notifications for Bellboard are turned off in Windows settings, so toasts won't appear.",
            "DisabledByGroupPolicy" => "Notifications are disabled by policy on this PC, so toasts won't appear.",
            _ => "Windows toasts aren't available, so Bellboard falls back to rings, badges and this list.",
        };
    }

    private void NoticeList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Notice n)
        {
            NoticePopup.IsOpen = false;
            Mux.Notifications.Jump(n);
        }
    }

    public void SetUnreadBadge(int count)
    {
        BellBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BellCount.Text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        Taskbar.Overlay = count > 0 ? BadgeImage(count) : null;
        Taskbar.Description = count > 0 ? $"Bellboard — {count} unread" : "Bellboard";
    }

    private ImageSource BadgeImage(int count)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(Bellboard.Panes.PaneBase.AccentColor), null, new Point(16, 16), 16, 16);
            var text = new FormattedText(count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                20, new SolidColorBrush(Color.FromRgb(0x1F, 0x14, 0x00)), dpi.PixelsPerDip);
            dc.DrawText(text, new Point(16 - text.Width / 2, 16 - text.Height / 2));
        }
        var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // ---- Misc ----

    public void UpdateTitle()
    {
        Title = Mux.Selected is { } ws
            ? $"{ws.Name}{(ws.ZoomedPane != null ? " (zoomed)" : "")}{(ws.Broadcast ? " (broadcasting input)" : "")} — Bellboard"
            : "Bellboard";
        UpdateChrome();
    }

    /// <summary>The breadcrumb in the title bar and the status bar follow the focused pane.</summary>
    public void UpdateChrome()
    {
        var ws = Mux.Selected;
        var pane = ws?.FocusedPane;
        var badge = ShellBadge.For(pane);
        var crumb = (CrumbName.Text, CrumbEnv.Text);
        CrumbName.Text = ws?.Name ?? "";
        CrumbEnv.Text = badge.Env + (ws?.Broadcast == true ? "  ·  broadcasting" : "") + (ws?.ZoomedPane != null ? "  ·  zoomed" : "");
        if (crumb != (CrumbName.Text, CrumbEnv.Text)) LayoutTitleBar();
        StatusDot.Stroke = badge.Dot;
        StatusEnv.Text = badge.Env;
        var remote = (pane as Bellboard.Panes.TerminalPane)?.Remote;
        var cwd = remote == null && pane?.Cwd is { } c ? Workspace.DisplayPath(c) : null;
        StatusCwd.Text = cwd ?? "";
        StatusCwdPanel.Visibility = cwd != null ? Visibility.Visible : Visibility.Collapsed;
        var branch = remote == null ? (pane as Bellboard.Panes.TerminalPane)?.GitBranch : null;
        StatusBranch.Text = branch ?? "";
        StatusBranchPanel.Visibility = branch != null ? Visibility.Visible : Visibility.Collapsed;
        StatusAgentPanel.Visibility = ws?.IsAgent == true || ws?.HasAgent == true ? Visibility.Visible : Visibility.Collapsed;
        StatusAgent.Text = ws == null ? "" : $"{ws.AgentWho} · {ws.AgentStateText.ToLowerInvariant()}";
        var font = Mux.Config.FontFamily == BellboardConfig.DefaultFontStack ? "Automatic"
            : Mux.Config.FontFamily.Split(',')[0].Trim().Trim('\'', '"');
        StatusFont.Content = $"{font} · {Mux.Config.FontSize:0.#}px";
        StatusTheme.Content = Mux.Config.ThemeName ?? "Bellboard Dark";
        AgentsEmpty.Visibility = Mux.Workspaces.Any(w => w.IsAgent) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTitleBar();

    /// <summary>In a narrow window the status bar keeps where the pane runs; the terminal look gives way first.</summary>
    private void StatusBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width;
        StatusUtf8.Visibility = w < 640 ? Visibility.Collapsed : Visibility.Visible;
        StatusTheme.Visibility = w < 540 ? Visibility.Collapsed : Visibility.Visible;
        StatusFont.Visibility = w < 320 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Keeps the title bar readable at any width. The search box is centred on the window while there is room; as the
    /// window narrows, the environment, the shortcut hint, the workspace name, the wordmark and finally the search
    /// label give way, in that order. The bell and the window buttons keep their place.
    /// </summary>
    private void LayoutTitleBar()
    {
        double total = TitleBar.ActualWidth;
        if (total <= 0) return;
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        UIElement[] giveWay = [CrumbEnv, SearchKeys, CrumbName, TitleWordmark, SearchLabel];
        foreach (var e in giveWay) e.Visibility = Visibility.Visible;
        TitleRight.Measure(infinite);
        double right = TitleRight.DesiredSize.Width;
        double Left()
        {
            CrumbSep.Visibility = CrumbName.Visibility;
            TitleLeft.Measure(infinite);
            return TitleLeft.DesiredSize.Width;
        }
        double Room() => total - Left() - right - 24;
        double Need() => SearchKeys.Visibility == Visibility.Visible ? 320 : SearchLabel.Visibility == Visibility.Visible ? 170 : 38;
        foreach (var e in giveWay)
        {
            if (Room() >= Need()) break;
            e.Visibility = Visibility.Collapsed;
        }
        double left = Left(), room = Room();
        double width = SearchLabel.Visibility == Visibility.Visible ? Math.Min(440, room) : 38;
        // Centred on the window when it fits, else as close to the centre as the crumb allows.
        double offset = (total - width) / 2 - left;
        offset = Math.Clamp(offset, 12, Math.Max(12, total - left - right - width - 12));
        SearchButton.Width = Math.Max(38, width);
        SearchButton.Margin = new Thickness(offset, 0, 0, 0);
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => OpenPalette();
    private void StatusLook_Click(object sender, RoutedEventArgs e) => OpenSettings(sender == StatusTheme ? "appearance" : "terminal");
    private void Shortcuts_Click(object sender, RoutedEventArgs e) => OpenSettings("keyboard");
    private void MinButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void BroadcastWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } ws) Mux.SetBroadcast(ws, !ws.Broadcast);
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnWindowStateChanged()
    {
        // A maximized window with custom chrome hangs over the screen edge by its resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    /// <summary>
    /// Windows 11 shows the snap-layouts flyout only over a real maximize button, so the window reports ours as
    /// HTMAXBUTTON and handles its hover and click itself.
    /// </summary>
    private IntPtr SnapLayoutHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCMOUSELEAVE = 0x02A2, HTMAXBUTTON = 9;
        switch (msg)
        {
            case WM_NCHITTEST:
                var over = OverMaxButton(lParam);
                SetMaxHover(over);
                if (over)
                {
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                SetMaxHover(false);
                ToggleMaximize();
                break;
            case WM_NCMOUSELEAVE:
                SetMaxHover(false);
                break;
        }
        return IntPtr.Zero;
    }

    private bool OverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible) return false;
        var x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        var y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        try
        {
            var p = MaxButton.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < MaxButton.ActualWidth && p.Y < MaxButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool _maxHover;
    private void SetMaxHover(bool on)
    {
        if (_maxHover == on) return;
        _maxHover = on;
        MaxButton.Background = on ? (Brush)FindResource("Bg3") : Brushes.Transparent;
        MaxButton.Foreground = (Brush)FindResource(on ? "FgStrong" : "Muted");
    }

    public void ActivateFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Mux.Selected?.FocusedPane?.FocusContent();
    }
}
