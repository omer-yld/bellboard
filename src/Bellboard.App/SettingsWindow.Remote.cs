using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bellboard.Core;
using Bellboard.Services;
using Bellboard.Shared;

namespace Bellboard;

/// <summary>Settings › Remote: turn on the phone view, reach it through Tailscale, pair and manage devices.</summary>
public partial class SettingsWindow
{
    private TextBlock _remoteReach = null!;
    private StackPanel _remoteReachPanel = null!;
    private StackPanel _remoteDevices = null!;
    private Border _pairBox = null!;
    private DispatcherTimer? _pairTimer;

    private void BuildRemote()
    {
        var s = Section("remote", "\uE8EA", "Remote",
            "Answer your agents from your phone: see who needs you, allow or deny, reply, and watch terminals live. " +
            "Nothing listens until you turn it on, and only devices you pair can connect.");

        Row(s, "Remote access", Desc("Runs a small server for the phone view (bellboard-remote.exe)."),
            Toggle(_edit.Remote.Enabled, v => { _edit.Remote.Enabled = v; Later(UpdateRemote); }), "phone mobile remote access server");

        Row(s, "Reach it through", Desc("Tailscale gives your phone a private, encrypted address with a real certificate."),
            Segmented([("Tailscale", "tailscale"), ("My own tunnel", "local"), ("Local network", "lan")], _edit.Remote.Mode,
                v => { _edit.Remote.Mode = v; Later(UpdateRemote); }), "tailscale tunnel lan wifi network");

        _remoteReach = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Res<Brush>("Muted"), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        _remoteReachPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var reach = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        reach.Children.Add(_remoteReach);
        reach.Children.Add(_remoteReachPanel);
        Block(s, reach, "tailscale serve url address certificate fingerprint");

        var pairButton = new Button { Content = "Pair a device…", Style = Res<Style>("PrimaryButton") };
        pairButton.Click += (_, _) => ShowPairing();
        Row(s, "Pair a phone", Desc("Shows a QR code. Scan it with the phone's camera; the code works once, for 2 minutes."), pairButton, "pair qr code phone device add");
        _pairBox = new Border { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 14) };
        Block(s, _pairBox, "pair qr code");

        _remoteDevices = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        Block(s, _remoteDevices, "devices paired revoke level view respond full");

        Row(s, "Permission requests", Desc("When Claude Code asks to run something, ask on the phone instead. Coming back to the desk hands the question back to the terminal."),
            Segmented([("When I'm away", "away"), ("Always", "always"), ("Never", "off")], _edit.Remote.AnswerPrompts, v => _edit.Remote.AnswerPrompts = v),
            "permission allow deny approve claude away");
        Row(s, "Away after", Desc("Without Bellboard in the foreground for this long. A locked screen counts as away at once."),
            Segmented([("2 min", 2), ("5 min", 5), ("15 min", 15)], new[] { 2, 5, 15 }.OrderBy(m => Math.Abs(m - _edit.Remote.AwayMinutes)).First(), v => _edit.Remote.AwayMinutes = v),
            "away idle minutes");
        Row(s, "Push notifications", Desc("Notify paired phones that turned notifications on, when they aren't looking at Bellboard."),
            Toggle(_edit.Remote.Push, v => _edit.Remote.Push = v), "push notification phone");
        Row(s, "Include message text", Desc("Show what the agent asked in the notification. Off: \"Claude Code needs your attention\"."),
            Toggle(_edit.Remote.PushText, v => _edit.Remote.PushText = v), "push privacy text message");

