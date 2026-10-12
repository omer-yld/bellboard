using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Bellboard.Core;
using Bellboard.Terminal;
using Xunit;

namespace Bellboard.Tests;

public class UrlToolsTests
{
    private const string Start = "https://bellboard.example/start.html";

    [Theory]
    [InlineData("", Start)]
    [InlineData("  start ", Start)]
    [InlineData("localhost:5173", "http://localhost:5173")]
    [InlineData(":3000/api", "http://localhost:3000/api")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("10.0.0.5", "http://10.0.0.5")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("docs.github.com/en", "https://docs.github.com/en")]
    [InlineData("https://x.dev/a?b=1", "https://x.dev/a?b=1")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("how to exit vim", "https://www.google.com/search?q=how%20to%20exit%20vim")]
    [InlineData("readme.md wont match", "https://www.google.com/search?q=readme.md%20wont%20match")]
    [InlineData("v1.2", "https://www.google.com/search?q=v1.2")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, UrlTools.Normalize(input, Start));

    [Fact]
    public void CustomSearchEngine()
    {
        Assert.Equal("https://duckduckgo.com/?q=a%20b", UrlTools.Normalize("a b", Start, "https://duckduckgo.com/?q=%s"));
        Assert.Equal("https://s.example/?q=x%20y", UrlTools.Search("https://s.example/?q=", "x y"));
    }

    [Theory]
    [InlineData("https://wmux.example/start.html", "https://bellboard.example/start.html")]
    [InlineData("https://WMUX.example/start.html?q=1", "https://bellboard.example/start.html?q=1")]
    [InlineData("https://example.com/wmux.example/", "https://example.com/wmux.example/")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    public void SessionsFromBeforeTheRenameKeepTheirStartPage(string saved, string expected) =>
        Assert.Equal(expected, UrlTools.MigrateLegacy(saved, "bellboard.example"));

    [Fact]
    public void OldStartAliasStillWorks() => Assert.Equal(Start, UrlTools.Normalize("wmux:start", Start));
}

public sealed class LegacyDataTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bellboard-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true); }

    [Fact]
    public void MovesTheOldFolderOnce()
    {
        var from = System.IO.Path.Combine(_dir, "wmux");
        var to = System.IO.Path.Combine(_dir, "Bellboard");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(from, "WebView2"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(from, "session.json"), "{}");
        var notes = new System.Collections.Generic.List<string>();

        Bellboard.Services.LegacyData.Move(from, to, notes);
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(to, "session.json")));
        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(to, "WebView2")));
        Assert.False(System.IO.Directory.Exists(from));
        Assert.Single(notes);

        // Bellboard already has its own folder: an old one that reappears is left alone.
        System.IO.Directory.CreateDirectory(from);
        System.IO.File.WriteAllText(System.IO.Path.Combine(from, "session.json"), "{\"old\":1}");
        Bellboard.Services.LegacyData.Move(from, to, notes);
        Assert.Equal("{}", System.IO.File.ReadAllText(System.IO.Path.Combine(to, "session.json")));
        Assert.Single(notes);
    }
}

public class LayoutJsonTests
{
    [Fact]
    public void RoundTripsANestedLayout()
    {
        LayoutSpec spec = new SplitSpec(true, 0.35,
            new TerminalSpec(@"C:\code\api", "scroll\x1b[0m", "claude", "3f2a9c1e-7b4d-4e8a-9c2f-1a2b3c4d5e6f"),
            new SplitSpec(false, 0.6,
                new BrowserSpec("http://localhost:5173/"),
                new StackSpec(new LayoutSpec[] { new TerminalSpec(@"C:\code"), new BrowserSpec("https://x.dev") }, 1)));

        var json = LayoutJson.ToJson(spec).ToJsonString();
        var back = LayoutJson.FromJson(JsonNode.Parse(json) as JsonObject);

        var outer = Assert.IsType<SplitSpec>(back);
        Assert.True(outer.Horizontal);
        Assert.Equal(0.35, outer.Ratio, 4);
        var term = Assert.IsType<TerminalSpec>(outer.A);
        Assert.Equal(@"C:\code\api", term.Cwd);
        Assert.Equal("scroll\x1b[0m", term.Scrollback);
        Assert.Equal("claude", term.AgentKind);
        Assert.Equal("3f2a9c1e-7b4d-4e8a-9c2f-1a2b3c4d5e6f", term.AgentSessionId);
        var inner = Assert.IsType<SplitSpec>(outer.B);
        Assert.False(inner.Horizontal);
        Assert.Equal("http://localhost:5173/", Assert.IsType<BrowserSpec>(inner.A).Url);
        var stack = Assert.IsType<StackSpec>(inner.B);
        Assert.Equal(2, stack.Tabs.Count);
        Assert.Equal(1, stack.Active);
    }

