using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bellboard.Services;
using Bellboard.Terminal;

namespace Bellboard.Core;

/// <summary>An agent working in its own git worktree (created by `bell agent new`).</summary>
public sealed record AgentTask(string Task, string Agent, string Repo, string Worktree, string Branch, string Base, DateTime Created);

/// <summary>What finishing an agent would involve, shown before the user picks merge / PR / keep / discard.</summary>
public sealed record AgentSummary(int Ahead, string? DiffStat, List<string> Dirty, string RepoBranch, bool RepoDirty, bool GhAvailable, bool HasRemote);

/// <summary>
/// Worktree-per-agent workflow: `agent new` creates branch bellboard/&lt;task&gt; in a sibling
/// "&lt;repo&gt;.worktrees" folder, opens a workspace there and starts the agent with the task.
/// `agent finish` merges, opens a PR, keeps, or discards that work.
/// </summary>
public sealed class AgentManager
{
    private readonly MuxController _mux;

    public AgentManager(MuxController mux) => _mux = mux;

    public static string Slug(string task)
    {
        var s = Regex.Replace(task.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 40)
        {
            var cut = s.LastIndexOf('-', 40);
            s = s[..(cut > 15 ? cut : 40)].Trim('-');
        }
        return s.Length == 0 ? "task" : s;
    }

    /// <summary>The command typed into the agent's shell, with the task safely quoted for that shell.</summary>
    public static string AgentCommand(string agent, string task, bool powershell)
    {
        var oneLine = Regex.Replace(task, @"\s*\r?\n\s*", " ").Trim();
        var quoted = powershell ? "'" + oneLine.Replace("'", "''") + "'" : "'" + oneLine.Replace("'", "'\\''") + "'";
        return agent.ToLowerInvariant() switch
        {
            "claude" => $"claude {quoted}",
            "codex" => $"codex {quoted}",
            "opencode" => $"opencode --prompt {quoted}",
            "pi" => $"pi -- {quoted}", // -- so a task that starts with "-" isn't read as an option
            _ => agent, // a custom command; it can read the task from BELLBOARD_AGENT_TASK
        };
    }

