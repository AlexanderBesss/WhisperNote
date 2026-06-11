using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
        var dir = AppDomain.CurrentDomain.BaseDirectory;

        if (provider.IsLocal)
        {
            _serverExe = !string.IsNullOrEmpty(provider.ServerExe)
                ? Path.Combine(dir, provider.ServerExe)
                : Path.Combine(dir, AppConfig.ServerExeRelative);
            _modelPath = Path.Combine(dir, "models", provider.Model);
            _mmprojPath = !string.IsNullOrEmpty(provider.Mmproj)
                ? Path.Combine(dir, "models", provider.Mmproj)
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

        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var modelDest = Path.Combine(dir, "models", CurrentProvider.Model);
        await ModelDownloader.EnsureModelAsync(CurrentProvider.HfRepo, CurrentProvider.Model, modelDest, progress, ct);

        if (!string.IsNullOrEmpty(CurrentProvider.Mmproj))
        {
            var mmprojDest = Path.Combine(dir, "models", CurrentProvider.Mmproj);
            await ModelDownloader.EnsureModelAsync(CurrentProvider.HfRepo, CurrentProvider.Mmproj, mmprojDest, progress, ct);
        }
    }

    public bool IsLocal => CurrentProvider?.IsLocal == true;
    public bool IsRunning => _process != null && !_process.HasExited;

    public async Task StartAsync()
    {
        if (!IsLocal) return;
        if (_serverExe == null || !File.Exists(_serverExe))
            throw new FileNotFoundException("llama-server.exe not found");

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
        catch { }
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
        catch { }
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
        if (_process == null)
            return;

        if (_process.HasExited)
        {
            _process = null;
            return;
        }

        try
        {
            Logger.Info($"Stopping server (PID: {_process.Id})");
            KillProcessTree(_process.Id);
            _process.WaitForExit(WaitForExitTimeoutMs);
        }
        catch (Exception ex)
        {
            Logger.Error($"Stop server: {ex.Message}");
        }
        finally
        {
            _process = null;
        }
    }

    static void KillProcessTree(int pid)
    {
        try
        {
            using var psi = new ProcessStartInfo("taskkill", $"/F /T /PID {pid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            Process.Start(psi)?.WaitForExit(WaitForExitTimeoutMs);
        }
        catch { }
    }

    string ServerArgs()
    {
        var mmprojArg = _mmprojPath != null ? $"--mmproj \"{_mmprojPath}\" --mmproj-offload " : "";
        return
            $"-m \"{_modelPath}\" " +
            mmprojArg +
            $"--port {AppConfig.ServerPort} --host 0.0.0.0 " +
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
