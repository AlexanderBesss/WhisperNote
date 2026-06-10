using System;
using System.Diagnostics;
using System.IO;

namespace WhisperNote.Services;

public static class Logger
{
    static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs.log");

    static Logger()
    {
        File.Delete(LogPath);
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Error(string message) => Log("ERROR", message);

    static void Log(string level, string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Logger failed to write: {ex.Message}");
        }
    }
}
