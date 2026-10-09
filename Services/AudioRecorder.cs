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

// WASAPI shared-mode capture. The capture object is created once and reused
// across recordings: this machine's Intel SST mic array sleeps when idle and
// costs 350-650 ms to wake inside AudioClient.Start, whether through MME or
// WASAPI. A stopped-but-alive capture keeps the endpoint warm, so every
// hotkey press after the first starts in ~0 ms (measured). WarmUpAsync pays
// the cold-open cost once at app start.
//
// Capture delivers the device's mix format (usually 48 kHz float); it is
// resampled to 16 kHz 16-bit mono when recording stops, so the transcription
// pipeline receives exactly the same PCM as before.
//
// Trade-off of the warm capture: the allocated (stopped) audio client keeps
// the endpoint claimed for the app's whole lifetime, so other applications
// cannot open the mic in WASAPI exclusive mode while WhisperNote runs.
// Accepted for the hot-start latency.
public class AudioRecorder : IDisposable
{
    const int DisposeTimeoutMs = 2000;
    const int ConvertChunkBytes = 16 * 1024;
    // How long to wait for the capture thread to reach Capturing (or to die)
    // after StartRecording before assuming the device is healthy.
    const int StartConfirmTimeoutMs = 250;

    WasapiCapture? _capture;
    MMDevice? _device;
    WaveFormat? _inputFormat;
    MemoryStream? _rawStream;
    bool _captureDirty;
    // Set by the permanent RecordingStopped handler when NAudio's capture
    // thread dies on its own (device disconnect, stale client after resume).
    volatile bool _captureDied;
    Exception? _captureDiedException;
    readonly SemaphoreSlim _stateLock = new(1, 1);
    readonly object _bufferLock = new();

    bool IsRecording { get; set; }
    public int ChannelCount { get; private set; } = 1;

    int _preferredDeviceNumber = -1;
    // -1 means "system default"; any other value is an index into the active
    // WASAPI capture endpoints, matching the list shown in Settings.
    public int PreferredDeviceNumber
    {
        get => _preferredDeviceNumber;
        set
        {
            if (_preferredDeviceNumber == value)
                return;
            _preferredDeviceNumber = value;
            _captureDirty = true;
        }
    }

    string? _preferredDeviceName;
    // Preferred over the number: WASAPI indexes reshuffle when devices change.
    public string? PreferredDeviceName
    {
        get => _preferredDeviceName;
        set
        {
            if (_preferredDeviceName == value)
                return;
            _preferredDeviceName = value;
            _captureDirty = true;
        }
    }

    public async Task StartAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            if (IsRecording)
            {
                Logger.Info("Already recording, ignoring Start()");
                return;
            }

            await Task.Run(StartCaptureCore);
            IsRecording = true;
        }
        catch
        {
            ResetBuffer();
            IsRecording = false;
            throw;
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
            // A stale endpoint (sleep/resume, device re-enumeration) is the
            // usual cause: rebuild the capture once and retry before giving up.
            Logger.Error($"Capture start failed ({ex.Message}); recreating the capture and retrying");
            ReleaseCapture();
            EnsureCapture();
            StartConfirmed();
        }
        Logger.Info($"Recording started in {sw.ElapsedMilliseconds} ms (input: {_inputFormat})");
    }

    void StartConfirmed()
    {
        _captureDied = false;
        _captureDiedException = null;
        _capture!.StartRecording();
        ConfirmCaptureStarted();
    }

    // NAudio surfaces a dead audio client asynchronously: StartRecording
    // returns normally and the capture thread dies right away, reporting the
    // error only through RecordingStopped. Give the thread a moment to fail
    // so the retry path can rebuild the capture instead of silently
    // recording silence until the stop timeout expires.
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

    // Briefly starts and stops the reusable capture so the first hotkey press
    // does not pay the device wake-up cost.
    public async Task WarmUpAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            if (IsRecording)
                return;
            await Task.Run(WarmUpCore);
        }
        finally
        {
            _stateLock.Release();
        }
    }

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
        _capture.DataAvailable += OnDataAvailable;
        // Permanent handler: NAudio reports a dying capture (device
        // disconnect, stale client) only through RecordingStopped, and only
        // while a handler is attached at that moment. Normal stops carry no
        // exception, so they pass through untouched.
        _capture.RecordingStopped += OnCaptureDied;
        _captureDied = false;
        _captureDiedException = null;
    }

    void ReleaseCapture()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnCaptureDied;
            try
            {
                _capture.Dispose();
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
            // Null outside recordings (e.g. during warm-up): audio is discarded.
            _rawStream?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    void OnCaptureDied(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null)
            return;
        _captureDiedException = e.Exception;
        _captureDied = true;
        Logger.Error($"Capture died unexpectedly: {e.Exception.Message}");
    }

    public async Task<byte[]> StopAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_capture == null)
            {
                IsRecording = false;
                return Array.Empty<byte>();
            }

            if (_captureDied)
            {
                // The capture thread already reported a device failure and
                // RecordingStopped has fired; waiting for it again would just
                // burn the full timeout. Rebuild for the next recording.
                var diedException = _captureDiedException
                    ?? new InvalidOperationException("Capture device stopped unexpectedly");
                ReleaseCapture();
                ResetBuffer();
                IsRecording = false;
                throw new InvalidOperationException("Recording failed: capture device disconnected", diedException);
            }

            var stopException = await StopCaptureAsync(_capture);
            IsRecording = false;
            var pcm = await Task.Run(ConvertToTargetFormat);

            if (stopException != null)
            {
                // Unknown capture state after a failed stop: rebuild next time.
                ReleaseCapture();
                throw new InvalidOperationException("Recording stop failed", stopException);
            }

            return pcm;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    // Stops the capture but keeps the object alive for the next recording;
    // that is what keeps the mic endpoint warm.
    static async Task<Exception?> StopCaptureAsync(WasapiCapture capture)
    {
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
            stopped.TrySetResult(e.Exception);

        capture.RecordingStopped += OnRecordingStopped;
        try
        {
            capture.StopRecording();
            return await stopped.Task.WaitAsync(TimeSpan.FromMilliseconds(DisposeTimeoutMs));
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

    // Mix-format recording -> 16 kHz 16-bit mono PCM for the ASR pipeline:
    // one whole-stream Media Foundation resample, which also remixes
    // stereo to mono. Runs off the UI thread.
    byte[] ConvertToTargetFormat()
    {
        lock (_bufferLock)
        {
            var rawLength = _rawStream?.Length ?? 0;
            Logger.Info($"Recording stopped, raw: {rawLength} bytes");

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
                // A dead capture already raised RecordingStopped; waiting on it
                // again would only burn the stop timeout during shutdown.
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
