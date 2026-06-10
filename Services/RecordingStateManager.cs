  using System;
using System.Threading.Tasks;
using System.Windows.Media;
using WhisperNote.Services;
using WhisperNote.ViewModels;

namespace WhisperNote.Services;

public enum RecordingState
{
    Idle,
    Recording,
    Processing,
    Success,
    Error
}

public class RecordingStateManager : ViewModel
{
    readonly AudioRecorder _recorder;
    static readonly Brush DefaultBrush = new SolidColorBrush(Color.FromArgb(0, 100, 100, 100));
    static readonly Brush RecordingBrush = new SolidColorBrush(Color.FromArgb(100, 220, 50, 50));

    RecordingState _state = RecordingState.Idle;
    public RecordingState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public bool IsRecording => State == RecordingState.Recording;
    public bool IsProcessing => State == RecordingState.Processing;
    public bool CanStart => State is RecordingState.Idle or RecordingState.Success or RecordingState.Error;

    Brush _mainButtonBackground = DefaultBrush;
    public Brush MainButtonBackground
    {
        get => _mainButtonBackground;
        set => SetProperty(ref _mainButtonBackground, value);
    }

    string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    Brush _statusTextColor = new SolidColorBrush(Color.FromRgb(204, 204, 204));
    public Brush StatusTextColor
    {
        get => _statusTextColor;
        set => SetProperty(ref _statusTextColor, value);
    }

    string _infoText = "";
    public string InfoText
    {
        get => _infoText;
        set => SetProperty(ref _infoText, value);
    }

    public int ChannelCount => _recorder.ChannelCount;

    public RecordingStateManager()
    {
        _recorder = new AudioRecorder();
    }

    public async Task StartRecording(bool isHotkey = false)
    {
        if (!CanStart)
            throw new InvalidOperationException($"Cannot start recording in state {_state}");

        await _recorder.StartAsync();
        TransitionTo(RecordingState.Recording, isHotkey);
    }

    public async Task<byte[]> StopRecording()
    {
        if (State != RecordingState.Recording)
        {
            Logger.Info($"[StopRecording] state is {State}, not Recording, returning empty");
            return Array.Empty<byte>();
        }

        var pcm = await _recorder.StopAsync();
        TransitionTo(RecordingState.Processing);
        Logger.Info($"[StopRecording] done, pcm.Length={pcm.Length}, state={State}");
        return pcm;
    }

    public bool SetSuccess(string text)
    {
        if (State != RecordingState.Processing)
            return false;

        TransitionTo(RecordingState.Success, text: text);
        return true;
    }

    public bool SetError(string message)
    {
        if (State != RecordingState.Processing)
            return false;

        TransitionTo(RecordingState.Error, errorMsg: message);
        return true;
    }

    public bool Cancel()
    {
        if (State != RecordingState.Processing)
            return false;

        TransitionTo(RecordingState.Idle);
        return true;
    }

    public void Reset()
    {
        TransitionTo(RecordingState.Idle);
    }

    void TransitionTo(RecordingState next, bool isHotkey = false, string? text = null, string? errorMsg = null)
    {
        Logger.Info($"[Transition] {State} → {next}");
        var valid = (State, next) switch
        {
            (RecordingState.Idle, RecordingState.Recording) => true,
            (RecordingState.Recording, RecordingState.Idle) => true,
            (RecordingState.Recording, RecordingState.Processing) => true,
            (RecordingState.Processing, RecordingState.Success) => true,
            (RecordingState.Processing, RecordingState.Error) => true,
            (RecordingState.Processing, RecordingState.Idle) => true,
            (RecordingState.Success, RecordingState.Idle) => true,
            (RecordingState.Success, RecordingState.Recording) => true,
            (RecordingState.Error, RecordingState.Idle) => true,
            (RecordingState.Error, RecordingState.Recording) => true,
            (RecordingState.Idle, RecordingState.Idle) => true,
            _ => false
        };

        if (!valid)
            throw new InvalidOperationException($"Invalid transition: {State} → {next}");

        State = next;

        switch (next)
        {
            case RecordingState.Idle:
                MainButtonBackground = DefaultBrush;
                StatusText = "Ready";
                StatusTextColor = Brushes.Gray;
                InfoText = "";
                break;
            case RecordingState.Recording:
                MainButtonBackground = RecordingBrush;
                StatusText = "Recording...";
                StatusTextColor = Brushes.Orange;
                InfoText = isHotkey ? "Release hotkey to stop" : "Press button to stop";
                break;
            case RecordingState.Processing:
                StatusText = "Processing...";
                StatusTextColor = Brushes.LightBlue;
                InfoText = "Waiting for server...";
                break;
            case RecordingState.Success:
                MainButtonBackground = DefaultBrush;
                StatusText = text ?? "";
                StatusTextColor = Brushes.LimeGreen;
                InfoText = "Copied to clipboard";
                break;
            case RecordingState.Error:
                MainButtonBackground = DefaultBrush;
                StatusText = "Error";
                StatusTextColor = Brushes.Red;
                InfoText = errorMsg ?? "";
                break;
        }
    }

    public void Dispose() => _recorder.Dispose();
}
