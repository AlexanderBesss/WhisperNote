using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using WhisperNote.Config;

namespace WhisperNote.Services;

public class TranscriptionService : IDisposable
{
    const string SystemPrompt = @"Transcribe the audio into proper English. Correct all errors:
- Grammar: subject-verb agreement, verb tenses, pronouns
- Spelling and word choice
- Capitalization and punctuation
- Always write numbers as digits, never as words, including small numbers: 25 not twenty-five, 3.5 not three point five, 1,000 not one thousand
- Keep numbers numeric in money, dates, times, phone numbers, addresses and measurements: $40, 5:30 pm, 12/05/2026, +1 555 0100, 10 kg
- Only spell out a number when it is part of a name or fixed expression, e.g. one of them, Code Red
- Translate any non-English speech to English
- Remove filler words (um, uh, you know)
- Maintain the original meaning and tone

Output ONLY the corrected transcription. No explanations, no quotes, no extra text.";
    const string TranscriptionTemperature = "0.3";
    // Must be 0: llama.cpp honours request temperature, and any other value overrides the server's --temp 0 and breaks the ASR output format.
    const string DedicatedAsrTemperature = "0";
    const int RetryDelayMs = 3000;
    const int EmptyResultRetryDelayMs = 250;
    const int MaxTranscriptAttempts = 3;
    const int TruncateMaxLen = 300;
    const int HealthCheckTimeoutSeconds = 2;
    const int HttpTimeoutMinutes = 5;
    const string AsrLanguage = "en";

    bool _serverRejectsLanguageField;

    public AsrFailure LastFailure { get; private set; }
        = AsrFailure.None;

    public static string TimeoutDescription => $"{HttpTimeoutMinutes} minutes";

    readonly HttpClient _http;
    readonly ProviderConfig _provider;

    public TranscriptionService(ProviderConfig provider)
        : this(provider, new HttpClientHandler())
    {
    }

    public TranscriptionService(ProviderConfig provider, HttpMessageHandler handler)
    {
        _provider = provider;
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(HttpTimeoutMinutes)
        };

