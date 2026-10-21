using System;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

// SilentPeakFloor is set from probing the running model: a sentence recorded at
// peak 250 and at peak 400 was transcribed word for word, so the floor only
// catches an endpoint that delivers nothing.
public class AudioLevelTests
{
    static byte[] Pcm(Func<int, short> sampleAt, int count)
    {
        var pcm = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            var value = sampleAt(i);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return pcm;
    }

    static byte[] Tone(int peak) => Pcm(
        i => (short)(Math.Sin(2 * Math.PI * 120 * i / 16000) * peak), 16000);

    [Fact]
    public void QuietSpeechIsSentToTheModelInsteadOfBeingRejected()
    {
        var quiet = Tone(250);

        Assert.Equal(250, AudioProcessor.GetMaxAmplitude(quiet));
        Assert.True(AudioProcessor.GetMaxAmplitude(quiet) >= AudioProcessor.SilentPeakFloor);
    }

    [Fact]
    public void DeadOrMutedEndpointStaysBelowTheFloor()
    {
        Assert.True(AudioProcessor.GetMaxAmplitude(new byte[4000]) < AudioProcessor.SilentPeakFloor);
        Assert.True(AudioProcessor.GetMaxAmplitude(Tone(40)) < AudioProcessor.SilentPeakFloor);
    }

    [Fact]
    public void RmsSeparatesASingleClickFromSustainedSignal()
    {
        // A transient peaks like speech with far less energy: that is why peak
        // and rms are both logged.
        var click = Pcm(i => i == 8000 ? (short)6000 : (short)0, 16000);
        var tone = Tone(6000);

        Assert.Equal(6000, AudioProcessor.GetMaxAmplitude(click));
        Assert.Equal(6000, AudioProcessor.GetMaxAmplitude(tone));
        Assert.True(AudioProcessor.GetRmsAmplitude(click) < AudioProcessor.GetRmsAmplitude(tone) / 10);
    }

    [Fact]
    public void EmptyBufferMeasuresZero()
    {
        Assert.Equal(0, AudioProcessor.GetMaxAmplitude(Array.Empty<byte>()));
        Assert.Equal(0, AudioProcessor.GetRmsAmplitude(Array.Empty<byte>()));
    }
}
