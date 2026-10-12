using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Bellboard.Core;

/// <summary>
/// The tab row in a pane header. Pinned tabs come first and always show; the others share the remaining width
/// (see <see cref="LayoutMath.FitTabs"/>), and the ones that still don't fit are hidden behind the <see cref="More"/>
/// button at the end, keeping the active tab in view. The mouse wheel scrolls through them.
/// </summary>
public sealed class TabStrip : Panel
{
    public const double MinTab = 108, MaxTab = 210;

    private FrameworkElement? _more;
    private double[] _widths = [];
    private int _first, _active, _hidden, _shown;
    private bool _scrolled;

    public TabStrip() => ClipToBounds = true;

    /// <summary>Reports how many tabs are hidden, after a layout pass that changed it.</summary>
    public event Action<int>? HiddenChanged;

    /// <summary>Shown after the visible tabs while some are hidden.</summary>
    public FrameworkElement? More
    {
        get => _more;
        set
        {
            if (_more != null) InternalChildren.Remove(_more);
            _more = value;
            if (value != null) InternalChildren.Add(value);
            InvalidateMeasure();
        }
    }

    /// <summary>The active tab's index; the row scrolls to keep it visible.</summary>
    public int Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            _scrolled = false;
            InvalidateMeasure();
        }
    }

    public int Hidden => _hidden;

    /// <summary>How many tabs at the start are pinned (always shown).</summary>
    public int Pinned { get; set; }

    /// <summary>Whether the tab at this index is on screen.</summary>
    public bool IsShown(int index) => index >= 0 && index < _widths.Length && _widths[index] > 0;

    private List<UIElement> Tabs => InternalChildren.Cast<UIElement>().Where(c => c != _more).ToList();

    public void SetTabs(IEnumerable<UIElement> tabs)
    {
        foreach (var t in Tabs) InternalChildren.Remove(t);
        int i = 0;
        foreach (var t in tabs) InternalChildren.Insert(i++, t);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_hidden == 0) return;
        _first += e.Delta > 0 ? -1 : 1;
        _scrolled = true;
        InvalidateMeasure();
        e.Handled = true;
    }

    protected override Size MeasureOverride(Size available)
    {
        var tabs = Tabs;
        double height = 0;
        var natural = new double[tabs.Count];
        for (int i = 0; i < tabs.Count; i++)
        {
            tabs[i].Measure(new Size(double.PositiveInfinity, available.Height));
            natural[i] = tabs[i].DesiredSize.Width;
            height = Math.Max(height, tabs[i].DesiredSize.Height);
        }
        double room = double.IsInfinity(available.Width) ? natural.Sum() : available.Width;
        // Pinned tabs keep their width; the rest share what's left.
        int pinned = Math.Clamp(Pinned, 0, tabs.Count);
        double pinnedWidth = natural.Take(pinned).Sum(w => Math.Min(w, MaxTab));
        var rest = natural.Skip(pinned).ToArray();
        double restRoom = Math.Max(0, room - pinnedWidth);
        int active = _scrolled || _active < pinned ? -1 : _active - pinned;
        var fit = LayoutMath.FitTabs(rest, restRoom, active, _first, MinTab, MaxTab, _shown);
        if (fit.Count < rest.Length && _more != null)
        {
            _more.Measure(new Size(double.PositiveInfinity, available.Height));
            fit = LayoutMath.FitTabs(rest, Math.Max(0, restRoom - _more.DesiredSize.Width), active, _first, MinTab, MaxTab, _shown);
        }
        _widths = natural.Take(pinned).Select(w => Math.Min(w, MaxTab)).Concat(fit.Widths).ToArray();
        _first = fit.First;
        _shown = fit.Count;
        int hidden = rest.Length - fit.Count;
        if (hidden != _hidden)
        {
            _hidden = hidden;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () => HiddenChanged?.Invoke(_hidden));
        }
        // Only tabs that shrank are measured again (re-measuring at exactly the natural width can trim after rounding).
        for (int i = 0; i < tabs.Count; i++)
            if (_widths[i] > 0 && _widths[i] < natural[i] - 0.01) tabs[i].Measure(new Size(_widths[i], available.Height));
        double width = _widths.Sum();
        if (_more != null)
        {
            _more.Measure(new Size(double.PositiveInfinity, available.Height));
            if (_hidden > 0) width += _more.DesiredSize.Width;
        }
        return new Size(Math.Min(width, room), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var tabs = Tabs;
        double x = 0;
        for (int i = 0; i < tabs.Count; i++)
        {
            double w = i < _widths.Length ? _widths[i] : 0;
            // Hidden tabs go outside the clip, so they neither draw nor take clicks.
            tabs[i].Arrange(w > 0 ? new Rect(x, 0, w, final.Height) : new Rect(-10000, 0, 0, final.Height));
            x += w;
        }
        _more?.Arrange(_hidden > 0 ? new Rect(x, 0, _more.DesiredSize.Width, final.Height) : new Rect(-10000, 0, 0, final.Height));
        return final;
    }
}
