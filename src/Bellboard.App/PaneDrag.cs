using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bellboard.Core;
using Bellboard.Panes;
using Bellboard.Services;

namespace Bellboard;

/// <summary>
/// Dragging a tab or a pane header. Drop it on another pane's edge to split that pane, on its middle or its tab row to
/// join it as a tab, on a workspace in the sidebar to move it there (hold to open that workspace and drop it in place),
/// or on the sidebar's empty space to give it a workspace of its own. Esc or a right-click cancels.
/// Terminals are WebView2 windows, which WPF can't draw over, so the preview lives in a transparent popup, and the
/// drag runs on mouse capture: OLE drag-and-drop would hand the mouse to the web views.
/// </summary>
public sealed class PaneDrag
{
    private sealed record Target(string Text, Rect Area, bool Caret, Action? Drop, Workspace? Hover = null);

    private static readonly TimeSpan HoldToOpen = TimeSpan.FromMilliseconds(700);

    private readonly MainWindow _window;
    private readonly Popup _overlay;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Border _area, _caret, _ghost, _ghostBadge;
    private readonly TextBlock _ghostBadgeText, _ghostTitle, _ghostText;
    private readonly DispatcherTimer _timer;
    private PaneBase? _pane;
    private Target? _target;
    private Workspace? _hover;
    private DateTime _hoverSince;

