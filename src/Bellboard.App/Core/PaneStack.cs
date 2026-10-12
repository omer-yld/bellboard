using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Bellboard.Panes;

namespace Bellboard.Core;

/// <summary>
/// A leaf of the split tree: one or more tabs (terminals/browsers) in the same spot, under a 36 px header that
/// shows the shell, title, folder and branch (or a chip per tab), with new-tab / split / close actions.
/// Draws the notification ring around itself.
/// </summary>
public sealed class PaneStack : Node
{
    private static readonly Color BroadcastColor = Color.FromRgb(0x72, 0xB4, 0xFF);
    private static readonly Geometry SplitRightIcon = Freeze(Geometry.Parse("M5,3 H19 A2,2 0 0 1 21,5 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 Z M12,3 V21"));
    private static readonly Geometry SplitDownIcon = Freeze(Geometry.Parse("M5,3 H19 A2,2 0 0 1 21,5 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 Z M3,12 H21"));
    private static readonly Geometry BranchIcon = Freeze(Geometry.Parse("M6,3 L6,15 M18,9 A9,9 0 0 1 9,18 M21,6 A3,3 0 1 1 15,6 A3,3 0 1 1 21,6 M9,18 A3,3 0 1 1 3,18 A3,3 0 1 1 9,18"));

    public const double HeaderHeight = 36;

    private readonly MuxController _mux;
    private readonly Grid _content = new();
    private readonly Border _header;
    private readonly Border _focusLine;
    /// <summary>Holds the one-tab summary (badge, title, folder, branch) or, with several tabs, the tab row.</summary>
    private readonly Border _info = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel _infoDock;
    private readonly TabStrip _strip = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _moreTabs;
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pick, _right, _down, _close;
    private readonly SolidColorBrush _ring = new(Colors.Transparent);
    private bool _pulsing;
    // A press on the header that may become a drag.
    private Point? _press;
    private PaneBase? _pressed;

    public List<PaneBase> Tabs { get; } = new();
    public PaneBase Active { get; private set; } = null!;
    public Workspace Workspace => Active.Workspace;
    /// <summary>The header row (hints about this pane appear under it).</summary>
    public FrameworkElement HeaderElement => _header;
    /// <summary>The element placed in the split layout (ring border around header + content).</summary>
    public Border Host { get; }

