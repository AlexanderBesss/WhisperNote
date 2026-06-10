using System;
using Microsoft.Win32;
using WhisperNote.Services;

namespace WhisperNote;

static class StartupRegistry
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "WhisperNote";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key == null) return false;
            return key.GetValue(AppName) != null;
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
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;

            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path))
                {
                    Logger.Error("Cannot add to startup: ProcessPath is null");
                    return;
                }
                key.SetValue(AppName, path);
            }
            else
                key.DeleteValue(AppName, false);
        }
        catch (Exception ex)
        {
            Logger.Error($"StartupRegistry.SetEnabled: {ex.Message}");
        }
    }
}
