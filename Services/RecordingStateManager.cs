using System;
using System.Threading.Tasks;
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
    const int AmplitudeSilenceThreshold = 500;

    readonly AudioRecorder _recorder;

    RecordingState _state = RecordingState.Idle;
    public RecordingState State
    {
        get => _state;
        private set
        {
            SetProperty(ref _state, value);
            OnPropertyChanged(nameof(IsRecording));
        }
    }

    public bool IsRecording => State == RecordingState.Recording;
    public bool IsProcessing => State == RecordingState.Processing;
    public bool CanStart => State is RecordingState.Idle or RecordingState.Success or RecordingState.Error;

    string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    string _statusTextKind = "Gray";
    public string StatusTextKind
    {
        get => _statusTextKind;
        set => SetProperty(ref _statusTextKind, value);
    }

    string _infoText = "";
    public string InfoText
    {
        get => _infoText;
        set => SetProperty(ref _infoText, value);
    }

    string _mainButtonBackgroundKind = "Default";
    public string MainButtonBackgroundKind
    {
        get => _mainButtonBackgroundKind;
        set => SetProperty(ref _mainButtonBackgroundKind, value);
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
            return Array.Empty<byte>();

        var pcm = await _recorder.StopAsync();
        TransitionTo(RecordingState.Processing);
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
                MainButtonBackgroundKind = "Default";
                StatusText = "Ready";
                StatusTextKind = "Gray";
                InfoText = "";
                break;
            case RecordingState.Recording:
                MainButtonBackgroundKind = "Recording";
                StatusText = "Recording...";
                StatusTextKind = "Orange";
                InfoText = isHotkey ? "Release hotkey to stop" : "Press button to stop";
                break;
            case RecordingState.Processing:
                StatusText = "Processing...";
                StatusTextKind = "LightBlue";
                InfoText = "Waiting for server...";
                break;
            case RecordingState.Success:
                MainButtonBackgroundKind = "Default";
                StatusText = text ?? "";
                StatusTextKind = "Green";
                InfoText = "Copied to clipboard";
                break;
            case RecordingState.Error:
                MainButtonBackgroundKind = "Default";
                StatusText = "Error";
                StatusTextKind = "Red";
                InfoText = errorMsg ?? "";
                break;
        }
    }

    public void Dispose() => _recorder.Dispose();
}
