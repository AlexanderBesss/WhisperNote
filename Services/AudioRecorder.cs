using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperNote.Services;

// The capture is created once and kept alive between recordings: the Intel SST
// mic costs 350-650 ms to wake, and the warm client blocks WASAPI exclusive mode.
public class AudioRecorder : IDisposable
{
    const int DisposeTimeoutMs = 2000;
    const int ConvertChunkBytes = 16 * 1024;
    const int StartConfirmTimeoutMs = 250;

    WasapiCapture? _capture;
    MMDevice? _device;
    WaveFormat? _inputFormat;
    MemoryStream? _rawStream;
    DateTime _captureOpenedAtUtc;
    volatile bool _captureDirty;
    volatile bool _captureDied;
    Exception? _captureDiedException;
    readonly SemaphoreSlim _stateLock = new(1, 1);
    readonly object _bufferLock = new();

    bool IsRecording { get; set; }
    public int ChannelCount { get; private set; } = 1;

    int _preferredDeviceNumber = -1;
    public int PreferredDeviceNumber
    {
        get => _preferredDeviceNumber;
        set => SetDevicePreference(ref _preferredDeviceNumber, value);
    }

    string? _preferredDeviceName;
    public string? PreferredDeviceName
    {
        get => _preferredDeviceName;
        set => SetDevicePreference(ref _preferredDeviceName, value);
    }

    public string? LastDeviceName { get; private set; }

