using System;

namespace WhisperNote.Services;

static class AudioProcessor
{
    // Floor proving a dead capture only — the ASR model, not a peak gate, decides whether audio is speech.
    public const int SilentPeakFloor = 80;

    public static byte[] DownmixToMono(byte[] pcm, int channels)
    {
        var sampleCount = pcm.Length / 2 / channels;
        var mono = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            var sum = 0;
            for (int ch = 0; ch < channels; ch++)
                sum += BitConverter.ToInt16(pcm, (i * channels + ch) * 2);
            var avg = (short)(sum / channels);
            mono[i * 2] = (byte)(avg & 0xFF);
            mono[i * 2 + 1] = (byte)((avg >> 8) & 0xFF);
        }
        return mono;
    }

    public static int GetMaxAmplitude(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        var max = 0;
        for (int i = 0; i <= pcm.Length - 2; i += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(pcm, i));
            if (sample > max) max = sample;
        }
        return max;
    }

    public static double GetRmsAmplitude(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double sum = 0;
        int count = 0;
        for (int i = 0; i <= pcm.Length - 2; i += 2)
        {
            var sample = (double)BitConverter.ToInt16(pcm, i);
            sum += sample * sample;
            count++;
        }
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    public static void LogAmplitude(byte[] pcm)
    {
        var maxAmplitude = GetMaxAmplitude(pcm);
        var label = maxAmplitude < SilentPeakFloor ? "no signal" : "signal present";
        Logger.Info($"PCM level: peak {maxAmplitude}, rms {GetRmsAmplitude(pcm):F0} ({label})");
    }
}