    [Fact]
    public void ClampsRatiosAndDefaultsUnknownNodes()
    {
        var o = JsonNode.Parse("""{"type":"split","ratio":5,"a":{"type":"mystery"},"b":null}""") as JsonObject;
        var s = Assert.IsType<SplitSpec>(LayoutJson.FromJson(o));
        Assert.Equal(0.9, s.Ratio);
        Assert.IsType<TerminalSpec>(s.A);
        Assert.IsType<TerminalSpec>(s.B);
        Assert.IsType<TerminalSpec>(LayoutJson.FromJson(null));
    }

    [Fact]
    public void SingleTabStackCollapsesAndActiveIsClamped()
    {
        var one = JsonNode.Parse("""{"type":"stack","active":0,"tabs":[{"type":"browser","url":"https://a"}]}""") as JsonObject;
        Assert.IsType<BrowserSpec>(LayoutJson.FromJson(one));
        var two = JsonNode.Parse("""{"type":"stack","active":9,"tabs":[{"type":"terminal"},{"type":"terminal"}]}""") as JsonObject;
        Assert.Equal(1, Assert.IsType<StackSpec>(LayoutJson.FromJson(two)).Active);
    }

    [Fact]
    public void DeepNestingIsCutOff()
    {
        var node = new JsonObject { ["type"] = "terminal" };
        for (int i = 0; i < 100; i++) node = new JsonObject { ["type"] = "split", ["a"] = node, ["b"] = new JsonObject { ["type"] = "terminal" } };
        var spec = LayoutJson.FromJson(node);
        int depth = 0;
        while (spec is SplitSpec s) { depth++; spec = s.A; }
        Assert.True(depth <= 33);
    }
}

public class KeySequencesTests
{
    [Theory]
    [InlineData("enter", "\r")]
    [InlineData("Tab", "\t")]
    [InlineData("shift+tab", "\x1b[Z")]
    [InlineData("esc", "\x1b")]
    [InlineData("backspace", "\x7f")]
    [InlineData("ctrl+c", "\x03")]
    [InlineData("ctrl+C", "\x03")]
    [InlineData("ctrl+d", "\x04")]
    [InlineData("ctrl+[", "\x1b")]
    [InlineData("ctrl+space", "\0")]
    [InlineData("alt+x", "\x1bx")]
    [InlineData("alt+enter", "\x1b\r")]
    [InlineData("up", "\x1b[A")]
    [InlineData("ctrl+left", "\x1b[1;5D")]
    [InlineData("shift+up", "\x1b[1;2A")]
    [InlineData("ctrl+shift+right", "\x1b[1;6C")]
    [InlineData("home", "\x1b[H")]
    [InlineData("delete", "\x1b[3~")]
    [InlineData("pagedown", "\x1b[6~")]
    [InlineData("f1", "\x1bOP")]
    [InlineData("f5", "\x1b[15~")]
    [InlineData("f12", "\x1b[24~")]
    [InlineData("y", "y")]
    [InlineData("shift+a", "A")]
    public void Encodes(string chord, string expected) => Assert.Equal(expected, KeySequences.ToSequence(chord));

    [Theory]
    [InlineData("hyper+x")]
    [InlineData("ctrl+f5")]
    [InlineData("nosuchkey")]
    [InlineData("")]
    public void RejectsUnknown(string chord) => Assert.Throws<ArgumentException>(() => KeySequences.ToSequence(chord));

    [Fact]
    public void Concatenates() => Assert.Equal("q\r", KeySequences.ToSequence(new[] { "q", "enter" }));
}

public class ShellResolverTests
{
    private static string DecodeScript(string commandLine)
    {
        var b64 = commandLine.Split(' ').Last();
        return Encoding.Unicode.GetString(Convert.FromBase64String(b64));
    }

