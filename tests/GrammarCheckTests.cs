using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

// The optional second pass must never cost the user their transcription: a
// dead host, a rejected key or a model that answers instead of correcting all
// have to end with the original text and a single round trip.
public class GrammarCheckTests
{
    const string Host = "http://127.0.0.1:8082";
    const string Transcript = "i has a apple and two banana";

    const string ModelsBody = """{"data":[{"id":"qwen2.5-7b-instruct"},{"id":"other"}]}""";
    const string CorrectedBody = """{"choices":[{"message":{"content":"I have an apple and two bananas."}}]}""";

    sealed class StubHandler : HttpMessageHandler
    {
        readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();

        public List<string> Requests { get; } = new();
        public List<string> Urls { get; } = new();
        public List<string> Bodies { get; } = new();
        public List<string?> Authorization { get; } = new();

        public StubHandler Route(string method, string path, Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _routes[$"{method} {path}"] = responder;
            return this;
        }

        public StubHandler Reply(string method, string path, string body) =>
            Route(method, path, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        public StubHandler Status(string method, string path, HttpStatusCode status) =>
            Route(method, path, _ => new HttpResponseMessage(status) { Content = new StringContent("") });

        public StubHandler Fail(string method, string path) =>
            Route(method, path, _ => throw new HttpRequestException("connection refused"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            Requests.Add(key);
            Urls.Add(request.RequestUri.AbsoluteUri);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            Authorization.Add(request.Headers.Authorization?.ToString());

            return _routes.TryGetValue(key, out var responder)
                ? responder(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
        }
    }

    // Wraps a content string in the chat-completion answer shape.
    static string ChatBody(string content) =>
        """{"choices":[{"message":{"content":""" + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";

    [Fact]
    public async Task ModelIsDiscoveredFromTheApiAndUsedInTheChatRequest()
    {
        var handler = new StubHandler()
            .Reply("GET", "/v1/models", ModelsBody)
            .Reply("POST", "/v1/chat/completions", CorrectedBody);
        using var service = new GrammarCheckService(Host, "sk-test", handler);

        var corrected = await service.CorrectAsync(Transcript);

        Assert.Equal("I have an apple and two bananas.", corrected);
        Assert.Equal(new[] { "GET /v1/models", "POST /v1/chat/completions" }, handler.Requests);
        Assert.Contains("\"model\":\"qwen2.5-7b-instruct\"", handler.Bodies[1]);
        Assert.Contains(Transcript, handler.Bodies[1]);
    }

    [Fact]
    public async Task KeyIsSentAsABearerTokenOnlyWhenConfigured()
    {
        var withKey = new StubHandler()
            .Reply("GET", "/v1/models", ModelsBody)
            .Reply("POST", "/v1/chat/completions", CorrectedBody);
        using (var service = new GrammarCheckService(Host, "sk-test", withKey))
            await service.CorrectAsync(Transcript);
        Assert.Equal("Bearer sk-test", withKey.Authorization[1]);

        var withoutKey = new StubHandler()
            .Reply("GET", "/v1/models", ModelsBody)
            .Reply("POST", "/v1/chat/completions", CorrectedBody);
        using (var service = new GrammarCheckService(Host, "", withoutKey))
            await service.CorrectAsync(Transcript);
        Assert.Null(withoutKey.Authorization[1]);
    }

    [Fact]
    public async Task UnreachableHostSpendsOnlyTheProbeAndSkipsThePass()
    {
        var handler = new StubHandler()
            .Fail("GET", "/v1/models")
            .Reply("POST", "/v1/chat/completions", CorrectedBody);
        using var service = new GrammarCheckService(Host, "", handler);

        Assert.Null(await service.CorrectAsync(Transcript));
        Assert.Equal(new[] { "GET /v1/models" }, handler.Requests);
        Assert.Contains("unreachable", service.ProbeError);
    }

    [Fact]
    public async Task RejectedKeyIsReportedInsteadOfGuessingAModel()
    {
        var handler = new StubHandler().Status("GET", "/v1/models", HttpStatusCode.Unauthorized);
        using var service = new GrammarCheckService(Host, "wrong", handler);

        Assert.Null(await service.CorrectAsync(Transcript));
        Assert.Single(handler.Requests);
        Assert.Contains("key was rejected", service.ProbeError);
    }

    [Fact]
    public async Task FailedChatRequestIsNeverRetried()
    {
        var handler = new StubHandler()
            .Reply("GET", "/v1/models", ModelsBody)
            .Status("POST", "/v1/chat/completions", HttpStatusCode.InternalServerError);
        using var service = new GrammarCheckService(Host, "", handler);

        Assert.Null(await service.CorrectAsync(Transcript));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ServerWithoutAModelListingStillGetsOneChatRequest()
    {
        // llama.cpp and Ollama answer /v1/chat/completions for any model name,
        // so a server that does not list models stays usable.
        var handler = new StubHandler()
            .Status("GET", "/v1/models", HttpStatusCode.NotFound)
            .Reply("POST", "/v1/chat/completions", CorrectedBody);
        using var service = new GrammarCheckService(Host, "", handler);

        Assert.Equal("I have an apple and two bananas.", await service.CorrectAsync(Transcript));
        Assert.Contains("\"model\":\"default\"", handler.Bodies[1]);
    }

    [Fact]
    public async Task EmptyAnswerIsTreatedAsNoCorrection()
    {
        var handler = new StubHandler()
            .Reply("GET", "/v1/models", ModelsBody)
            .Reply("POST", "/v1/chat/completions", """{"choices":[{"message":{"content":"   "}}]}""");
        using var service = new GrammarCheckService(Host, "", handler);

        Assert.Null(await service.CorrectAsync(Transcript));
    }

    [Fact]
    public async Task AnswerThatWroteMoreThanItFixedIsDiscarded()
    {
        var rambling = new string('x', 200);
        var handler = new StubHandler()
            .Reply("GET", "/v1/models", ModelsBody)
            .Reply("POST", "/v1/chat/completions", ChatBody(rambling));
        using var service = new GrammarCheckService(Host, "", handler);

        Assert.Null(await service.CorrectAsync(Transcript));
    }

    [Fact]
    public async Task SecondRecordingWaitsForTheProbeCooldown()
    {
        var handler = new StubHandler()
            .Fail("GET", "/v1/models")
            .Reply("POST", "/v1/chat/completions", CorrectedBody);
        using var service = new GrammarCheckService(Host, "", handler);

        await service.CorrectAsync(Transcript);
        await service.CorrectAsync(Transcript);

        // One probe, then the cooldown skips it: a dead host costs the 5 s
        // probe timeout once, not on every recording.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void FailedProbeIsRetriedOnlyAfterTheCooldown()
    {
        var failedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(GrammarCheckService.ShouldRetryProbe(null, failedAt));
        Assert.False(GrammarCheckService.ShouldRetryProbe(failedAt, failedAt.AddSeconds(10)));
        Assert.False(GrammarCheckService.ShouldRetryProbe(failedAt, failedAt.AddSeconds(59)));
        Assert.True(GrammarCheckService.ShouldRetryProbe(failedAt, failedAt.AddSeconds(60)));
    }

    [Theory]
    [InlineData("\"I fixed it.\"", "I fixed it.")]
    [InlineData("\u201CI fixed it.\u201D", "I fixed it.")]
    [InlineData("```\nI fixed it.\n```", "I fixed it.")]
    [InlineData("```text\r\nI fixed it.\r\n```", "I fixed it.")]
    [InlineData("I fixed it.", "I fixed it.")]
    public void DecorationAroundTheAnswerIsRemoved(string raw, string expected) =>
        Assert.Equal(expected, GrammarCheckService.ExtractContent(ChatBody(raw)));

    [Fact]
    public void CompletionStyleAnswersAreAlsoRead() =>
        Assert.Equal("I fixed it.", GrammarCheckService.ExtractContent(
            """{"choices":[{"text":"I fixed it."}]}"""));

    [Fact]
    public void GarbageAndMissingChoicesYieldNoText()
    {
        Assert.Null(GrammarCheckService.ExtractContent("not json"));
        Assert.Null(GrammarCheckService.ExtractContent("""{"choices":[]}"""));
        Assert.Null(GrammarCheckService.ExtractContent("""{"error":{"message":"no"}}"""));
    }

    [Fact]
    public async Task BareHostWithoutASchemeStillReachesTheServer()
    {
        // A LAN server is typed as "192.168.0.68:8080"; that is not a Uri, but
        // it is unambiguous, so plain HTTP is assumed.
        var handler = new StubHandler()
            .Reply("GET", "/v1/models", ModelsBody)
            .Reply("POST", "/v1/chat/completions", CorrectedBody);
        using var service = new GrammarCheckService("192.168.0.68:8080", "", handler);

        Assert.Equal("I have an apple and two bananas.", await service.CorrectAsync(Transcript));
        Assert.Equal("http://192.168.0.68:8080/v1/models", handler.Urls[0]);
    }

    [Fact]
    public async Task ApiSegmentIsNotDoubledWhenTheHostAlreadyNamesIt()
    {
        // "https://api.openai.com/v1" is the base URL people copy from providers,
        // so /v1 must not be appended a second time.
        var handler = new StubHandler()
            .Reply("GET", "/v1/models", ModelsBody)
            .Reply("POST", "/v1/chat/completions", CorrectedBody);
        using var service = new GrammarCheckService("https://api.openai.com/v1", "sk-test", handler);

        Assert.Equal("I have an apple and two bananas.", await service.CorrectAsync(Transcript));
        Assert.Equal("https://api.openai.com/v1/models", handler.Urls[0]);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Urls[1]);
    }

    [Fact]
    public void EffortIsSentOnlyWhenItIsOneOfTheKnownValues()
    {
        Assert.Contains(@"""reasoning_effort"":""low""",
            GrammarCheckService.BuildRequestPayload("m", "text", "low"));
        Assert.Contains(@"""reasoning_effort"":""minimal""",
            GrammarCheckService.BuildRequestPayload("m", "text", " MINIMAL "));

        // Not set, and a value the server would not understand, both mean the
        // parameter is left out; a hand-edited config cannot inject JSON.
        Assert.DoesNotContain("reasoning_effort", GrammarCheckService.BuildRequestPayload("m", "text", null));
        Assert.DoesNotContain("reasoning_effort", GrammarCheckService.BuildRequestPayload("m", "text", ""));
        Assert.DoesNotContain("reasoning_effort",
            GrammarCheckService.BuildRequestPayload("m", "text", "low\",\"x\":\""));
    }

    [Fact]
    public void TheOffOptionAsksForTheLowestEffortServersAccept()
    {
        // The dropdown says "Off" because that is what it feels like; the wire
        // value stays "minimal", which both llama.cpp and OpenAI accept.
        var off = GrammarCheckService.EffortOptions.Single(option => option.Name == "Off");
        Assert.Equal("minimal", off.Value);
        Assert.Equal("minimal", GrammarCheckService.NormalizeEffort(off.Value));
    }

    [Fact]
    public async Task EffortReachesTheServerInTheChatRequest()
    {
        var handler = new StubHandler()
            .Reply("GET", "/v1/models", ModelsBody)
            .Reply("POST", "/v1/chat/completions", CorrectedBody);
        using var service = new GrammarCheckService(Host, "", handler, "minimal");

        Assert.Equal("I have an apple and two bananas.", await service.CorrectAsync(Transcript));
        Assert.Contains(@"""reasoning_effort"":""minimal""", handler.Bodies[1]);
    }

    [Fact]
    public async Task MissingHostIsReportedInsteadOfThrowing()
    {
        using var service = new GrammarCheckService("", "", new StubHandler());

        Assert.False(await service.ProbeAsync());
        Assert.Contains("invalid API host", service.ProbeError);
        Assert.Null(await service.CorrectAsync(Transcript));
    }
}
