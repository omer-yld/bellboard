using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Toolkit.Uwp.Notifications;
using Bellboard.Core;
using Bellboard.Panes;
using Forms = System.Windows.Forms;

namespace Bellboard.Services;

public sealed class Notice : INotifyPropertyChanged
{
    private static int _counter;
    public string Id { get; } = "n" + System.Threading.Interlocked.Increment(ref _counter);
    public DateTime At { get; } = DateTime.Now;
    /// <summary>Where the pane is now (panes can move between workspaces).</summary>
    public Workspace Workspace => Pane.Workspace;
    public required PaneBase Pane { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string Source { get; init; } = "osc";

    private bool _read;
    public bool Read
    {
        get => _read;
        set { _read = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); }
    }

    public string Meta => $"{Workspace.Name} · {At:HH:mm}";
    public Visibility UnreadDot => _read ? Visibility.Hidden : Visibility.Visible;

    /// <summary>"now", "3m", "2h": shown on the right of an inbox row.</summary>
    public string Ago => (DateTime.Now - At).TotalMinutes is var m && m < 1 ? "now" : m < 60 ? $"{(int)m}m" : m < 2880 ? $"{(int)(m / 60)}h" : $"{(int)(m / 1440)}d";

    private bool FromAgent => Source is "claude" or "codex" or "pi" or "opencode";
    private bool IsDone => Title.Contains("finish", StringComparison.OrdinalIgnoreCase) || Body.StartsWith("Finished", StringComparison.Ordinal)
                           || Body.StartsWith("done", StringComparison.OrdinalIgnoreCase);
    public string IconGlyph => IsDone ? "\uE73E" : FromAgent ? "\uE99A" : "\uEA8F";
    public Brush IconForeground => IsDone ? Tile.GreenFg : FromAgent ? Tile.AmberFg : Tile.GrayFg;
    public Brush IconBackground => IsDone ? Tile.GreenBg : FromAgent ? Tile.AmberBg : Tile.GrayBg;

    private static class Tile
    {
        private static Brush F(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }
        public static readonly Brush GreenFg = F(0xFF5FD39A), GreenBg = F(0x1F5FD39A);
        public static readonly Brush AmberFg = F(0xFFF5A524), AmberBg = F(0x1FF5A524);
        public static readonly Brush GrayFg = F(0xFFA7ADBA), GrayBg = F(0xFF21252E);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The attention system: notification rings on panes, unread badges in the sidebar,
/// the notifications popover, taskbar flash + overlay badge, and Windows toasts.
/// </summary>
public sealed class NotificationCenter : IDisposable
{
    private const string ToastGroup = "bellboard";
    private readonly MuxController _mux;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _dndItem;
    private Notice? _lastToast;
    private bool _toastsWork = true;
    private string? _toastStatus;

    public ObservableCollection<Notice> Items { get; } = new();
    public int UnreadCount => Items.Count(n => !n.Read);

    /// <summary>Do not disturb: no toasts or taskbar flashes; rings and badges still update.</summary>
    public bool DoNotDisturb
    {
        get => _dndItem.Checked;
        set
        {
            _dndItem.Checked = value;
            _tray.Text = value ? "Bellboard (do not disturb)" : "Bellboard";
            _mux.Window.SetDoNotDisturb(value);
        }
    }