    [Fact]
    public void CmdUsesPromptIntegrationAndSlashK()
    {
        var plain = ShellResolver.Resolve("cmd.exe", null);
        Assert.Equal(ShellKind.Cmd, plain.Kind);
        Assert.Equal("cmd.exe", plain.CommandLine);
        Assert.Equal("cmd.exe /k npm test", ShellResolver.Resolve("cmd.exe", "npm test").CommandLine);
        Assert.Contains("9;9;", ShellResolver.CmdPrompt);
    }

    [Fact]
    public void PowerShellGetsEncodedIntegrationAndCommand()
    {
        var s = ShellResolver.Resolve("pwsh.exe", "npm run dev");
        Assert.Equal(ShellKind.Pwsh, s.Kind);
        Assert.Contains(" -NoLogo -NoExit -EncodedCommand ", s.CommandLine);
        var script = DecodeScript(s.CommandLine);
        Assert.Contains("__bellboardPromptWrapped", script);
        Assert.Contains("]9;9;", script);
        Assert.EndsWith("npm run dev", script);
    }

    [Fact]
    public void QuotedPathWithSpacesIsRecognised()
    {
        var s = ShellResolver.Resolve("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo", null);
        Assert.Equal(ShellKind.Pwsh, s.Kind);
        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", s.Exe);
        Assert.DoesNotContain("-NoLogo -NoLogo", s.CommandLine);
    }

    [Fact]
    public void GitBashGetsLoginFlagsAndPromptCommand()
    {
        var s = ShellResolver.Resolve("\"C:\\Program Files\\Git\\bin\\bash.exe\"", "npm test");
        Assert.Equal(ShellKind.Bash, s.Kind);
        Assert.EndsWith(" -i -l", s.CommandLine);
        Assert.Contains("]7;file://", s.Env["PROMPT_COMMAND"]);
        Assert.Contains("pwd -W", s.Env["PROMPT_COMMAND"]);
        Assert.Equal("1", s.Env["CHERE_INVOKING"]);
        Assert.Equal("npm test", s.TypedCommand);
    }

    [Fact]
    public void WslStartsInLinuxFolderAndForwardsPaneVariables()
    {
        var s = ShellResolver.Resolve("wsl", "ls", @"\\wsl.localhost\Ubuntu-24.04\home\me\app");
        Assert.Equal(ShellKind.Wsl, s.Kind);
        Assert.Equal("wsl.exe -d Ubuntu-24.04 --cd \"/home/me/app\"", s.CommandLine);
        Assert.Contains("BELLBOARD_PANE_ID", s.Env["WSLENV"]);
        Assert.Contains("PROMPT_COMMAND", s.Env["WSLENV"]);
        Assert.Contains("wsl.localhost", s.Env["PROMPT_COMMAND"]);
        Assert.Equal("ls", s.TypedCommand);

        var plain = ShellResolver.Resolve("wsl:Debian", null, @"C:\code");
        Assert.Equal("wsl.exe -d Debian", plain.CommandLine);
        Assert.False(plain.Env.ContainsKey("ZDOTDIR"));

        var zsh = ShellResolver.Resolve("wsl", null, null, @"C:\Users\me\AppData\Local\Bellboard\shell\zsh");
        Assert.Equal(@"C:\Users\me\AppData\Local\Bellboard\shell\zsh", zsh.Env["ZDOTDIR"]);
        Assert.EndsWith(":ZDOTDIR/p", zsh.Env["WSLENV"]);
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me", "Ubuntu", "/home/me")]
    [InlineData(@"\\wsl$\Debian", "Debian", "/")]
    public void ParsesWslPaths(string path, string distro, string linux)
    {
        var p = ShellResolver.ParseWslPath(path);
        Assert.NotNull(p);
        Assert.Equal(distro, p!.Value.Distro);
        Assert.Equal(linux, p.Value.LinuxPath);
        Assert.Null(ShellResolver.ParseWslPath(@"C:\Users\me"));
    }

    [Fact]
    public void ExplicitCommandArgumentsArePassedThrough()
    {
        var s = ShellResolver.Resolve("powershell.exe -Command Get-Date", null);
        Assert.Equal(ShellKind.WindowsPowerShell, s.Kind);
        Assert.Equal("powershell.exe -Command Get-Date", s.CommandLine);
    }
}

public class ShellChoiceTests
{
    [Fact]
    public void SshHostsComeFromConcreteHostLines()
    {
        const string config = """
            # work
            Host devbox
                HostName 10.0.0.7
                User me
            Host build-1 build-2   # two names
            Host *.internal !secret
            host=gpu
            Host "quoted box"
            Match host x
            Host devbox
            """;
        Assert.Equal(new[] { "devbox", "build-1", "build-2", "gpu", "quoted box" }, ShellResolver.ParseSshHosts(config));
    }

