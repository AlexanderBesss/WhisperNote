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

    public bool IsStarting => _isStarting;

    public Task StartAsync(Action<string, long, long> progress)
    {
        lock (_startLock)
        {
            if (_isStarting)
                return _startTask ?? Task.CompletedTask;

            _isStarting = true;
            _startTask = StartCoreAsync(progress);
            return _startTask;
        }
    }

    async Task StartCoreAsync(Action<string, long, long> progress)
    {
        await _operationLock.WaitAsync();
        try
        {
            _server.SetThinkingEnabled(_state.ThinkingEnabled);
            await _server.EnsureModelsAsync(progress);
            await _server.StartAsync();
            Status = ServerStatus.Launching;

            if (await WaitForReadyAsync(StartMaxAttempts, StartPollIntervalMs))
            {
                Status = ServerStatus.Online;
                return;
            }

            throw new TimeoutException("Server failed to start within 60 seconds");
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
            _operationLock.Release();
        }
    }

    async Task<bool> WaitForReadyAsync(int maxAttempts, int pollIntervalMs)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(pollIntervalMs);
            if (await _transcription.IsServerReady())
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
        try
        {
            await WithOperationLockAsync(() => Task.Run(() =>
            {
                _server.Dispose();
                _transcription.Dispose();

                _server = new LlmServer();
                _server.Configure(provider);
                _server.SetThinkingEnabled(_state.ThinkingEnabled);
                _transcription = new TranscriptionService(provider);
                App.RegisterServerForCleanup(_server);
            }));

            UpdateProviderStatus(provider);
        }
        finally
        {
            _switchingProvider = false;
        }
    }

    void UpdateProviderStatus(ProviderConfig provider)
    {
        Status = provider.IsLocal
            ? ServerStatus.Offline
            : ServerStatus.Cloud(provider.Name);
        Logger.Info($"Provider changed to {provider.Name} ({provider.Model})");
    }

    public Task<string?> TranscribeAsync(byte[] pcm, int channels, CancellationToken ct) =>
        WithOperationLockAsync(() => _transcription.Transcribe(pcm, channels, ct: ct), ct);

    public Task<bool> IsServerReady() =>
        WithOperationLockAsync(() => _transcription.IsServerReady());

    public Task OffloadServerAsync() =>
        WithOperationLockAsync(async () =>
        {
            await Task.Run(() => _server.Stop());
            Status = ServerStatus.Offline;
        });

    public void Dispose()
    {
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
