using System;

namespace WhisperNote.Services;

// AddBytes runs on the WASAPI capture thread and Read* on the WPF thread; every
// public entry point takes the same lock, and the capture path must not allocate per frame.
public sealed class SpectrumAnalyzer
{
    public const int BandCount = 20;
    public const int WaveformPoints = 64;

    const int FftSize = 1024;
    const int HopSize = 512;
    const int RingSize = 2048;

    const double MinBandHz = 60;
    const double MaxBandHz = 8000;
    const double FloorDb = -58;
    const double PeakHeadroomDb = -12;
    const double MaxGainDb = 40;
    const float SilenceRms = 1e-4f;
    const double BandDecay = 0.80;
    const double WaveDecay = 0.75;
    const double LevelAttack = 0.5;
    const double LevelDecay = 0.15;
    const double PeakDecay = 0.995;

    // A Hann-windowed real FFT of size N turns a sine of amplitude a into a bin
    // of about a * N * 0.5 / 2; dividing by this puts band magnitudes back into
    // amplitude units, which is what the dB floor and the auto-gain assume.
    const double MagnitudeToAmplitude = 1.0 / (FftSize * 0.25);

    readonly object _sync = new();

    readonly float[] _ring = new float[RingSize];
    readonly float[] _hann = new float[FftSize];
    readonly float[] _re = new float[FftSize];
    readonly float[] _im = new float[FftSize];
    readonly float[] _bandRaw = new float[BandCount];
    readonly float[] _bands = new float[BandCount];
    readonly float[] _wave = new float[WaveformPoints];
    readonly int[] _bitReversal = new int[FftSize];
    readonly int[] _bandLowBin = new int[BandCount];
    readonly int[] _bandHighBin = new int[BandCount];

    float[] _scratch = Array.Empty<float>();

    int _writePos;
    int _samplesReady;
    int _bandSampleRate;
    double _framePeak;
    double _wavePeak = 1e-6;
    double _gainDb;
    double _level;

