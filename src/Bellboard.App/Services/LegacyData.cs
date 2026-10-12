using System;
using System.Collections.Generic;
using System.IO;
using Bellboard.Shared;

namespace Bellboard.Services;

/// <summary>
/// Bellboard was called wmux until 0.3. On the first start after the rename, %LOCALAPPDATA%\wmux and
/// %APPDATA%\wmux move to their Bellboard names so the session, settings and browser data carry over.
/// </summary>
internal static class LegacyData
{
    /// <summary>Runs before anything opens the data folders. Returns what happened, for the log.</summary>
    public static List<string> Migrate()
    {
        var notes = new List<string>();
        if (Environment.GetEnvironmentVariable(BellboardProtocol.EnvHome) is { Length: > 0 }) return notes; // isolated instance
        Move(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wmux"), BellboardProtocol.LocalDataDir, notes);
        Move(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wmux"), BellboardProtocol.RoamingDataDir, notes);
        return notes;
    }

    internal static void Move(string from, string to, List<string> notes)
    {
        if (!Directory.Exists(from) || Directory.Exists(to)) return;
        try
        {
            Directory.Move(from, to);
            notes.Add($"moved {from} to {to}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // wmux is still running and holds its browser data: take the settings and session, leave the rest.
            try
            {
                Directory.CreateDirectory(to);
                foreach (var file in Directory.GetFiles(from, "*.json"))
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
                notes.Add($"copied settings and session from {from} to {to} ({ex.Message.Trim()})");
            }
            catch (Exception copyEx)
            {
                notes.Add($"couldn't bring over {from}: {copyEx.Message}");
            }
        }
    }
}
