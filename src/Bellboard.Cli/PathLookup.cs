using System;
using System.IO;

namespace Bellboard.Cli.Terminal;

/// <summary>PATH lookup for the CLI (the app has its own in ShellResolver).</summary>
internal static class ShellResolverLite
{
    public static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