    [Theory]
    [InlineData("wsl.exe", "wsl", "wsl")]
    [InlineData("wsl.exe", "\"C:\\Windows\\system32\\wsl.exe\" -d Ubuntu-24.04", "wsl:Ubuntu-24.04")]
    [InlineData("wsl.exe", "wsl --distribution Debian --cd ~", "wsl:Debian")]
    [InlineData("wsl.exe", "wsl -e htop", null)]
    [InlineData("ssh.exe", "ssh devbox", "ssh:devbox")]
    [InlineData("ssh.exe", "\"C:\\Windows\\System32\\OpenSSH\\ssh.exe\" -p 2222 me@10.0.0.7", "ssh:-p 2222 me@10.0.0.7")]
    [InlineData("ssh.exe", "ssh", null)]
    [InlineData("node.exe", "node server.js", null)]
    public void ShellsStartedAtThePromptAreReopened(string exe, string commandLine, string? expected) =>
        Assert.Equal(expected, ShellResolver.NestedShellAlias(exe, commandLine));

    [Theory]
    [InlineData("ssh:devbox", "devbox")]
    [InlineData("ssh:me@10.0.0.7", "10.0.0.7")]
    [InlineData("ssh:-o ConnectTimeout=5 -p 2222 -i \"C:\\keys\\id\" demo@build-1", "build-1")]
    [InlineData("ssh:ssh://me@host.example:2222", "host.example")]
    [InlineData("ssh:-v gpu", "gpu")]
    [InlineData("wsl:Ubuntu", null)]
    [InlineData(null, null)]
    public void SshPanesAreNamedAfterTheirHost(string? alias, string? expected) =>
        Assert.Equal(expected, ShellResolver.SshDestination(alias));

    [Fact]
    public void SshAliasRunsTheOpenSshClient()
    {
        if (ShellResolver.SshPath() is not { } ssh) return; // OpenSSH client not installed on this machine
        var spec = ShellResolver.Resolve("ssh:devbox", null);
        Assert.Equal(ShellKind.Other, spec.Kind);
        Assert.EndsWith(" devbox", spec.CommandLine);
        Assert.Contains("ssh.exe", spec.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ShellResolver.ExpandAlias("ssh:  "));
    }
}

public class AgentPlacementTests
{
    [Fact]
    public void WslReposRunTheirToolsInsideTheDistro()
    {
        var psi = AgentManager.ToolStart("git", @"\\wsl.localhost\Ubuntu-24.04\home\me\my repo", ["worktree", "add", "x"]);
        Assert.Equal("wsl.exe", psi.FileName);
        Assert.Equal(new[] { "-d", "Ubuntu-24.04", "--cd", "/home/me/my repo", "--exec", "git", "worktree", "add", "x" }, psi.ArgumentList);
        Assert.Equal("/home/me/x.worktrees/t", AgentManager.ToolPath(@"\\wsl.localhost\Ubuntu-24.04\home\me\x.worktrees\t"));
        Assert.Equal("WSL · Ubuntu-24.04", AgentManager.WhereItRuns(@"\\wsl$\Ubuntu-24.04\home\me"));
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\me\repo", ShellResolver.WslUncPath("Ubuntu-24.04", "/home/me/repo"));
    }

