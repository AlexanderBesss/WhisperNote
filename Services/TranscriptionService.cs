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
- Convert numbers to words if appropriate
- Translate any non-English speech to English
- Remove filler words (um, uh, you know)
- Maintain the original meaning and tone

Output ONLY the corrected transcription. No explanations, no quotes, no extra text.";
    const string TranscriptionTemperature = "0.3";
    const int RetryDelayMs = 3000;
    const int TruncateMaxLen = 300;
    const int HealthCheckTimeoutSeconds = 2;
    const int HttpTimeoutMinutes = 5;

    readonly HttpClient _http;
    readonly ProviderConfig _provider;

    public TranscriptionService(ProviderConfig provider)
    {
        _provider = provider;
        _http = new HttpClient
        {
            BaseAddress = new Uri(provider.ApiEndpoint.TrimEnd('/')),
            Timeout = TimeSpan.FromMinutes(HttpTimeoutMinutes)
        };

        if (!string.IsNullOrEmpty(provider.ApiKey))
        {
            if (provider.Type == "azure")
                _http.DefaultRequestHeaders.Add("api-key", provider.ApiKey);
            else
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }
    }

    public async Task<bool> IsServerReady()
    {
        if (!_provider.IsLocal)
            return true;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(HealthCheckTimeoutSeconds));
            using var response = await _http.GetAsync("/health", cts.Token);
            return response.IsSuccessStatusCode;
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

        var durationSec = pcm.Length / (double)(AppConfig.SampleRate * 1 * AppConfig.BitsPerSample / 8);
        Logger.Info($"Model: {modelName}, WAV: {wavBytes.Length} bytes, duration: {durationSec:F2}s");
        AudioProcessor.LogAmplitude(pcm);

        var raw = await SendWithRetry(wavBytes, modelName, ct);

        return TranscriptionParser.Parse(raw);
    }

    MultipartFormDataContent BuildFormContent(byte[] wavBytes, string modelName)
    {
        var boundary = $"----FormBoundary{Guid.NewGuid():N}";
        var content = new MultipartFormDataContent(boundary);
        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent(modelName), "model");
        content.Add(new StringContent(SystemPrompt), "prompt");
        content.Add(new StringContent(TranscriptionTemperature), "temperature");
        content.Add(new StringContent(AppConfig.MaxTokens.ToString()), "max_tokens");
        return content;
    }

    public void Dispose() => _http.Dispose();

    async Task<string?> SendWithRetry(byte[] wavBytes, string modelName, CancellationToken ct)
    {
        using var content = BuildFormContent(wavBytes, modelName);
        using (var response = await _http.PostAsync("/v1/audio/transcriptions", content, ct))
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            Logger.Info($"Response [{response.StatusCode}]: {Truncate(raw)}");

            if (_provider.IsLocal && ShouldRetry(response, raw))
            {
                Logger.Info("Retrying after 3s...");
                await Task.Delay(RetryDelayMs, ct);
                using var retryContent = BuildFormContent(wavBytes, modelName);
                using var retryResponse = await _http.PostAsync("/v1/audio/transcriptions", retryContent, ct);
                raw = await retryResponse.Content.ReadAsStringAsync(ct);
                Logger.Info($"Retry response [{retryResponse.StatusCode}]: {Truncate(raw)}");
                EnsureSuccess(retryResponse, raw);
                return raw;
            }

            EnsureSuccess(response, raw);
            return raw;
        }
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

    static bool ShouldRetry(HttpResponseMessage response, string body) =>
        (int)response.StatusCode == 400 && body.Contains("Failed to load image or audio file");

    static string Truncate(string s) =>
        s.Length <= TruncateMaxLen ? s : s.Substring(0, TruncateMaxLen);
}