    void SetDevicePreference<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        _captureDirty = true;
    }

    public async Task StartAsync()
    {
        await WithStateLockAsync(async () =>
        {
            if (IsRecording)
            {
                Logger.Info("Already recording, ignoring Start()");
                return;
            }

            try
            {
                await Task.Run(StartCaptureCore);
                IsRecording = true;
            }
            catch
            {
                ResetBuffer();
                IsRecording = false;
                throw;
            }
        });
    }

    async Task WithStateLockAsync(Func<Task> operation)
    {
        await _stateLock.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    async Task<T> WithStateLockAsync<T>(Func<Task<T>> operation)
    {
        await _stateLock.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    void StartCaptureCore()
    {
        var sw = Stopwatch.StartNew();
        _rawStream = new MemoryStream();
        EnsureCapture();
        try
        {
            StartConfirmed();
        }
        catch (Exception ex)
        {
            Logger.Error($"Capture start failed ({ex.Message}); recreating the capture and retrying");
            ReleaseCapture();
            EnsureCapture();
            StartConfirmed();
        }
        Logger.Info($"Recording started in {sw.ElapsedMilliseconds} ms (input: {_inputFormat})");
    }

    void StartConfirmed()
    {
        ResetCaptureDied();
        _capture!.StartRecording();
        ConfirmCaptureStarted();
    }

    void ResetCaptureDied()
    {
        _captureDied = false;
        _captureDiedException = null;
    }

    // NAudio reports a capture that died right after StartRecording only through
    // RecordingStopped, so wait briefly for that signal before recording silence.
    void ConfirmCaptureStarted()
    {
        var deadline = Environment.TickCount64 + StartConfirmTimeoutMs;
        while (!_captureDied &&
               _capture!.CaptureState == CaptureState.Starting &&
               Environment.TickCount64 < deadline)
            Thread.Sleep(10);

        if (_captureDied)
            throw new InvalidOperationException("Capture device failed to start", _captureDiedException);
    }

    public Task WarmUpAsync() => WithStateLockAsync(async () =>
    {
        if (IsRecording)
            return;
        await Task.Run(WarmUpCore);
    });

    async Task WarmUpCore()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            EnsureCapture();
            StartConfirmed();
            var stopException = await StopCaptureAsync(_capture!);
            if (stopException != null)
                throw new InvalidOperationException("Mic warm-up stop failed", stopException);
            Logger.Info($"Mic warmed up in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Logger.Error($"Mic warm-up failed: {ex.Message}");
            ReleaseCapture();
        }
    }

    void EnsureCapture()
    {
        if (_capture != null && !_captureDirty)
            return;

        ReleaseCapture();
        _captureDirty = false;

        _device = SelectDevice();
        Logger.Info($"Using mic: \"{_device.FriendlyName}\"");
        _capture = new WasapiCapture(_device);
        _inputFormat = _capture.WaveFormat;
        LastDeviceName = _device.FriendlyName;
        _captureOpenedAtUtc = DateTime.UtcNow;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnCaptureDied;
        ResetCaptureDied();
    }

    void ReleaseCapture()
    {
        if (_capture != null)
        {
            var capture = _capture;
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnCaptureDied;
            try
            {
                // NAudio's WasapiCapture.Dispose joins its capture thread without a
                // timeout (naudio/NAudio#630); ReleaseCapture runs on the UI thread at
                // exit, so bound the join and let a wedged dispose die with the process.
                var disposed = Task.Run(() => capture.Dispose());
                if (!disposed.Wait(TimeSpan.FromSeconds(2)))
                    Logger.Error("Capture device dispose still pending; leaving it to process exit");
            }
            catch (Exception ex)
            {
                Logger.Error($"Capture dispose failed: {ex.Message}");
            }
            _capture = null;
        }
        _inputFormat = null;
        _device?.Dispose();
        _device = null;
    }

    MMDevice SelectDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<MMDevice>(enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active));
        if (devices.Count == 0)
            throw new InvalidOperationException("No recording devices found");

        MMDevice? selected = null;
        if (!string.IsNullOrEmpty(PreferredDeviceName))
        {
            selected = devices.FirstOrDefault(d =>
                string.Equals(d.FriendlyName?.Trim(), PreferredDeviceName, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
                Logger.Error($"Saved mic \"{PreferredDeviceName}\" not found; trying its old device number");
        }

        if (selected == null && PreferredDeviceNumber >= 0 && PreferredDeviceNumber < devices.Count)
            selected = devices[PreferredDeviceNumber];

        if (selected == null)
        {
            if (PreferredDeviceNumber >= 0)
                Logger.Error($"Mic device {PreferredDeviceNumber} not available, falling back to default");
            selected = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }

        foreach (var device in devices)
            if (!ReferenceEquals(device, selected))
                device.Dispose();

        return selected;
    }

    public static IReadOnlyList<(int Number, string Name)> GetInputDevices()
    {
        var devices = new List<(int, string)>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            var name = device.FriendlyName?.Trim();
            if (string.IsNullOrEmpty(name))
                name = $"Microphone {devices.Count}";
            devices.Add((devices.Count, name));
            device.Dispose();
        }
        return devices;
    }

    public static void LogAvailableDevices()
    {
        foreach (var (number, name) in GetInputDevices())
            Logger.Info($"  Mic {number}: \"{name}\"");
    }

    void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_bufferLock)
        {
            _rawStream?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    void OnCaptureDied(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null)
            return;
        _captureDiedException = e.Exception;
        _captureDied = true;
        _captureDirty = true;
        Logger.Error($"Capture died unexpectedly: {e.Exception.Message}");
    }

    public Task<byte[]> StopAsync() => WithStateLockAsync(async () =>
    {
        if (_capture == null)
        {
            IsRecording = false;
            return Array.Empty<byte>();
        }

        if (_captureDied)
        {
            var diedException = _captureDiedException
                ?? new InvalidOperationException("Capture device stopped unexpectedly");
            var partial = RawBufferLength();
            ReleaseCapture();
            ResetBuffer();
            IsRecording = false;
            Logger.Warn($"Discarded {partial} bytes captured before the device died");
            throw new InvalidOperationException("Recording failed: capture device disconnected", diedException);
        }

        var stopException = await StopCaptureAsync(_capture);
        IsRecording = false;
        var pcm = await Task.Run(ConvertToTargetFormat);
        LogDefaultEndpointDrift();

        if (stopException != null)
        {
            ReleaseCapture();
            throw new InvalidOperationException("Recording stop failed", stopException);
        }

        return pcm;
    });

    static async Task<Exception?> StopCaptureAsync(WasapiCapture capture)
    {
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
            stopped.TrySetResult(e.Exception);

        capture.RecordingStopped += OnRecordingStopped;
        try
        {
            capture.StopRecording();
            // ConfigureAwait(false) is load-bearing: AudioRecorder.Dispose blocks the UI
            // thread on this task, and posting the timeout continuation back to the WPF
            // dispatcher queue would deadlock the exit-during-recording path.
            return await stopped.Task.WaitAsync(TimeSpan.FromMilliseconds(DisposeTimeoutMs)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            capture.RecordingStopped -= OnRecordingStopped;
        }
    }

    byte[] ConvertToTargetFormat()
    {
        lock (_bufferLock)
        {
            var rawLength = _rawStream?.Length ?? 0;
            Logger.Info($"Recording stopped, raw: {rawLength} bytes{CaptureContext()}");

            try
            {
                if (_rawStream == null || rawLength == 0 || _inputFormat == null)
                    return Array.Empty<byte>();

                var targetFormat = new WaveFormat(AppConfig.SampleRate, AppConfig.BitsPerSample, 1);
                if (_inputFormat == targetFormat)
                    return _rawStream.ToArray();

                _rawStream.Position = 0;
                using var source = new RawSourceWaveStream(_rawStream, _inputFormat);
                using var resampler = new MediaFoundationResampler(source, targetFormat);

                using var output = new MemoryStream((int)(rawLength / 4));
                var buffer = new byte[ConvertChunkBytes];
                int read;
                while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                    output.Write(buffer, 0, read);
                return output.ToArray();
            }
            finally
            {
                _rawStream?.Dispose();
                _rawStream = null;
            }
        }
    }

    string CaptureContext()
    {
        if (_device == null)
            return " (no capture device)";

        var alive = (DateTime.UtcNow - _captureOpenedAtUtc).TotalSeconds;
        return $" from \"{_device.FriendlyName}\", capture alive {alive:F1} s";
    }

    long RawBufferLength()
    {
        lock (_bufferLock)
            return _rawStream?.Length ?? 0;
    }

    // The default endpoint can move on its own while the warm capture stays
    // bound to the device it opened. Only meaningful while following the default.
    void LogDefaultEndpointDrift()
    {
        var device = _device;
        if (device == null || PreferredDeviceNumber >= 0 || !string.IsNullOrEmpty(PreferredDeviceName))
            return;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

            var bound = device.FriendlyName?.Trim();
            var nowDefault = current.FriendlyName?.Trim();
            if (string.Equals(bound, nowDefault, StringComparison.OrdinalIgnoreCase))
                return;

            Logger.Warn($"Default mic is now \"{nowDefault}\"; this recording came from \"{bound}\"");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Default mic could not be read: {ex.Message}");
        }
    }

    void ResetBuffer()
    {
        lock (_bufferLock)
        {
            _rawStream?.Dispose();
            _rawStream = null;
        }
    }

    public void Dispose()
    {
        if (!_stateLock.Wait(DisposeTimeoutMs))
        {
            Logger.Error("Timed out waiting to dispose audio recorder");
            return;
        }

        try
        {
            if (IsRecording)
            {
                if (_capture != null && !_captureDied)
                {
                    try
                    {
                        StopCaptureAsync(_capture).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"AudioRecorder dispose stop failed: {ex.Message}");
                    }
                }
                IsRecording = false;
            }

            ReleaseCapture();
            ResetBuffer();
        }
        finally
        {
            _stateLock.Release();
            _stateLock.Dispose();
        }
    }
}
