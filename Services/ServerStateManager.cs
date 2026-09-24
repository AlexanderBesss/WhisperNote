using System;
using System.Threading;
using System.Threading.Tasks;
using WhisperNote.Config;
using WhisperNote.Models;
using WhisperNote.ViewModels;

namespace WhisperNote.Services;

public class ServerStateManager : ViewModel, IDisposable
{
    const int StartMaxAttempts = 60;
    const int StartPollIntervalMs = 1000;
    const int WaitPollIntervalMs = 2000;

    LlmServer _server;
    TranscriptionService _transcription;
    readonly AppState _state;
    readonly SemaphoreSlim _operationLock = new(1, 1);
    readonly object _startLock = new();
    readonly object _remoteWarmupLock = new();
    CancellationTokenSource? _remoteWarmupCts;

    ServerStatus _status = ServerStatus.Offline;
    public ServerStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    public ServerStateManager(AppState state)
    {
        _state = state;
        var provider = state.ActiveProvider;
        if (provider == null)
            throw new InvalidOperationException("No active provider configured. Check whispernote.json or restart to create defaults.");
        _server = new LlmServer();
        _server.Configure(provider);
        _server.SetThinkingEnabled(_state.ThinkingEnabled);
        _server.SetUseCpuOnly(_state.UseCpuOnly);
        _transcription = new TranscriptionService(provider);
        App.RegisterServerForCleanup(_server);
    }