    [Fact]
    public void WindowsReposRunTheirToolsOnWindows()
    {
        var psi = AgentManager.ToolStart("git", @"C:\code\repo", ["status"]);
        Assert.Equal("git", psi.FileName);
        Assert.Equal(@"C:\code\repo", psi.WorkingDirectory);
        Assert.Equal(new[] { "status" }, psi.ArgumentList);
        Assert.Equal(@"C:\code\repo", AgentManager.ToolPath(@"C:\code\repo"));
        Assert.Equal("Windows", AgentManager.WhereItRuns(@"C:\code\repo"));
    }
}

public class PiAgentTests
{
    [Fact]
    public void PiStartsWithTheTaskAndResumesItsSession()
    {
        Assert.Equal("pi -- '-v is broken; fix it'", AgentManager.AgentCommand("pi", "-v is broken; fix it", powershell: false));
        Assert.Equal("pi -- 'don''t panic'", AgentManager.AgentCommand("pi", "don't panic", powershell: true));
        Assert.Equal("pi --session 0198c2a4-5e1f-7b2a-9c3d-1e2f3a4b5c6d", AgentManager.ResumeCommand("pi", "0198c2a4-5e1f-7b2a-9c3d-1e2f3a4b5c6d"));
        Assert.Equal("pi --continue", AgentManager.ResumeCommand("pi", null));
        Assert.Equal("pi --continue", AgentManager.ResumeCommand("pi", "bad id; rm -rf"));
        Assert.Equal("Pi", AgentManager.DisplayName("pi"));
        Assert.Equal("OpenCode", AgentManager.DisplayName("opencode"));
    }
}

public class FuzzyTests
{
    [Fact]
    public void RanksPrefixAndWordStartsAboveScatteredMatches()
    {
        Assert.True(Fuzzy.Score("Split right", "split") > Fuzzy.Score("Show split notifications", "split"));
        Assert.True(Fuzzy.Score("Split right", "sr") > Fuzzy.Score("Restore session", "sr"));
        Assert.True(Fuzzy.Score("New workspace", "nw") > 0);
        Assert.Equal(0, Fuzzy.Score("Zoom pane", "xyz"));
        Assert.Equal(0, Fuzzy.Score("", "a"));
        Assert.True(Fuzzy.Score("Anything", "") > 0);
    }

    [Fact]
    public void QueryWithSpacesMatchesAcrossWords() =>
        Assert.True(Fuzzy.Score("Open browser pane", "open brow") > Fuzzy.Score("Open folder in Explorer", "open brow"));
}

public class AgentManagerTests
{
    [Theory]
    [InlineData("Fix the flaky login test!", "fix-the-flaky-login-test")]
    [InlineData("  ", "task")]
    [InlineData("Ünïcode & symbols *** only", "n-code-symbols-only")]
    [InlineData("a very long task description that goes on and on past forty characters", "a-very-long-task-description-that-goes")]
    public void Slugs(string task, string slug) => Assert.Equal(slug, AgentManager.Slug(task));

    [Fact]
    public void AgentCommandsQuoteTheTaskForTheShell()
    {
        Assert.Equal("claude 'it''s broken'", AgentManager.AgentCommand("claude", "it's broken", powershell: true));
        Assert.Equal("claude 'it'\\''s broken'", AgentManager.AgentCommand("claude", "it's broken", powershell: false));
        Assert.Equal("codex 'two lines'", AgentManager.AgentCommand("codex", "two\n  lines", powershell: true));
        Assert.Equal("aider --yes", AgentManager.AgentCommand("aider --yes", "ignored", powershell: true));
    }
}

public class ResumeCommandTests
{
    [Theory]
    [InlineData("claude", "3f2a9c1e-7b4d-4e8a-9c2f-1a2b3c4d5e6f", "claude --resume 3f2a9c1e-7b4d-4e8a-9c2f-1a2b3c4d5e6f")]
    [InlineData("claude", null, "claude --continue")]
    [InlineData("claude", "bad id; rm -rf", "claude --continue")]
    [InlineData("codex", "0199a1b2-c3d4", "codex resume 0199a1b2-c3d4")]
    [InlineData("codex", null, "codex resume --last")]
    [InlineData("opencode", null, "opencode --continue")]
    [InlineData("aider --yes", null, null)]
    public void PicksTheAgentsResumeCommand(string kind, string? id, string? expected) =>
        Assert.Equal(expected, AgentManager.ResumeCommand(kind, id));
}

public class LayoutMathTests
{
    private static readonly System.Windows.Rect Pane = new(100, 50, 400, 300);

    [Theory]
    [InlineData(110, 200, DropSide.Left)]
    [InlineData(490, 200, DropSide.Right)]
    [InlineData(300, 60, DropSide.Up)]
    [InlineData(300, 340, DropSide.Down)]
    [InlineData(300, 200, DropSide.Tab)]
    public void Zone_edges_split_and_the_middle_joins(double x, double y, DropSide expected) =>
        Assert.Equal(expected, LayoutMath.Zone(Pane, new System.Windows.Point(x, y)));

