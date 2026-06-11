using System;
using System.IO;

namespace WhisperNote.Services;

public static class AppPaths
{
    public static string BaseDirectory => AppDomain.CurrentDomain.BaseDirectory;

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperNote");

    public static string SettingsPath => Path.Combine(BaseDirectory, "whispernote.json");
    public static string LogPath => Path.Combine(BaseDirectory, "logs.log");
    public static string ModelsDirectory => Path.Combine(DataDirectory, "models");
    public static string BundledModelsDirectory => Path.Combine(BaseDirectory, "models");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);

    public static string ResolveModelPath(string fileName)
    {
        var bundledPath = Path.Combine(BundledModelsDirectory, fileName);
        return File.Exists(bundledPath)
            ? bundledPath
            : WritableModelPath(fileName);
    }

    public static string WritableModelPath(string fileName) =>
        Path.Combine(ModelsDirectory, fileName);
}
