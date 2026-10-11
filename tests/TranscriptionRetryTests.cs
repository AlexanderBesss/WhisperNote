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

    [Fact]
    public async Task EmptyAnswerResendsTheSameAudio()
    {
        var handler = new StubHandler("""{"text":""}""", Formatted);
        using var service = new TranscriptionService(LocalProvider(), handler);

        var text = await service.Transcribe(new byte[3200]);

        Assert.Equal("Apply small fixes.", text);
        Assert.Equal(2, handler.RequestBodies.Count);
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
        Assert.Equal(3, handler.RequestBodies.Count);
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