    public PaneStack(MuxController mux, PaneBase first)
    {
        _mux = mux;
        var res = Application.Current.Resources;

        var add = Action("\uE710", "New tab with the same shell (Ctrl+Shift+N)", () => _mux.NewTab(Active));
        _pick = Action("\uE70D", "New tab with another shell (Ctrl+Shift+Space)", null);
        _pick.FontSize = 10;
        _pick.Click += (_, _) => _mux.Window.ShowShellMenu(_pick, "tab", shell => _mux.NewTab(Active, shell: shell));
        _right = Action(Icon(SplitRightIcon), "Split right (Ctrl+Shift+D)", () => _mux.SplitTerminal(Active, true));
        _down = Action(Icon(SplitDownIcon), "Split down (Ctrl+Shift+E)", () => _mux.SplitTerminal(Active, false));
        var menu = Action("\uE712", "Pane: split, flip, swap, move to a workspace…", null);
        menu.Click += (_, _) => ShowPaneMenu(Active, menu, PlacementMode.Bottom);
        _close = Action("\uE711", "Close pane (Ctrl+Shift+W)", () => _mux.RequestClose(Active));
        _close.FontSize = 11;
        foreach (var b in new[] { add, _pick, _right, _down, menu, _close }) _actions.Children.Add(b);
        _actions.Opacity = 0.5;

        _moreTabs = new Button { Style = (Style)Application.Current.Resources["TabChip"], ToolTip = "All tabs in this pane" };
        _moreTabs.Click += (_, _) => ShowTabList();
        _strip.More = _moreTabs;
        _strip.HiddenChanged += n => _moreTabs.Content = MoreTabsLabel(n);

        _infoDock = new DockPanel { LastChildFill = true, Margin = new Thickness(12, 0, 6, 0) };
        DockPanel.SetDock(_actions, Dock.Right);
        _infoDock.Children.Add(_actions);
        _infoDock.Children.Add(_info);
        var dock = _infoDock;
        _focusLine = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Background = (Brush)res["Accent"], Visibility = Visibility.Collapsed };
        var headerGrid = new Grid();
        headerGrid.Children.Add(dock);
        headerGrid.Children.Add(_focusLine);
        _header = new Border
        {
            Background = (Brush)res["PaneHeaderBg"],
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = HeaderHeight,
            Child = headerGrid,
        };
        _header.MouseEnter += (_, _) => _actions.Opacity = 0.9;
        _header.MouseLeave += (_, _) => _actions.Opacity = Active?.IsFocused == true ? 0.85 : 0.5;
        _header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) _mux.ToggleZoom(Active);
            else _mux.Focus(Active);
            // Hold the mouse from the press on, so a quick flick into the terminal (another window) still starts a drag.
            // Tab chips are buttons and capture it themselves.
            if (_press != null) _header.CaptureMouse();
        };
        _header.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled || Within(e.OriginalSource, _actions)) return;
            e.Handled = true;
            ShowPaneMenu(TabAt(e.OriginalSource) ?? Active, _header, PlacementMode.MousePoint);
        };
        // Dragging a tab, or the header of a one-tab pane, moves it (see PaneDrag).
        _header.PreviewMouseLeftButtonDown += (_, e) =>
        {
            bool onButton = Within(e.OriginalSource, _actions) || Within(e.OriginalSource, _moreTabs);
            _press = onButton || e.ClickCount > 1 ? null : e.GetPosition(_header);
            _pressed = TabAt(e.OriginalSource) ?? Active;
        };
        _header.PreviewMouseMove += (_, e) =>
        {
            if (_press is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(_header) - start;
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
            _press = null;
            if (_pressed != null && Tabs.Contains(_pressed)) _mux.Window.Drag.Begin(_pressed);
        };
        _header.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _press = null;
            if (_header.IsMouseCaptured) _header.ReleaseMouseCapture();
        };
        // Narrow panes keep the essentials; splitting stays in the pane menu.
        _header.SizeChanged += (_, e) => Fit(e.NewSize.Width);

        var outer = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        outer.Children.Add(_header);
        outer.Children.Add(_content);
        Host = new Border { BorderThickness = new Thickness(2), BorderBrush = _ring, Child = outer, Tag = this };
        Add(first);
    }

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }

    private static Path Icon(Geometry g, double size = 14) => new()
    {
        Data = g,
        Stroke = (Brush)Application.Current.Resources["Muted"],
        StrokeThickness = 1.75,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Stretch = Stretch.Uniform,
        Width = size,
        Height = size,
    };

    private Button Action(object content, string tip, Action? run)
    {
        var b = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["IconButtonSm"],
            ToolTip = tip,
            Margin = new Thickness(2, 0, 0, 0),
        };
        if (run != null) b.Click += (_, _) => run();
        return b;
    }

    private static bool Within(object source, DependencyObject? container)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == container) return true;
        return false;
    }

    /// <summary>The tab whose chip contains <paramref name="source"/>, if any.</summary>
    private PaneBase? TabAt(object source)
    {
        for (var d = source as DependencyObject; d != null && d != _header; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: PaneBase pane } && Tabs.Contains(pane)) return pane;
        return null;
    }

    private void Fit(double width)
    {
        _right.Visibility = _down.Visibility = width < 430 ? Visibility.Collapsed : Visibility.Visible;
        _pick.Visibility = width < 300 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Where things are, for dragging ----

    public Rect Bounds(Visual to) => Host.TransformToAncestor(to).TransformBounds(new Rect(Host.RenderSize));
    public Rect HeaderBounds(Visual to) => _header.TransformToAncestor(to).TransformBounds(new Rect(_header.RenderSize));
    public Rect ContentBounds(Visual to) => _content.TransformToAncestor(to).TransformBounds(new Rect(_content.RenderSize));

    /// <summary>The chips on screen, with each one's tab index, when this pane has several tabs.</summary>
    public List<(int Index, Rect Bounds)> VisibleTabs(Visual to)
    {
        var list = new List<(int, Rect)>();
        if (Tabs.Count < 2) return list;
        var header = HeaderBounds(to);
        foreach (var chip in _strip.Children.OfType<FrameworkElement>())
        {
            if (chip.Tag is not PaneBase tab || chip.ActualWidth < 1) continue;
            var r = chip.TransformToAncestor(to).TransformBounds(new Rect(chip.RenderSize));
            if (r.Left >= header.Left - 1 && r.Right <= header.Right + 1) list.Add((Tabs.IndexOf(tab), r));
        }
        return list;
    }

    // ---- Menus ----

    private static object MoreTabsLabel(int hidden)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "+" + hidden, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text = "\uE70D",
            FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
            FontSize = 9,
            Margin = new Thickness(6, 1, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    /// <summary>Opens the hidden-tabs list (dev builds' tests).</summary>
    public void OpenTabList() => ShowTabList();

    /// <summary>The tabs the row has no room for, numbered by their place in the row.</summary>
    private void ShowTabList()
    {
        var res = Application.Current.Resources;
        var menu = new ContextMenu { PlacementTarget = _moreTabs, Placement = PlacementMode.Bottom };
        var hidden = Tabs.Select((t, i) => (Tab: t, Index: i)).Where(x => !_strip.IsShown(x.Index)).ToList();
        menu.Items.Add(new MenuItem { Header = hidden.Count == 1 ? "1 MORE TAB" : $"{hidden.Count} MORE TABS", Style = (Style)res["MenuLabel"] });
        foreach (var (tab, index) in hidden)
        {
            var header = new TextBlock();
            header.Inlines.Add(new System.Windows.Documents.Run($"{index + 1}  ") { Foreground = (Brush)res["Faint"], FontFamily = (FontFamily)res["MonoFont"], FontSize = 11 });
            header.Inlines.Add(new System.Windows.Documents.Run(tab.TabLabel));
            var item = new MenuItem
            {
                Header = header,
                InputGestureText = ShellBadge.For(tab).Text + (tab.Attention ? "  •" : ""),
                ToolTip = tab.Cwd,
            };
            var target = tab;
            item.Click += (_, _) => _mux.Focus(target);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = $"All {Tabs.Count} tabs…", Tag = "\uE8FD" };
        all.Click += (_, _) => ShowAllTabs();
        menu.Items.Add(all);
        menu.IsOpen = true;
    }

    /// <summary>Every tab, for when the hidden ones aren't enough to find it.</summary>
    private void ShowAllTabs()
    {
        var res = Application.Current.Resources;
        var menu = new ContextMenu { PlacementTarget = _moreTabs, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = $"{Tabs.Count} TABS", Style = (Style)res["MenuLabel"] });
        for (int i = 0; i < Tabs.Count; i++)
        {
            var tab = Tabs[i];
            var header = new TextBlock();
            header.Inlines.Add(new System.Windows.Documents.Run($"{i + 1}  ") { Foreground = (Brush)res["Faint"], FontFamily = (FontFamily)res["MonoFont"], FontSize = 11 });
            header.Inlines.Add(new System.Windows.Documents.Run(tab.TabLabel));
            var item = new MenuItem
            {
                Header = header,
                Tag = tab == Active ? "\uE73E" : tab.Pinned ? "\uE718" : "",
                InputGestureText = ShellBadge.For(tab).Text,
                FontWeight = tab == Active ? FontWeights.SemiBold : FontWeights.Normal,
            };
            item.Click += (_, _) => _mux.Focus(tab);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Layout actions for one pane: split, flip, swap, zoom, move to a workspace, close.</summary>
    public void ShowPaneMenu(PaneBase pane, UIElement target, PlacementMode placement)
    {
        var res = Application.Current.Resources;
        var ws = pane.Workspace;
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        MenuItem Item(string header, string glyph, Action click, string gesture = "", bool enabled = true)
        {
            var mi = new MenuItem { Header = header, Tag = glyph, InputGestureText = gesture, IsEnabled = enabled };
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
            return mi;
        }

        Item("Split right", "\uE8A0", () => _mux.SplitTerminal(pane, true), "Ctrl+Shift+D");
        Item("Split down", "\uE8A0", () => _mux.SplitTerminal(pane, false), "Ctrl+Shift+E");
        menu.Items.Add(new Separator());
        if (pane.Stack.Parent is { } split)
        {
            Item(split.Horizontal ? "Stack top and bottom" : "Place side by side", "\uE7AD", () => _mux.FlipSplit(pane));
            Item(split.Horizontal ? "Swap left and right" : "Swap top and bottom", "\uE8AB", () => _mux.SwapSides(pane));
        }
        if (pane.Stack.Tabs.Count > 1)
            Item("Move tab into its own pane", "\uE8A7", () => _mux.MovePane(pane, pane, DropSide.Right));
        var noun = pane.Stack.Tabs.Count > 1 ? "tab" : "pane";
        Item(pane.Pinned ? $"Unpin {noun}" : $"Pin {noun}", pane.Pinned ? "\uE77A" : "\uE718", () => _mux.SetPinned(pane, !pane.Pinned));
        Item(ws.ZoomedPane != null ? "Unzoom" : "Zoom", ws.ZoomedPane != null ? "\uE73F" : "\uE740", () => _mux.ToggleZoom(pane), "Ctrl+Shift+Enter",
            enabled: ws.ZoomedPane != null || ws.StackCount > 1);

        bool canLeave = MuxController.CanLeave(pane);
        var others = _mux.Workspaces.Where(w => w != ws).Take(8).ToList();
        if (ws.PaneCount > 1 || others.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "MOVE TO", Style = (Style)res["MenuLabel"] });
            if (ws.PaneCount > 1) Item("New workspace", "\uE710", () => _mux.MoveToWorkspace(pane, null), enabled: canLeave);
            foreach (var w in others)
            {
                var dest = w;
                Item(dest.Name, dest.IsAgent ? "\uE99A" : "\uE8A9", () => _mux.MoveToWorkspace(pane, dest), enabled: canLeave);
            }
            if (MuxController.StayReason(pane) is { } stays)
                menu.Items.Add(new MenuItem { Header = stays, Style = (Style)res["MenuLabel"] });
        }

        menu.Items.Add(new Separator());
        var why = MuxController.PinnedReason(pane);
        var close = Item(why != null ? $"Close {noun} (unpin first)" : $"Close {noun}", "\uE711", () => _mux.RequestClose(pane), "Ctrl+Shift+W", enabled: why == null);
        close.Style = (Style)res["DangerMenuItem"];
        menu.IsOpen = true;
    }

    /// <summary>Keeps pinned tabs first (stable: their order, and the others', is kept).</summary>
    public void SortPinned()
    {
        var sorted = Tabs.Where(t => t.Pinned).Concat(Tabs.Where(t => !t.Pinned)).ToList();
        Tabs.Clear();
        Tabs.AddRange(sorted);
    }

    public void Add(PaneBase pane, int? index = null)
    {
        pane.Stack = this;
        Tabs.Insert(Math.Clamp(index ?? Tabs.Count, 0, Tabs.Count), pane);
        Detach(pane.Host);
        _content.Children.Add(pane.Host);
        Activate(pane);
    }

    /// <summary>Removes a tab; returns false if the stack is now empty.</summary>
    public bool Remove(PaneBase pane)
    {
        int i = Tabs.IndexOf(pane);
        if (i < 0) return Tabs.Count > 0;
        Tabs.RemoveAt(i);
        _content.Children.Remove(pane.Host);
        if (Tabs.Count == 0) return false;
        if (Active == pane) Activate(Tabs[Math.Min(i, Tabs.Count - 1)]);
        else Refresh();
        return true;
    }

    public void Activate(PaneBase pane)
    {
        Active = pane;
        foreach (var t in Tabs) t.Host.Visibility = t == pane ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    public PaneBase Neighbor(int delta) =>
        Tabs[((Tabs.IndexOf(Active) + delta) % Tabs.Count + Tabs.Count) % Tabs.Count];

    public void Refresh()
    {
        bool attention = Tabs.Any(t => t.Attention);
        if (attention)
        {
            if (!_pulsing)
            {
                // Notification ring: a slow amber pulse until the pane is focused.
                var c = PaneBase.AccentColor;
                _ring.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, Color.FromArgb(0x50, c.R, c.G, c.B), TimeSpan.FromMilliseconds(900))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                });
                _pulsing = true;
            }
        }
        else
        {
            _ring.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _pulsing = false;
            _ring.Color = Active?.Workspace.Broadcast == true && Active is TerminalPane ? BroadcastColor : Colors.Transparent;
        }
        RefreshHeader();
    }

    /// <summary>Rebuilds the header from the active tab (called on focus, cwd, branch, title and agent changes).</summary>
    public void RefreshHeader()
    {
        if (Active == null) return;
        var res = Application.Current.Resources;
        bool split = Active.Workspace.StackCount > 1;
        bool focused = Active.IsFocused;
        // In a split, the focused pane is marked by an amber line and the others fade back.
        _focusLine.Visibility = split && focused ? Visibility.Visible : Visibility.Collapsed;
        _actions.Opacity = _header.IsMouseOver ? 0.9 : focused ? 0.85 : 0.5;
        _close.Visibility = (split || Tabs.Count > 1) && MuxController.PinnedReason(Active) == null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in Tabs.OfType<TerminalPane>()) t.SetDimmed(split && !(focused && t == Active));

        if (Tabs.Count == 1)
        {
            _infoDock.Margin = new Thickness(12, 0, 6, 0);
            _info.Child = Summary(res);
        }
        else
        {
            _infoDock.Margin = new Thickness(6, 0, 6, 0);
            _strip.SetTabs(Tabs.Select(Chip));
            _strip.Pinned = Tabs.Count(t => t.Pinned);
            _strip.Active = Tabs.IndexOf(Active);
            _strip.InvalidateMeasure();
            _info.Child = _strip;
        }
    }

    /// <summary>
    /// The one-tab header: badge, title, folder, branch. When the pane is narrow the folder shortens first, then the
    /// branch pill goes, then the title shortens; the badge always stays.
    /// </summary>
    private SqueezeRow Summary(ResourceDictionary res)
    {
        var row = new SqueezeRow { VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ClipToBounds = true };
        var badge = ShellBadge.For(Active);
        row.Children.Add(Badge(badge, small: true));
        var title = Active is TerminalPane { Agent: { } agent } ? AgentManager.DisplayName(agent.Kind)
            : Active.Workspace.AgentTask is { } task && Active == Active.Workspace.Panes.OfType<TerminalPane>().FirstOrDefault()
                ? AgentManager.DisplayName(task.Agent)
                : badge.Label;
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)res["FgStrong"],
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
            ToolTip = "Drag to move this pane: onto another pane's edge to split it, onto its middle to make a tab, or onto a workspace",
        };
        row.Children.Add(titleText);
        if (Active.Pinned)
        {
            var pin = PinGlyph();
            pin.Margin = new Thickness(7, 1, 0, 0);
            row.Children.Add(pin);
        }
        var where = Active is TerminalPane { Remote: null } && Active.Cwd is { } cwd ? Workspace.DisplayPath(cwd)
            : Active is BrowserPane b ? b.Url : null;
        TextBlock? whereText = null;
        if (where != null)
        {
            whereText = new TextBlock
            {
                Text = where,
                FontFamily = (FontFamily)res["MonoFont"],
                FontSize = 11.5,
                Foreground = (Brush)res["Faint"],
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 420,
                ToolTip = Active.Cwd ?? (Active as BrowserPane)?.Url,
            };
            row.Children.Add(whereText);
        }
        Border? pill = null;
        if (Active is TerminalPane { Remote: null, GitBranch: { } branch })
            row.Children.Add(pill = BranchPill(branch));
        if (Active.Attention) row.Children.Add(Dot());
        if (whereText != null) row.Shrink.Add((whereText, 0, false));
        if (pill != null) row.Shrink.Add((pill, 0, true));
        row.Shrink.Add((titleText, 40, false));
        return row;
    }

    private static Border Badge(ShellBadge badge, bool small) => new()
    {
        Background = badge.Background,
        CornerRadius = new CornerRadius(small ? 5 : 7),
        Height = small ? 20 : 26,
        MinWidth = small ? 28 : 32,
        Padding = new Thickness(5, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = badge.Env,
        Child = new TextBlock
        {
            Text = badge.Text,
            Foreground = badge.Foreground,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static Border BranchPill(string branch)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Icon(BranchIcon, 11);
        icon.Stroke = (Brush)Application.Current.Resources["Muted"];
        icon.Margin = new Thickness(0, 0, 5, 0);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock
        {
            Text = branch,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["Muted"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 9, 3),
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = row,
        };
    }

    private static System.Windows.Shapes.Ellipse Dot() => new()
    {
        Width = 6,
        Height = 6,
        Margin = new Thickness(8, 0, 0, 0),
        Fill = new SolidColorBrush(PaneBase.AccentColor),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock PinGlyph() => new()
    {
        Text = "\uE718",
        FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
        FontSize = 10.5,
        Foreground = (Brush)Application.Current.Resources["Muted"],
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Pinned",
    };

    private Button Chip(PaneBase t)
    {
        // Badge and close keep their size; the label gives way (with an ellipsis) when the row is tight.
        // A pinned tab shows a pin instead of a close button.
        var row = new DockPanel { LastChildFill = true };
        var badge = Badge(ShellBadge.For(t), small: true);
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        if (t.Pinned)
        {
            var pin = PinGlyph();
            pin.Margin = new Thickness(7, 1, 4, 0);
            DockPanel.SetDock(pin, Dock.Right);
            row.Children.Add(pin);
        }
        else
        {
            var close = new Button { Content = "\uE711", Style = (Style)Application.Current.Resources["TabCloseButton"], ToolTip = "Close tab (Ctrl+Shift+W)" };
            close.Click += (_, e) => { e.Handled = true; _mux.RequestClose(t); };
            DockPanel.SetDock(close, Dock.Right);
            row.Children.Add(close);
        }
        if (t.Attention)
        {
            var dot = Dot();
            DockPanel.SetDock(dot, Dock.Right);
            row.Children.Add(dot);
        }
        row.Children.Add(new TextBlock
        {
            Text = t.TabLabel,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = t.Pinned ? 110 : 150,
        });
        var chip = new Button
        {
            Content = row,
            Tag = t,
            Style = (Style)Application.Current.Resources[t == Active ? "TabChipActive" : "TabChip"],
            ToolTip = $"{t.TabLabel}{(t.Pinned ? " (pinned)" : "")}{(t.Cwd != null ? "\n" + t.Cwd : "")}\nDrag to move or split · right-click for more",
        };
        chip.Click += (_, _) => _mux.Focus(t);
        chip.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) _mux.RequestClose(t); };
        return chip;
    }

    public static void Detach(FrameworkElement e)
    {
        switch (e.Parent)
        {
            case Panel panel: panel.Children.Remove(e); break;
            case Decorator d: d.Child = null; break;
        }
    }

    public void StopAnimations() => _ring.BeginAnimation(SolidColorBrush.ColorProperty, null);
}
