using System;
using System.Diagnostics;
using System.IO;

namespace WhisperNote.Services;

public static class Logger
{
    const long MaxLogSizeBytes = 5 * 1024 * 1024;

    static readonly string LogPath;
    static readonly object Lock = new();

    static Logger()
    {
        LogPath = AppPaths.LogPath;
        try
        {
            RotateIfNeeded();
            EnsureLogFile();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Logger initialization failed: {ex.Message}");
        }
    }

    public static void Initialize()
    {
    }

    static void EnsureLogFile()
    {
        using var _ = File.Open(LogPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
    }

    static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > MaxLogSizeBytes)
            {
                var backup = Path.ChangeExtension(LogPath, ".log.old");
                if (File.Exists(backup))
                    File.Delete(backup);
                File.Move(LogPath, backup);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Logger rotation failed: {ex.Message}");
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Error(string message) => Log("ERROR", message);

    static void Log(string level, string message)
    {
        lock (Lock)
        {
            try
            {
                RotateIfNeeded();
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Logger failed to write: {ex.Message}");
            }
        }
    }
}
