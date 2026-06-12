using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using WhisperNote.Config;

namespace WhisperNote.Services;

public class LlmServer : IDisposable
{
    const int WaitForExitTimeoutMs = 10000;
    const int PortWaitTimeoutMs = 30000;
    const int PortWaitIntervalMs = 500;

    string? _serverExe;
    string? _modelPath;
    string? _mmprojPath;
    Process? _process;
    bool _thinkingEnabled;

    public ProviderConfig? CurrentProvider { get; private set; }

    public void SetThinkingEnabled(bool enabled) => _thinkingEnabled = enabled;

    public void Configure(ProviderConfig provider)
    {
        CurrentProvider = provider;
        var dir = AppPaths.BaseDirectory;

        if (provider.IsLocal)
        {
            _serverExe = !string.IsNullOrEmpty(provider.ServerExe)
                ? Path.Combine(dir, provider.ServerExe)
                : Path.Combine(dir, AppConfig.ServerExeRelative);
            _modelPath = AppPaths.ResolveModelPath(provider.Model);
            _mmprojPath = !string.IsNullOrEmpty(provider.Mmproj)
                ? AppPaths.ResolveModelPath(provider.Mmproj)
                : null;
        }
        else
        {
            _serverExe = null;
            _modelPath = null;
            _mmprojPath = null;
        }
    }

    public async Task EnsureModelsAsync(Action<string, long, long> progress, CancellationToken ct = default)
    {
        if (!IsLocal || CurrentProvider == null) return;
        if (string.IsNullOrEmpty(CurrentProvider.HfRepo)) return;

        _modelPath = await EnsureModelFileAsync(
            CurrentProvider.HfRepo,
            CurrentProvider.Model,
            progress,
            ct);

        if (!string.IsNullOrEmpty(CurrentProvider.Mmproj))
        {
            _mmprojPath = await EnsureModelFileAsync(
                CurrentProvider.HfRepo,
                CurrentProvider.Mmproj,
                progress,
                ct);
        }
    }

    static async Task<string> EnsureModelFileAsync(
        string repo,
        string fileName,
        Action<string, long, long> progress,
        CancellationToken ct)
    {
        var resolvedPath = AppPaths.ResolveModelPath(fileName);
        if (File.Exists(resolvedPath))
        {
            Logger.Info($"Model exists: {resolvedPath}");
            return resolvedPath;
        }

        var writablePath = AppPaths.WritableModelPath(fileName);
        await ModelDownloader.EnsureModelAsync(repo, fileName, writablePath, progress, ct);
        return writablePath;
    }

    public bool IsLocal => CurrentProvider?.IsLocal == true;
    public bool IsRunning => _process != null && !_process.HasExited;

    public async Task StartAsync()
    {
        if (!IsLocal) return;
        if (IsRunning) return;
        if (_serverExe == null || !File.Exists(_serverExe))
            throw new FileNotFoundException("llama-server.exe not found");
        if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
            throw new FileNotFoundException("Model file not found", _modelPath);
        if (!string.IsNullOrEmpty(_mmprojPath) && !File.Exists(_mmprojPath))
            throw new FileNotFoundException("Multimodal projector file not found", _mmprojPath);

        Stop();

        if (!await WaitForPortFreeAsync())
            throw new InvalidOperationException($"Port {AppConfig.ServerPort} is still in use after {PortWaitTimeoutMs}ms");

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = _serverExe,
            Arguments = ServerArgs(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Failed to start server process");

        _ = Task.Run(() => LogProcessOutput(_process));
        Logger.Info($"Server started (PID: {_process.Id})");
    }

    static async Task LogProcessOutput(Process process)
    {
        try
        {
            await Task.WhenAll(
                ReadStreamAsync(process.StandardOutput, "[Server-out]"),
                ReadStreamAsync(process.StandardError, "[Server-err]")
            );
        }
        catch (Exception ex)
        {
            Logger.Error($"Server output logging failed: {ex.Message}");
        }
    }

    static async Task ReadStreamAsync(StreamReader reader, string prefix)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (!string.IsNullOrEmpty(line))
                    Logger.Info($"{prefix} {line}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Server stream read failed: {ex.Message}");
        }
    }

    static bool IsPortInUse(int port)
    {
        try
        {
            var ipProps = IPGlobalProperties.GetIPGlobalProperties();
            var listeners = ipProps.GetActiveTcpListeners();
            foreach (var ep in listeners)
            {
                if (ep.Port == port)
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"IsPortInUse check failed: {ex.Message}");
            return false;
        }
    }

    async Task<bool> WaitForPortFreeAsync()
    {
        var elapsed = 0;
        while (IsPortInUse(AppConfig.ServerPort) && elapsed < PortWaitTimeoutMs)
        {
            Logger.Info($"Port {AppConfig.ServerPort} in use, waiting... ({elapsed}ms)");
            await Task.Delay(PortWaitIntervalMs);
            elapsed += PortWaitIntervalMs;
        }
        return !IsPortInUse(AppConfig.ServerPort);
    }

    public void Stop()
    {
        var process = _process;
        if (process == null)
            return;

        _process = null;

        try
        {
            if (!process.HasExited)
            {
                Logger.Info($"Stopping server (PID: {process.Id})");
                KillProcessTree(process.Id);
                process.WaitForExit(WaitForExitTimeoutMs);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Stop server: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    static void KillProcessTree(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill", $"/F /T /PID {pid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            Process.Start(psi)?.WaitForExit(WaitForExitTimeoutMs);
        }
        catch (Exception ex)
        {
            Logger.Error($"Kill process tree: {ex.Message}");
        }
    }

    string ServerArgs()
    {
        var mmprojArg = _mmprojPath != null ? $"--mmproj \"{_mmprojPath}\" --mmproj-offload " : "";
        return
            $"-m \"{_modelPath}\" " +
            mmprojArg +
            $"--port {AppConfig.ServerPort} --host 127.0.0.1 " +
            $"--gpu-layers {AppConfig.GpuLayers} --ctx-size {AppConfig.ContextSize} " +
            $"--cache-type-k q4_0 --cache-type-v q4_0 " +
            $"--flash-attn on " +
            $"--batch-size {AppConfig.BatchSize} --ubatch-size {AppConfig.UBatchSize} " +
            $"--no-mmap " +
            "--jinja " +
            $"--temp {AppConfig.Temperature} --top-p {AppConfig.TopP} --min-p {AppConfig.MinP} --repeat-penalty {AppConfig.RepeatPenalty} " +
            $"--reasoning {(_thinkingEnabled ? "on" : "off")} " +
            $"--metrics --slots --perf";
    }

    public void Dispose() => Stop();
}