    [Fact]
    public void Preview_takes_the_half_on_that_side()
    {
        Assert.Equal(new System.Windows.Rect(300, 50, 200, 300), LayoutMath.Preview(Pane, DropSide.Right));
        Assert.Equal(new System.Windows.Rect(100, 50, 400, 150), LayoutMath.Preview(Pane, DropSide.Up));
        Assert.Equal(Pane, LayoutMath.Preview(Pane, DropSide.Tab));
    }

    [Fact]
    public void TabIndexAt_inserts_before_the_tab_whose_middle_is_right_of_the_cursor()
    {
        var tabs = new[] { (0.0, 100.0), (100.0, 100.0), (200.0, 100.0) };
        Assert.Equal(0, LayoutMath.TabIndexAt(tabs, 20));
        Assert.Equal(1, LayoutMath.TabIndexAt(tabs, 60));
        Assert.Equal(3, LayoutMath.TabIndexAt(tabs, 290));
    }

    [Fact]
    public void FitTabs_keeps_natural_widths_when_they_fit()
    {
        var (widths, first, count) = LayoutMath.FitTabs([120, 90, 150], 500, active: 1, first: 0, min: 100, max: 200);
        Assert.Equal([120, 90, 150], widths);
        Assert.Equal((0, 3), (first, count));
    }

    [Fact]
    public void FitTabs_shrinks_the_widest_first()
    {
        var (widths, _, count) = LayoutMath.FitTabs([200, 200, 100], 440, active: 0, first: 0, min: 100, max: 210);
        Assert.Equal(3, count);
        Assert.Equal([170, 170, 100], widths);
    }

    [Fact]
    public void FitTabs_hides_the_overflow_but_keeps_the_active_tab_in_view()
    {
        var natural = Enumerable.Repeat(180.0, 8).ToArray();
        var (widths, first, count) = LayoutMath.FitTabs(natural, 450, active: 6, first: 0, min: 108, max: 210);
        Assert.Equal(4, count);
        Assert.InRange(6, first, first + count - 1);
        Assert.Equal(count, widths.Count(w => w > 0));
        Assert.True(widths.Sum() <= 450);
    }

    [Fact]
    public void FitTabs_honours_a_scrolled_position_when_no_tab_is_pinned()
    {
        var natural = Enumerable.Repeat(180.0, 8).ToArray();
        var (_, first, _) = LayoutMath.FitTabs(natural, 450, active: -1, first: 2, min: 108, max: 210);
        Assert.Equal(2, first);
    }
}

public class TabRevealTests
{
    [Fact]
    public void FitTabs_reveals_left_tabs_again_when_the_row_widens()
    {
        var natural = Enumerable.Repeat(180.0, 5).ToArray();
        // Narrow: one tab fits, the active one (index 1).
        var narrow = LayoutMath.FitTabs(natural, 120, active: 1, first: 0, min: 108, max: 210);
        Assert.Equal((1, 1), (narrow.First, narrow.Count));
        // Wider again: three fit, and the hidden tab on the left comes back.
        var wide = LayoutMath.FitTabs(natural, 340, active: 1, first: narrow.First, min: 108, max: 210, wasShown: narrow.Count);
        Assert.Equal((0, 3), (wide.First, wide.Count));
    }
}

public class PinnedLayoutTests
{
    [Fact]
    public void Pinned_tabs_survive_the_session_file()
    {
        var spec = new StackSpec([new TerminalSpec("C:\\code", Shell: "pwsh", Pinned: true), new BrowserSpec("http://localhost:3000", Pinned: true), new TerminalSpec("C:\\x")], 0);
        var back = (StackSpec)LayoutJson.FromJson(JsonNode.Parse(LayoutJson.ToJson(spec).ToJsonString())!.AsObject());
        Assert.True(((TerminalSpec)back.Tabs[0]).Pinned);
        Assert.True(((BrowserSpec)back.Tabs[1]).Pinned);
        Assert.False(((TerminalSpec)back.Tabs[2]).Pinned);
        Assert.Null(LayoutJson.ToJson(new TerminalSpec("C:\\x"))["pinned"]);
    }
}
