using System;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace WhisperNote.Services;

// Kernel anti-cheat drivers block SendInput/keybd_event system-wide; no injection flag bypasses this.
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
                    if (MatchLabel(name) is { } label)
                        return label;
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
                if (MatchLabel(name) is { } label)
                    return label;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"InputBlockerDetector (processes): {ex.Message}");
        }

        return null;
    }

    static string? MatchLabel(string name)
    {
        foreach (var (match, label) in KnownBlockers)
        {
            if (name.Contains(match, StringComparison.Ordinal))
                return label;
        }

        return null;
    }
}
