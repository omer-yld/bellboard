using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Bellboard.Core;

namespace Bellboard;

public sealed class PaletteItem
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string Glyph { get; init; } = "\uE756";
    public string? Shortcut { get; init; }
    public string Group { get; init; } = "Action";
    public required Action Run { get; init; }
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);
    internal int Score;

    /// <summary>"Ctrl+Shift+T" as key caps: Ctrl, ⇧, T.</summary>
    public IEnumerable<string> Keys => string.IsNullOrEmpty(Shortcut) ? Array.Empty<string>()
        : Shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim() switch { "Shift" => "⇧", "Enter" => "↵", var x => x });

    /// <summary>The section a result is listed under.</summary>
    public string Section => Group switch
    {
        "Action" => "COMMANDS",
        "Workspace" => "WORKSPACES",
        "Pane" => "PANES",
        "Folder" => "FOLDERS",
        "Page" => "PAGES",
        "Theme" => "THEMES",
        var other => other.ToUpperInvariant() + "S",
    };
}

/// <summary>Ctrl+Shift+P: fuzzy search over actions, workspaces, panes and folders.</summary>
public partial class PaletteWindow : Window
{
    private readonly List<PaletteItem> _all;
    private bool _closing;

    public PaletteWindow(Window owner, List<PaletteItem> items, string initialQuery = "")
    {
        InitializeComponent();
        Owner = owner;
        _all = items;
        var area = owner.WindowState == WindowState.Maximized
            ? new Rect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height)
            : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 56;
        Deactivated += (_, _) => SafeClose();
        Loaded += (_, _) =>
        {
            Query.Text = initialQuery;
            Query.CaretIndex = Query.Text.Length;
            Query.Focus();
            Keyboard.Focus(Query);
            Filter();
        };
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Filter();
    }

    private void Filter()
    {
        var q = Query.Text.Trim();
        IEnumerable<PaletteItem> pool = _all;
        if (q.StartsWith('>'))
        {
            pool = pool.Where(i => i.Group == "Action");
            q = q[1..].Trim();
        }
        List<PaletteItem> shown;
        if (q.Length == 0)
        {
            // Unfiltered: commands first, then workspaces, panes, folders, pages (stable within each).
            string[] order = ["Action", "Workspace", "Pane", "Folder", "Page"];
            shown = pool.Select((item, i) => (item, i))
                .OrderBy(x => Array.IndexOf(order, x.item.Group) is var r && r < 0 ? order.Length : r).ThenBy(x => x.i)
                .Select(x => x.item).ToList();
        }
        else
        {
            foreach (var i in pool) i.Score = Math.Max(Fuzzy.Score(i.Title, q) * 2, Fuzzy.Score(i.Subtitle ?? "", q));
            shown = pool.Where(i => i.Score > 0).OrderByDescending(i => i.Score).ToList();
        }
        var view = new System.Windows.Data.ListCollectionView(shown);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(PaletteItem.Section)));
        Results.ItemsSource = view;
        if (shown.Count > 0)
        {
            Results.SelectedIndex = 0;
            Results.ScrollIntoView(Results.SelectedItem);
        }
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int count = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                break;
            case Key.Down or Key.Up or Key.PageDown or Key.PageUp when count > 0:
                int step = e.Key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 8, _ => -8 };
                Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + step, 0, count - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
        }
    }

    private void Results_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaletteItem item)
        {
            Results.SelectedItem = item;
            RunSelected();
        }
    }

    private void RunSelected()
    {
        if (Results.SelectedItem is not PaletteItem item) return;
        SafeClose();
        // Run after the palette is gone so focus/dialogs land in the main window.
        Owner?.Dispatcher.InvokeAsync(item.Run, System.Windows.Threading.DispatcherPriority.Input);
    }
}
