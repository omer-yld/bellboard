extern alias cli;

using System;
using System.Linq;
using System.Text.Json.Nodes;
using cli::Bellboard.Cli;
using Bellboard.Core;
using Bellboard.Ipc;
using Bellboard.Services;
using Bellboard.Shared;
using Xunit;

namespace Bellboard.Tests;

public class PromptParserTests
{
    private const string Claude = """
        ╭──────────────────────────────────────────────────────╮
        │ Bash command                                         │
        │                                                      │
        │   npm test                                           │
        │   Run the test suite                                 │
        │                                                      │
        │ Do you want to proceed?                              │
        │ ❯ 1. Yes                                             │
        │   2. Yes, and don't ask again for npm test commands  │
        │   3. No, and tell Claude what to do differently (esc)│
        ╰──────────────────────────────────────────────────────╯
        """;

    [Fact]
    public void ClaudeBoxedMenu()
    {
        var p = PromptParser.Parse(Claude)!;
        Assert.Equal("Do you want to proceed?", p.Question);
        Assert.Equal(["1", "2", "3"], p.Choices.Select(c => c.Key));
        Assert.Equal("Yes", p.Choices[0].Label);
        Assert.True(p.Choices[0].Selected);
        Assert.False(p.Choices[1].Selected);
        Assert.Equal("No, and tell Claude what to do differently (esc)", p.Choices[2].Label);
    }

    [Fact]
    public void UnboxedMenuWithOtherPointer()
    {
        var p = PromptParser.Parse("Allow command?\n\n  1. Yes, proceed (y)\n› 2. Yes, and don't ask again (a)\n  3. No (esc)\n")!;
        Assert.Equal("Allow command?", p.Question);
        Assert.Equal(3, p.Choices.Count);
        Assert.True(p.Choices[1].Selected);
    }

    [Fact]
    public void LastMenuOnScreenWins()
    {
        var p = PromptParser.Parse("Old?\n1. A\n2. B\noutput\nNew?\n❯ 1. C\n  2. D\n")!;
        Assert.Equal("New?", p.Question);
        Assert.Equal(["C", "D"], p.Choices.Select(c => c.Label));
    }

    [Theory]
    [InlineData("PS C:\\> npm test\n> build\n")]
    [InlineData("Steps:\n2. second\n3. third\n")]         // doesn't start at 1
    [InlineData("1. only one option\n")]
    [InlineData("")]
    public void NoMenu(string screen) => Assert.Null(PromptParser.Parse(screen));

    [Fact]
    public void HashIgnoresTrailingSpaceAndLineEndings()
    {
        Assert.Equal(PromptParser.Hash("a  \r\nb\n\n"), PromptParser.Hash("a\nb"));
        Assert.NotEqual(PromptParser.Hash("a\nb"), PromptParser.Hash("a\nc"));
        Assert.Equal(16, PromptParser.Hash("x").Length);
    }
}

public class RemoteDevicesTests
{
    private DateTime _now = new(2026, 10, 14, 12, 0, 0);
    private RemoteDevices New() => new(null, () => _now);

    [Fact]
    public void PairOnceWithAFreshCode()
    {
        var d = New();
        var code = d.CreateCode();
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
        var (device, token) = d.Pair(code.ToLowerInvariant().Replace("-", " "), "  Pixel\u0007 8 ", "100.64.0.2");
        Assert.Equal("Pixel 8", device.Name);
        Assert.Equal(RemotePolicy.Respond, device.Level);
        Assert.True(token.Length >= 40);
        Assert.NotEqual(token, device.TokenHash);
        Assert.Same(device, d.Authenticate(token, "100.64.0.2"));
        Assert.Null(d.Authenticate(token + "x", null));
        Assert.Throws<RemoteAuthException>(() => d.Pair(code, "again", null));
    }

    [Fact]
    public void CodesExpire()
    {
        var d = New();
        var code = d.CreateCode();
        _now += RemoteDevices.CodeLifetime + TimeSpan.FromSeconds(1);
        var ex = Assert.Throws<RemoteAuthException>(() => d.Pair(code, "late", null));
        Assert.Contains("expired", ex.Message);
        Assert.Null(d.CodeExpires);
    }

    [Fact]
    public void WrongCodesLockPairing()
    {
        var d = New();
        var code = d.CreateCode();
        for (int i = 0; i < RemoteDevices.MaxFailures; i++)
            Assert.Equal(400, Assert.Throws<RemoteAuthException>(() => d.Pair("AAAA-AAA" + (char)('B' + i), "x", null)).Status);
        // Locked: even the right code fails, and the code is gone.
        Assert.Equal(429, Assert.Throws<RemoteAuthException>(() => d.Pair(code, "x", null)).Status);
        _now += RemoteDevices.Lockout + TimeSpan.FromSeconds(1);
        var fresh = d.CreateCode();
        Assert.Equal("x", d.Pair(fresh, "x", null).Device.Name);
    }

    [Fact]
    public void WithoutALiveCodeStrangersCantLockPairing()
    {
        var d = New();
        for (int i = 0; i < 3 * RemoteDevices.MaxFailures; i++)
            Assert.Contains("No pairing code", Assert.Throws<RemoteAuthException>(() => d.Pair("AAAA-AAAA", "x", null)).Message);
        Assert.Equal("phone", d.Pair(d.CreateCode(), "phone", null).Device.Name);
    }

