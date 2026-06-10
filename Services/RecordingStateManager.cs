using System;
using System.Threading.Tasks;
using System.Windows.Media;
using WhisperNote.ViewModels;

namespace WhisperNote.Services;

public class RecordingStateManager : ViewModel
{
    readonly AudioRecorder _recorder;

    public RecordingStateManager()
    {
        _recorder = new AudioRecorder();
    }

    Brush _mainButtonBackground = new SolidColorBrush(Color.FromArgb(0, 100, 100, 100));
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

    bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        private set => SetProperty(ref _isRecording, value);
    }
    public int ChannelCount => _recorder.ChannelCount;

    bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        private set => SetProperty(ref _isProcessing, value);
    }

    public async Task StartRecording(bool isHotkey = false)
    {
        await _recorder.StartAsync();
        IsRecording = true;
        SetRecordingState(true, isHotkey);
    }

    public async Task<byte[]> StopRecording()
    {
        var pcm = await _recorder.StopAsync();
        IsRecording = false;
        SetRecordingState(false);
        return pcm;
    }

    public void SetProcessingState()
    {
        IsProcessing = true;
        StatusText = "Processing...";
        StatusTextColor = Brushes.LightBlue;
        InfoText = "Waiting for server...";
    }

    public void SetSuccessState(string text)
    {
        IsProcessing = false;
        StatusText = text;
        StatusTextColor = Brushes.LimeGreen;
        InfoText = "Copied to clipboard";
    }

    public void SetErrorState(string message)
    {
        IsProcessing = false;
        StatusText = "Error";
        StatusTextColor = Brushes.Red;
        InfoText = message;
    }

    public void SetNoTextState()
    {
        IsProcessing = false;
        StatusText = "No text returned";
        StatusTextColor = Brushes.Gray;
        InfoText = "";
    }

    public void SetReadyState()
    {
        IsProcessing = false;
        StatusText = "Ready";
        StatusTextColor = Brushes.Gray;
        InfoText = "";
    }

    void SetRecordingState(bool recording, bool isHotkey = false)
    {
        var recordingBrush = new SolidColorBrush(Color.FromArgb(100, 220, 50, 50));
        var defaultBrush = new SolidColorBrush(Color.FromArgb(0, 100, 100, 100));
        MainButtonBackground = recording ? recordingBrush : defaultBrush;

        if (recording)
        {
            StatusText = "Recording...";
            StatusTextColor = Brushes.Orange;
            InfoText = isHotkey ? "Release hotkey to stop" : "Press button to stop";
        }
    }

    public void Dispose() => _recorder.Dispose();
}