        _mux.Remote.StatusChanged += UpdateRemote;
        Closed += (_, _) =>
        {
            _mux.Remote.StatusChanged -= UpdateRemote;
            _pairTimer?.Stop();
        };
        UpdateRemote();
    }

    /// <summary>After the live preview applied the change (the sidecar starts or stops).</summary>
    private void Later(Action a) => Dispatcher.BeginInvoke(async () =>
    {
        await System.Threading.Tasks.Task.Delay(400);
        a();
    });

    private void UpdateRemote()
    {
        var r = _mux.Remote;
        var cfg = _edit.Remote;
        _remoteReachPanel.Children.Clear();
        if (!cfg.Enabled)
            _remoteReach.Text = "Off. Turn it on, then pair your phone.";
        else if (r.Problem is { } problem)
            _remoteReach.Text = "Problem: " + problem;
        else if (cfg.Mode == "tailscale")
        {
            _remoteReach.Text = cfg.Url is { Length: > 0 } url
                ? $"Your phone opens {url} (on your tailnet only)."
                : $"Not published yet. Install Tailscale on this PC and your phone, sign in to both, then set it up here (runs `{Tailscale.ServeCommand(cfg.Port)}`).";
            var setup = new Button { Content = cfg.Url is { Length: > 0 } ? "Set up again" : "Set up Tailscale", Style = Res<Style>("DialogButton") };
            setup.Click += async (_, _) =>
            {
                setup.IsEnabled = false;
                _remoteReach.Text = "Talking to Tailscale…";
                var st = await Tailscale.GetStatusAsync();
                string? err = !st.Installed ? "Tailscale isn't installed: get it from tailscale.com/download and sign in."
                    : !st.Running || st.Url == null ? (st.Error ?? "Tailscale isn't connected") + ". Sign in, then try again."
                    : await Tailscale.ServeAsync(cfg.Port);
                setup.IsEnabled = true;
                if (err != null)
                {
                    _remoteReach.Text = "Tailscale: " + err + (st.Running ? "\nIf HTTPS certificates are off for your tailnet, turn them on in the Tailscale admin console (DNS › HTTPS Certificates)." : "");
                    return;
                }
                cfg.Url = Tailscale.UrlFor(st.DnsName!, cfg.Port);
                Changed();
                Later(UpdateRemote);
            };
            _remoteReachPanel.Children.Add(setup);
        }
        else if (cfg.Mode == "local")
        {
            _remoteReach.Text = $"Bellboard listens on http://127.0.0.1:{cfg.Port} only. Put your tunnel's https address here (Cloudflare Tunnel, ngrok, a reverse proxy…).";
            var box = new TextBox { Text = cfg.Url ?? "", Style = Res<Style>("FieldBox"), Width = 340 };
            box.LostFocus += (_, _) =>
            {
                var v = box.Text.Trim();
                if (v.Length > 0 && !Uri.TryCreate(v, UriKind.Absolute, out _)) return;
                cfg.Url = v.Length > 0 ? v.TrimEnd('/') : null;
                Changed();
            };
            _remoteReachPanel.Children.Add(box);
        }
        else
        {
            _remoteReach.Text = $"Phones on your network open {r.Url}. The certificate is self-signed: check this fingerprint when the browser warns." +
                                (r.CertificateFingerprint is { } fp ? $"\n{fp}" : "") +
                                "\nWindows Firewall asks once whether bellboard-remote may listen. Notifications on iPhone need Tailscale instead.";
        }
        UpdateDevices();
    }

    private void UpdateDevices()
    {
        var r = _mux.Remote;
        _remoteDevices.Children.Clear();
        foreach (var d in r.Devices.Devices)
        {
            var connected = r.Connected.Contains(d.Id);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(new TextBlock { Text = d.Name, Style = Res<Style>("SettingTitle") });
            if (connected)
                name.Children.Add(new TextBlock { Text = "  ● connected", FontSize = 12, Foreground = Res<Brush>("Green"), VerticalAlignment = VerticalAlignment.Center });
            info.Children.Add(name);
            var seen = d.LastSeen is { } t ? AgentRow.Ago(t) : "never";
            info.Children.Add(new TextBlock
            {
                Text = $"Paired {d.Paired:d} · seen {seen}{(d.LastIp != null ? " from " + d.LastIp : "")}{(d.Push != null ? " · notifications on" : "")}",
                Style = Res<Style>("SettingDesc"),
            });
            var level = SegmentedNow([("View", RemotePolicy.View), ("Respond", RemotePolicy.Respond), ("Full", RemotePolicy.Full)], d.Level, v =>
            {
                r.Devices.SetLevel(d.Id, v);
                RemoteCommandsAudit($"{d.Name} ({d.Id}) is now {v} (Settings)");
            });
            level.ToolTip = "View: look only. Respond: answer agents and type into agent panes. Full: also type anywhere, start and finish agents.";
            var revoke = new Button { Content = "Revoke", Style = Res<Style>("DangerButton"), Margin = new Thickness(8, 0, 0, 0) };
            revoke.Click += (_, _) =>
            {
                if (revoke.Tag == null)
                {
                    revoke.Tag = "armed";
                    revoke.Content = "Revoke — sure?";
                    return;
                }
                r.Devices.Revoke(d.Id);
                RemoteCommandsAudit($"revoked {d.Name} ({d.Id}) (Settings)");
                UpdateDevices();
            };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(level);
            right.Children.Add(revoke);
            var grid = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            grid.Children.Add(right);
            grid.Children.Add(info);
            _remoteDevices.Children.Add(new Border
            {
                BorderBrush = Res<Brush>("Divider"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 12), Child = grid,
            });
        }
        if (r.Devices.Devices.Count == 0)
            _remoteDevices.Children.Add(new TextBlock { Text = "No paired devices yet.", Style = Res<Style>("SettingDesc"), Margin = new Thickness(0, 0, 0, 8) });
    }

    private static void RemoteCommandsAudit(string text) => Ipc.RemoteCommands.Audit(text);

    /// <summary>Like Segmented, but applies at once instead of waiting for Save (device levels aren't settings).</summary>
    private Border SegmentedNow(System.Collections.Generic.IEnumerable<(string Label, string Value)> options, string current, Action<string> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "seg-" + Guid.NewGuid().ToString("N");
        foreach (var (label, value) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = group, Style = Res<Style>("Segment"), IsChecked = value == current };
            rb.Checked += (_, _) => set(value);
            panel.Children.Add(rb);
        }
        return new Border { Style = Res<Style>("SegmentTrack"), Child = panel };
    }

    private void ShowPairing()
    {
        _pairTimer?.Stop();
        var r = _mux.Remote;
        var body = new StackPanel();
        _pairBox.Child = body;
        _pairBox.Visibility = Visibility.Visible;
        if (!_edit.Remote.Enabled || !_mux.Config.Remote.Enabled)
        {
            body.Children.Add(new TextBlock { Text = "Turn on remote access first.", Style = Res<Style>("SettingDesc") });
            return;
        }
        if (!r.Running)
        {
            body.Children.Add(new TextBlock { Text = r.Problem ?? "The remote server is starting; try again in a moment.", Style = Res<Style>("SettingDesc") });
            return;
        }
        if (_edit.Remote.Mode == "tailscale" && string.IsNullOrEmpty(_mux.Config.Remote.Url))
        {
            body.Children.Add(new TextBlock { Text = "Set up Tailscale first, so the phone has an address to open.", Style = Res<Style>("SettingDesc") });
            return;
        }
        var code = r.Devices.CreateCode();
        var url = $"{r.Url}/#pair={code}";
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(10),
            Child = new Image { Source = QrCode.ToBitmap(url, 5), Width = 180, Height = 180, SnapsToDevicePixels = true },
        });
        var text = new StackPanel { Margin = new Thickness(18, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 330 };
        text.Children.Add(new TextBlock { Text = "Scan with the phone's camera", Style = Res<Style>("SettingTitle") });
        text.Children.Add(new TextBlock { Text = $"or open {r.Url} and enter", Style = Res<Style>("SettingDesc"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = code, FontFamily = Res<FontFamily>("MonoFont"), FontSize = 26, FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("Accent"), Margin = new Thickness(0, 8, 0, 8),
        });
        var countdown = new TextBlock { Style = Res<Style>("SettingDesc") };
        text.Children.Add(countdown);
        text.Children.Add(new TextBlock
        {
            Text = "New devices can answer agents (Respond). Change it below once it shows up. On iPhone, add it to the Home Screen for notifications.",
            Style = Res<Style>("SettingDesc"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        row.Children.Add(text);
        body.Children.Add(row);

        var before = r.Devices.Devices.Count;
        _pairTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pairTimer.Tick += (_, _) =>
        {
            if (r.Devices.Devices.Count > before)
            {
                _pairTimer!.Stop();
                body.Children.Clear();
                body.Children.Add(new TextBlock { Text = $"✓ {r.Devices.Devices[^1].Name} is paired.", FontSize = 13, Foreground = Res<Brush>("Green") });
                UpdateDevices();
                return;
            }
            var left = r.Devices.CodeExpires is { } exp ? exp - DateTime.Now : TimeSpan.Zero;
            if (left <= TimeSpan.Zero)
            {
                _pairTimer!.Stop();
                body.Children.Clear();
                body.Children.Add(new TextBlock { Text = "The code expired. Make a new one with Pair a device.", Style = Res<Style>("SettingDesc") });
                return;
            }
            countdown.Text = $"Valid for {left:m\\:ss}";
        };
        _pairTimer.Start();
        countdown.Text = "Valid for 2:00";
    }
}
