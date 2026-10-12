using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Bellboard.Core;

/// <summary>
/// Lays its children out left to right at their natural widths. When they don't fit, it takes width back from the
/// children in <see cref="Shrink"/>, first to last: text shortens with its ellipsis down to its minimum, and a child
/// marked to drop disappears whole (a pill half-cut looks broken).
/// </summary>
public sealed class SqueezeRow : Panel
{
    public List<(UIElement Child, double Min, bool Drop)> Shrink { get; } = new();
    private double[] _widths = [];

    protected override Size MeasureOverride(Size available)
    {
        var kids = InternalChildren.Cast<UIElement>().ToList();
        _widths = new double[kids.Count];
        double height = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            kids[i].Measure(new Size(double.PositiveInfinity, available.Height));
            _widths[i] = kids[i].DesiredSize.Width;
            height = Math.Max(height, kids[i].DesiredSize.Height);
        }
        var natural = (double[])_widths.Clone();
        double excess = double.IsInfinity(available.Width) ? 0 : _widths.Sum() - available.Width;
        foreach (var (child, min, drop) in Shrink)
        {
            if (excess <= 0) break;
            int i = kids.IndexOf(child);
            if (i < 0) continue;
            double give = drop ? _widths[i] : Math.Min(excess, Math.Max(0, _widths[i] - min));
            _widths[i] -= give;
            excess -= give;
        }
        // Only the children that gave width up are measured again: re-measuring text at exactly its own width can
        // trim it after layout rounding.
        for (int i = 0; i < kids.Count; i++)
            if (_widths[i] < natural[i]) kids[i].Measure(new Size(_widths[i], available.Height));
        return new Size(Math.Min(_widths.Sum(), available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var kids = InternalChildren.Cast<UIElement>().ToList();
        double x = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            double w = i < _widths.Length ? _widths[i] : 0;
            kids[i].Arrange(w > 0 ? new Rect(x, 0, w, final.Height) : new Rect(-10000, 0, 0, final.Height));
            x += w;
        }
        return final;
    }
}
