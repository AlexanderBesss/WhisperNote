using System;
using System.Collections.Generic;
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
    // A GPU the bundled kernels were not compiled for aborts within seconds, so watch the
    // first moments of the process: it lets the fallback chain pick another backend instead
    // of reporting "server exited during startup".
    const int StartupHandshakeTimeoutMs = 20000;
    const int StartupPollIntervalMs = 250;
    const int RecentOutputLimit = 200;

    // Text llama.cpp prints when the selected device cannot run the compiled kernels.
    static readonly string[] UnsupportedDeviceMarkers =
    {
        "no kernel image is available",
        "CUDA error",
        "CUDA driver version is insufficient",
        "system not yet initialized",
        "ggml_cuda_init: failed",
        "no compatible GPUs",
        "no Vulkan devices",
        "vkCreateInstance"
    };

    string? _modelPath;
    string? _mmprojPath;
    string? _preferredExeOverride;
    Process? _process;
    bool _useCpuOnly;
    HardwareBackend _preferredBackend = HardwareBackend.Unknown;
    HardwareBackend _backend = HardwareBackend.Unknown;
    string _serverExe = "";
    CancellationTokenSource? _downloadCts;
    readonly Queue<string> _recentOutput = new();
    readonly object _outputLock = new();

    enum StartupOutcome { Ready, Failed, Pending }

    public ProviderConfig? CurrentProvider { get; private set; }
    public HardwareBackend Backend => _backend;

    public void SetUseCpuOnly(bool enabled) => _useCpuOnly = enabled;

    public void Configure(ProviderConfig provider)
    {
        CurrentProvider = provider;
        var dir = AppPaths.BaseDirectory;

        if (provider.IsLocal)
        {
            _preferredBackend = App.DetectedBackend;
            _backend = _preferredBackend;
            _preferredExeOverride = ResolveExeOverride(dir, provider.ServerExe);
            _serverExe = ResolveServerExe(_backend);

            Logger.Info($"Local server: {_serverExe} (backend: {_backend})");
            _modelPath = AppPaths.ResolveModelPath(provider.Model);
            _mmprojPath = !string.IsNullOrEmpty(provider.Mmproj)
                ? AppPaths.ResolveModelPath(provider.Mmproj)
                : null;
        }
        else
        {
            _modelPath = null;
            _mmprojPath = null;
        }
    }

    /// <summary>
    /// Explicit ServerExe from the provider settings. Settings written by older versions
    /// always pinned the CUDA 13 binary; on a machine where another backend was detected
    /// that stale value would force the wrong exe and burn the first fallback attempt,
    /// so it is ignored unless the CUDA 13 backend is the one actually detected.
    /// </summary>
    string? ResolveExeOverride(string dir, string? serverExe)
    {
        if (string.IsNullOrEmpty(serverExe))
            return null;

        if (string.Equals(serverExe, AppConfig.CudaServerExeRelative, StringComparison.OrdinalIgnoreCase)
            && _preferredBackend != HardwareBackend.NvidiaCuda)
        {
            Logger.Info($"Ignoring the default ServerExe pin ({serverExe}) on the {_preferredBackend} backend");
            return null;
        }

        return Path.Combine(dir, serverExe);
    }

    /// <summary>
    /// Server binary for a backend. An explicit provider override pins the detected
    /// backend only, so a stale value cannot block the fallback chain from switching.
    /// </summary>
    internal string ResolveServerExe(HardwareBackend backend) =>
        backend == _preferredBackend && _preferredExeOverride != null
            ? _preferredExeOverride
            : Path.Combine(AppPaths.BaseDirectory, ExeRelativeForBackend(backend));

    static string ExeRelativeForBackend(HardwareBackend backend) => backend switch
    {
        HardwareBackend.Vulkan => AppConfig.VulkanServerExeRelative,
        HardwareBackend.NvidiaCudaLegacy => AppConfig.CudaLegacyServerExeRelative,
        HardwareBackend.IntelNpu => AppConfig.NpuServerExeRelative,
        _ => AppConfig.CudaServerExeRelative
    };

    /// <summary>
    /// Backends to try, best first. The CUDA 13 build cannot run pre-Turing NVIDIA GPUs,
    /// Vulkan covers those plus AMD and Intel, and the CPU is the last resort.
    /// </summary>
    internal HardwareBackend[] FallbackChain()
    {
        if (_useCpuOnly)
            return new[] { HardwareBackend.Cpu };

        return _preferredBackend switch
        {
            HardwareBackend.NvidiaCuda => new[]
            {
                HardwareBackend.NvidiaCuda, HardwareBackend.NvidiaCudaLegacy,
                HardwareBackend.Vulkan, HardwareBackend.Cpu
            },
            HardwareBackend.NvidiaCudaLegacy => new[]
            {
                HardwareBackend.NvidiaCudaLegacy, HardwareBackend.Vulkan, HardwareBackend.Cpu
            },
            HardwareBackend.Vulkan => new[] { HardwareBackend.Vulkan, HardwareBackend.Cpu },
            HardwareBackend.IntelNpu => new[] { HardwareBackend.IntelNpu, HardwareBackend.Cpu },
            HardwareBackend.Cpu => new[] { HardwareBackend.Cpu },
            _ => new[] { _preferredBackend, HardwareBackend.Cpu }
        };
    }

    public async Task EnsureModelsAsync(Action<string, long, long> progress, CancellationToken ct = default)
    {
        if (!IsLocal || CurrentProvider == null) return;
        if (string.IsNullOrEmpty(CurrentProvider.HfRepo)) return;

        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token).Token;

        _modelPath = await EnsureModelFileAsync(
            CurrentProvider.HfRepo,
            CurrentProvider.Model,
            progress,
            linkedCt);

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

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!IsLocal) return;
        if (IsRunning) return;
        if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
            throw new FileNotFoundException("Model file not found", _modelPath);
        if (!string.IsNullOrEmpty(_mmprojPath) && !File.Exists(_mmprojPath))
            throw new FileNotFoundException("Multimodal projector file not found", _mmprojPath);

        BackendUnavailableException? lastFailure = null;
        foreach (var backend in FallbackChain())
        {
            ct.ThrowIfCancellationRequested();

            var serverExe = ResolveServerExe(backend);
            if (!File.Exists(serverExe))
            {
                Logger.Info($"Skipping the {backend} backend: {serverExe} is not installed");
                continue;
            }

            _backend = backend;
            _serverExe = serverExe;

            try
            {
                await StartOnBackendAsync(ct);
                return;
            }
            catch (BackendUnavailableException ex) when (backend != HardwareBackend.Cpu)
            {
                lastFailure = ex;
                Logger.Warn($"{backend} backend cannot run on this device ({ex.Message}); trying the next one");
            }
        }

        if (lastFailure != null)
            throw new InvalidOperationException(
                "No GPU backend can run this model on this device. Check logs.log for details.", lastFailure);

        throw new FileNotFoundException("llama-server.exe not found", _serverExe);
    }

    async Task StartOnBackendAsync(CancellationToken ct)
    {
        Stop();
        lock (_outputLock) _recentOutput.Clear();

        if (!await WaitForPortFreeAsync(ct))
            throw new InvalidOperationException($"Port {AppConfig.ServerPort} is still in use after {PortWaitTimeoutMs}ms");

        ct.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = _serverExe,
            Arguments = ServerArgs(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        if (_backend == HardwareBackend.IntelNpu && !_useCpuOnly)
        {
            startInfo.EnvironmentVariables["GGML_OPENVINO_DEVICE"] = "NPU";
            startInfo.EnvironmentVariables["GGML_OPENVINO_PREFILL_CHUNK_SIZE"] = "512";
            startInfo.EnvironmentVariables["GGML_OPENVINO_STATEFUL_EXECUTION"] = "0";

            var ovRuntime = @"C:\Program Files (x86)\Intel\openvino_2026\runtime\bin\intel64\Release";
            if (Directory.Exists(ovRuntime))
            {
                var existing = startInfo.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH");
                startInfo.EnvironmentVariables["PATH"] = ovRuntime + ";" + existing;
            }
        }

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start server process");

        _ = Task.Run(() => LogProcessOutput(_process));
        Logger.Info($"Server started (PID: {_process.Id}, backend: {_backend})");

        if (await WaitForStartupOutcomeAsync(ct) == StartupOutcome.Failed)
        {
            var output = RecentOutputTail();
            Stop();
            if (IsUnsupportedDeviceError(output))
                throw new BackendUnavailableException(FirstMatchingLine(output));
            throw new InvalidOperationException(
                "Server process exited during startup. Check logs.log for details.");
        }
    }

    /// <summary>
    /// Watches the first moments of the process: it either starts listening, dies on an
    /// unusable device, or stays slow (large model, PTX JIT) and is left to the caller's
    /// health polling.
    /// </summary>
    async Task<StartupOutcome> WaitForStartupOutcomeAsync(CancellationToken ct)
    {
        var elapsed = 0;
        while (elapsed < StartupHandshakeTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (IsListening())
                return StartupOutcome.Ready;
            if (_process == null || _process.HasExited)
                return StartupOutcome.Failed;

            await Task.Delay(StartupPollIntervalMs, ct);
            elapsed += StartupPollIntervalMs;
        }
        return StartupOutcome.Pending;
    }

    bool IsListening()
    {
        lock (_outputLock)
        {
            foreach (var line in _recentOutput)
            {
                if (line.Contains("listening on", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    internal static bool IsUnsupportedDeviceError(string output)
    {
        foreach (var marker in UnsupportedDeviceMarkers)
        {
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static string FirstMatchingLine(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var marker in UnsupportedDeviceMarkers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return line.Trim();
            }
        }
        return "device is not supported by this backend";
    }

    string RecentOutputTail()
    {
        lock (_outputLock)
        {
            return string.Join('\n', _recentOutput);
        }
    }

    async Task LogProcessOutput(Process process)
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

    async Task ReadStreamAsync(StreamReader reader, string prefix)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrEmpty(line))
                    continue;

                Logger.Info($"{prefix} {line}");

                // Kept so a startup failure can be classified without re-reading the log file.
                lock (_outputLock)
                {
                    _recentOutput.Enqueue(line);
                    while (_recentOutput.Count > RecentOutputLimit)
                        _recentOutput.Dequeue();
                }
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

    async Task<bool> WaitForPortFreeAsync(CancellationToken ct)
    {
        var elapsed = 0;
        while (IsPortInUse(AppConfig.ServerPort) && elapsed < PortWaitTimeoutMs)
        {
            Logger.Info($"Port {AppConfig.ServerPort} in use, waiting... ({elapsed}ms)");
            await Task.Delay(PortWaitIntervalMs, ct);
            elapsed += PortWaitIntervalMs;
        }
        return !IsPortInUse(AppConfig.ServerPort);
    }

    public void Stop()
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = null;

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

    // Dedicated ASR models (Qwen3-ASR) are decoded greedily; chat-style
    // sampling parameters (min-p, repeat penalty, temperature) degrade their output.
    string SamplingArgs() => LocalModels.IsDedicatedAsr(CurrentProvider?.Model)
        ? "--temp 0 "
        : $"--temp {AppConfig.Temperature} --top-p {AppConfig.TopP} --min-p {AppConfig.MinP} --repeat-penalty {AppConfig.RepeatPenalty} ";

    internal string ServerArgs()
    {
        if (_useCpuOnly || _backend == HardwareBackend.Cpu)
            return CpuServerArgs();

        if (_backend == HardwareBackend.IntelNpu)
            return NpuServerArgs();

        var mmprojArg = _mmprojPath != null ? $"--mmproj \"{_mmprojPath}\" --mmproj-offload " : "";
        return
            $"-m \"{_modelPath}\" " +
            mmprojArg +
            $"--port {AppConfig.ServerPort} --host 127.0.0.1 " +
            $"--gpu-layers {AppConfig.GpuLayers} --ctx-size {AppConfig.ContextSize} " +
            $"--cache-type-k q4_0 --cache-type-v q4_0 " +
            $"--flash-attn on " +
            $"--batch-size {AppConfig.BatchSize} --ubatch-size {AppConfig.UBatchSize} " +
            "--jinja " +
            SamplingArgs() +
            $"--metrics --slots --perf";
    }

    string CpuServerArgs()
    {
        var mmprojArg = _mmprojPath != null ? $"--mmproj \"{_mmprojPath}\" " : "";
        return
            $"-m \"{_modelPath}\" " +
            mmprojArg +
            $"--port {AppConfig.ServerPort} --host 127.0.0.1 " +
            // --device none keeps the CUDA backend from initializing at all; without it
            // the CUDA build probes the GPU even with --gpu-layers 0 and aborts on GPUs
            // the bundled kernels were not compiled for (e.g. "no kernel image").
            "--device none " +
            "--gpu-layers 0 " +
            $"--ctx-size {AppConfig.ContextSize} " +
            // Quantized KV cache requires flash attention, which is unreliable on CPU; use the f16 default instead.
            "--flash-attn off " +
            $"--batch-size {AppConfig.BatchSize} --ubatch-size {AppConfig.UBatchSize} " +
            "--jinja " +
            SamplingArgs() +
            "--metrics --slots --perf";
    }

    string NpuServerArgs()
    {
        var mmprojArg = _mmprojPath != null ? $"--mmproj \"{_mmprojPath}\" " : "";
        return
            $"-m \"{_modelPath}\" " +
            mmprojArg +
            $"--port {AppConfig.ServerPort} --host 127.0.0.1 " +
            $"--gpu-layers {AppConfig.NpuGpuLayers} " +
            $"--parallel 1 " +
            $"--ctx-size {AppConfig.NpuContextSize} " +
            $"--cache-type-k q4_0 --cache-type-v q4_0 " +
            $"--batch-size {AppConfig.NpuBatchSize} --ubatch-size {AppConfig.NpuUBatchSize} " +
            "--jinja " +
            $"--temp {AppConfig.Temperature} --min-p {AppConfig.MinP} --repeat-penalty {AppConfig.RepeatPenalty} " +
            $"--metrics --slots --perf";
    }

    public void Dispose() => Stop();
}

/// <summary>The selected llama.cpp backend has no kernels for the current device.</summary>
sealed class BackendUnavailableException : Exception
{
    public BackendUnavailableException(string message) : base(message) { }
}

