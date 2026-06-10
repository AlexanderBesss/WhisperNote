using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using WhisperNote.Config;
using WhisperNote.ViewModels;

namespace WhisperNote.Services;

public class ServerStateManager : ViewModel, IDisposable
{
    LlmServer _server;
    TranscriptionService _transcription;
    readonly AppState _state;

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

    Brush _serverDotColor = Brushes.Gray;
    public Brush ServerDotColor
    {
        get => _serverDotColor;
        set => SetProperty(ref _serverDotColor, value);
    }

    string _serverStatusMessage = "Server offline";
    public string ServerStatusMessage
    {
        get => _serverStatusMessage;
        set => SetProperty(ref _serverStatusMessage, value);
    }

    Brush _serverStatusTextColor = Brushes.Gray;
    public Brush ServerStatusTextColor
    {
        get => _serverStatusTextColor;
        set => SetProperty(ref _serverStatusTextColor, value);
    }

    public bool IsServerRunning => _server.IsRunning;
    public bool IsLocal => _state.ActiveProvider?.IsLocal ?? false;

    public void Start()
    {
        _server.Start();
        ServerDotColor = Brushes.Orange;
        ServerStatusMessage = "Launching...";
        ServerStatusTextColor = Brushes.Orange;
    }

    public async Task InitializeAsync()
    {
        var provider = _state.ActiveProvider;
        if (provider == null)
        {
            ServerDotColor = Brushes.Red;
            ServerStatusMessage = "No provider configured";
            ServerStatusTextColor = Brushes.Red;
            return;
        }
        if (!provider.IsLocal)
        {
            ServerDotColor = Brushes.LimeGreen;
            ServerStatusMessage = $"Cloud · {provider.Name}";
            ServerStatusTextColor = Brushes.LimeGreen;
            return;
        }

        var ready = await _transcription.IsServerReady();
        if (ready)
        {
            ServerDotColor = Brushes.LimeGreen;
            ServerStatusMessage = "Server online";
            ServerStatusTextColor = Brushes.LimeGreen;
        }
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
                ServerDotColor = Brushes.Gray;
                ServerStatusMessage = "Server offline";
                ServerStatusTextColor = Brushes.Gray;
            }
            else
            {
                _server.Start();
                ServerDotColor = Brushes.Orange;
                ServerStatusMessage = "Launching...";
                ServerStatusTextColor = Brushes.Orange;
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
            ServerDotColor = Brushes.LimeGreen;
            ServerStatusMessage = "Server online";
            ServerStatusTextColor = Brushes.LimeGreen;
            return true;
        }

        for (int i = 0; i < 60; i++)
        {
            if (await _transcription.IsServerReady())
            {
                ServerDotColor = Brushes.LimeGreen;
                ServerStatusMessage = "Server online";
                ServerStatusTextColor = Brushes.LimeGreen;
                return true;
            }
            await Task.Delay(2000);
            updateInfo($"Waiting for server ({i * 2 + 2}s)");
        }

        return false;
    }

    bool _switchingProvider;
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
                ServerDotColor = Brushes.Gray;
                ServerStatusMessage = "Server offline";
                ServerStatusTextColor = Brushes.Gray;
                Logger.Info($"Provider changed to {provider.Name} ({provider.Model})");
            }
            else
            {
                ServerDotColor = Brushes.LimeGreen;
                ServerStatusMessage = $"Cloud · {provider.Name}";
                ServerStatusTextColor = Brushes.LimeGreen;
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
            ServerDotColor = Brushes.Gray;
            ServerStatusMessage = "Server offline";
            ServerStatusTextColor = Brushes.Gray;
        }
    }

    public void Dispose()
    {
        _server.Dispose();
        _transcription.Dispose();
    }
}
