using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WhisperNote.Config;

namespace WhisperNote.Services;

public class LlmServer : IDisposable
{
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

    public void Start()
    {
        if (!IsLocal) return;
        if (_serverExe == null || !File.Exists(_serverExe))
            throw new FileNotFoundException("llama-server.exe not found");

        Stop();

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = _serverExe,
            Arguments = ServerArgs(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Failed to start server process");

        Logger.Info($"Server started (PID: {_process.Id})");
    }

    public void Stop()
    {
        if (_process != null && !_process.HasExited)
        {
            try
            {
                Logger.Info($"Stopping server (PID: {_process.Id})");
                _process.Kill();
                _process.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Logger.Error($"Stop server: {ex.Message}");
            }
        }
        _process = null;
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
