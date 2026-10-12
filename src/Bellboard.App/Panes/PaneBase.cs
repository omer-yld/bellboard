using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Bellboard.Core;

namespace Bellboard.Panes;

/// <summary>A terminal or browser. Panes live as tabs in a PaneStack, which is the split tree's leaf.</summary>
public abstract class PaneBase : IDisposable
{
    public static readonly Color AccentColor = Color.FromRgb(0xF5, 0xA5, 0x24);
    /// <summary>Agents at work, links: the secondary blue.</summary>
    public static readonly Color BlueColor = Color.FromRgb(0x72, 0xB4, 0xFF);

    public string Id { get; }
    public Workspace Workspace { get; set; }
    /// <summary>The stack (tab group) holding this pane.</summary>
    public PaneStack Stack { get; set; } = null!;
    public abstract string Kind { get; }
    /// <summary>The pane's content; the stack around it draws the focus/notification ring and tab strip.</summary>
    public Border Host { get; }
    public WebView2 Web { get; protected set; }
    protected MuxController Mux { get; }

    public string? Title { get; set; }
    /// <summary>Pinned tabs sit first in their tab row, always visible, and don't close until unpinned.</summary>
    public bool Pinned { get; set; }
    public string? Cwd { get; set; }
    public string? GitBranch { get; set; }
    public int[] Ports { get; set; } = [];
    protected bool Disposed { get; private set; }

    private bool _attention, _focused;

    protected PaneBase(MuxController mux, Workspace ws, string id, WebView2? web = null)
    {
        Mux = mux;
        Workspace = ws;
        Id = id;
        Web = web ?? new WebView2
        {
            DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(mux.Config.Theme["background"]),
        };
        Host = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(mux.Config.Theme["background"])),
            Tag = this,
        };
        Host.PreviewMouseDown += (_, _) => Mux.OnPaneFocused(this);
    }

    /// <summary>Title shown in the sidebar/notifications; ignores titles that are just an exe path.</summary>
    public virtual string? DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) || Title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Title.Contains('\\')
            ? null
            : Title.Trim();

    /// <summary>Short label for the tab strip.</summary>
    public virtual string TabLabel =>
        DisplayTitle ?? (Cwd is { Length: > 0 } c ? System.IO.Path.GetFileName(c.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : c : Kind);

    public bool Attention
    {
        get => _attention;
        set
        {
            if (_attention == value) return;
            _attention = value;
            UpdateRing();
            Workspace.Refresh();
        }
    }

    public bool IsFocused
    {
        get => _focused;
        set
        {
            if (_focused == value) return;
            _focused = value;
            UpdateRing();
        }
    }

    public void UpdateRing() => Stack?.Refresh();

    public abstract Task InitializeAsync();

    /// <summary>Move keyboard focus into the pane's content.</summary>
    public virtual void FocusContent()
    {
        try { Web.Focus(); } catch { }
    }

    public async Task<byte[]> CapturePngAsync()
    {
        if (this is TerminalPane t) await t.EnsureLoadedAsync();
        if (Web.CoreWebView2 == null) throw new InvalidOperationException("pane is not ready");
        using var ms = new MemoryStream();
        await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
    }

    public virtual Dictionary<string, object?> Describe() => new()
    {
        ["id"] = Id,
        ["kind"] = Kind,
        ["title"] = DisplayTitle,
        ["cwd"] = Cwd,
        ["focused"] = Workspace.FocusedPane == this,
        ["tab"] = Stack != null && Stack.Tabs.Count > 1 ? $"{Stack.Tabs.IndexOf(this) + 1}/{Stack.Tabs.Count}" : null,
        ["active"] = Stack?.Active == this,
        ["attention"] = _attention,
        ["pinned"] = Pinned,
    };

    public virtual void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        try { Web.Dispose(); } catch { }
    }
}
