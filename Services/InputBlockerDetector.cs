using System;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace WhisperNote.Services;

/// <summary>
/// Kernel-level anti-cheat drivers (FACEIT, Vanguard, EAC, BattlEye) block
/// synthesized keystrokes system-wide while active. No SendInput/keybd_event
/// flag can get through that; the only fix is closing the blocker. Detect the
/// common ones so a paste failure can name the culprit instead of looking like
/// an app bug.
/// </summary>
static class InputBlockerDetector
{
    static readonly (string Match, string Label)[] KnownBlockers =
    {
        ("faceit", "FACEIT Anti-Cheat"),
        ("vgc", "Riot Vanguard"),
        ("vanguard", "Riot Vanguard"),
        ("easyanticheat", "EasyAntiCheat"),
        ("beservice", "BattlEye"),
        ("battleye", "BattlEye"),
    };

    static readonly object CacheLock = new();
    static string? _cached;
    static DateTime _cachedAt = DateTime.MinValue;
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public static string? FindActiveBlocker()
    {
        lock (CacheLock)
        {
            if (DateTime.UtcNow - _cachedAt < CacheFor)
                return _cached;
            _cached = Detect();
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    static string? Detect()
    {
        try
        {
            using var services = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Service WHERE Started = TRUE");
            foreach (var service in services.Get().Cast<ManagementObject>())
            {
                using (service)
                {
                    var name = (service["Name"] as string ?? "").ToLowerInvariant();
                    foreach (var (match, label) in KnownBlockers)
                    {
                        if (name.Contains(match, StringComparison.Ordinal))
                            return label;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"InputBlockerDetector (services): {ex.Message}");
        }

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                string name;
                try
                {
                    name = process.ProcessName.ToLowerInvariant();
                }
                catch
                {
                    continue;
                }
                finally
                {
                    process.Dispose();
                }
                foreach (var (match, label) in KnownBlockers)
                {
                    if (name.Contains(match, StringComparison.Ordinal))
                        return label;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"InputBlockerDetector (processes): {ex.Message}");
        }

        return null;
    }
}
