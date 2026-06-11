using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace WhisperNote.Services;

public class AudioRecorder : IDisposable
{
    const int DisposeTimeoutMs = 2000;

    WaveInEvent? _waveIn;
    MemoryStream? _pcmStream;
    readonly SemaphoreSlim _stateLock = new(1, 1);
    readonly object _bufferLock = new();

    public bool IsRecording { get; private set; }
    public int ChannelCount { get; private set; }

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

            _pcmStream = new MemoryStream();
            var deviceNum = SelectDeviceNumber();

            var caps = WaveInEvent.GetCapabilities(deviceNum);
            var deviceName = caps.ProductName;
            ChannelCount = Math.Min(caps.Channels, 2);
            Logger.Info($"Using mic: device={deviceNum} \"{deviceName}\" ({ChannelCount}ch)");

            _waveIn = CreateWaveIn(deviceNum, ChannelCount);
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            IsRecording = true;
            Logger.Info($"Recording started ({ChannelCount}ch, {AppConfig.SampleRate}Hz)");
        }
        catch
        {
            _waveIn?.Dispose();
            _waveIn = null;
            ResetBuffer();
            IsRecording = false;
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    static int SelectDeviceNumber()
    {
        if (WaveInEvent.DeviceCount == 0)
            throw new InvalidOperationException("No recording devices found");

        var deviceNum = AppConfig.MicDeviceNumber >= 0 ? AppConfig.MicDeviceNumber : 0;
        if (deviceNum < WaveInEvent.DeviceCount)
            return deviceNum;

        Logger.Error($"Mic device {deviceNum} not available, falling back to 0");
        return 0;
    }

    static WaveInEvent CreateWaveIn(int deviceNum, int channelCount) =>
        new()
        {
            DeviceNumber = deviceNum,
            WaveFormat = new WaveFormat(AppConfig.SampleRate, AppConfig.BitsPerSample, channelCount)
        };

    public static void LogAvailableDevices()
    {
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            Logger.Info($"  Mic {i}: \"{caps.ProductName}\" (channels={caps.Channels})");
        }
    }

    void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_bufferLock)
        {
            _pcmStream?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    public async Task<byte[]> StopAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            var waveIn = _waveIn;
            _waveIn = null;

            Exception? stopException = null;
            if (waveIn != null)
                stopException = await StopWaveInAsync(waveIn);

            IsRecording = false;
            var pcm = DrainBuffer();

            if (stopException != null)
                throw new InvalidOperationException("Recording stop failed", stopException);

            return pcm;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    async Task<Exception?> StopWaveInAsync(WaveInEvent waveIn)
    {
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
            stopped.TrySetResult(e.Exception);

        waveIn.RecordingStopped += OnRecordingStopped;
        try
        {
            waveIn.StopRecording();
            return await stopped.Task.WaitAsync(TimeSpan.FromMilliseconds(DisposeTimeoutMs));
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.Dispose();
        }
    }

    byte[] DrainBuffer()
    {
        lock (_bufferLock)
        {
            Logger.Info($"Recording stopped, PCM: {_pcmStream?.Length ?? 0} bytes");

            var pcm = _pcmStream?.ToArray() ?? Array.Empty<byte>();
            _pcmStream?.Dispose();
            _pcmStream = null;
            return pcm;
        }
    }

    void ResetBuffer()
    {
        lock (_bufferLock)
        {
            _pcmStream?.Dispose();
            _pcmStream = null;
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
                var waveIn = _waveIn;
                _waveIn = null;
                if (waveIn != null)
                {
                    waveIn.DataAvailable -= OnDataAvailable;
                    try
                    {
                        waveIn.StopRecording();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"AudioRecorder dispose stop failed: {ex.Message}");
                    }
                    finally
                    {
                        waveIn.Dispose();
                    }
                }
                IsRecording = false;
            }

            ResetBuffer();
        }
        finally
        {
            _stateLock.Release();
            _stateLock.Dispose();
        }
    }
}
