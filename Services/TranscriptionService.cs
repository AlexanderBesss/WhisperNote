using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using WhisperNote.Config;

namespace WhisperNote.Services;

public class TranscriptionService : IDisposable
{
    readonly HttpClient _http;
    readonly ProviderConfig _provider;

    const string SystemPrompt = "Transcribe the audio into proper English. You MUST correct all grammar errors. Fix subject-verb agreement ('i were' -> 'I was', 'she have' -> 'she has'), verb tenses, capitalization, and punctuation. Examples: 'i were going' -> 'I was going', 'she dont know' -> 'she doesn't know', 'they was here' -> 'they were here', 'he have done' -> 'he has done'. Output only the corrected transcription.";
    const string TranscriptionTemperature = "0.3";
    const int RetryDelayMs = 3000;

    public TranscriptionService(ProviderConfig provider)
    {
        _provider = provider;
        _http = new HttpClient
        {
            BaseAddress = new Uri(provider.ApiEndpoint.TrimEnd('/')),
            Timeout = TimeSpan.FromMinutes(5)
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
            using var response = await _http.GetAsync("/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> Transcribe(byte[] pcm, int channels = 1)
    {
        if (channels > 1)
            pcm = AudioProcessor.DownmixToMono(pcm, channels);

        var wavBytes = WavBuilder.Build(pcm);
        var modelName = _provider.Model;

        var durationSec = pcm.Length / (double)(AppConfig.SampleRate * 1 * AppConfig.BitsPerSample / 8);
        Logger.Info($"Model: {modelName}, WAV: {wavBytes.Length} bytes, duration: {durationSec:F2}s");
        AudioProcessor.LogAmplitude(pcm);

        var raw = await SendWithRetry(wavBytes, modelName);

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

    async Task<string?> SendWithRetry(byte[] wavBytes, string modelName)
    {
        using var content = BuildFormContent(wavBytes, modelName);
        using (var response = await _http.PostAsync("/v1/audio/transcriptions", content))
        {
            var raw = await response.Content.ReadAsStringAsync();
            Logger.Info($"Response [{response.StatusCode}]: {Truncate(raw)}");

            if (_provider.IsLocal && ShouldRetry(response, raw))
            {
                Logger.Info("Retrying after 3s...");
                await Task.Delay(RetryDelayMs);
                using var retryContent = BuildFormContent(wavBytes, modelName);
                using var retryResponse = await _http.PostAsync("/v1/audio/transcriptions", retryContent);
                raw = await retryResponse.Content.ReadAsStringAsync();
                Logger.Info($"Retry response [{retryResponse.StatusCode}]: {Truncate(raw)}");
                return raw;
            }

            return raw;
        }
    }

    static bool ShouldRetry(HttpResponseMessage response, string body) =>
        (int)response.StatusCode == 400 && body.Contains("Failed to load image or audio file");

    static string Truncate(string s, int maxLen = 300) =>
        s.Length <= maxLen ? s : s.Substring(0, maxLen);
}
