using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Bellboard.Core;
using Bellboard.Panes;

namespace Bellboard.Services;

public static class GitInfo
{
    /// <summary>Branch for a directory by reading .git/HEAD (no git.exe spawn). Handles worktrees and submodules.</summary>
    public static string? Branch(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || dir.StartsWith('/')) return null;
        try
        {
            var d = new DirectoryInfo(dir);
            while (d != null)
            {
                var git = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(git)) return ReadHead(Path.Combine(git, "HEAD"));
                if (File.Exists(git))
                {
                    var content = File.ReadAllText(git).Trim();
                    if (content.StartsWith("gitdir:"))
                    {
                        var gd = content[7..].Trim();
                        if (!Path.IsPathRooted(gd)) gd = Path.GetFullPath(Path.Combine(d.FullName, gd));
                        return ReadHead(Path.Combine(gd, "HEAD"));
                    }
                }
                d = d.Parent;
            }
        }
        catch { }
        return null;
    }

    private static string? ReadHead(string head)
    {
        if (!File.Exists(head)) return null;
        var s = File.ReadAllText(head).Trim();
        if (s.StartsWith("ref:")) return s[4..].Trim().Replace("refs/heads/", "");
        return s.Length >= 7 ? s[..7] : s;
    }
}

/// <summary>
/// Every few seconds: which TCP ports are being listened on by each pane's process tree,
/// and which git branch each pane's cwd is on. Work happens off the UI thread.
/// </summary>
public sealed class Monitors : IDisposable
{
    private readonly MuxController _mux;
    private readonly Dispatcher _ui;
    private readonly Timer _timer;
    private int _busy;

    public Monitors(MuxController mux, Dispatcher ui)
    {
        _mux = mux;
        _ui = ui;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    public void Poke() => _timer.Change(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            List<(TerminalPane Pane, int Pid, string? Cwd)> shells;
            try
            {
                shells = _ui.Invoke(() => _mux.Workspaces.SelectMany(w => w.Panes).OfType<TerminalPane>()
                    .Where(t => t.ShellPid != null).Select(t => (t, t.ShellPid!.Value, t.Cwd)).ToList());
            }
            catch (TaskCanceledException) { return; }
            if (shells.Count == 0) return;

            var children = new Dictionary<int, List<int>>();
            foreach (var p in Win32.Processes())
            {
                if (p.Pid == p.ParentPid) continue;
                if (!children.TryGetValue(p.ParentPid, out var l)) children[p.ParentPid] = l = new List<int>();
                l.Add(p.Pid);
            }
            var listeners = Win32.ListeningPortsByPid();

            var ports = new Dictionary<TerminalPane, int[]>();
            var branches = new Dictionary<TerminalPane, string?>();
            foreach (var (pane, pid, cwd) in shells)
            {
                var found = new SortedSet<int>();
                var stack = new Stack<int>();
                var seen = new HashSet<int>();
                stack.Push(pid);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    if (!seen.Add(cur) || seen.Count > 2000) continue;
                    if (listeners.TryGetValue(cur, out var set)) found.UnionWith(set);
                    if (children.TryGetValue(cur, out var kids)) foreach (var k in kids) stack.Push(k);
                }
                ports[pane] = found.ToArray();
                branches[pane] = GitInfo.Branch(cwd);
            }

            _ui.InvokeAsync(() =>
            {
                foreach (var (pane, list) in ports)
                {
                    pane.Ports = list;
                    pane.GitBranch = branches[pane];
                }
                foreach (var ws in _mux.Workspaces) ws.Refresh();
            });
        }
        catch (Exception ex)
        {
            Log.Error("monitor tick", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
