using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

// The server can answer 200 with a body the parser rejects; that must not throw the recording away.
public class TranscriptionRetryTests
{
    const string AsrModel = "Qwen3-ASR-1.7B-Q8_0.gguf";
    const string Formatted = """{"text":"language English<asr_text>Apply small fixes.</asr_text>"}""";
    const string ChatStyle = """{"text":"language English. Yeah, apply small fixes."}""";

    static ProviderConfig LocalProvider() => new()
    {
        Name = "local",
        Type = "local",
        ApiEndpoint = "http://127.0.0.1:8082",
        Model = AsrModel,
    };

    sealed class StubHandler : HttpMessageHandler
    {
        readonly Queue<string> _bodies = new();

        public List<string> RequestBodies { get; } = new();

        public StubHandler(params string[] bodies)
        {
            foreach (var body in bodies)
                _bodies.Enqueue(body);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var body = _bodies.Count > 1 ? _bodies.Dequeue() : _bodies.Peek();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task FormattedAnswerIsAcceptedWithoutRetrying()
    {
        var handler = new StubHandler(Formatted);
        using var service = new TranscriptionService(LocalProvider(), handler);

        var text = await service.Transcribe(new byte[3200]);

        Assert.Equal("Apply small fixes.", text);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task UnusableFirstAnswerResendsTheSameAudio()
    {
        var handler = new StubHandler(ChatStyle, Formatted);
        using var service = new TranscriptionService(LocalProvider(), handler);

        var text = await service.Transcribe(new byte[3200]);

        Assert.Equal("Apply small fixes.", text);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    // Decoding is greedy: probes with silence, a hum, a click and room noise all answer
    // "language None" or an empty <asr_text>, so identical resends can only repeat that answer.
    [Fact]
    public async Task EmptyAnswerIsNotResentBecauseTheModelHeardNoSpeech()
    {
        var handler = new StubHandler("""{"text":""}""");
        using var service = new TranscriptionService(LocalProvider(), handler);

        var text = await service.Transcribe(new byte[3200]);

        Assert.Null(text);
        Assert.Single(handler.RequestBodies);
        Assert.Equal(AsrFailure.NoSpeech, service.LastFailure);
    }

    [Fact]
    public async Task ExplicitNoSpeechAnswerIsNotResent()
    {
        var handler = new StubHandler("""{"text":"language None<asr_text>"}""");
        using var service = new TranscriptionService(LocalProvider(), handler);

        Assert.Null(await service.Transcribe(new byte[3200]));

        Assert.Single(handler.RequestBodies);
        Assert.Equal(AsrFailure.NoSpeech, service.LastFailure);
    }

    // Measured against the running model: a 1.4 s low-level vocalisation answered
    // "language Chinese<asr_text>啊哈哈！", which must never be pasted into the user's document.
    [Fact]
    public async Task AnswerInAnotherScriptIsDroppedInsteadOfPasted()
    {
        var handler = new StubHandler("""{"text":"language Chinese<asr_text>啊哈哈！</asr_text>"}""");
        using var service = new TranscriptionService(LocalProvider(), handler);

        Assert.Null(await service.Transcribe(new byte[3200]));

        Assert.Single(handler.RequestBodies);
        Assert.Equal(AsrFailure.ForeignLanguage, service.LastFailure);
    }

    [Fact]
    public async Task DedicatedAsrRequestPinsTheSpokenLanguage()
    {
        var handler = new StubHandler(Formatted);
        using var service = new TranscriptionService(LocalProvider(), handler);

        await service.Transcribe(new byte[3200]);

        Assert.Equal("en", FormValue(handler.RequestBodies[0], "language"));
    }

    // The language field belongs to dedicated ASR models only; a chat model must not be told what language to speak.
    [Fact]
    public async Task GenericModelFormSendsThePromptInsteadOfALanguage()
    {
        var provider = LocalProvider();
        provider.Model = "qwen2.5-7b-instruct-q4_k_m.gguf";
        var handler = new StubHandler("""{"text":"Understood."}""");
        using var service = new TranscriptionService(provider, handler);

        Assert.Equal("Understood.", await service.Transcribe(new byte[3200]));

        Assert.Null(FormValue(handler.RequestBodies[0], "language"));
        Assert.NotNull(FormValue(handler.RequestBodies[0], "prompt"));
    }

    // llama.cpp answers 400 to every recording for a model build that does not know the
    // language field, so the field is dropped after the first such answer.
    [Fact]
    public async Task LanguageFieldIsDroppedWhenTheServerRejectsIt()
    {
        var handler = new StatusStub(
            (HttpStatusCode.BadRequest, """{"error":{"message":"Invalid parameter: language"}}"""),
            (HttpStatusCode.OK, Formatted));
        using var service = new TranscriptionService(LocalProvider(), handler);

        var text = await service.Transcribe(new byte[3200]);

        Assert.Equal("Apply small fixes.", text);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal("en", FormValue(handler.RequestBodies[0], "language"));
        Assert.Null(FormValue(handler.RequestBodies[1], "language"));
    }

    sealed class StatusStub : HttpMessageHandler
    {
        readonly Queue<(HttpStatusCode Status, string Body)> _replies = new();

        public List<string> RequestBodies { get; } = new();

        public StatusStub(params (HttpStatusCode Status, string Body)[] replies)
        {
            foreach (var reply in replies)
                _replies.Enqueue(reply);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var (status, body) = _replies.Count > 1
                ? _replies.Dequeue()
                : (HttpStatusCode.OK, Formatted);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task ChatStyleAnswerIsSalvagedOnceRetriesAreExhausted()
    {
        var handler = new StubHandler(ChatStyle);
        using var service = new TranscriptionService(LocalProvider(), handler);

        var text = await service.Transcribe(new byte[3200]);

        Assert.Equal("Yeah, apply small fixes.", text);
        Assert.Equal(3, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task NothingIsInventedWhenEveryAnswerIsEmpty()
    {
        var handler = new StubHandler("""{"text":""}""");
        using var service = new TranscriptionService(LocalProvider(), handler);

        Assert.Null(await service.Transcribe(new byte[3200]));
        Assert.Equal(AsrFailure.NoSpeech, service.LastFailure);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task DedicatedAsrIsSentGreedySoTheServerTempIsNotOverridden()
    {
        var handler = new StubHandler(Formatted);
        using var service = new TranscriptionService(LocalProvider(), handler);

        await service.Transcribe(new byte[3200]);

        Assert.Equal("0", FormValue(handler.RequestBodies[0], "temperature"));
    }

    static string? FormValue(string body, string field)
    {
        var quoted = $"name=\"{field}\"";
        var marker = body.Contains(quoted) ? quoted : $"name={field}";

        var start = body.IndexOf(marker, System.StringComparison.Ordinal);
        if (start < 0)
            return null;

        var valueStart = body.IndexOf("\r\n\r\n", start, System.StringComparison.Ordinal);
        if (valueStart < 0)
            return null;
        valueStart += 4;

        var valueEnd = body.IndexOf("\r\n", valueStart, System.StringComparison.Ordinal);
        return valueEnd < 0 ? null : body[valueStart..valueEnd];
    }
}
