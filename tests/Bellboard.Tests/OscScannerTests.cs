using System.Collections.Generic;
using Bellboard.Terminal;
using Xunit;

namespace Bellboard.Tests;

public class OscScannerTests
{
    private sealed class Recorder
    {
        public readonly List<string> Titles = new();
        public readonly List<string> Cwds = new();
        public readonly List<(string? Title, string Body)> Notes = new();
        public readonly List<(int State, int Pct)> Progress = new();
        public int Bells;
        public readonly OscScanner Scanner = new();

        public Recorder()
        {
            Scanner.Title += Titles.Add;
            Scanner.Cwd += Cwds.Add;
            Scanner.Notify += (t, b) => Notes.Add((t, b));
            Scanner.Progress += (s, p) => Progress.Add((s, p));
            Scanner.Bell += () => Bells++;
        }
    }

    [Theory]
    [InlineData("\x1b]0;hello\a", "hello")]
    [InlineData("\x1b]2;with ST\x1b\\", "with ST")]
    [InlineData("\x1b]0;✳ Fix login bug\a", "✳ Fix login bug")]
    public void Title(string input, string expected)
    {
        var r = new Recorder();
        r.Scanner.Feed(input);
        Assert.Equal(new[] { expected }, r.Titles);
        Assert.Equal(0, r.Bells);
    }

    [Theory]
    [InlineData("\x1b]7;file://host/C:/Users/me\a", @"C:\Users\me")]
    [InlineData("\x1b]7;file://host/C:/My%20Projects/app\a", @"C:\My Projects\app")]
    [InlineData("\x1b]7;file:///home/me/src\a", "/home/me/src")]
    [InlineData("\x1b]7;file://wsl.localhost/Ubuntu-24.04/home/me/src\a", @"\\wsl.localhost\Ubuntu-24.04\home\me\src")]
    [InlineData("\x1b]9;9;\"C:\\Users\\me\\code\"\x1b\\", @"C:\Users\me\code")]
    [InlineData("\x1b]9;9;C:\\plain\a", @"C:\plain")]
    public void Cwd(string input, string expected)
    {
        var r = new Recorder();
        r.Scanner.Feed(input);
        Assert.Equal(new[] { expected }, r.Cwds);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Osc7WithoutPathIsIgnored()
    {
        var r = new Recorder();
        r.Scanner.Feed("\x1b]7;not-a-url\a\x1b]7;file://hostonly\a");
        Assert.Empty(r.Cwds);
    }

    [Fact]
    public void Osc9IsANotification()
    {
        var r = new Recorder();
        r.Scanner.Feed("\x1b]9;Build finished in 42s\a");
        Assert.Equal(new[] { ((string?)null, "Build finished in 42s") }, r.Notes);
    }

    [Theory]
    [InlineData("\x1b]9;4;1;50\a", 1, 50)]
    [InlineData("\x1b]9;4;0;0\a", 0, 0)]
    [InlineData("\x1b]9;4;3\a", 3, 0)]
    public void Osc94IsProgressNotANotification(string input, int state, int pct)
    {
        var r = new Recorder();
        r.Scanner.Feed(input);
        Assert.Equal(new[] { (state, pct) }, r.Progress);
        Assert.Empty(r.Notes);
    }

    [Theory]
    [InlineData("\x1b]9;1;500\a")]
    [InlineData("\x1b]9;12\a")]
    public void ConEmuNumericSubcommandsAreNotNotifications(string input)
    {
        var r = new Recorder();
        r.Scanner.Feed(input);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Osc777Notify()
    {
        var r = new Recorder();
        r.Scanner.Feed("\x1b]777;notify;Build;Done; all green\a");
        Assert.Equal(new[] { ((string?)"Build", "Done; all green") }, r.Notes);
    }

    [Fact]
    public void KittyTitleOnly()
    {
        var r = new Recorder();
        r.Scanner.Feed("\x1b]99;;Tests passed\x1b\\");
        Assert.Equal(new[] { ((string?)null, "Tests passed") }, r.Notes);
    }

    [Fact]
    public void KittyChunkedTitleAndBody()
    {
        var r = new Recorder();
        r.Scanner.Feed("\x1b]99;i=1:d=0;Deploy\x1b\\");
        Assert.Empty(r.Notes);
        r.Scanner.Feed("\x1b]99;i=1:d=0:p=body;to stag\x1b\\\x1b]99;i=1:p=body;ing done\x1b\\");
        Assert.Equal(new[] { ((string?)"Deploy", "to staging done") }, r.Notes);
    }

    [Fact]
    public void KittyBase64Payload()
    {
        var r = new Recorder();
        r.Scanner.Feed("\x1b]99;e=1;" + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("héllo")) + "\a");
        Assert.Equal(new[] { ((string?)null, "héllo") }, r.Notes);
    }

    [Fact]
    public void SequenceSplitAcrossChunks()
    {
        var r = new Recorder();
        r.Scanner.Feed("output\x1b");
        r.Scanner.Feed("]9;Hel");
        r.Scanner.Feed("lo\x1b");
        r.Scanner.Feed("\\more");
        Assert.Equal(new[] { ((string?)null, "Hello") }, r.Notes);
    }

    [Fact]
    public void BellOutsideOscOnly()
    {
        var r = new Recorder();
        r.Scanner.Feed("a\ab\x1b]0;t\ac\a");
        Assert.Equal(2, r.Bells);
        Assert.Equal(new[] { "t" }, r.Titles);
    }

    [Fact]
    public void RunawaySequenceIsDroppedAndScannerRecovers()
    {
        var r = new Recorder();
        r.Scanner.Feed("\x1b]9;" + new string('x', 20000));
        r.Scanner.Feed("\a\x1b]0;after\a");
        Assert.Empty(r.Notes);
        Assert.Equal(new[] { "after" }, r.Titles);
    }

    [Fact]
    public void NewOscRestartsAnUnterminatedOne()
    {
        var r = new Recorder();
        r.Scanner.Feed("\x1b]0;first\x1b]0;second\a");
        Assert.Equal(new[] { "second" }, r.Titles);
    }

    [Fact]
    public void OtherEscapeSequencesAreIgnored()
    {
        var r = new Recorder();
        r.Scanner.Feed("\x1b[31mred\x1b[0m \x1b[?1049h\x1b]133;A\a\x1b]8;;https://x.dev\ax\x1b]8;;\a");
        Assert.Empty(r.Titles);
        Assert.Empty(r.Notes);
        Assert.Empty(r.Cwds);
        Assert.Equal(0, r.Bells);
    }
}