    public SpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
            _hann[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));

        for (int i = 0; i < FftSize; i++)
            _bitReversal[i] = ReverseBits(i, Log2(FftSize));
    }

    public void Reset()
    {
        lock (_sync)
        {
            Array.Clear(_ring);
            Array.Clear(_bands);
            Array.Clear(_wave);
            _writePos = 0;
            _samplesReady = 0;
            _framePeak = 0;
            _wavePeak = 1e-6;
            _gainDb = 0;
            _level = 0;
        }
    }

    public void ReadLevels(float[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_sync)
        {
            for (int i = 0; i < Math.Min(destination.Length, BandCount); i++)
                destination[i] = _bands[i];
        }
    }

    public void ReadWaveform(float[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_sync)
        {
            for (int i = 0; i < Math.Min(destination.Length, WaveformPoints); i++)
                destination[i] = _wave[i];
        }
    }

    public double Level
    {
        get
        {
            lock (_sync)
                return _level;
        }
    }

    public void AddBytes(byte[] buffer, int count, int sampleRate, int channels, int bitsPerSample, bool isFloat)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0 || channels <= 0 || sampleRate <= 0)
            return;

        var bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample < 1)
            return;

        var usable = Math.Min(count, buffer.Length);
        if (usable / bytesPerSample < channels)
            return;

        lock (_sync)
        {
            var monoCount = DecodeMono(buffer, usable, channels, bytesPerSample, isFloat);
            if (monoCount == 0)
                return;

            for (int i = 0; i < monoCount; i++)
            {
                _ring[_writePos] = _scratch[i];
                _writePos = (_writePos + 1) % RingSize;
            }

            _samplesReady += monoCount;
            // Drops the oldest audio when the reader falls behind, keeping the framing work per call bounded.
            if (_samplesReady > RingSize)
                _samplesReady = RingSize;

            EnsureBands(sampleRate);
            ProcessPendingWindows();
        }
    }

    // Pending windows must run oldest first, HopSize apart, so the band decay follows real
    // time; the newest window's samples stay in the ring as the overlap for the next call.
    void ProcessPendingWindows()
    {
        if (_samplesReady < FftSize)
            return;

        var lastLag = 0;
        while (FftSize + lastLag + HopSize <= _samplesReady)
            lastLag += HopSize;

        for (var lag = lastLag; lag >= 0; lag -= HopSize)
            ProcessWindow(lag);

        _samplesReady = FftSize;
    }

    int DecodeMono(byte[] buffer, int usable, int channels, int bytesPerSample, bool isFloat)
    {
        var frameCount = usable / bytesPerSample / channels;
        if (_scratch.Length < frameCount)
            _scratch = new float[Math.Max(frameCount, 4096)];

        if (channels == 1)
        {
            for (int frame = 0; frame < frameCount; frame++)
                _scratch[frame] = SampleAt(buffer, frame * bytesPerSample, bytesPerSample, isFloat);

            return frameCount;
        }

        for (int frame = 0; frame < frameCount; frame++)
        {
            var sum = 0f;
            for (int ch = 0; ch < channels; ch++)
                sum += SampleAt(buffer, (frame * channels + ch) * bytesPerSample, bytesPerSample, isFloat);
            _scratch[frame] = sum / channels;
        }

        return frameCount;
    }

    static float SampleAt(byte[] buffer, int offset, int bytesPerSample, bool isFloat)
    {
        switch (bytesPerSample)
        {
            case 4 when isFloat:
                return BitConverter.ToSingle(buffer, offset);
            case 4:
                return BitConverter.ToInt32(buffer, offset) / 2147483648f;
            case 3:
            {
                var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((value & 0x800000) != 0)
                    value |= unchecked((int)0xFF000000);
                return value / 8388608f;
            }
            case 2:
                return BitConverter.ToInt16(buffer, offset) / 32768f;
            case 1:
                return (buffer[offset] - 128) / 128f;
            default:
                return 0;
        }
    }

    void EnsureBands(int sampleRate)
    {
        if (_bandSampleRate == sampleRate)
            return;

        _bandSampleRate = sampleRate;
        var binHz = (double)sampleRate / FftSize;
        var lastBin = FftSize / 2 - 1;
        var ratio = MaxBandHz / MinBandHz;

        // Bands chain off each other's rounded boundary so no bin is shared by
        // two bands: a tone then lights exactly one bar.
        var previous = 1;
        for (int band = 0; band < BandCount; band++)
        {
            var high = MinBandHz * Math.Pow(ratio, (double)(band + 1) / BandCount);
            var boundary = Math.Min(lastBin, (int)Math.Round(high / binHz));
            var lo = previous;
            var hi = Math.Max(lo, Math.Min(lastBin, boundary - 1));

            _bandLowBin[band] = lo;
            _bandHighBin[band] = hi;
            previous = Math.Max(lo + 1, boundary);
        }
    }

    void ProcessWindow(int lag)
    {
        var start = (_writePos - lag - FftSize + 2 * RingSize) % RingSize;

        var energy = 0.0;
        var timePeak = 0.0;
        for (int i = 0; i < FftSize; i++)
        {
            var sample = _ring[(start + i) % RingSize];
            energy += (double)sample * sample;
            var magnitude = Math.Abs(sample);
            if (magnitude > timePeak)
                timePeak = magnitude;
            _re[i] = sample * _hann[i];
            _im[i] = 0;
        }

        var rms = Math.Sqrt(energy / FftSize);
        var silent = rms < SilenceRms;

        Transform();

        var framePeak = 0.0;
        for (int band = 0; band < BandCount; band++)
        {
            var max = 0.0;
            for (int bin = _bandLowBin[band]; bin <= _bandHighBin[band]; bin++)
            {
                var magnitude = Math.Sqrt((double)_re[bin] * _re[bin] + (double)_im[bin] * _im[bin]) * MagnitudeToAmplitude;
                if (magnitude > max)
                    max = magnitude;
            }

            _bandRaw[band] = (float)max;
            if (max > framePeak)
                framePeak = max;
        }

        // Auto-gain lifts the frame's loudest band to PeakHeadroomDb, capped by MaxGainDb so a loud voice is not blown out.
        _framePeak = Math.Max(framePeak, _framePeak * PeakDecay);
        if (_framePeak < 1e-6)
            _framePeak = 1e-6;

        _gainDb = Math.Clamp(PeakHeadroomDb - 20 * Math.Log10(_framePeak), 0, MaxGainDb);

        for (int band = 0; band < BandCount; band++)
        {
            var target = silent ? 0.0 : MappedLevel(_bandRaw[band]);
            _bands[band] = (float)Math.Max(target, _bands[band] * BandDecay);
        }

        UpdateLevel(silent, rms);

        _wavePeak = Math.Max(timePeak, _wavePeak * PeakDecay);
        BuildWaveform(silent, start);
    }

    void UpdateLevel(bool silent, double rms)
    {
        var target = silent ? 0.0 : MappedLevel(rms);
        _level += (target - _level) * (target > _level ? LevelAttack : LevelDecay);
    }

    double MappedLevel(double amplitude)
    {
        var db = 20 * Math.Log10(Math.Max(amplitude, 1e-9)) + _gainDb;
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    void BuildWaveform(bool silent, int start)
    {
        var groupSize = FftSize / WaveformPoints;
        var normalize = silent ? 0.0 : 1.0 / _wavePeak;

        for (int point = 0; point < WaveformPoints; point++)
        {
            var peak = 0.0;
            for (int i = 0; i < groupSize; i++)
            {
                var magnitude = Math.Abs(_ring[(start + point * groupSize + i) % RingSize]);
                if (magnitude > peak)
                    peak = magnitude;
            }

            var target = Math.Clamp(peak * normalize, 0, 1);
            _wave[point] = (float)Math.Max(target, _wave[point] * WaveDecay);
        }
    }

    void Transform()
    {
        for (int i = 0; i < FftSize; i++)
        {
            var j = _bitReversal[i];
            if (j <= i)
                continue;

            (_re[i], _re[j]) = (_re[j], _re[i]);
            (_im[i], _im[j]) = (_im[j], _im[i]);
        }

        for (var len = 2; len <= FftSize; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var stepRe = Math.Cos(angle);
            var stepIm = Math.Sin(angle);
            var half = len / 2;

            for (var i = 0; i < FftSize; i += len)
            {
                double curRe = 1, curIm = 0;
                for (var k = 0; k < half; k++)
                {
                    var a = i + k;
                    var b = a + half;

                    var tRe = (float)(curRe * _re[b] - curIm * _im[b]);
                    var tIm = (float)(curRe * _im[b] + curIm * _re[b]);

                    _re[b] = (float)(_re[a] - tRe);
                    _im[b] = (float)(_im[a] - tIm);
                    _re[a] += tRe;
                    _im[a] += tIm;

                    var nextRe = curRe * stepRe - curIm * stepIm;
                    curIm = curRe * stepIm + curIm * stepRe;
                    curRe = nextRe;
                }
            }
        }
    }

    static int ReverseBits(int value, int bits)
    {
        var result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }

    static int Log2(int value)
    {
        var bits = 0;
        while ((1 << bits) < value)
            bits++;
        return bits;
    }
}