        if (!string.IsNullOrEmpty(provider.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
    }

    public async Task<bool> IsServerReady(CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(HealthCheckTimeoutSeconds));
            using var response = await _http.GetAsync(
                BuildEndpointUri(_provider.ApiEndpoint, "/health"),
                timeoutCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> Transcribe(byte[] pcm, int channels = 1, CancellationToken ct = default)
    {
        if (channels > 1)
            pcm = AudioProcessor.DownmixToMono(pcm, channels);

        var wavBytes = WavBuilder.Build(pcm);
        var modelName = _provider.Model;
        LastFailure = AsrFailure.None;

        var durationSec = pcm.Length / (double)(AppConfig.SampleRate * 1 * AppConfig.BitsPerSample / 8);
        Logger.Info($"Model: {modelName}, WAV: {wavBytes.Length} bytes, duration: {durationSec:F2}s");
        AudioProcessor.LogAmplitude(pcm);

        return await TranscribeWithRetriesAsync(wavBytes, modelName, LocalModels.IsDedicatedAsr(modelName), ct);
    }

    internal MultipartFormDataContent BuildFormContent(byte[] wavBytes, string modelName)
    {
        var boundary = $"----FormBoundary{Guid.NewGuid():N}";
        var content = new MultipartFormDataContent(boundary);
        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent(modelName), "model");

        // Prompt replaces llama.cpp's trained instruction; omit it for dedicated ASR or its output format breaks.
        var dedicatedAsr = LocalModels.IsDedicatedAsr(modelName);
        if (!dedicatedAsr)
        {
            content.Add(new StringContent(SystemPrompt), "prompt");
        }
        else if (!_serverRejectsLanguageField)
        {
            content.Add(new StringContent(AsrLanguage), "language");
        }

        content.Add(new StringContent(dedicatedAsr ? DedicatedAsrTemperature : TranscriptionTemperature), "temperature");
        content.Add(new StringContent(AppConfig.MaxTokens.ToString()), "max_tokens");
        return content;
    }

    public void Dispose() => _http.Dispose();

    async Task<string?> TranscribeWithRetriesAsync(
        byte[] wavBytes, string modelName, bool dedicatedAsr, CancellationToken ct)
    {
        string? lastRaw = null;
        var lastFailure = AsrFailure.Malformed;

        for (int attempt = 1; attempt <= MaxTranscriptAttempts; attempt++)
        {
            using var content = BuildFormContent(wavBytes, modelName);
            var (response, raw) = await PostTranscriptionAsync(content, ct);
            using (response)
            {
                lastRaw = raw;
                Logger.Info($"Response [{response.StatusCode}] (attempt {attempt}/{MaxTranscriptAttempts}): {Truncate(raw)}");

                if (ShouldRetryAudioLoad(response, raw) && attempt < MaxTranscriptAttempts)
                {
                    Logger.Info("Server rejected the audio file; retrying after 3s...");
                    await Task.Delay(RetryDelayMs, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode && dedicatedAsr && !_serverRejectsLanguageField
                    && RejectsLanguageField(raw))
                {
                    _serverRejectsLanguageField = true;
                    Logger.Warn("Server rejected the language field; sending the audio without it");
                    if (attempt < MaxTranscriptAttempts)
                    {
                        await Task.Delay(EmptyResultRetryDelayMs, ct);
                        continue;
                    }
                }

                EnsureSuccess(response, raw);

                var text = TranscriptionParser.Parse(raw, dedicatedAsr, out var failure);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    LastFailure = AsrFailure.None;
                    return text;
                }

                lastFailure = failure;

                if (failure is AsrFailure.NoSpeech
                    or AsrFailure.ForeignLanguage)
                {
                    Logger.Info($"Attempt {attempt}: {DescribeFailure(failure)}; not retrying");
                    LastFailure = failure;
                    return null;
                }

                if (attempt < MaxTranscriptAttempts)
                {
                    Logger.Info($"Attempt {attempt} produced no usable transcript; resending the same audio...");
                    await Task.Delay(EmptyResultRetryDelayMs, ct);
                    continue;
                }

                var salvaged = TranscriptionParser.SalvageUnformatted(raw);
                LastFailure = salvaged == null ? failure : AsrFailure.None;
                return salvaged;
            }
        }

        var fallback = TranscriptionParser.SalvageUnformatted(lastRaw);
        LastFailure = fallback == null ? lastFailure : AsrFailure.None;
        return fallback;
    }

    static string DescribeFailure(AsrFailure failure) => failure switch
    {
        AsrFailure.NoSpeech => "the model heard no speech",
        AsrFailure.ForeignLanguage => "the model answered in a non-Latin script",
        _ => "unusable answer format"
    };

    async Task<(HttpResponseMessage response, string raw)> PostTranscriptionAsync(
        MultipartFormDataContent content, CancellationToken ct)
    {
        var response = await _http.PostAsync(
            BuildEndpointUri(_provider.ApiEndpoint, "/v1/audio/transcriptions"), content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return (response, raw);
    }

    // Pin 127.0.0.1: .NET stalls ~2 s probing IPv6 [::1], and llama.cpp listens on IPv4 only.
    internal static Uri BuildEndpointUri(string endpoint, string path)
    {
        var uri = new Uri(endpoint.TrimEnd('/') + path, UriKind.Absolute);
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            uri = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
        return uri;
    }

    static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode)
            return;

        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? response.StatusCode.ToString()
            : response.ReasonPhrase;
        throw new HttpRequestException(
            $"Transcription failed ({(int)response.StatusCode} {reason}): {Truncate(body)}");
    }

    static bool ShouldRetryAudioLoad(HttpResponseMessage response, string body) =>
        (int)response.StatusCode == 400 && body.Contains("Failed to load image or audio file");

    static bool RejectsLanguageField(string body) =>
        body.Contains("language", StringComparison.OrdinalIgnoreCase) &&
        (body.Contains("invalid parameter", StringComparison.OrdinalIgnoreCase) ||
         body.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
         body.Contains("unsupported", StringComparison.OrdinalIgnoreCase));

    static string Truncate(string s) =>
        s.Length <= TruncateMaxLen ? s : s.Substring(0, TruncateMaxLen);
}
