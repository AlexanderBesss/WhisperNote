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
            var deviceNum = AppConfig.MicDeviceNumber >= 0 ? AppConfig.MicDeviceNumber : 0;

            if (deviceNum >= WaveInEvent.DeviceCount)
            {
                Logger.Error($"Mic device {deviceNum} not available, falling back to 0");
                deviceNum = 0;
            }

            var caps = WaveInEvent.GetCapabilities(deviceNum);
            var deviceName = caps.ProductName;
            ChannelCount = Math.Min(caps.Channels, 2);
            Logger.Info($"Using mic: device={deviceNum} \"{deviceName}\" ({ChannelCount}ch)");

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNum,
                WaveFormat = new WaveFormat(AppConfig.SampleRate, AppConfig.BitsPerSample, ChannelCount)
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            IsRecording = true;
            Logger.Info($"Recording started ({ChannelCount}ch, {AppConfig.SampleRate}Hz)");
        }
        finally
        {
            _stateLock.Release();
        }
    }

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
        _pcmStream?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    public async Task<byte[]> StopAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            var waveIn = _waveIn;
            _waveIn = null;

            if (waveIn != null)
            {
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.StopRecording();
                waveIn.Dispose();
            }

            IsRecording = false;
            Logger.Info($"Recording stopped, PCM: {_pcmStream?.Length ?? 0} bytes");

            var pcm = _pcmStream?.ToArray() ?? Array.Empty<byte>();
            _pcmStream?.Dispose();
            _pcmStream = null;
            return pcm;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void Dispose()
    {
        if (_stateLock.Wait(DisposeTimeoutMs))
        {
            try
            {
                if (IsRecording)
                {
                    var waveIn = _waveIn;
                    _waveIn = null;
                    if (waveIn != null)
                    {
                        waveIn.DataAvailable -= OnDataAvailable;
                        waveIn.StopRecording();
                        waveIn.Dispose();
                    }
                    IsRecording = false;
                }
                _pcmStream?.Dispose();
                _pcmStream = null;
            }
            finally
            {
                _stateLock.Release();
            }
        }
        _stateLock.Dispose();
    }
}
