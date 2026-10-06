using System.IO;
using System.Text;

namespace WhisperNote.Services;

public static class WavBuilder
{
    const int Channels = 1;

    public static byte[] Build(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var dataSize = pcm.Length;
        var byteRate = AppConfig.SampleRate * Channels * AppConfig.BitsPerSample / 8;
        var blockAlign = Channels * AppConfig.BitsPerSample / 8;

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)Channels);
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
