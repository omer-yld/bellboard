using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Bellboard.Core;

/// <summary>Where a moved pane goes relative to another: into its tab group, or splitting its spot on one side.</summary>
public enum DropSide { Tab, Left, Right, Up, Down }

/// <summary>The geometry behind dragging panes and fitting tabs, kept free of WPF controls so it can be tested.</summary>
public static class LayoutMath
{
    /// <summary>
    /// Which part of a pane's content a drop at <paramref name="p"/> hits: the outer <paramref name="edge"/> on each side
    /// splits that side, the middle joins the pane as a tab.
    /// </summary>
    public static DropSide Zone(Rect content, Point p, double edge = 0.3)
    {
        double x = (p.X - content.Left) / Math.Max(1, content.Width);
        double y = (p.Y - content.Top) / Math.Max(1, content.Height);
        double left = x, right = 1 - x, up = y, down = 1 - y;
        double nearest = Math.Min(Math.Min(left, right), Math.Min(up, down));
        if (nearest > edge) return DropSide.Tab;
        return nearest == left ? DropSide.Left : nearest == right ? DropSide.Right : nearest == up ? DropSide.Up : DropSide.Down;
    }

    /// <summary>The area a dropped pane would take: half of the pane on a side, or all of it as a tab.</summary>
    public static Rect Preview(Rect pane, DropSide side) => side switch
    {
        DropSide.Left => new Rect(pane.X, pane.Y, pane.Width / 2, pane.Height),
        DropSide.Right => new Rect(pane.X + pane.Width / 2, pane.Y, pane.Width / 2, pane.Height),
        DropSide.Up => new Rect(pane.X, pane.Y, pane.Width, pane.Height / 2),
        DropSide.Down => new Rect(pane.X, pane.Y + pane.Height / 2, pane.Width, pane.Height / 2),
        _ => pane,
    };

    /// <summary>The insert position for a tab dropped at <paramref name="x"/> over tabs spanning these (left, width) slots.</summary>
    public static int TabIndexAt(IReadOnlyList<(double Left, double Width)> tabs, double x)
    {
        for (int i = 0; i < tabs.Count; i++)
            if (x < tabs[i].Left + tabs[i].Width / 2) return i;
        return tabs.Count;
    }

    /// <summary>
    /// Fits tabs into one row. Tabs keep their natural width while everything fits; then the widest shrink first
    /// (never below <paramref name="min"/>); past that only a window of tabs around the active one is shown.
    /// Returns each tab's width (0 when hidden) and the first and count of the visible ones. A negative
    /// <paramref name="active"/> keeps <paramref name="first"/> as given (the user scrolled the row). When the row
    /// has room for more tabs than before (<paramref name="wasShown"/>), it reveals the ones hidden on the left first.
    /// </summary>
    public static (double[] Widths, int First, int Count) FitTabs(IReadOnlyList<double> natural, double available, int active, int first, double min, double max,
        int wasShown = 0)
    {
        int n = natural.Count;
        var widths = new double[n];
        if (n == 0) return (widths, 0, 0);
        var nat = natural.Select(w => Math.Clamp(w, Math.Min(min, w), max)).ToArray();
        active = Math.Min(active, n - 1);

        if (Cap(nat, available) is { } cap && cap >= min)
        {
            for (int i = 0; i < n; i++) widths[i] = Math.Min(nat[i], cap);
            return (widths, 0, n);
        }

        int count = Math.Clamp((int)Math.Floor(available / min), 1, n);
        if (wasShown > 0 && count > wasShown) first -= count - wasShown;
        first = Math.Clamp(first, 0, n - count);
        if (active >= 0 && active < first) first = active;
        else if (active >= first + count) first = active - count + 1;
        var shown = nat.Skip(first).Take(count).ToArray();
        var shownCap = Cap(shown, available) ?? double.MaxValue;
        for (int i = 0; i < count; i++) widths[first + i] = Math.Min(shown[i], shownCap);
        return (widths, first, count);
    }

    /// <summary>The largest width w such that sum(min(width_i, w)) fits; null when everything fits as is.</summary>
    private static double? Cap(double[] widths, double available)
    {
        if (widths.Sum() <= available) return null;
        var sorted = widths.OrderBy(w => w).ToArray();
        double left = available;
        for (int i = 0; i < sorted.Length; i++)
        {
            int rest = sorted.Length - i;
            if (sorted[i] * rest > left) return left / rest;
            left -= sorted[i];
        }
        return null;
    }
}
