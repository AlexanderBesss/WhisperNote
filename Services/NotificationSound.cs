using System;
using System.IO;
using System.Media;

namespace WhisperNote.Services;

static class NotificationSound
{
    static readonly byte[] _beepWav = GenerateBeep();

    static byte[] GenerateBeep()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int sampleRate = 44100;
        int durationMs = 100;
        int numSamples = sampleRate * durationMs / 1000;
        int dataLen = numSamples * 2;

        bw.Write(new byte[] { 82, 73, 70, 70 });
        bw.Write(36 + dataLen);
        bw.Write(new byte[] { 87, 65, 86, 69 });
        bw.Write(new byte[] { 102, 109, 116, 32 });
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(new byte[] { 100, 97, 116, 97 });
        bw.Write(dataLen);

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Exp(-t * 15.0);
            double sample = Math.Sin(2 * Math.PI * 880 * t) * env * 0.01;
            bw.Write((short)(sample * 32767));
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
