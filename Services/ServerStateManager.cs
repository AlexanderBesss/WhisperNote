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

    ServerStatus _status = ServerStatus.Offline;
    public ServerStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
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

    public bool IsServerRunning => _server.IsRunning;
    public bool IsLocal => _state.ActiveProvider?.IsLocal ?? false;

    public Task StartAsync(Action<string, long, long> progress)
    {
        if (_startTask != null && !_startTask.IsCompleted)
            return _startTask;

        _startTask = StartCoreAsync(progress);
        return _startTask;
    }

    async Task StartCoreAsync(Action<string, long, long> progress)
    {
        try
        {
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
            _startTask = null;
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

        var ready = await _transcription.IsServerReady();
        if (ready)
            Status = ServerStatus.Online;
    }

    public void ToggleServer(Action<string?> updateInfo)
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

        try
        {
            if (_server.IsRunning)
            {
                _server.Stop();
                Status = ServerStatus.Offline;
            }
            else
            {
                _ = StartAsync((_, _, _) => { }).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Logger.Error($"StartAsync: {t.Exception?.GetBaseException().Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
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
    }

    bool _switchingProvider;
    Task? _startTask;
    public async Task SwitchProvider(ProviderConfig provider)
    {
        if (_switchingProvider) return;
        _switchingProvider = true;
        try
        {
            await Task.Run(() =>
            {
                _server.Dispose();
                _transcription.Dispose();

                _server = new LlmServer();
                _server.Configure(provider);
                _server.SetThinkingEnabled(_state.ThinkingEnabled);
                _transcription = new TranscriptionService(provider);
                App.RegisterServerForCleanup(_server);
            });

            if (provider.IsLocal)
            {
                Status = ServerStatus.Offline;
                Logger.Info($"Provider changed to {provider.Name} ({provider.Model})");
            }
            else
            {
                Status = ServerStatus.Cloud(provider.Name);
                Logger.Info($"Provider changed to {provider.Name} ({provider.Model})");
            }
        }
        finally
        {
            _switchingProvider = false;
        }
    }

    public Task<string?> TranscribeAsync(byte[] pcm, int channels, CancellationToken ct) =>
        _transcription.Transcribe(pcm, channels, ct: ct);

    public Task<bool> IsServerReady() => _transcription.IsServerReady();

    public void OffloadServer()
    {
        if (_server.IsRunning)
        {
            _server.Stop();
            Status = ServerStatus.Offline;
        }
    }

    public void Dispose()
    {
        _server.Dispose();
        _transcription.Dispose();
    }
}
