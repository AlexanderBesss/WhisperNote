using System.IO;
using System.Text;

namespace WhisperNote.Services;

public static class WavBuilder
{
    public static byte[] Build(byte[] pcm, int channels = 1)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var dataSize = pcm.Length;
        var byteRate = AppConfig.SampleRate * channels * AppConfig.BitsPerSample / 8;
        var blockAlign = channels * AppConfig.BitsPerSample / 8;

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(AppConfig.SampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)AppConfig.BitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        bw.Write(pcm);
        bw.Flush();

        return ms.ToArray();
    }
}
