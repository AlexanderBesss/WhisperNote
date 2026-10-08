using System;
using System.IO;
using Microsoft.Win32;
using WhisperNote.Services;

namespace WhisperNote;

static class StartupRegistry
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "WhisperNote";
    // Passed when Windows launches the app at sign-in so it starts hidden in the tray.
    public const string StartupArg = "--startup";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(AppName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            // A stale entry pointing at another copy (old dev bin, moved folder)
            // must not count: it would neither start this exe nor be ours to adopt.
            return PointsAtCurrentExe(value);
        }
        catch (Exception ex)
        {
            Logger.Error($"StartupRegistry.IsEnabled: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key == null) return;

            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path))
                {
                    Logger.Error("Cannot add to startup: ProcessPath is null");
                    return;
                }
                key.SetValue(AppName, $"\"{path}\" {StartupArg}");
            }
            else
                key.DeleteValue(AppName, false);
        }
        catch (Exception ex)
        {
            Logger.Error($"StartupRegistry.SetEnabled: {ex.Message}");
        }
    }

    static bool PointsAtCurrentExe(string registeredValue)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current))
            return true; // Cannot compare: fall back to the legacy existence check.
        var registeredPath = ExtractExePath(registeredValue);
        if (string.IsNullOrEmpty(registeredPath))
            return false;
        return PathsMatch(registeredPath, current);
    }

    internal static bool PathsMatch(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string ExtractExePath(string value)
    {
        value = value.Trim();
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1)
                return value.Substring(1, end - 1);
            return "";
        }
        // Unquoted legacy value: the exe path is the first whitespace-delimited token.
        var space = value.IndexOf(' ');
        return space < 0 ? value : value.Substring(0, space);
    }
}
