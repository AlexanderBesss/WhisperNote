using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperNote.Services;

public static class BackendDownloader
{
    const string ReleasesApiUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=15";
    static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    sealed record BackendSpec(string Directory, string Label, string[] AssetPatterns);

    // Patterns must match official llama.cpp Windows release zip asset names.
    static readonly Dictionary<HardwareBackend, BackendSpec> Specs = new()
    {
        [HardwareBackend.NvidiaCuda] = new("llama", "CUDA 13 backend",
            new[] { @"^llama-.*-bin-win-cuda-13.*-x64\.zip$" }),
        [HardwareBackend.NvidiaCudaLegacy] = new("cuda12", "CUDA 12.4 backend",
            new[] { @"^cudart-llama-bin-win-cuda-12\.4-x64\.zip$", @"^llama-.*-bin-win-cuda-12\.4-x64\.zip$" }),
        [HardwareBackend.Vulkan] = new("vulkan", "Vulkan backend",
            new[] { @"^llama-.*-bin-win-vulkan.*-x64\.zip$" }),
        [HardwareBackend.IntelNpu] = new(@"NPU\llama-ov", "OpenVINO NPU backend",
            new[] { @"^llama-.*-bin-win-openvino-.*-x64\.zip$" }),
        [HardwareBackend.Cpu] = new("cpu", "CPU backend",
            new[] { @"^llama-.*-bin-win-cpu-x64\.zip$" }),
    };

    public static bool IsInstalled(HardwareBackend backend) =>
        Specs.TryGetValue(backend, out var spec) &&
        File.Exists(Path.Combine(AppPaths.BaseDirectory, spec.Directory, "llama-server.exe"));

    public static async Task<bool> EnsureAsync(
        HardwareBackend backend,
        Action<string, long, long> progress,
        CancellationToken ct = default)
    {
        if (!Specs.TryGetValue(backend, out var spec))
            return false;
        if (IsInstalled(backend))
            return true;

        try
        {
            var assets = await FindAssetsAsync(spec, ct);
            if (assets == null)
            {
                Logger.Error($"No llama.cpp release asset matched the {spec.Label}");
                return false;
            }

            var targetDir = Path.Combine(AppPaths.BaseDirectory, spec.Directory);
            for (var i = 0; i < assets.Count; i++)
                await DownloadAndExtractAsync(assets[i], targetDir, spec.Label, i + 1, assets.Count, progress, ct);

            if (!IsInstalled(backend))
            {
                Logger.Error($"The {spec.Label} download finished but llama-server.exe is still missing");
                return false;
            }

            Logger.Info($"{spec.Label} installed into {targetDir}");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Backend download failed: {ex.Message}");
            return false;
        }
    }

    static async Task<List<(string Name, string Url)>?> FindAssetsAsync(BackendSpec spec, CancellationToken ct)
    {
        using var client = ModelDownloader.CreateClient(DownloadTimeout);
        using var response = await client.GetAsync(ReleasesApiUrl, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return SelectAssets(json, spec.AssetPatterns);
    }

    /// <summary>GitHub lists releases newest-first, so the first full-pattern match is the newest.</summary>
    internal static List<(string Name, string Url)>? SelectAssets(string releasesJson, string[] patterns)
    {
        using var document = JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || !release.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
                continue;

            var picked = new List<(string Name, string Url)>();
            foreach (var pattern in patterns)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name != null && url != null && Regex.IsMatch(name, pattern))
                    {
                        picked.Add((name, url));
                        break;
                    }
                }
            }

            if (picked.Count == patterns.Length)
                return picked;
        }

        return null;
    }

    static async Task DownloadAndExtractAsync(
        (string Name, string Url) asset,
        string targetDir,
        string label,
        int index,
        int count,
        Action<string, long, long> progress,
        CancellationToken ct)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"whispernote-backend-{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"whispernote-backend-{Guid.NewGuid():N}");
        var message = count > 1 ? $"Downloading {label} ({index}/{count})..." : $"Downloading {label}...";

        try
        {
            progress(message, 0, 0);
            Logger.Info($"Downloading {asset.Url}");

            using (var client = ModelDownloader.CreateClient(DownloadTimeout))
            using (var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                progress(message, 0, total);

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var file = File.Create(zipPath);
                await ModelDownloader.CopyWithProgressAsync(stream, file, message, total, progress, ct);
            }

            progress($"Installing {label}...", 0, 0);
            ExtractZip(zipPath, extractDir);
            CopyContents(ResolveZipRoot(extractDir), targetDir);
            Logger.Info($"Installed {asset.Name} ({ModelDownloader.FormatBytes(new FileInfo(zipPath).Length)})");
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
            try { Directory.Delete(extractDir, true); } catch { }
        }
    }

    static void ExtractZip(string zipPath, string extractDir)
    {
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir);
    }

    /// <summary>Release zips wrap everything in one top-level folder; descend into it.</summary>
    static string ResolveZipRoot(string extractDir)
    {
        var directories = Directory.GetDirectories(extractDir);
        var files = Directory.GetFiles(extractDir);
        return directories.Length == 1 && files.Length == 0 ? directories[0] : extractDir;
    }

    static void CopyContents(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var nested = Path.Combine(targetDir, Path.GetFileName(dir));
            Directory.CreateDirectory(nested);
            CopyContents(dir, nested);
        }
    }
}
