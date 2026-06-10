using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperNote.Services;

public static class ModelDownloader
{
    const string HfBaseUrl = "https://huggingface.co";

    public static async Task EnsureModelAsync(
        string repo, string filename, string destPath,
        Action<string, long, long> progress, CancellationToken ct = default)
    {
        if (File.Exists(destPath))
        {
            Logger.Info($"Model exists: {destPath}");
            return;
        }

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        progress($"Downloading {filename}...", 0, 0);

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromHours(2)
        };
        client.DefaultRequestHeaders.Add("User-Agent", "WhisperNote/1.0");

        var url = $"{HfBaseUrl}/{repo}/resolve/main/{filename}";
        Logger.Info($"Downloading {url}");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        progress($"Downloading {filename}...", 0, total);

        var tmpPath = destPath + ".tmp";
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var file = File.Create(tmpPath);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        int read;
        var lastReport = DateTimeOffset.UtcNow;

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromSeconds(1))
            {
                progress($"Downloading {filename}...", downloaded, total);
                lastReport = DateTimeOffset.UtcNow;
            }
        }

        File.Move(tmpPath, destPath);

        Logger.Info($"Downloaded {Path.GetFileName(destPath)} ({FormatBytes(downloaded)})");
        progress($"Downloaded {filename}", downloaded, total);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