    [Fact]
    public void RevokeAndLevelChangesNotify()
    {
        var d = New();
        var revoked = new System.Collections.Generic.List<string>();
        d.Revoked += revoked.Add;
        var (device, token) = d.Pair(d.CreateCode(), "phone", null);
        d.SetLevel(device.Id, RemotePolicy.Full);
        Assert.Equal(RemotePolicy.Full, d.Find(device.Id)!.Level);
        Assert.Throws<InvalidOperationException>(() => d.SetLevel(device.Id, "admin"));
        Assert.True(d.Revoke(device.Id));
        Assert.Equal([device.Id, device.Id], revoked);
        Assert.Null(d.Authenticate(token, null));
        Assert.False(d.Revoke(device.Id));
    }
}

public class RemotePolicyTests
{
    [Theory]
    [InlineData("view", "agent.inbox", true)]
    [InlineData("view", "terminal.attach", true)]
    [InlineData("view", "agent.answer", false)]
    [InlineData("respond", "agent.answer", true)]
    [InlineData("respond", "terminal.send", true)]
    [InlineData("respond", "agent.new", false)]
    [InlineData("respond", "agent.finish", false)]
    [InlineData("full", "agent.finish", true)]
    [InlineData("full", "app.quit", false)]
    [InlineData("full", "browser.eval", false)]
    [InlineData("full", "remote.pair", false)]
    [InlineData("full", "dev.unload", false)]
    [InlineData("full", "events.subscribe", false)]
    [InlineData(null, "agent.inbox", false)]
    [InlineData("admin", "agent.inbox", false)]
    public void Allows(string? level, string method, bool allowed) => Assert.Equal(allowed, RemotePolicy.Allows(level, method));

    [Fact]
    public void Refusals()
    {
        Assert.Equal("needs full access", RemotePolicy.Refusal("agent.new"));
        Assert.Contains("isn't available remotely", RemotePolicy.Refusal("app.quit"));
    }
}

public class PermissionRequestTests
{
    [Fact]
    public void DescribesBash()
    {
        var (summary, detail) = PromptBroker.Describe("Bash", JsonNode.Parse("""{"command":"npm   test\n-- --watch=false","description":"Run tests"}""")!.AsObject());
        Assert.Equal("npm test -- --watch=false", summary);
        Assert.StartsWith("Run tests", detail);
    }

    [Fact]
    public void DescribesEditsAndMcp()
    {
        var (path, diff) = PromptBroker.Describe("Edit", JsonNode.Parse("""{"file_path":"src/a.ts","old_string":"x","new_string":"y"}""")!.AsObject());
        Assert.Equal("src/a.ts", path);
        Assert.Equal("- x\n+ y", diff);
        var (mcp, json) = PromptBroker.Describe("mcp__github__create_issue", JsonNode.Parse("""{"title":"Bug","body":"long"}""")!.AsObject());
        Assert.Equal("Bug", mcp);
        Assert.Contains("\"body\"", json);
        Assert.Equal(("", (string?)null), PromptBroker.Describe("Unknown", null));
    }

    [Fact]
    public void HookOutputShape()
    {
        var o = Hooks.PermissionOutput(new JsonObject { ["behavior"] = "deny", ["message"] = "no" });
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"no"}}}""", o.ToJsonString());
    }

    [Fact]
    public void PermissionHookWaitsLongerThanOthers()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bb-perm-{Guid.NewGuid():N}.json");
        try
        {
            Hooks.InstallClaude(path, @"C:\bb\bell.exe");
            var root = JsonNode.Parse(System.IO.File.ReadAllText(path))!;
            var perm = root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!;
            Assert.Equal(@"C:\bb\bell.exe hook claude PermissionRequest", (string?)perm["command"]);
            Assert.Equal(Hooks.PermissionTimeout, (int)perm["timeout"]!);
            Assert.Equal(10, (int)root["hooks"]!["Stop"]![0]!["hooks"]![0]!["timeout"]!);
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(path + ".bellboard-backup");
        }
    }
}

public class PipeConnectionTests
{
    [Fact]
    public void SlowClientLosesItsSubscriptionNotMemory()
    {
        using var conn = new PipeConnection(0);
        int ended = 0;
        var sub = conn.AddSubscription(() => ended++);
        var big = new string('x', 64 * 1024);
        bool ok = true;
        int pushed = 0;
        while (ok && pushed < 1000) { ok = conn.Push(sub, "output", JsonValue.Create(big)); pushed++; }
        Assert.False(ok);
        Assert.InRange(pushed, 2, PipeConnection.MaxQueuedChars / big.Length + 2);
        Assert.Equal(1, ended);
        Assert.False(conn.EndSubscription(sub));
    }

    [Fact]
    public void EndingAllRunsEachOnce()
    {
        using var conn = new PipeConnection(0);
        int a = 0, b = 0;
        conn.AddSubscription(() => a++);
        var sb = conn.AddSubscription(() => b++);
        Assert.True(conn.EndSubscription(sb));
        conn.EndSubscriptions();
        conn.EndSubscriptions();
        Assert.Equal((1, 1), (a, b));
    }
}

public class QrCodeTests
{
    [Fact]
    public void TextQrIsSquareAndHasQuietZone()
    {
        var m = QrCode.Matrix("https://pc.tailnet.ts.net/#pair=ABCD-EFGH");
        int n = m.GetLength(0);
        Assert.Equal(n, m.GetLength(1));
        for (int i = 0; i < n; i++) Assert.False(m[0, i]);
        var lines = QrCode.ToText("https://pc.tailnet.ts.net/#pair=ABCD-EFGH").TrimEnd('\n').Split('\n');
        Assert.Equal((n + 1) / 2, lines.Length);
        Assert.All(lines, l => Assert.Equal(n, l.Length));
    }
}
