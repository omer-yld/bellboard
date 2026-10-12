using System;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Bellboard.Core;
using Bellboard.Panes;
using Bellboard.Terminal;

namespace Bellboard.Services;

/// <summary>A shell started before anyone asked for it. Output is buffered until a pane adopts it.</summary>
public sealed class PrewarmedShell
{
    public required string WorkspaceId { get; init; }
    public required string PaneId { get; init; }
    public required string Cwd { get; init; }
    public required string? ShellKey { get; init; }
    public required ShellSpec Spec { get; init; }
    public required PtySession Pty { get; init; }
    public required int Cols { get; init; }
    public required int Rows { get; init; }
    public DateTime Started { get; } = DateTime.UtcNow;

    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private Action<string>? _output;
    private Action<int>? _exited;
    private int? _exitCode;

    internal void Attach()
    {
        Pty.Output += text =>
        {
            Action<string>? target;
            lock (_gate)
            {
                target = _output;
                if (target == null && _buffer.Length < 256 * 1024) _buffer.Append(text);
            }
            target?.Invoke(text);
        };
        Pty.Exited += code =>
        {
            Action<int>? target;
            lock (_gate)
            {
                _exitCode = code;
                target = _exited;
            }
            target?.Invoke(code);
        };
    }

    /// <summary>Hand the shell to a pane: buffered output is replayed first, in order, then output flows directly.</summary>
    public void Adopt(Action<string> output, Action<int> exited)
    {
        lock (_gate)
        {
            if (_buffer.Length > 0) output(_buffer.ToString());
            _buffer.Clear();
            _output = output;
            _exited = exited;
            if (_exitCode is { } code) exited(code);
        }
    }

    public bool IsAlive => _exitCode == null;
}

/// <summary>
/// Keeps one shell started in the folder a new workspace would open in, so the next workspace
/// shows a ready prompt instantly (PowerShell 5.1 takes ~0.75 s to start). Only plain new
/// workspaces use it; anything with a command, custom environment or different folder starts normally.
/// </summary>
public sealed class ShellPool : IDisposable
{
    private readonly MuxController _mux;
    private PrewarmedShell? _ready;

    public ShellPool(MuxController mux) => _mux = mux;

    public void Warm()
    {
        if (!_mux.Config.PrestartShell || (_ready != null && _ready.IsAlive)) return;
        Discard();
        var cwd = _mux.DefaultNewWorkspaceDir() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(cwd)) return;
        try
        {
            var (wsId, paneId) = _mux.ReserveIds();
            var shellKey = _mux.Config.Shell;
            var (spec, env, processCwd) = TerminalPane.PrepareLaunch(_mux, paneId, wsId, shellKey, null, cwd, null);
            if (spec.TypedCommand != null) return;
            var host = _mux.Window.ContentHost;
            var (cols, rows) = TerminalPane.SizeFor(host.ActualWidth, host.ActualHeight, _mux.Config.FontSize, _mux.Config.LineHeight);
            var pty = PtySession.Start(spec.CommandLine, processCwd, env, (short)cols, (short)rows, _mux.Config.Conpty != "system");
            var shell = new PrewarmedShell
            {
                WorkspaceId = wsId, PaneId = paneId, Cwd = cwd, ShellKey = shellKey, Spec = spec, Pty = pty, Cols = cols, Rows = rows,
            };
            shell.Attach();
            _ready = shell;
            Log.Info($"pre-started a shell for the next workspace in {cwd} (pid {pty.ProcessId})");
        }
        catch (Exception ex)
        {
            Log.Error("pre-starting a shell failed", ex);
        }
    }

    /// <summary>The pre-started shell if it fits a new workspace in this folder with the default shell; otherwise null.</summary>
    public PrewarmedShell? Take(string? cwd, string? shell, string? command, bool hasExtraEnv)
    {
        var ready = _ready;
        if (ready == null) return null;
        var wantCwd = cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool fits = ready.IsAlive && command == null && !hasExtraEnv && (shell ?? _mux.Config.Shell) == ready.ShellKey &&
                    string.Equals(Path.GetFullPath(wantCwd).TrimEnd('\\'), Path.GetFullPath(ready.Cwd).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (!fits) return null;
        _ready = null;
        return ready;
    }

    /// <summary>Refill after a take or when the likely folder changed.</summary>
    public void Refill() =>
        _mux.Window.Dispatcher.InvokeAsync(() =>
        {
            var want = _mux.DefaultNewWorkspaceDir() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (_ready != null && !string.Equals(_ready.Cwd, want, StringComparison.OrdinalIgnoreCase)) Discard();
            Warm();
        }, DispatcherPriority.ApplicationIdle);

    private void Discard()
    {
        _ready?.Pty.Dispose();
        _ready = null;
    }

    public System.Threading.Tasks.Task CloseAsync()
    {
        var task = _ready?.Pty.CloseAsync() ?? System.Threading.Tasks.Task.CompletedTask;
        _ready = null;
        return task;
    }

    public void Dispose() => Discard();
}
