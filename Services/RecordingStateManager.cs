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

    public bool IsRecording => _recorder.IsRecording;
    public int ChannelCount => _recorder.ChannelCount;

    public async Task StartRecording()
    {
        await _recorder.StartAsync();
        SetRecordingState(true);
    }

    public async Task<byte[]> StopRecording()
    {
        var pcm = await _recorder.StopAsync();
        SetRecordingState(false);
        return pcm;
    }

    public void SetProcessingState()
    {
        StatusText = "Processing...";
        StatusTextColor = Brushes.LightBlue;
        InfoText = "Waiting for server...";
    }

    public void SetSuccessState(string text)
    {
        StatusText = text;
        StatusTextColor = Brushes.LimeGreen;
        InfoText = "Copied to clipboard";
    }

    public void SetErrorState(string message)
    {
        StatusText = "Error";
        StatusTextColor = Brushes.Red;
        InfoText = message;
    }

    public void SetNoTextState()
    {
        StatusText = "No text returned";
        StatusTextColor = Brushes.Gray;
        InfoText = "";
    }

    public void SetReadyState()
    {
        StatusText = "Ready";
        StatusTextColor = Brushes.Gray;
        InfoText = "";
    }

    void SetRecordingState(bool recording)
    {
        var recordingBrush = new SolidColorBrush(Color.FromArgb(100, 220, 50, 50));
        var defaultBrush = new SolidColorBrush(Color.FromArgb(0, 100, 100, 100));
        MainButtonBackground = recording ? recordingBrush : defaultBrush;

        if (recording)
        {
            StatusText = "Recording...";
            StatusTextColor = Brushes.Orange;
            InfoText = "Click to stop";
        }
    }

    public void Dispose() => _recorder.Dispose();
}
