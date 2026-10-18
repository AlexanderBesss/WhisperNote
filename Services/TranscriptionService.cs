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
    // Dedicated ASR models are trained to emit their "language X<asr_text>..."
    // answer under greedy decoding. llama.cpp honours a request-level
    // temperature (a non-numeric value is rejected with 400 "invalid stof
    // argument"), so sending 0.3 here would override the server's --temp 0 and
    // push the model into chat-style answers the parser rejects.
    const string DedicatedAsrTemperature = "0";
    const int RetryDelayMs = 3000;
    const int EmptyResultRetryDelayMs = 250;
    // Qwen3-ASR format drift and one-token answers are transient: re-sending the
    // same audio usually produces a well-formed answer, so the recording is not
    // thrown away on the first unusable response.
    const int MaxTranscriptAttempts = 3;
    const int TruncateMaxLen = 300;
    const int HealthCheckTimeoutSeconds = 2;
    const int HttpTimeoutMinutes = 5;
    // Qwen3-ASR picks the language itself, and for a hum, a throat-clear or a
    // clipped syllable it picks Chinese: measured on this machine, a low-level
    // vocalisation answered "language Chinese<asr_text>啊哈哈！". llama.cpp
    // forwards the `language` field, and the same audio with en answered
    // "Ahaha." instead. The whole app dictates and cleans up in English.
    const string AsrLanguage = "en";

    // Surfaced to the UI after a failed transcription so the message can name
    // the reason (no speech heard / wrong language / format drift) instead of
    // blaming the model for all three. Written by Transcribe, read after it.
    public AsrFailure LastFailure { get; private set; }
        = AsrFailure.None;

    // Surfaced in the UI when a request stalls, so a timeout is not mistaken
    // for the user's own cancellation.
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

        // llama.cpp's /v1/audio/transcriptions replaces its built-in
        // "Transcribe audio to text" instruction with any non-empty prompt.
        // Dedicated ASR models (Qwen3-ASR) only produce a proper
        // "language X<asr_text>..." output for the instruction they were
        // trained on, so omit the prompt for them and let the server default win.
        var dedicatedAsr = LocalModels.IsDedicatedAsr(modelName);
        if (!dedicatedAsr)
        {
            content.Add(new StringContent(SystemPrompt), "prompt");
        }
        else
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

                EnsureSuccess(response, raw);

                var text = TranscriptionParser.Parse(raw, dedicatedAsr, out var failure);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    LastFailure = AsrFailure.None;
                    return text;
                }

                lastFailure = failure;

                // The model heard the recording and said there is no speech in
                // it, or answered in a language the user is not dictating in:
                // decoding is greedy, so the two retries that used to follow
                // posted the same bytes and got the same answer 250 ms later.
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

                // Every attempt failed to produce a formatted transcript: keep a
                // chat-style answer if the model gave one, instead of reporting
                // "no speech" for audio that clearly contained speech.
                var salvaged = TranscriptionParser.SalvageUnformatted(raw);
                LastFailure = salvaged == null ? failure : AsrFailure.None;
                return salvaged;
            }
        }

        // Only reachable when every attempt was rejected before a parse decision.
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

    // Posts the form and reads the body; both the initial attempt and the
    // retry share this path.
    async Task<(HttpResponseMessage response, string raw)> PostTranscriptionAsync(
        MultipartFormDataContent content, CancellationToken ct)
    {
        var response = await _http.PostAsync(
            BuildEndpointUri(_provider.ApiEndpoint, "/v1/audio/transcriptions"), content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return (response, raw);
    }

    // .NET stalls ~2 s probing IPv6 [::1] before falling back to IPv4 when the
    // host is "localhost", while llama.cpp only listens on the IPv4 loopback.
    // Pinning the loopback address removes that stall from every request.
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

    static string Truncate(string s) =>
        s.Length <= TruncateMaxLen ? s : s.Substring(0, TruncateMaxLen);
}