    public PaneDrag(MainWindow window)
    {
        _window = window;
        var res = Application.Current.Resources;
        var accent = PaneBase.AccentColor;
        _area = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x2A, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xD0, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
        };
        _caret = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(accent) };

        _ghostBadgeText = new TextBlock
        {
            FontFamily = (FontFamily)res["MonoFont"],
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _ghostBadge = new Border { CornerRadius = new CornerRadius(5), Height = 20, MinWidth = 28, Padding = new Thickness(5, 0, 5, 0), Child = _ghostBadgeText };
        _ghostTitle = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)res["FgStrong"],
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _ghostText = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 260, TextWrapping = TextWrapping.Wrap };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_ghostBadge);
        top.Children.Add(_ghostTitle);
        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(_ghostText);
        _ghost = new Border
        {
            Background = (Brush)res["Bg2"],
            BorderBrush = (Brush)res["Line2"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 12, 8),
            Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.5, Color = Colors.Black },
        };
        TextElement.SetFontFamily(_ghost, (FontFamily)res["UiFont"]);
        _canvas.Children.Add(_area);
        _canvas.Children.Add(_caret);
        _canvas.Children.Add(_ghost);
        _overlay = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Relative,
            Focusable = false,
            IsHitTestVisible = false,
            Child = _canvas,
        };

        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();

        var root = _window.Root;
        root.MouseMove += (_, e) => { if (_pane != null) Update(e.GetPosition(root)); };
        root.MouseLeftButtonUp += (_, e) =>
        {
            if (_pane == null) return;
            e.Handled = true;
            End(drop: true);
        };
        root.MouseRightButtonDown += (_, e) =>
        {
            if (_pane == null) return;
            e.Handled = true;
            End(drop: false);
        };
        root.LostMouseCapture += (_, _) =>
        {
            if (_pane == null) return;
            // Switching workspaces mid-drag can take the capture; take it back while the button is still held.
            _window.Dispatcher.BeginInvoke(() =>
            {
                if (_pane == null || Mouse.Captured == root) return;
                if (!Win32.IsDown(Win32.VkLButton) || !Mouse.Capture(root, CaptureMode.Element)) End(drop: false);
            });
        };
    }

    public bool IsDragging => _pane != null;
    /// <summary>What a drop would do now (for tests).</summary>
    public string? TargetText => _target?.Text;

    /// <summary>Drives a drag without the mouse (dev builds' tests): positions are in window coordinates.</summary>
    public void MoveTo(Point p) { if (_pane != null) Update(p); }
    public void Finish(bool drop) { if (_pane != null) End(drop); }

    /// <param name="simulated">Driven by <see cref="MoveTo"/> instead of the mouse: no capture, so the real pointer is left alone.</param>
    public void Begin(PaneBase pane, bool simulated = false)
    {
        if (_pane != null) return;
        var root = _window.Root;
        _pane = pane;
        _target = null;
        _hover = null;
        var badge = ShellBadge.For(pane);
        _ghostBadge.Background = badge.Background;
        _ghostBadgeText.Text = badge.Text;
        _ghostBadgeText.Foreground = badge.Foreground;
        _ghostTitle.Text = pane.TabLabel;
        _canvas.Width = root.ActualWidth;
        _canvas.Height = root.ActualHeight;
        _overlay.PlacementTarget = root;
        _overlay.HorizontalOffset = 0;
        _overlay.VerticalOffset = 0;
        _overlay.IsOpen = true;
        _timer.Start();
        if (simulated) return;
        if (!Mouse.Capture(root, CaptureMode.Element))
        {
            End(drop: false);
            return;
        }
        Mouse.OverrideCursor = Cursors.SizeAll;
        Update(Mouse.GetPosition(root));
    }

    private void Tick()
    {
        if (_pane == null) return;
        if (Win32.IsDown(Win32.VkEscape))
        {
            End(drop: false);
            return;
        }
        // Spring-loaded workspaces: hovering one in the sidebar opens it, to drop the pane at an exact spot.
        if (_hover != null && _hover != _window.Mux.Selected && DateTime.UtcNow - _hoverSince > HoldToOpen)
        {
            var ws = _hover;
            _hover = null;
            _window.Mux.Select(ws);
            _window.Root.UpdateLayout();
            Update(Mouse.GetPosition(_window.Root));
        }
    }

    private void Update(Point p)
    {
        var target = Hit(p);
        _target = target;
        if (target?.Hover != _hover)
        {
            _hover = target?.Hover;
            _hoverSince = DateTime.UtcNow;
        }
        Show(target, p);
    }

    private void End(bool drop)
    {
        var pane = _pane;
        var target = _target;
        _pane = null;
        _target = null;
        _hover = null;
        _timer.Stop();
        _overlay.IsOpen = false;
        Mouse.OverrideCursor = null;
        if (Mouse.Captured == _window.Root) Mouse.Capture(null);
        if (pane == null) return;
        if (drop && target?.Drop is { } move)
        {
            try { move(); }
            catch (Exception ex) { Log.Error("moving a pane", ex); }
        }
        else if (_window.Mux.Workspaces.Contains(pane.Workspace))
        {
            _window.Mux.Focus(pane);
        }
    }

    private Rect Bounds(FrameworkElement e) => e.TransformToAncestor(_window.Root).TransformBounds(new Rect(e.RenderSize));

    private (Workspace Workspace, ListBoxItem Row)? WorkspaceAt(Point p)
    {
        if (_window.Root.InputHitTest(p) is not DependencyObject hit) return null;
        foreach (var list in new[] { _window.WorkspaceList, _window.AgentWorkspaceList })
            if (ItemsControl.ContainerFromElement(list, hit) is ListBoxItem { DataContext: Workspace ws } row) return (ws, row);
        return null;
    }

    private Target? Hit(Point p)
    {
        var mux = _window.Mux;
        var pane = _pane!;
        var root = _window.Root;

        if (!_window.SidebarHidden && Bounds(_window.Sidebar).Contains(p))
        {
            if (WorkspaceAt(p) is var (ws, row))
            {
                var area = Bounds(row);
                area.Inflate(-4, -1);
                if (ws == pane.Workspace) return new Target("It's in this workspace", area, false, null);
                if (MuxController.StayReason(pane) is { } stays) return new Target(stays, area, false, null);
                var hint = ws == mux.Selected ? "" : "  ·  hold to open it";
                return new Target($"Move to {ws.Name}{hint}", area, false, () => mux.MoveToWorkspace(pane, ws), ws);
            }
            var sidebar = Bounds(_window.Sidebar);
            sidebar.Inflate(-8, -8);
            if (pane.Workspace.PaneCount < 2) return new Target("Drop on another workspace", Rect.Empty, false, null);
            if (MuxController.StayReason(pane) is { } stay) return new Target(stay, Rect.Empty, false, null);
            return new Target("Move to a new workspace", sidebar, false, () => mux.MoveToWorkspace(pane, null));
        }

        if (mux.Selected is not { } shown) return null;
        foreach (var stack in shown.Stacks)
        {
            if (!stack.Host.IsVisible) continue;
            var bounds = stack.Bounds(root);
            if (!bounds.Contains(p)) continue;
            var header = stack.HeaderBounds(root);
            var anchor = stack.Active;
            bool own = stack == pane.Stack;

            if (header.Contains(p) && stack.Tabs.Count > 1)
            {
                // Over a tab row: a caret shows where the tab lands.
                var chips = stack.VisibleTabs(root);
                int i = LayoutMath.TabIndexAt(chips.Select(c => (c.Bounds.Left, c.Bounds.Width)).ToList(), p.X);
                int index = i < chips.Count ? chips[i].Index : chips.Count > 0 ? chips[^1].Index + 1 : stack.Tabs.Count;
                double x = i < chips.Count ? chips[i].Bounds.Left - 2 : chips.Count > 0 ? chips[^1].Bounds.Right - 2 : header.Left + 8;
                var caret = new Rect(x - 1.5, header.Top + 8, 3, Math.Max(4, header.Height - 16));
                if (own)
                {
                    int old = stack.Tabs.IndexOf(pane);
                    if (index == old || index == old + 1) return new Target("Drag along the tabs to reorder", caret, true, null);
                    return new Target("Reorder", caret, true, () => mux.MovePane(pane, anchor, DropSide.Tab, index));
                }
                return new Target("Add to these tabs", caret, true, () => mux.MovePane(pane, anchor, DropSide.Tab, index));
            }

            var side = header.Contains(p) ? DropSide.Tab : LayoutMath.Zone(stack.ContentBounds(root), p);
            if (own && (stack.Tabs.Count == 1 || side == DropSide.Tab))
                return new Target(stack.Tabs.Count == 1 ? "Drop on another pane, or on a workspace in the sidebar" : "Drop on an edge to split this tab out",
                    Rect.Empty, false, null);
            var preview = LayoutMath.Preview(bounds, side);
            preview.Inflate(-5, -5);
            var text = side switch
            {
                DropSide.Left => "Place on the left",
                DropSide.Right => "Place on the right",
                DropSide.Up => "Place above",
                DropSide.Down => "Place below",
                _ => "Add as a tab here",
            };
            return new Target(text, preview, false, () => mux.MovePane(pane, anchor, side));
        }
        return null;
    }

    private void Show(Target? target, Point p)
    {
        var res = Application.Current.Resources;
        bool area = target is { Caret: false } && !target.Area.IsEmpty;
        bool caret = target is { Caret: true } && !target.Area.IsEmpty;
        _area.Visibility = area ? Visibility.Visible : Visibility.Collapsed;
        _caret.Visibility = caret ? Visibility.Visible : Visibility.Collapsed;
        if (area)
        {
            Canvas.SetLeft(_area, target!.Area.X);
            Canvas.SetTop(_area, target.Area.Y);
            _area.Width = target.Area.Width;
            _area.Height = target.Area.Height;
        }
        if (caret)
        {
            Canvas.SetLeft(_caret, target!.Area.X);
            Canvas.SetTop(_caret, target.Area.Y);
            _caret.Height = target.Area.Height;
        }
        _ghostText.Text = target?.Text ?? "Drop on a pane, its tabs, or a workspace";
        _ghostText.Foreground = (Brush)res[target?.Drop != null ? "Accent" : "Faint"];
        _ghost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _ghost.DesiredSize;
        double x = Math.Min(p.X + 16, Math.Max(0, _canvas.Width - size.Width - 4));
        double y = p.Y + 18 + size.Height > _canvas.Height ? p.Y - size.Height - 10 : p.Y + 18;
        Canvas.SetLeft(_ghost, x);
        Canvas.SetTop(_ghost, Math.Max(0, y));
    }
}
