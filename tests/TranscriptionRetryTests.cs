using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

// The "recorded but got no response" failure: the server answers 200 with a
// body the parser rejects, and the recording used to be thrown away.
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

    // Probing the running model with silence, a hum, a click and room noise
    // always answers "language None" or an empty <asr_text>, and decoding is
    // greedy: the three identical requests the old code fired 250 ms apart
    // (19 such triples in one day of logs) could only repeat that answer.
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

    // Measured against the running model: a 1.4 s low-level vocalisation
    // answered "language Chinese<asr_text>啊哈哈！", which used to be pasted
    // into the user's document.
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

    // The mirror of the test above: the language field belongs to dedicated ASR
    // models only. A chat-style model still gets the transcription prompt, and
    // must not be told what language to speak.
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

    // llama.cpp handles the language field per audio model, so a model whose
    // build does not know it would answer 400 to every single recording. The
    // field is dropped after the first such answer instead.
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
        // Better than reporting "no speech" for audio that clearly had speech.
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

    // Reads one field out of a multipart/form-data body, skipping any
    // Content-Type header the part carries.
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