    public NotificationCenter(MuxController mux)
    {
        _mux = mux;
        _tray = new Forms.NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "Bellboard",
            Visible = true,
        };
        _tray.Click += (_, _) => _mux.Window.ActivateFromTray();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_lastToast is { } n) _mux.Window.Dispatcher.InvokeAsync(() => Jump(n));
        };
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = true },
            ShowImageMargin = false,
            BackColor = System.Drawing.Color.FromArgb(0x1B, 0x1E, 0x25),
            ForeColor = System.Drawing.Color.FromArgb(0xD7, 0xDA, 0xE0),
            Font = new System.Drawing.Font("Segoe UI", 9.5f),
        };
        menu.Items.Add("Show Bellboard", null, (_, _) => _mux.Window.ActivateFromTray());
        menu.Items.Add("Jump to latest unread", null, (_, _) => _mux.Window.Dispatcher.InvokeAsync(JumpToLatestUnread));
        menu.Items.Add("Settings…", null, (_, _) => _mux.Window.Dispatcher.InvokeAsync(() => { _mux.Window.ActivateFromTray(); _mux.Window.OpenSettings(); }));
        _dndItem = new Forms.ToolStripMenuItem("Do not disturb") { CheckOnClick = true };
        _dndItem.CheckedChanged += (_, _) => _mux.Window.Dispatcher.InvokeAsync(() => DoNotDisturb = _dndItem.Checked);
        menu.Items.Add(_dndItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _mux.Window.Dispatcher.InvokeAsync(() => _mux.Window.Close()));
        _tray.ContextMenuStrip = menu;

        _mux.Window.Dispatcher.InvokeAsync(RefreshToastStatus, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            // Toast buttons ("Jump", "Mark read") come back here, on a background thread.
            ToastNotificationManagerCompat.OnActivated += e =>
                _mux.Window.Dispatcher.InvokeAsync(() => OnToastActivated(e.Argument));
        }
        catch (Exception ex)
        {
            _toastsWork = false;
            Log.Error("toast activation unavailable; using tray balloons", ex);
        }
    }

    public void OnToastActivated(string argument)
    {
        var args = ToastArguments.Parse(argument);
        if (!args.TryGetValue("notice", out var id)) { _mux.Window.ActivateFromTray(); return; }
        var n = Items.FirstOrDefault(i => i.Id == id);
        if (n == null) { _mux.Window.ActivateFromTray(); return; }
        if (args.TryGetValue("action", out var action) && action == "read")
        {
            n.Read = true;
            n.Pane.Attention = Items.Any(i => i.Pane == n.Pane && !i.Read);
            UpdateCounts();
        }
        else
        {
            Jump(n);
        }
    }

    public Notice Post(PaneBase pane, string? title, string body, string source)
    {
        title = string.IsNullOrWhiteSpace(title) ? (pane.DisplayTitle ?? "Terminal") : title.Trim();
        body = body.Trim();
        if (body.Length > 500) body = body[..500] + "…";

        var window = _mux.Window;
        bool looking = window.IsActive && _mux.Selected == pane.Workspace && pane.Workspace.FocusedPane == pane;
        var n = new Notice { Pane = pane, Title = title, Body = body, Source = source, Read = looking };
        Items.Insert(0, n);
        while (Items.Count > 200) Items.RemoveAt(Items.Count - 1);

        pane.Workspace.LastNotice = string.IsNullOrEmpty(body) ? title : body;
        if (!looking) pane.Attention = true;
        UpdateCounts();
        Log.Info($"notify [{source}] {pane.Id}: {title} — {body}");

        bool quiet = DoNotDisturb || pane.Workspace.Muted;
        if (!window.IsActive && !quiet) Win32.FlashTaskbar(window.Handle);
        if (_mux.Config.DesktopNotifications && !quiet && (!window.IsActive || _mux.Selected != pane.Workspace))
            ShowToast(n);
        return n;
    }

    private void ShowToast(Notice n)
    {
        _lastToast = n;
        if (_toastsWork)
        {
            try
            {
                new ToastContentBuilder()
                    .AddArgument("action", "jump")
                    .AddArgument("notice", n.Id)
                    .AddText(n.Title)
                    .AddText(string.IsNullOrEmpty(n.Body) ? " " : n.Body)
                    .AddAttributionText($"{n.Workspace.Name} · {n.Pane.Id}")
                    .AddButton(new ToastButton().SetContent("Jump").AddArgument("action", "jump").AddArgument("notice", n.Id))
                    .AddButton(new ToastButton().SetContent("Mark read").AddArgument("action", "read").AddArgument("notice", n.Id).SetBackgroundActivation())
                    .Show(t =>
                    {
                        t.Tag = n.Id;
                        t.Group = ToastGroup;
                    });
                Log.Info($"toast {n.Id} shown");
                return;
            }
            catch (Exception ex)
            {
                _toastsWork = false;
                Log.Error("toast failed; falling back to tray balloons", ex);
            }
        }
        // On Windows 10/11 balloon tips are shown as (button-less) toasts.
        _tray.ShowBalloonTip(5000, n.Title, string.IsNullOrEmpty(n.Body) ? " " : n.Body, Forms.ToolTipIcon.None);
    }

    /// <summary>Remove toasts for notices that were read inside Bellboard from the Action Center.</summary>
    private void RemoveToasts(IEnumerable<Notice> notices)
    {
        if (!_toastsWork) return;
        try
        {
            foreach (var n in notices) ToastNotificationManagerCompat.History.Remove(n.Id, ToastGroup);
        }
        catch { }
    }

    /// <summary>Whether Windows will display Bellboard's toasts ("Enabled", "DisabledForUser", ...); cached.</summary>
    public string ToastStatus => _toastsWork ? _toastStatus ?? "Unknown" : "Unavailable";

    private bool _toastStatusErrorLogged;

    /// <summary>
    /// Re-reads the notifier setting. WinRT calls fail inside input-synchronous calls
    /// (RPC_E_CANTCALLOUT_ININPUTSYNCCALL), so callers run this from a deferred dispatcher operation.
    /// </summary>
    public void RefreshToastStatus()
    {
        if (!_toastsWork) return;
        try { _toastStatus = ToastNotificationManagerCompat.CreateToastNotifier().Setting.ToString(); }
        catch (Exception ex)
        {
            if (!_toastStatusErrorLogged) Log.Error("toast status", ex);
            _toastStatusErrorLogged = true;
        }
    }

    /// <summary>Tags of Bellboard toasts currently in the Action Center (diagnostics/tests).</summary>
    public List<string> ToastHistory()
    {
        try
        {
            var list = ToastNotificationManagerCompat.History.GetHistory().Select(t => $"{t.Group}/{t.Tag}").ToList();
            list.Insert(0, "notifier: " + ToastNotificationManagerCompat.CreateToastNotifier().Setting);
            return list;
        }
        catch (Exception ex) { Log.Error("toast history", ex); return new() { "error: " + ex.Message }; }
    }

    public void MarkRead(PaneBase pane)
    {
        var unread = Items.Where(n => n.Pane == pane && !n.Read).ToList();
        foreach (var n in unread) n.Read = true;
        if (unread.Count == 0) return;
        RemoveToasts(unread);
        UpdateCounts();
    }

    public void Forget(PaneBase pane)
    {
        foreach (var n in Items.Where(n => n.Pane == pane).ToList()) Items.Remove(n);
        UpdateCounts();
    }

    public void MarkRead(Workspace ws)
    {
        var unread = Items.Where(n => n.Workspace == ws && !n.Read).ToList();
        foreach (var n in unread) n.Read = true;
        RemoveToasts(unread);
        foreach (var p in ws.Panes) p.Attention = false;
        UpdateCounts();
    }

    public void Jump(Notice n)
    {
        if (!_mux.Workspaces.Contains(n.Workspace) || n.Workspace.FindPane(n.Pane.Id) == null) return;
        _mux.Window.ActivateFromTray();
        _mux.Focus(n.Pane);
    }

    public void JumpToLatestUnread()
    {
        var n = Items.FirstOrDefault(i => !i.Read && _mux.Workspaces.Contains(i.Workspace));
        if (n != null) Jump(n);
    }

    public void MarkAllRead()
    {
        RemoveToasts(Items.Where(n => !n.Read).ToList());
        foreach (var n in Items) n.Read = true;
        foreach (var p in _mux.Workspaces.SelectMany(w => w.Panes)) p.Attention = false;
        UpdateCounts();
    }

    public void UpdateCounts()
    {
        foreach (var ws in _mux.Workspaces)
        {
            ws.Unread = Items.Count(n => !n.Read && n.Workspace == ws);
            ws.Refresh();
        }
        _mux.Window.SetUnreadBadge(UnreadCount);
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        // Toasts point at notices that won't exist after exit.
        try { if (_toastsWork) ToastNotificationManagerCompat.History.RemoveGroup(ToastGroup); } catch { }
    }

    /// <summary>Dark palette for the tray menu so it matches the app.</summary>
    private sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0x1B, 0x1E, 0x25);
        private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x27, 0x2C, 0x36);
        private static readonly System.Drawing.Color Line = System.Drawing.Color.FromArgb(0x2E, 0x33, 0x3E);
        public override System.Drawing.Color ToolStripDropDownBackground => Bg;
        public override System.Drawing.Color MenuBorder => Line;
        public override System.Drawing.Color MenuItemBorder => Hover;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override System.Drawing.Color SeparatorDark => Line;
        public override System.Drawing.Color SeparatorLight => Line;
        public override System.Drawing.Color ImageMarginGradientBegin => Bg;
        public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
        public override System.Drawing.Color ImageMarginGradientEnd => Bg;
    }
}
