extern alias cli;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using cli::Bellboard.Cli;
using Xunit;

namespace Bellboard.Tests;

public class CliParsingTests
{
    [Theory]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"tab\there", "tab\there")]
    [InlineData(@"\e[31m", "\x1b[31m")]
    [InlineData(@"\x41\x62", "Ab")]
    [InlineData(@"C:\Users\me", @"C:\Users\me")]
    [InlineData(@"double \\ slash", @"double \ slash")]
    [InlineData(@"trailing\", @"trailing\")]
    [InlineData(@"bad \xZZ", @"bad \xZZ")]
    public void Unescape(string input, string expected) => Assert.Equal(expected, Program.Unescape(input));

    [Fact]
    public void ArgsParsing()
    {
        var a = new Args(new[] { "right", "--cwd", @"C:\x", "--json", "--name=api", "-o", "f.png", "rest", "--", "--literal" },
            new HashSet<string> { "json" });
        Assert.Equal(new[] { "right", "rest", "--literal" }, a.Positional);
        Assert.Equal(@"C:\x", a.Opt("cwd"));
        Assert.True(a.Has("json"));
        Assert.Null(a.Opt("json"));
        Assert.Equal("api", a.Opt("name"));
        Assert.Equal("f.png", a.Opt("o"));
        Assert.False(a.Has("missing"));
    }
}

public sealed class HooksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bellboard-tests-" + Guid.NewGuid().ToString("N"));
    private const string Exe = "C:/tools/Bellboard/bell.exe";

    public HooksTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static IEnumerable<string> Commands(JsonObject root, string ev) =>
        (root["hooks"]?[ev] as JsonArray ?? new JsonArray())
        .SelectMany(m => (m?["hooks"] as JsonArray ?? new JsonArray()).Select(h => (string?)h?["command"] ?? ""));

    [Fact]
    public void ClaudeInstallIsIdempotentAndKeepsUserSettings()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              // comments and trailing commas are tolerated
              "model": "opus",
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "echo mine" } ] } ] },
            }
            """);

        Assert.Equal(10, Hooks.InstallClaude(path, Exe));
        Assert.Equal(0, Hooks.InstallClaude(path, Exe));
        Assert.True(Hooks.IsClaudeInstalled(path));
        Assert.True(File.Exists(path + ".bellboard-backup"));

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("opus", (string?)root["model"]);
        Assert.Equal(new[] { "echo mine", $"{Exe} hook claude Stop" }, Commands(root, "Stop"));
        Assert.Equal(new[] { $"{Exe} hook claude Notification" }, Commands(root, "Notification"));

        Assert.Equal(10, Hooks.UninstallClaude(path));
        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(new[] { "echo mine" }, Commands(after, "Stop"));
        Assert.Null(after["hooks"]?["Notification"]);
        Assert.False(Hooks.IsClaudeInstalled(path));
    }

    [Fact]
    public void ClaudeInstallReplacesHooksFromBellboardAndOtherFolders()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            { "hooks": {
                "Stop": [ { "hooks": [ { "type": "command", "command": "C:/old/wmux.exe hook claude Stop" },
                                       { "type": "command", "command": "echo mine" } ] } ],
                "Notification": [ { "hooks": [ { "type": "command", "command": "\"C:/Program Files/x/bell.exe\" hook claude Notification" } ] } ],
                "SessionStart": [ { "hooks": [ { "type": "command", "command": "notify-bell hook claude-ish" } ] } ]
            } }
            """);

        Assert.Equal(10, Hooks.InstallClaude(path, Exe, out var replaced));
        Assert.Equal(2, replaced);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(new[] { "echo mine", $"{Exe} hook claude Stop" }, Commands(root, "Stop"));
        Assert.Equal(new[] { $"{Exe} hook claude Notification" }, Commands(root, "Notification"));
        Assert.Equal(new[] { "notify-bell hook claude-ish", $"{Exe} hook claude SessionStart" }, Commands(root, "SessionStart"));
        Assert.Equal(0, Hooks.InstallClaude(path, Exe));
    }

    [Fact]
    public void WslHooksWithQuotedPathsAreRecognised()
    {
        var path = Path.Combine(_dir, "settings.json");
        const string wslExe = "'/mnt/c/Program Files/Bellboard/bell.exe'";
        Assert.Equal(10, Hooks.InstallClaude(path, wslExe));
        Assert.Equal(0, Hooks.InstallClaude(path, wslExe));
        Assert.True(Hooks.IsClaudeInstalled(path));
        Assert.Equal(10, Hooks.InstallClaude(path, "/mnt/d/bb/bell.exe", out var replaced));
        Assert.Equal(10, replaced);
    }

    [Fact]
    public void CodexInWslLeavesAnotherNotifyAlone()
    {
        // A Linux notify command can't be passed on from the Windows bell.exe, so it isn't wrapped.
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "notify = [\"notify-send\", \"codex\"]\n");
        Assert.Equal(Hooks.CodexResult.OtherNotify, Hooks.InstallCodex(path, "/mnt/c/bb/bell.exe", chain: false));
        Assert.Equal("notify = [\"notify-send\", \"codex\"]\n", File.ReadAllText(path));
    }

    [Fact]
    public void PiExtensionIsWrittenUpdatedAndRemoved()
    {
        var agentDir = Path.Combine(_dir, ".pi", "agent");
        var file = Path.Combine(agentDir, "extensions", "bellboard.ts");
        Assert.Equal(Hooks.PiResult.Added, Hooks.InstallPi(agentDir, @"C:\tools\Bellboard\bell.exe"));
        var source = File.ReadAllText(file);
        Assert.Contains("const BELL: string = \"C:\\\\tools\\\\Bellboard\\\\bell.exe\";", source);
        Assert.DoesNotContain("__BELL__", source);
        Assert.Contains("\"hook\", \"pi\"", source);
        Assert.Equal(Hooks.PiResult.AlreadyInstalled, Hooks.InstallPi(agentDir, @"C:\tools\Bellboard\bell.exe"));
        Assert.Equal(Hooks.PiResult.Updated, Hooks.InstallPi(agentDir, "/mnt/c/tools/Bellboard/bell.exe"));
        Assert.True(Hooks.IsPiInstalled(agentDir));
        Assert.True(Hooks.UninstallPi(agentDir));
        Assert.False(File.Exists(file));
        Assert.False(Hooks.UninstallPi(agentDir));
    }

    [Fact]
    public void PiExtensionOfTheSameNameIsLeftAlone()
    {
        var agentDir = Path.Combine(_dir, ".pi", "agent");
        var file = Path.Combine(agentDir, "extensions", "bellboard.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "// my own bellboard helper\n");
        Assert.Equal(Hooks.PiResult.Other, Hooks.InstallPi(agentDir, "C:/bb/bell.exe"));
        Assert.False(Hooks.UninstallPi(agentDir));
        Assert.Equal("// my own bellboard helper\n", File.ReadAllText(file));
    }

    [Fact]
    public void ClaudeInstallCreatesMissingFile()
    {
        var path = Path.Combine(_dir, "new", "settings.json");
        Assert.Equal(10, Hooks.InstallClaude(path, Exe));
        Assert.True(Hooks.IsClaudeInstalled(path));
    }

    [Fact]
    public void CodexNotifyIsPrependedAboveTables()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "model = \"o4\"\n\n[profiles.fast]\nnotify = \"inside a table is fine\"\n");
        Assert.Equal(Hooks.CodexResult.Added, Hooks.InstallCodex(path, Exe));
        Assert.Equal(Hooks.CodexResult.AlreadyInstalled, Hooks.InstallCodex(path, Exe));
        var text = File.ReadAllText(path);
        Assert.StartsWith($"notify = [\"{Exe}\", \"hook\", \"codex\"]\n", text);
        Assert.Contains("[profiles.fast]", text);

        Assert.True(Hooks.UninstallCodex(path));
        Assert.DoesNotContain("\"hook\", \"codex\"", File.ReadAllText(path));
        Assert.False(Hooks.UninstallCodex(path));
    }

    [Fact]
    public void CodexNotifyFromBellboardIsUpdated()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "notify = [\"C:/old/wmux.exe\", \"hook\", \"codex\"]\nmodel = \"o4\"\n");
        Assert.Equal(Hooks.CodexResult.Updated, Hooks.InstallCodex(path, Exe));
        Assert.Equal($"notify = [\"{Exe}\", \"hook\", \"codex\"]\nmodel = \"o4\"\n", File.ReadAllText(path));
        Assert.Equal(Hooks.CodexResult.AlreadyInstalled, Hooks.InstallCodex(path, Exe));
    }

    [Fact]
    public void OnlyTheSkillBellboardWroteIsRemoved()
    {
        var ours = Path.Combine(_dir, "wmux", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(ours)!);
        File.WriteAllText(ours, "---\nname: wmux\ndescription: x\n---\n");
        Assert.Equal(ours, Hooks.RemoveLegacySkill(_dir));
        Assert.False(Directory.Exists(Path.GetDirectoryName(ours)));

        Directory.CreateDirectory(Path.GetDirectoryName(ours)!);
        File.WriteAllText(ours, "---\nname: my-wmux-notes\n---\n");
        Assert.Null(Hooks.RemoveLegacySkill(_dir));
        Assert.True(File.Exists(ours));
    }

    [Fact]
    public void CodexChainsAnExistingNotifyAndUninstallPutsItBack()
    {
        // The Codex app's computer-use helper owns `notify` on a real machine.
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "notify = [ \"C:\\\\Codex\\\\helper.exe\", \"turn-ended\" ]  # theirs\nmodel = \"o4\"\n");

        Assert.Equal(Hooks.CodexResult.Chained, Hooks.InstallCodex(path, Exe));
        Assert.Equal($"notify = [\"{Exe}\", \"hook\", \"codex\", \"--then\", \"C:\\\\Codex\\\\helper.exe\", \"turn-ended\"]\nmodel = \"o4\"\n", File.ReadAllText(path));
        Assert.Equal(Hooks.CodexResult.AlreadyInstalled, Hooks.InstallCodex(path, Exe));

        // Bellboard moved: the path changes, the chain stays.
        Assert.Equal(Hooks.CodexResult.Updated, Hooks.InstallCodex(path, "D:/bb/bell.exe"));
        Assert.StartsWith("notify = [\"D:/bb/bell.exe\", \"hook\", \"codex\", \"--then\", \"C:\\\\Codex\\\\helper.exe\"", File.ReadAllText(path));

        Assert.True(Hooks.UninstallCodex(path, out var restored));
        Assert.True(restored);
        Assert.Equal("notify = [\"C:\\\\Codex\\\\helper.exe\", \"turn-ended\"]\nmodel = \"o4\"\n", File.ReadAllText(path));
    }

    [Fact]
    public void CodexLeavesNotifyItCannotReadAndHandlesMissingInstall()
    {
        var path = Path.Combine(_dir, "config.toml");
        var multiLine = "notify = [\n  \"other\",\n]\n";
        File.WriteAllText(path, multiLine);
        Assert.Equal(Hooks.CodexResult.OtherNotify, Hooks.InstallCodex(path, Exe));
        Assert.Equal(multiLine, File.ReadAllText(path));
        Assert.Equal(Hooks.CodexResult.NoCodex, Hooks.InstallCodex(Path.Combine(_dir, "nope", "config.toml"), Exe));
    }

    [Fact]
    public void CodexHookArgumentsSplitIntoChainAndPayload()
    {
        var (chain, payload) = Hooks.ParseCodexArgs(["codex", "--then", @"C:\Codex\helper.exe", "turn-ended", "{\"type\":\"agent-turn-complete\"}"]);
        Assert.Equal(new[] { @"C:\Codex\helper.exe", "turn-ended" }, chain);
        Assert.Equal("{\"type\":\"agent-turn-complete\"}", payload);
        Assert.Equal(((string[]?)null, "{}"), Hooks.ParseCodexArgs(["codex", "{}"]));
    }

    [Theory]
    [InlineData("notify = ['C:\\x\\y.exe', \"a\\\"b\", \"\\u0041\"]", new[] { "C:\\x\\y.exe", "a\"b", "A" })]
    [InlineData("notify=[]", new string[0])]
    [InlineData("notify = [\"a\"] # comment", new[] { "a" })]
    public void TomlStringArrays(string line, string[] expected) => Assert.Equal(expected, Hooks.ParseTomlStrings(line));

    [Theory]
    [InlineData("notify = \"not an array\"")]
    [InlineData("notify = [\"unterminated]")]
    [InlineData("notify = [\"bad \\q escape\"]")]
    [InlineData("notify = [\"\\uZZZZ\"]")]
    [InlineData("notify = [\"a\"] trailing")]
    public void TomlStringArraysRejectWhatTheyCannotRead(string line) => Assert.Null(Hooks.ParseTomlStrings(line));
}
