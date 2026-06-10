using System;
using System.IO;
using System.Media;

namespace WhisperNote.Services;

static class NotificationSound
{
    const int SampleRate = 44100;
    const int DurationMs = 100;
    const int Frequency = 880;
    const double DecayRate = 15.0;
    const double Volume = 0.01;
    const short MaxAmplitude = short.MaxValue;

    static readonly byte[] _beepWav = GenerateBeep();

    static byte[] GenerateBeep()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var numSamples = SampleRate * DurationMs / 1000;
        var dataLen = numSamples * 2;

        bw.Write(new byte[] { 82, 73, 70, 70 });
        bw.Write(36 + dataLen);
        bw.Write(new byte[] { 87, 65, 86, 69 });
        bw.Write(new byte[] { 102, 109, 116, 32 });
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(new byte[] { 100, 97, 116, 97 });
        bw.Write(dataLen);

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / SampleRate;
            double env = Math.Exp(-t * DecayRate);
            double sample = Math.Sin(2 * Math.PI * Frequency * t) * env * Volume;
            bw.Write((short)(sample * MaxAmplitude));
        }

        return ms.ToArray();
    }

    public static void Play()
    {
        try
        {
            using var ms = new MemoryStream(_beepWav);
            using var player = new SoundPlayer(ms);
            player.Play();
        }
        catch (Exception ex)
        {
            Logger.Error($"NotificationSound: {ex.Message}");
        }
    }
}