    /// <summary>How an agent kind is named on cards, in the inbox and in notifications.</summary>
    public static string DisplayName(string kind) => kind.ToLowerInvariant() switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "opencode" => "OpenCode",
        "pi" => "Pi",
        // A run that AI Buddy started and reports into a feed pane: "buddy:claude" → "Claude Code · Buddy".
        var k when k.StartsWith("buddy:") && k.Length > 6 => DisplayName(kind[6..]) + " · Buddy",
        // A custom command line ("aider --message …") is not a name.
        _ when kind.IndexOfAny([' ', '/', '\\']) >= 0 => "Custom agent",
        _ => kind,
    };

    /// <summary>Command that resumes an agent's conversation in a restored pane, or null if unknown.</summary>
    public static string? ResumeCommand(string kind, string? sessionId)
    {
        bool validId = sessionId != null && Regex.IsMatch(sessionId, "^[A-Za-z0-9-]{8,80}$");
        return kind.ToLowerInvariant() switch
        {
            "claude" => validId ? $"claude --resume {sessionId}" : "claude --continue",
            "codex" => validId ? $"codex resume {sessionId}" : "codex resume --last",
            "opencode" => validId ? $"opencode --session {sessionId}" : "opencode --continue",
            "pi" => validId ? $"pi --session {sessionId}" : "pi --continue",
            _ => null,
        };
    }

    /// <summary>
    /// How to run `program` for the repo at `dir`: on Windows for a Windows folder, and inside the distro for a
    /// \\wsl.localhost\... folder, so a repo in WSL gets Linux git (and Linux paths in its worktrees).
    /// </summary>
    internal static ProcessStartInfo ToolStart(string program, string dir, IEnumerable<string> args)
    {
        ProcessStartInfo psi;
        if (ShellResolver.ParseWslPath(dir) is { } wsl)
        {
            psi = new ProcessStartInfo("wsl.exe") { WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            foreach (var a in new[] { "-d", wsl.Distro, "--cd", wsl.LinuxPath, "--exec", program }) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(program) { WorkingDirectory = dir };
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        return psi;
    }

    /// <summary>A path as the repo's own tools see it: the Linux path for a repo inside WSL.</summary>
    internal static string ToolPath(string path) => ShellResolver.ParseWslPath(path) is { } wsl ? wsl.LinuxPath : path;

    /// <summary>Where an agent's worktrees go: "&lt;repo&gt;.worktrees" next to the repo.</summary>
    public static string WorktreeContainer(string root) =>
        Path.Combine(Path.GetDirectoryName(root) ?? root, Path.GetFileName(root.TrimEnd('\\', '/')) + ".worktrees");

    /// <summary>The repo root (as Windows sees it) and checked-out branch for a folder, or null if it isn't in a git repo.</summary>
    public static async Task<(string Root, string Head)?> InspectRepoAsync(string dir)
    {
        try
        {
            var (code, top, _) = await WithTimeout(Git(dir, "rev-parse", "--show-toplevel"), 15000);
            if (code != 0 || top.Length == 0) return null;
            var root = ShellResolver.ParseWslPath(dir) is { } w ? ShellResolver.WslUncPath(w.Distro, top) : top.Replace('/', '\\');
            var head = (await WithTimeout(Git(root, "rev-parse", "--abbrev-ref", "HEAD"), 15000)).Out;
            return (root, head.Length > 0 ? head : "HEAD");
        }
        catch
        {
            return null;
        }
    }

    public static readonly string[] KnownAgents = ["claude", "codex", "opencode", "pi"];

    /// <summary>
    /// Which of the known agent CLIs can be started where an agent for `dir` runs. Inside WSL it asks the user's
    /// login shell, so tools installed under ~/.local/bin, nvm or npm-global are found.
    /// </summary>
    public static async Task<HashSet<string>> InstalledAgentsAsync(string dir)
    {
        var found = new HashSet<string>();
        if (ShellResolver.ParseWslPath(dir) != null)
        {
            const string probe = "for c in claude codex opencode pi; do command -v \"$c\" >/dev/null 2>&1 && echo \"bellboard-has:$c\"; done";
            var (_, output, _) = await WithTimeout(Run("sh", dir, "-c", $"exec \"${{SHELL:-/bin/sh}}\" -lic '{probe}'"), 20000);
            foreach (var line in output.Split('\n'))
                if (line.Trim().StartsWith("bellboard-has:")) found.Add(line.Trim()["bellboard-has:".Length..]);
            return found;
        }
        foreach (var name in KnownAgents)
            if (new[] { ".exe", ".cmd", ".bat", ".ps1" }.Any(ext => ShellResolver.FindOnPath(name + ext) != null)) found.Add(name);
        return found;
    }

    private static async Task<(int Code, string Out, string Err)> WithTimeout(Task<(int Code, string Out, string Err)> task, int ms) =>
        await Task.WhenAny(task, Task.Delay(ms)) == task ? await task : (-1, "", "timed out");

    /// <summary>"WSL · Ubuntu" or "Windows": where an agent for the repo at `dir` runs.</summary>
    public static string WhereItRuns(string dir) => ShellResolver.ParseWslPath(dir) is { } wsl ? "WSL · " + wsl.Distro : "Windows";

    public static async Task<(int Code, string Out, string Err)> Run(string program, string dir, params string[] args)
    {
        Process p;
        try
        {
            p = Process.Start(ToolStart(program, dir, args)) ?? throw new InvalidOperationException($"couldn't start {program}");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException($"{program} isn't installed or isn't on PATH");
        }
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, (await stdout).Trim(), (await stderr).Trim());
        }
    }

    public static Task<(int Code, string Out, string Err)> Git(string dir, params string[] args) => Run("git", dir, args);

    private static async Task<string> GitOk(string dir, params string[] args)
    {
        var (code, output, err) = await Git(dir, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {(err.Length > 0 ? err : output)}");
        return output;
    }

    /// <param name="from">The pane the request came from (default: the focused one); its folder is the repo unless repoDir is given.</param>
    /// <param name="branchName">The new branch; default bellboard/&lt;task slug&gt;.</param>
    public async Task<Workspace> NewAsync(string task, string? agent, string? repoDir, string? baseBranch, string? name, string? shell, bool focus,
        Panes.TerminalPane? from = null, string? branchName = null)
    {
        if (string.IsNullOrWhiteSpace(task)) throw new InvalidOperationException("describe the task, e.g. bell agent new \"fix the login redirect\"");
        from ??= _mux.Selected?.FocusedPane as Panes.TerminalPane;
        if (repoDir == null && from?.Remote is { } host)
            throw new InvalidOperationException($"this pane is connected to {host} over SSH; agents run on this PC or in WSL, so open the repo in a Windows or WSL tab");
        repoDir ??= from?.Cwd ?? _mux.Selected?.Cwd
                    ?? throw new InvalidOperationException("no folder: run it inside a repo or pass --repo");
        // A repo inside WSL is handled by the distro's own git; Bellboard keeps the \\wsl.localhost\... form.
        var wsl = ShellResolver.ParseWslPath(repoDir);
        var top = await GitOk(repoDir, "rev-parse", "--show-toplevel");
        var root = wsl is { } w ? ShellResolver.WslUncPath(w.Distro, top) : top.Replace('/', '\\');
        var @base = baseBranch ?? await GitOk(root, "rev-parse", "--abbrev-ref", "HEAD");
        if (@base == "HEAD") @base = await GitOk(root, "rev-parse", "HEAD"); // detached

        var custom = string.IsNullOrWhiteSpace(branchName) ? null : branchName.Trim();
        if (custom != null)
        {
            await GitOk(root, "check-ref-format", "--branch", custom);
            if ((await Git(root, "rev-parse", "--verify", "--quiet", "refs/heads/" + custom)).Code == 0)
                throw new InvalidOperationException($"branch '{custom}' already exists; pick another name");
        }
        var slug = custom != null ? Slug(custom.Split('/')[^1]) : Slug(task);
        var container = WorktreeContainer(root);
        string branch = "", dir = "";
        for (int n = 1; n < 100; n++)
        {
            var suffix = n == 1 ? "" : "-" + n;
            branch = custom ?? $"bellboard/{slug}{suffix}";
            dir = Path.Combine(container, slug + suffix);
            var exists = custom == null && (await Git(root, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch)).Code == 0;
            if (!exists && !Directory.Exists(dir)) break;
        }
        Directory.CreateDirectory(container);
        await GitOk(root, "worktree", "add", "-b", branch, ToolPath(dir), @base);
        Log.Info($"agent worktree {dir} on {branch} (from {@base})");

        var agentKind = string.IsNullOrWhiteSpace(agent) ? "claude" : agent!;
        var info = new AgentTask(task.Trim(), agentKind, root, dir, branch, @base, DateTime.Now);
        // The agent runs where the repo is: in the distro's shell for a WSL repo, else the chosen/default shell.
        shell ??= wsl is { } d ? "wsl:" + d.Distro : null;
        var isPwsh = ShellResolver.Resolve(shell ?? _mux.Config.Shell, null).Kind is ShellKind.Pwsh or ShellKind.WindowsPowerShell;
        var ws = _mux.NewWorkspace(name ?? Title(task), dir, AgentCommand(agentKind, task, isPwsh), focus, shell,
            extraEnv: new Dictionary<string, string> { ["BELLBOARD_AGENT_TASK"] = info.Task, ["BELLBOARD_AGENT_BRANCH"] = branch });
        ws.AgentTask = info;
        ws.Refresh();
        return ws;
    }

    private static string Title(string task)
    {
        var t = Regex.Replace(task.Trim(), @"\s+", " ");
        return t.Length <= 32 ? t : t[..31].TrimEnd() + "…";
    }

    public async Task<AgentSummary> InspectAsync(AgentTask t)
    {
        var dirty = Directory.Exists(t.Worktree)
            ? (await Git(t.Worktree, "status", "--porcelain")).Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList()
            : new List<string>();
        int.TryParse((await Git(t.Repo, "rev-list", "--count", $"{t.Base}..{t.Branch}")).Out, out var ahead);
        var stat = (await Git(t.Repo, "diff", "--shortstat", $"{t.Base}...{t.Branch}")).Out;
        var repoBranch = (await Git(t.Repo, "rev-parse", "--abbrev-ref", "HEAD")).Out;
        var repoDirty = (await Git(t.Repo, "status", "--porcelain", "--untracked-files=no")).Out.Length > 0;
        var hasRemote = (await Git(t.Repo, "remote")).Out.Split('\n').Any(r => r.Trim() == "origin");
        var gh = ShellResolver.ParseWslPath(t.Repo) != null
            ? (await Run("sh", t.Repo, "-c", "command -v gh")).Code == 0
            : ShellResolver.FindOnPath("gh.exe") != null;
        return new AgentSummary(ahead, stat.Length > 0 ? stat : null, dirty, repoBranch, repoDirty, gh, hasRemote);
    }

    /// <summary>mode: merge | pr | keep | discard. Returns a human summary of what happened.</summary>
    public async Task<string> FinishAsync(Workspace ws, string mode, bool force)
    {
        var t = ws.AgentTask ?? throw new InvalidOperationException($"{ws.Name} isn't an agent workspace");
        var s = await InspectAsync(t);
        switch (mode)
        {
            case "merge":
            {
                if (s.Dirty.Count > 0 && !force)
                    throw new InvalidOperationException($"the agent's worktree has {s.Dirty.Count} uncommitted change(s); ask it to commit, or use --force to merge only what's committed");
                if (s.RepoBranch != t.Base)
                    throw new InvalidOperationException($"{t.Repo} has '{s.RepoBranch}' checked out; check out '{t.Base}' there to merge into it");
                if (s.RepoDirty)
                    throw new InvalidOperationException($"{t.Repo} has uncommitted changes; commit or stash them before merging");
                if (s.Ahead == 0)
                {
                    await CloseAndRemoveAsync(ws, t, deleteBranch: true, forceRemove: force);
                    return $"nothing to merge ({t.Branch} has no commits); removed its worktree";
                }
                var (code, output, err) = await Git(t.Repo, "merge", "--no-ff", "-m", $"Merge {t.Branch}: {t.Task}", t.Branch);
                if (code != 0)
                {
                    await Git(t.Repo, "merge", "--abort");
                    throw new InvalidOperationException($"merge failed and was aborted: {(err.Length > 0 ? err : output)}");
                }
                await CloseAndRemoveAsync(ws, t, deleteBranch: true, forceRemove: force);
                return $"merged {s.Ahead} commit(s) from {t.Branch} into {t.Base}";
            }
            case "pr":
            {
                if (!s.GhAvailable) throw new InvalidOperationException("GitHub CLI (gh) isn't installed; install it or push the branch yourself");
                if (!s.HasRemote) throw new InvalidOperationException("the repo has no 'origin' remote");
                if (s.Dirty.Count > 0 && !force) throw new InvalidOperationException($"the agent's worktree has {s.Dirty.Count} uncommitted change(s); commit them first");
                await GitOk(t.Worktree, "push", "-u", "origin", t.Branch);
                var (ghCode, url, err) = await Run("gh", t.Worktree, "pr", "create", "--fill", "--base", t.Base, "--head", t.Branch);
                if (ghCode != 0) throw new InvalidOperationException("gh pr create failed: " + err);
                var i = ws.Status.FindIndex(x => x.Key == "pr");
                var pill = new StatusPill("pr", "PR open", "purple");
                if (i >= 0) ws.Status[i] = pill; else ws.Status.Add(pill);
                ws.Logs.Add(new LogEntry(DateTime.Now, "success", "PR: " + url));
                ws.Refresh();
                return "opened " + url;
            }
            case "keep":
                _mux.CloseWorkspace(ws, quitIfLast: false);
                return $"closed the workspace; {t.Branch} and {t.Worktree} are kept";
            case "discard":
                if ((s.Ahead > 0 || s.Dirty.Count > 0) && !force)
                    throw new InvalidOperationException($"{t.Branch} has {s.Ahead} commit(s) and {s.Dirty.Count} uncommitted change(s); use --force to throw them away");
                return await DiscardAsync(ws, t, $"discarded {t.Branch} and its worktree");
            default:
                throw new InvalidOperationException("finish mode must be merge | pr | keep | discard");
        }
    }

    private async Task<string> DiscardAsync(Workspace ws, AgentTask t, string message)
    {
        await CloseAndRemoveAsync(ws, t, deleteBranch: true, forceRemove: true, forceDeleteBranch: true);
        return message;
    }

    private async Task CloseAndRemoveAsync(Workspace ws, AgentTask t, bool deleteBranch, bool forceRemove, bool forceDeleteBranch = false)
    {
        // The panes' shells live in the worktree; close them first so Windows releases the folder.
        if (_mux.Workspaces.Contains(ws)) _mux.CloseWorkspace(ws, quitIfLast: false, remember: false);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(400);
            var args = forceRemove ? new[] { "worktree", "remove", "--force", ToolPath(t.Worktree) } : new[] { "worktree", "remove", ToolPath(t.Worktree) };
            var (code, _, err) = await Git(t.Repo, args);
            if (code == 0 || !Directory.Exists(t.Worktree)) break;
            if (attempt == 9) throw new InvalidOperationException("couldn't remove the worktree: " + err);
        }
        await Git(t.Repo, "worktree", "prune");
        if (deleteBranch) await Git(t.Repo, "branch", forceDeleteBranch ? "-D" : "-d", t.Branch);
    }
}