    async Task WithOperationLockAsync(Func<Task> operation, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            await operation();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    async Task<T> WithOperationLockAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            return await operation();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public bool IsServerRunning => _server.IsRunning;
    public bool IsLocal => _state.ActiveProvider?.IsLocal ?? false;
    public HardwareBackend Backend => _server.Backend;

    public bool IsStarting => _isStarting;

    public Task StartAsync(Action<string, long, long> progress, CancellationToken ct = default)
    {
        lock (_startLock)
        {
            ct.ThrowIfCancellationRequested();
            if (_server.IsRunning)
            {
                Status = ServerStatus.Online;
                return Task.CompletedTask;
            }

            if (_isStarting)
            {
                var activeStart = _startTask ?? Task.CompletedTask;
                return ct.CanBeCanceled ? activeStart.WaitAsync(ct) : activeStart;
            }

            _isStarting = true;
            _startTask = StartCoreAsync(progress, ct);
            return _startTask;
        }
    }

    async Task StartCoreAsync(Action<string, long, long> progress, CancellationToken ct)
    {
        var lockTaken = false;
        try
        {
            await _operationLock.WaitAsync(ct);
            lockTaken = true;

            if (await _transcription.IsServerReady(ct))
            {
                Status = ServerStatus.Online;
                return;
            }

            _server.SetThinkingEnabled(_state.ThinkingEnabled);
            _server.SetUseCpuOnly(_state.UseCpuOnly);
            await _server.EnsureModelsAsync(progress, ct);
            await _server.StartAsync(ct);
            Status = ServerStatus.Launching;

            if (await WaitForReadyAsync(StartMaxAttempts, StartPollIntervalMs, ct))
            {
                Status = ServerStatus.Online;
                return;
            }

            throw new TimeoutException("Server failed to start within 60 seconds");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (lockTaken)
            {
                _server.Stop();
                Status = ServerStatus.Offline;
            }
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Server start: {ex.Message}");
            Status = ServerStatus.Failed(ex.Message);
            throw;
        }
        finally
        {
            lock (_startLock)
            {
                _isStarting = false;
                _startTask = null;
            }
            if (lockTaken)
                _operationLock.Release();
        }
    }

    async Task<bool> WaitForReadyAsync(int maxAttempts, int pollIntervalMs, CancellationToken ct)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(pollIntervalMs, ct);
            if (await _transcription.IsServerReady(ct))
                return true;
        }
        return false;
    }

    public async Task InitializeAsync()
    {
        var provider = _state.ActiveProvider;
        if (provider == null)
        {
            Status = ServerStatus.Failed("No provider configured");
            return;
        }
        if (!provider.IsLocal)
        {
            if (provider.IsRemoteExecution)
            {
                var ready = await _transcription.IsServerReady();
                Status = ready ? ServerStatus.RemoteConnected : ServerStatus.RemoteUnavailable;
            }
            else
                Status = ServerStatus.Cloud(provider.Name);
            return;
        }

        await WithOperationLockAsync(async () =>
        {
            var ready = await _transcription.IsServerReady();
            if (ready)
                Status = ServerStatus.Online;
        });
    }

    public async Task ToggleServerAsync(Action<string?> updateInfo)
    {
        var provider = _state.ActiveProvider;
        if (provider == null)
        {
            updateInfo("No provider configured");
            return;
        }
        if (!provider.IsLocal)
        {
            if (provider.IsRemoteExecution)
            {
                var ready = await IsServerReady();
                Status = ready ? ServerStatus.RemoteConnected : ServerStatus.RemoteUnavailable;
                updateInfo(ready ? "Remote server connected" : "Remote server unavailable");
            }
            else
                updateInfo($"Cloud provider ({provider.Name}) has no local server");
            return;
        }

        var shouldStart = false;
        try
        {
            await WithOperationLockAsync(async () =>
            {
                if (_server.IsRunning)
                {
                    await Task.Run(() => _server.Stop());
                    Status = ServerStatus.Offline;
                }
                else
                {
                    shouldStart = true;
                }
            });

            if (shouldStart)
                await StartAsync((_, _, _) => { });
        }
        catch (Exception ex)
        {
            Logger.Error($"Server: {ex.Message}");
            updateInfo(ex.Message);
        }
    }

    public async Task<bool> WaitForServerReady(Action<string> updateInfo)
    {
        var provider = _state.ActiveProvider;
        if (provider == null || !provider.IsLocal)
            return true;

        return await WithOperationLockAsync(async () =>
        {
            if (await _transcription.IsServerReady())
            {
                Status = ServerStatus.Online;
                return true;
            }

            for (int i = 0; i < StartMaxAttempts; i++)
            {
                if (await _transcription.IsServerReady())
                {
                    Status = ServerStatus.Online;
                    return true;
                }
                await Task.Delay(WaitPollIntervalMs);
                updateInfo($"Waiting for server ({i * WaitPollIntervalMs / 1000 + WaitPollIntervalMs / 1000}s)");
            }

            return false;
        });
    }

    bool _switchingProvider;
    bool _isStarting;
    Task? _startTask;
    public async Task SwitchProvider(ProviderConfig provider)
    {
        if (_switchingProvider) return;
        _switchingProvider = true;
        CancelRemoteWarmup();
        try
        {
            await WithOperationLockAsync(() => Task.Run(() =>
            {
                _server.Dispose();
                _transcription.Dispose();

                _server = new LlmServer();
                _server.Configure(provider);
                _server.SetThinkingEnabled(_state.ThinkingEnabled);
                _server.SetUseCpuOnly(_state.UseCpuOnly);
                _transcription = new TranscriptionService(provider);
                App.RegisterServerForCleanup(_server);
            }));

            UpdateProviderStatus(provider);
            if (provider.IsRemoteExecution)
            {
                var ready = await _transcription.IsServerReady();
                Status = ready ? ServerStatus.RemoteConnected : ServerStatus.RemoteUnavailable;
            }
        }
        finally
        {
            _switchingProvider = false;
        }
    }

    void UpdateProviderStatus(ProviderConfig provider)
    {
        Status = provider.IsLocal ? ServerStatus.Offline :
            provider.IsRemoteExecution ? ServerStatus.RemoteUnavailable : ServerStatus.Cloud(provider.Name);
        Logger.Info($"Provider changed to {provider.Name} ({provider.Model})");
    }

    public async Task<string?> TranscribeAsync(byte[] pcm, int channels, CancellationToken ct)
    {
        CancelRemoteWarmup();
        try
        {
            var text = await WithOperationLockAsync(() => _transcription.Transcribe(pcm, channels, ct: ct), ct);
            if (_state.ActiveProvider?.IsRemoteExecution == true)
                Status = ServerStatus.RemoteConnected;
            return text;
        }
        catch
        {
            if (_state.ActiveProvider?.IsRemoteExecution == true)
                Status = ServerStatus.RemoteUnavailable;
            throw;
        }
    }

    public async Task<bool> WarmupRemoteAsync(CancellationToken ct = default)
    {
        if (_state.ActiveProvider?.IsRemoteExecution != true)
            return false;

        var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? previousWarmup;
        lock (_remoteWarmupLock)
        {
            previousWarmup = _remoteWarmupCts;
            _remoteWarmupCts = warmupCts;
        }
        TryCancel(previousWarmup);

        try
        {
            // HttpClient supports concurrent requests. Keeping this outside the
            // operation lock prevents a slow best-effort warm-up from delaying audio.
            var transcription = _transcription;
            return await transcription.WarmupRemoteAsync(warmupCts.Token);
        }
        finally
        {
            lock (_remoteWarmupLock)
            {
                if (ReferenceEquals(_remoteWarmupCts, warmupCts))
                    _remoteWarmupCts = null;
            }
            warmupCts.Dispose();
        }
    }

    void CancelRemoteWarmup()
    {
        CancellationTokenSource? warmupCts;
        lock (_remoteWarmupLock)
        {
            warmupCts = _remoteWarmupCts;
            _remoteWarmupCts = null;
        }
        TryCancel(warmupCts);
    }

    static void TryCancel(CancellationTokenSource? cts)
    {
        if (cts == null)
            return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public Task<bool> SyncRemoteSettingsAsync(CancellationToken ct = default) =>
        SyncRemoteSettingsAsync(
            new RemoteExecutionSettings(_state.AutoOffloadVram, _state.ThinkingEnabled),
            ct);

    public async Task<bool> SyncRemoteSettingsAsync(
        RemoteExecutionSettings settings,
        CancellationToken ct = default)
    {
        if (_state.ActiveProvider?.IsRemoteExecution != true)
            return false;

        var applied = await WithOperationLockAsync(() => _transcription.UpdateRemoteSettingsAsync(
            settings.AutoOffloadVram,
            settings.ThinkingEnabled,
            ct), ct);
        Status = applied ? ServerStatus.RemoteConnected : ServerStatus.RemoteSettingsSyncFailed;
        return applied;
    }

    public async Task<RemoteExecutionSettings> ApplyRemoteSettingsAsync(
        RemoteExecutionSettings settings,
        CancellationToken ct)
    {
        if (_state.ActiveProvider?.IsLocal != true)
            throw new InvalidOperationException("This server instance must be in Local LLM mode to apply remote settings.");

        await WithOperationLockAsync(async () =>
        {
            var thinkingChanged = _state.ThinkingEnabled != settings.ThinkingEnabled;
            _state.SetModelBehaviorSettings(settings.AutoOffloadVram, settings.ThinkingEnabled);
            _server.SetThinkingEnabled(settings.ThinkingEnabled);

            if (thinkingChanged && _server.IsRunning)
            {
                await Task.Run(() => _server.Stop());
                Status = ServerStatus.Offline;
            }
        }, ct);
        return settings;
    }

    public Task<bool> IsServerReady(CancellationToken ct = default) =>
        WithOperationLockAsync(() => _transcription.IsServerReady(ct), ct);

    public async Task StopServerAsync()
    {
        await WithOperationLockAsync(async () =>
        {
            await Task.Run(() => _server.Stop());
            Status = ServerStatus.Offline;
        });
    }

    public Task OffloadServerAsync(CancellationToken ct = default) =>
        WithOperationLockAsync(async () =>
        {
            await Task.Run(() => _server.Stop());
            Status = ServerStatus.Offline;
        }, ct);

    public void Dispose()
    {
        CancelRemoteWarmup();
        if (!_operationLock.Wait(TimeSpan.FromSeconds(10)))
        {
            Logger.Error("Timed out waiting to dispose server manager");
            return;
        }

        try
        {
            _server.Dispose();
            _transcription.Dispose();
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
        }
    }
}
