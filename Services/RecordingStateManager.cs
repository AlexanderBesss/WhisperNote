using System;
using System.Threading.Tasks;
using WhisperNote.ViewModels;

namespace WhisperNote.Services;

public enum RecordingResult
{
    None,
    Success,
    Error
}

// What the pill and the overlay show. Recording and processing are independent:
// the user can already be recording the next clip while the previous one is
// being transcribed, so both are true at the same time and both icons light up.
public class RecordingStateManager : ViewModel
{
    readonly AudioRecorder _recorder;

    bool _isCapturing;
    bool _queueBusy;
    int _queuedCount;
    int _queueOutstanding;

    string _hotkeyName = "";
    bool _captureFromHotkey;

    RecordingResult _result = RecordingResult.None;
    string _resultText = "";

    public bool IsRecording => _isCapturing;

    // True while a job is running or a job is still waiting in the queue.
    public bool IsProcessing => _queueBusy;

    public int QueuedCount => _queuedCount;

    // A new clip may be recorded while the previous ones are still processing;
    // only a running capture or a full queue blocks it.
    public bool CanStartRecording =>
        !_isCapturing && _queueOutstanding < TranscriptionQueue.Capacity;

    public RecordingResult Result => _result;

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

    public int ChannelCount => _recorder.ChannelCount;

    public string? MicrophoneName => _recorder.LastDeviceName;

    // -1 = system default mic.
    public void SetMicDevice(int deviceNumber, string? deviceName)
    {
        _recorder.PreferredDeviceNumber = deviceNumber;
        _recorder.PreferredDeviceName = deviceName;
    }

    public RecordingStateManager()
    {
        _recorder = new AudioRecorder();
    }

    public async Task StartRecording(bool isHotkey = false, string? hotkeyName = null)
    {
        if (!CanStartRecording)
            throw new InvalidOperationException(
                _isCapturing ? "Already recording" : "The processing queue is full");

        Logger.Info(isHotkey
            ? $"Recording requested by hotkey{(string.IsNullOrEmpty(hotkeyName) ? "" : $" ({hotkeyName})")}"
            : "Recording requested from the window");

        _captureFromHotkey = isHotkey;
        _hotkeyName = hotkeyName ?? "";

        // Set before the hardware start so a second press during the 350-650 ms
        // mic wake is ignored instead of double-starting the capture.
        SetCapturing(true);
        try
        {
            await _recorder.StartAsync();
        }
        catch
        {
            SetCapturing(false);
            throw;
        }
    }

    public Task WarmUpMic() => _recorder.WarmUpAsync();

    public async Task<byte[]> StopRecording()
    {
        if (!_isCapturing)
            return Array.Empty<byte>();

        try
        {
            return await _recorder.StopAsync();
        }
        finally
        {
            SetCapturing(false);
        }
    }

    // Mirrors the queue's counters so the pill/overlay can count down what is left.
    public void SetQueueState(int waitingCount, int outstandingCount)
    {
        _queuedCount = waitingCount;
        _queueBusy = outstandingCount > 0;
        _queueOutstanding = outstandingCount;
        RaiseState();
    }

    public void ReportSuccess(string text)
    {
        _result = RecordingResult.Success;
        _resultText = text;
        InfoText = "Copied to clipboard";
        RaiseState();
    }

    public void ReportError(string message)
    {
        _result = RecordingResult.Error;
        InfoText = message;
        RaiseState();
    }

    public void Reset()
    {
        _result = RecordingResult.None;
        _resultText = "";
        InfoText = "";
        RaiseState();
    }

    // Internal so the display model can be driven in tests without a microphone.
    internal void SetCapturing(bool value)
    {
        if (_isCapturing == value)
            return;

        _isCapturing = value;
        if (value)
            InfoText = _captureFromHotkey
                ? $"Release {(_hotkeyName.Length > 0 ? _hotkeyName : "hotkey")} to stop"
                : "Press button to stop";

        RaiseState();
    }

    void RaiseState()
    {
        RefreshStatus();
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(CanStartRecording));
        OnPropertyChanged(nameof(Result));
    }

    void RefreshStatus()
    {
        // The newest thing the user is doing wins the single pill line; the
        // overlay shows both icons, so it never needs to hide one of them.
        var waiting = _queuedCount > 0 ? $" · {_queuedCount} waiting" : "";

        if (_isCapturing)
        {
            StatusText = "Recording…" + waiting;
            StatusTextKind = "Orange";
        }
        else if (_queueBusy)
        {
            StatusText = "Processing…" + waiting;
            StatusTextKind = "LightBlue";
        }
        else if (_result == RecordingResult.Error)
        {
            StatusText = "Error";
            StatusTextKind = "Red";
        }
        else if (_result == RecordingResult.Success)
        {
            StatusText = _resultText;
            StatusTextKind = "Green";
        }
        else
        {
            StatusText = "Ready";
            StatusTextKind = "Gray";
        }
    }

    public void Dispose() => _recorder.Dispose();
}
