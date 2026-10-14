using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bellboard.Core;
using Bellboard.Services;
using Bellboard.Terminal;

namespace Bellboard;

/// <summary>
/// Settings: sections on the left, rows on the right. Changes preview live in every pane; Save writes config.json,
/// Cancel (or closing the window) puts the previous settings back.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly double[] LineHeights = [1.0, 1.1, 1.2, 1.3, 1.4];
    private static readonly (string Label, string Value)[] Cursors = [("Block", "block"), ("Bar", "bar"), ("Underline", "underline")];
    private static readonly (string Label, int Minutes)[] UnloadChoices = [("Never", 0), ("15 min", 15), ("30 min", 30), ("1 hour", 60)];
    private static readonly (string Name, string Url)[] Engines =
    [
        ("Google", "https://www.google.com/search?q=%s"),
        ("Bing", "https://www.bing.com/search?q=%s"),
        ("DuckDuckGo", "https://duckduckgo.com/?q=%s"),
        ("Brave", "https://search.brave.com/search?q=%s"),
    ];
    private static readonly (string Action, string Keys)[] Shortcuts =
    [
        ("Command palette", "Ctrl+Shift+P"), ("New workspace", "Ctrl+Shift+T"), ("New agent", "Ctrl+Shift+A"),
        ("New tab", "Ctrl+Shift+N"), ("New tab with another shell", "Ctrl+Shift+Space"),
        ("Split pane right", "Ctrl+Shift+D"), ("Split pane down", "Ctrl+Shift+E"), ("Close tab or pane", "Ctrl+Shift+W"),
        ("Reopen what you closed", "Ctrl+Shift+Z"), ("Go to workspace 1–9", "Ctrl+1–9"), ("Next workspace", "Ctrl+Tab"),
        ("Move focus between panes", "Ctrl+Alt+Arrows"), ("Resize the pane", "Ctrl+Shift+Alt+Arrows"),
        ("Zoom the pane", "Ctrl+Shift+Enter"), ("Equalize splits", "Ctrl+Shift+="), ("Find in terminal", "Ctrl+Shift+F"),
        ("Open the browser", "Ctrl+Shift+L"), ("Inbox", "Ctrl+Shift+I"), ("Jump to the latest unread", "Ctrl+Shift+U"),
        ("Rename the workspace", "Ctrl+Shift+R"), ("Toggle the sidebar", "Ctrl+Shift+B"), ("Settings", "Ctrl+,"),
    ];

    private readonly MuxController _mux;
    private readonly BellboardConfig _original;
    private readonly BellboardConfig _edit;
    private readonly DispatcherTimer _preview;
    private readonly List<(RadioButton Nav, StackPanel Section, string Title)> _sections = new();
    private readonly List<(FrameworkElement Row, StackPanel Section, string Text)> _rows = new();
    private readonly List<(Border Card, ThemePreset Theme)> _themeCards = new();
    private readonly List<(Border Row, string? Alias)> _shellRows = new();
    private Button _fontButton = null!;
    private TextBlock _fontName = null!, _fontDesc = null!, _sizeValue = null!, _previewName = null!;
    private Slider _size = null!;
    private Border _previewBox = null!;
    private TextBlock _previewText = null!;
    private List<string>? _monoFonts;
    private bool _dirty, _saved, _scrolling;

    public SettingsWindow(Window owner, MuxController mux, string? section = null)
    {
        InitializeComponent();
        Owner = owner;
        _mux = mux;
        _original = mux.Config.Clone();
        _edit = mux.Config.Clone();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.92);
        _preview = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _preview.Tick += (_, _) =>
        {
            _preview.Stop();
            _mux.ApplyConfig(_edit.Clone());
            _mux.Window.UpdateChrome();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) =>
        {
            _preview.Stop();
            if (!_saved && _dirty)
            {
                _mux.ApplyConfig(_original);
                _mux.Window.UpdateChrome();
            }
        };

        BuildTerminal();
        BuildAppearance();
        BuildShell();
        BuildWorkspaces();
        BuildNotifications();
        BuildBrowser();
        BuildRemote();
        BuildKeyboard();

        Loaded += (_, _) =>
        {
            var start = _sections.FirstOrDefault(s => (string)s.Section.Tag == section);
            (start.Nav ?? _sections[0].Nav).IsChecked = true;
            if (start.Section != null) Dispatcher.BeginInvoke(() => ScrollTo(start.Section), DispatcherPriority.Loaded);
        };
    }

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];

    // ---- Sections ----

    private void BuildTerminal()
    {
        var s = Section("terminal", "", "Terminal", "How text looks and behaves inside every pane.");

        _fontName = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var fontContent = new DockPanel();
        var chevron = new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 10, Foreground = Res<Brush>("Faint"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(chevron, Dock.Right);
        fontContent.Children.Add(chevron);
        fontContent.Children.Add(_fontName);
        _fontButton = new Button { Style = Res<Style>("RepoButton"), FontFamily = Res<FontFamily>("UiFont"), FontSize = 13, MinWidth = 230, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = fontContent };
        _fontButton.Click += (_, _) => FontMenu();
        _fontDesc = new TextBlock();
        Row(s, "Font family", _fontDesc, _fontButton, "typeface nerd font jetbrains cascadia consolas");
        UpdateFontRow();

        _size = new Slider { Minimum = 11, Maximum = 22, SmallChange = 1, LargeChange = 1, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 150, Value = _edit.FontSize, Style = Res<Style>("AccentSlider"), Margin = new Thickness(8, 0, 8, 0) };
        _sizeValue = new TextBlock { FontFamily = Res<FontFamily>("MonoFont"), FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = Res<Brush>("FgStrong"), MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _size.ValueChanged += (_, _) =>
        {
            _edit.FontSize = Math.Round(_size.Value);
            _sizeValue.Text = $"{_edit.FontSize:0}px";
            Changed();
        };
        _sizeValue.Text = $"{_edit.FontSize:0}px";
        var stepper = new StackPanel { Orientation = Orientation.Horizontal };
        stepper.Children.Add(Step("−", -1));
        stepper.Children.Add(_size);
        stepper.Children.Add(Step("+", 1));
        stepper.Children.Add(_sizeValue);
        Row(s, "Font size", Desc("Also Ctrl + scroll inside a pane."), stepper, "zoom text bigger smaller");

        var nearest = LineHeights.OrderBy(h => Math.Abs(h - _edit.LineHeight)).First();
        Row(s, "Line height", Desc("Taller lines are easier to scan; tighter fits more history."),
            Segmented(LineHeights.Select(h => (h.ToString("0.0", CultureInfo.InvariantCulture), h)), nearest, v => _edit.LineHeight = v), "spacing");
        Row(s, "Cursor", null, Segmented(Cursors, _edit.CursorStyle, v => _edit.CursorStyle = v), "caret block bar underline");
        Row(s, "Blinking cursor", null, Toggle(_edit.CursorBlink, v => _edit.CursorBlink = v), "caret blink");
    }

    private void BuildAppearance()
    {
        var s = Section("appearance", "", "Appearance", "Pick a colour theme for terminals. Your Windows Terminal schemes are listed too.");

        _previewName = new TextBlock { FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Height = 28, Margin = new Thickness(12, 0, 12, 0) };
        for (int i = 0; i < 3; i++) bar.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 6, 0), Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_previewName);
        _previewText = new TextBlock { FontFamily = Res<FontFamily>("MonoFont"), FontSize = 12.5, LineHeight = 21, Margin = new Thickness(18, 12, 18, 16) };
        var previewBody = new StackPanel();
        previewBody.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), Child = bar });
        previewBody.Children.Add(_previewText);
        _previewBox = new Border { CornerRadius = new CornerRadius(12), BorderBrush = Res<Brush>("Line2"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 18), Child = previewBody, ClipToBounds = true };
        Block(s, _previewBox, "preview sample");

        var grid = new WrapPanel();
        foreach (var theme in Themes.All()) grid.Children.Add(ThemeCard(theme));
        Block(s, grid, "theme colour color scheme " + string.Join(' ', Themes.All().Select(t => t.Name)));
        UpdateThemes();
    }

    private void BuildShell()
    {
        var s = Section("shell", "", "Shell",
            "Used when there's nothing to copy, like your first workspace. New workspaces, tabs and splits reuse the shell you're in.");
        var list = new StackPanel();
        list.Children.Add(ShellOption(null, "AUTO", "Automatic", "PowerShell 7 if installed, otherwise Windows PowerShell", "", Res<Brush>("Accent"), Res<Brush>("AccentTint")));
        foreach (var (alias, label) in ShellResolver.Available())
        {
            var badge = ShellBadge.ForAlias(alias);
            list.Children.Add(ShellOption(alias, badge.Text, label, (ShellResolver.ExpandAlias(alias) ?? alias).Replace("\"", ""), alias.StartsWith("wsl") ? "WSL" : "", badge.Foreground, badge.Background));
        }
        Block(s, list, "default shell powershell pwsh cmd git bash wsl ubuntu");
        UpdateShells();
    }

    private void BuildWorkspaces()
    {
        var s = Section("workspaces", "", "Workspaces", "Where new workspaces start, how fast they open and what comes back after a restart.");
        Row(s, "New workspaces open in", Desc("The folder of the pane you're in, or your home folder."),
            Segmented([("Current folder", "inherit"), ("Home folder", "home")], _edit.NewWorkspaceDirectory == "home" ? "home" : "inherit", v => _edit.NewWorkspaceDirectory = v), "directory folder cwd");
        Row(s, "Keep a shell warm", Desc("Instant prompt for the next workspace. Keeps one idle shell running in the background."),
            Toggle(_edit.PrestartShell, v => _edit.PrestartShell = v), "prestart fast startup");
        Row(s, "Restore workspaces", Desc("Reopen workspaces, layout and scrollback when Bellboard starts."),
            Toggle(_edit.RestoreSession, v => _edit.RestoreSession = v), "session restore");
        Row(s, "Resume agents", Desc("Continue Claude Code, Codex, OpenCode and Pi conversations in restored panes."),
            Toggle(_edit.RestoreAgents, v => _edit.RestoreAgents = v), "session claude codex pi resume");
        Row(s, "Free memory of hidden workspaces", Desc("Unloads the view of a workspace you haven't looked at for a while. Its shells keep running."),
            Segmented(UnloadChoices.Select(c => (c.Label, c.Minutes)), UnloadChoices.Select(c => c.Minutes).OrderBy(m => Math.Abs(m - _edit.UnloadIdleWorkspacesMinutes)).First(),
                v => _edit.UnloadIdleWorkspacesMinutes = v), "memory unload idle");
    }

    private void BuildNotifications()
    {
        var s = Section("notifications", "", "Notifications", "Bellboard watches for BEL characters, finished commands and agents that need you.");
        Row(s, "Windows notifications", Desc("Show a toast when a pane needs attention and Bellboard isn't focused."),
            Toggle(_edit.DesktopNotifications, v => _edit.DesktopNotifications = v), "toast");
        Row(s, "Ring background workspaces", Desc("A bell in a background pane lights its ring in the sidebar until you look."),
            Toggle(_edit.BellIsAttention, v => _edit.BellIsAttention = v), "bel bell attention");
    }

    private void BuildBrowser()
    {
        var s = Section("browser", "", "Browser", "The browser pane and its address bar.");
        Row(s, "Search engine", Desc("Used for anything you type in the address bar that isn't a URL."),
            Segmented(Engines.Select(e => (e.Name, e.Url)), _edit.SearchUrl, v => _edit.SearchUrl = v), "google bing duckduckgo brave address");
    }

    private void BuildKeyboard()
    {
        var s = Section("keyboard", "", "Keyboard", "Shortcuts work from anywhere in the window, terminals included.");
        foreach (var (action, keys) in Shortcuts)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var key in keys.Split('+'))
                chips.Children.Add(new Border { Style = Res<Style>("Kbd"), Child = new TextBlock { Text = key == "Shift" ? "⇧" : key, Style = Res<Style>("KbdText") } });
            var row = new DockPanel();
            DockPanel.SetDock(chips, Dock.Right);
            row.Children.Add(chips);
            row.Children.Add(new TextBlock { Text = action, FontSize = 13, Foreground = Res<Brush>("FgStrong"), VerticalAlignment = VerticalAlignment.Center });
            var border = new Border { BorderBrush = Res<Brush>("Divider"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 10), Child = row };
            s.Children.Add(border);
            _rows.Add((border, s, action + " " + keys + " shortcut keyboard"));
        }
    }

    // ---- Building blocks ----

    private StackPanel Section(string key, string glyph, string title, string description)
    {
        var nav = new RadioButton { Content = title, Tag = glyph, GroupName = "nav", Style = Res<Style>("SettingsNav") };
        var panel = new StackPanel { Tag = key, Margin = new Thickness(0, 26, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("FgStrong") });
        panel.Children.Add(new TextBlock { Text = description, Style = Res<Style>("SettingDesc"), Margin = new Thickness(0, 3, 0, 12), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12.5 });
        nav.Checked += (_, _) => { if (!_scrolling) ScrollTo(panel); };
        Nav.Children.Add(nav);
        Sections.Children.Add(panel);
        _sections.Add((nav, panel, title));
        return panel;
    }

    private static TextBlock Desc(string text) => new() { Text = text };

    private void Row(StackPanel section, string title, TextBlock? description, UIElement control, string keywords = "")
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, Style = Res<Style>("SettingTitle") });
        if (description != null)
        {
            description.Style = Res<Style>("SettingDesc");
            left.Children.Add(description);
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.Margin = new Thickness(24, 0, 0, 0);
        }
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        var row = new Border { BorderBrush = Res<Brush>("Divider"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 14), Child = grid };
        section.Children.Add(row);
        _rows.Add((row, section, $"{title} {description?.Text} {keywords}"));
    }

    private void Block(StackPanel section, FrameworkElement block, string keywords)
    {
        section.Children.Add(block);
        _rows.Add((block, section, keywords));
    }

    private Border Segmented<T>(IEnumerable<(string Label, T Value)> options, T current, Action<T> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "seg-" + Guid.NewGuid().ToString("N");
        foreach (var (label, value) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = group, Style = Res<Style>("Segment"), IsChecked = EqualityComparer<T>.Default.Equals(value, current) };
            rb.Checked += (_, _) => { set(value); Changed(); };
            panel.Children.Add(rb);
        }
        return new Border { Style = Res<Style>("SegmentTrack"), Child = panel };
    }

    private CheckBox Toggle(bool current, Action<bool> set)
    {
        var box = new CheckBox { Style = Res<Style>("ToggleSwitch"), IsChecked = current, Margin = new Thickness(0) };
        box.Checked += (_, _) => { set(true); Changed(); };
        box.Unchecked += (_, _) => { set(false); Changed(); };
        return box;
    }

    private Button Step(string text, int delta)
    {
        var b = new Button { Content = text, Style = Res<Style>("StepButton"), ToolTip = delta < 0 ? "Smaller" : "Larger" };
        b.Click += (_, _) => _size.Value = Math.Clamp(_size.Value + delta, _size.Minimum, _size.Maximum);
        return b;
    }

    // ---- Font ----

    private const string Bundled = "JetBrains Mono";

    private void UpdateFontRow()
    {
        var auto = _edit.FontFamily == BellboardConfig.DefaultFontStack;
        var first = _edit.FontFamily.Split(',')[0].Trim().Trim('\'', '"');
        _fontName.Text = auto ? "Automatic" : first;
        _fontDesc.Text = auto
            ? "Uses Windows Terminal's font (" + string.Join(", ", FontDetector.Resolve(BellboardConfig.DefaultFontStack, BellboardConfig.DefaultFontStack)
                .Split(',').Take(2).Select(f => f.Trim().Trim('\'', '"'))) + ") and adds a Nerd Font for prompt icons."
            : first.Equals(Bundled, StringComparison.OrdinalIgnoreCase) ? "Ships with Bellboard; a Nerd Font is added for prompt icons."
            : "A Nerd Font is added for prompt icons if this one lacks them.";
    }

    private void FontMenu()
    {
        _monoFonts ??= MonospaceFonts();
        var menu = new ContextMenu { PlacementTarget = _fontButton, Placement = PlacementMode.Bottom };
        void Item(string label, string stack, string? hint = null)
        {
            var mi = new MenuItem { Header = label, InputGestureText = hint ?? "", Tag = _edit.FontFamily == stack ? "" : "" };
            mi.Click += (_, _) =>
            {
                _edit.FontFamily = stack;
                UpdateFontRow();
                UpdateThemes();
                Changed();
            };
            menu.Items.Add(mi);
        }
        Item("Automatic", BellboardConfig.DefaultFontStack, "Windows Terminal's");
        Item(Bundled, $"'{Bundled}', monospace", "bundled");
        menu.Items.Add(new Separator());
        foreach (var f in _monoFonts.Where(f => !f.Equals(Bundled, StringComparison.OrdinalIgnoreCase)))
            Item(f, $"'{f}', monospace");
        menu.MaxHeight = 420;
        menu.IsOpen = true;
    }

    /// <summary>Installed fixed-pitch families ('i' as wide as 'W').</summary>
    private static List<string> MonospaceFonts()
    {
        var list = new List<string>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                var name = family.Source;
                if (name.StartsWith("Global ", StringComparison.Ordinal)) continue;
                var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (!face.TryGetGlyphTypeface(out var g)) continue;
                if (!g.CharacterToGlyphMap.TryGetValue('i', out var i) || !g.CharacterToGlyphMap.TryGetValue('W', out var w)) continue;
                if (Math.Abs(g.AdvanceWidths[i] - g.AdvanceWidths[w]) < 0.0001) list.Add(name);
            }
            catch
            {
                // A broken font shouldn't break Settings.
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- Themes ----

    private Border ThemeCard(ThemePreset theme)
    {
        Brush C(string key, string fallback = "#888888") => Brush(theme.Colors.GetValueOrDefault(key, fallback));
        var sample = new TextBlock { FontFamily = Res<FontFamily>("MonoFont"), FontSize = 11, LineHeight = 18 };
        sample.Inlines.Add(new Run("~/api ") { Foreground = C("blue") });
        sample.Inlines.Add(new Run("main") { Foreground = C("green") });
        sample.Inlines.Add(new LineBreak());
        sample.Inlines.Add(new Run("$ ") { Foreground = C("brightBlack") });
        sample.Inlines.Add(new Run("npm test") { Foreground = C("foreground") });
        var swatches = new UniformGrid { Rows = 1, Margin = new Thickness(0, 8, 0, 0), Height = 5 };
        foreach (var key in new[] { "red", "green", "yellow", "blue", "magenta", "cyan" })
            swatches.Children.Add(new Border { Background = C(key), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 3, 0) });
        var inner = new StackPanel { Margin = new Thickness(12, 11, 12, 10) };
        inner.Children.Add(sample);
        inner.Children.Add(swatches);
        var top = new Border { Background = C("background", "#14161b"), CornerRadius = new CornerRadius(11, 11, 0, 0), Child = inner };
        var check = new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 12, Foreground = Res<Brush>("Accent"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
        var name = new DockPanel { Margin = new Thickness(12, 9, 12, 9) };
        DockPanel.SetDock(check, Dock.Right);
        name.Children.Add(check);
        name.Children.Add(new TextBlock { Text = theme.Name, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = Res<Brush>("FgStrong"), TextTrimming = TextTrimming.CharacterEllipsis });
        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(name);
        var card = new Border
        {
            Width = 190,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Background = Res<Brush>("InputBg"),
            Child = body,
            ClipToBounds = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = check,
            ToolTip = theme.Source,
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            _edit.Theme = new Dictionary<string, string>(theme.Colors);
            _edit.ThemeName = theme.Name;
            UpdateThemes();
            Changed();
        };
        _themeCards.Add((card, theme));
        return card;
    }

    private void UpdateThemes()
    {
        var current = _themeCards.FirstOrDefault(c => c.Theme.Name == _edit.ThemeName).Theme
                      ?? _themeCards.FirstOrDefault(c => c.Theme.Colors.GetValueOrDefault("background", "").Equals(_edit.Theme.GetValueOrDefault("background"), StringComparison.OrdinalIgnoreCase)).Theme;
        foreach (var (card, theme) in _themeCards)
        {
            var on = theme == current;
            card.BorderBrush = Res<Brush>(on ? "Accent" : "Line2");
            ((TextBlock)card.Tag).Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }

        Brush C(string key, string fallback = "#888888") => Brush(_edit.Theme.GetValueOrDefault(key, fallback));
        _previewBox.Background = C("background", "#14161b");
        _previewName.Foreground = C("brightBlack");
        _previewName.Text = $"{_edit.ThemeName ?? "Custom"} · {_fontName.Text} {_edit.FontSize:0}px";
        _previewText.Foreground = C("foreground");
        _previewText.Inlines.Clear();
        _previewText.Inlines.Add(new Run("~/projects/api ") { Foreground = C("blue") });
        _previewText.Inlines.Add(new Run("main") { Foreground = C("green") });
        _previewText.Inlines.Add(new Run(" ✓") { Foreground = C("green") });
        _previewText.Inlines.Add(new LineBreak());
        _previewText.Inlines.Add(new Run("$ ") { Foreground = C("brightBlack") });
        _previewText.Inlines.Add(new Run("npm test") { Foreground = C("yellow") });
        _previewText.Inlines.Add(new LineBreak());
        _previewText.Inlines.Add(new Run("  ✓ 142 passing ") { Foreground = C("green") });
        _previewText.Inlines.Add(new Run("✗ 1 failing") { Foreground = C("red") });
        _previewText.Inlines.Add(new LineBreak());
        _previewText.Inlines.Add(new Run("  ") );
        _previewText.Inlines.Add(new Run("src/server.ts") { Foreground = C("cyan") });
        _previewText.Inlines.Add(new Run(":42 ") { Foreground = C("brightBlack") });
        _previewText.Inlines.Add(new Run("expected 200, got 500") { Foreground = C("magenta") });
    }

    private static SolidColorBrush Brush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Colors.Gray); }
    }

    // ---- Shells ----

    private Border ShellOption(string? alias, string badgeText, string name, string desc, string tag, Brush badgeFg, Brush badgeBg)
    {
        var badge = new Border { Style = Res<Style>("KindBadge"), Background = badgeBg, Child = new TextBlock { Text = badgeText, Foreground = badgeFg, Style = Res<Style>("KindBadgeText") } };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Res<Brush>("FgStrong") });
        if (tag.Length > 0)
            title.Children.Add(new Border
            {
                Background = Res<Brush>("Bg3"),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(7, 2, 7, 3),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = tag, FontSize = 10.5, Foreground = Res<Brush>("Muted") },
            });
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = desc, FontFamily = Res<FontFamily>("MonoFont"), FontSize = 11.5, Foreground = Res<Brush>("Faint"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        var row = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(text);
        var border = new Border
        {
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = row,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            _edit.Shell = alias;
            UpdateShells();
            Changed();
        };
        _shellRows.Add((border, alias));
        return border;
    }

    private void UpdateShells()
    {
        foreach (var (row, alias) in _shellRows)
        {
            var on = string.Equals(alias ?? "", _edit.Shell ?? "", StringComparison.OrdinalIgnoreCase);
            row.BorderBrush = Res<Brush>(on ? "Accent" : "Divider");
            row.Background = Res<Brush>(on ? "AccentTint" : "InputBg");
        }
    }

    // ---- Live preview, navigation, search ----

    private void Changed()
    {
        _dirty = true;
        Dirty.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = true;
        if (_previewName != null && _themeCards.Count > 0) UpdateThemes();
        _preview.Stop();
        _preview.Start();
    }

    private void ScrollTo(FrameworkElement section)
    {
        try
        {
            var y = section.TransformToAncestor(Sections).Transform(new Point(0, 0)).Y;
            Scroller.ScrollToVerticalOffset(Math.Max(0, y - 6));
        }
        catch (InvalidOperationException)
        {
            // Not laid out yet.
        }
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var visible = _sections.Where(s => s.Section.Visibility == Visibility.Visible).ToList();
        if (visible.Count == 0) return;
        var current = visible[0];
        foreach (var s in visible)
        {
            try
            {
                if (s.Section.TransformToAncestor(Sections).Transform(new Point(0, 0)).Y <= Scroller.VerticalOffset + 40) current = s;
            }
            catch (InvalidOperationException) { }
        }
        // At the very bottom, the last section counts as current even if it's short.
        if (Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1 && Scroller.ScrollableHeight > 0) current = visible[^1];
        if (current.Nav.IsChecked == true) return;
        _scrolling = true;
        current.Nav.IsChecked = true;
        _scrolling = false;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (section, _, title) in _sections.Select(s => (s.Section, s.Nav, s.Title)))
        {
            var titleHit = q.Length == 0 || title.Contains(q, StringComparison.OrdinalIgnoreCase);
            var any = false;
            foreach (var (row, owner, text) in _rows.Where(r => r.Section == section))
            {
                var hit = titleHit || text.Contains(q, StringComparison.OrdinalIgnoreCase);
                row.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                any |= hit;
            }
            section.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        Scroller.ScrollToTop();
    }

    private void Search_Focus(object sender, KeyboardFocusChangedEventArgs e) =>
        SearchFrame.BorderBrush = Res<Brush>(SearchBox.IsKeyboardFocused ? "Accent" : "Divider");

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BellboardConfig.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open config", ex); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _preview.Stop();
            _edit.Save();
            _mux.ApplyConfig(_edit);
            _mux.Window.UpdateChrome();
            _saved = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("saving settings", ex);
            MessageBox.Show(this, "Couldn't save settings: " + ex.Message, "Bellboard");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
