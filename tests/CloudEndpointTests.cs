using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WhisperNote.Config;
using WhisperNote.Services;
using WhisperNote.ViewModels;
using Xunit;

namespace WhisperNote.Tests;

public class CloudEndpointTests
{
    [Fact]
    public void LegacySingleEndpointRemainsPrimary()
    {
        var provider = JsonSerializer.Deserialize<ProviderConfig>("""
            { "Name": "Legacy cloud", "Type": "remote", "ApiEndpoint": "https://legacy.example" }
            """)!;

        Assert.Equal(new[] { "https://legacy.example" }, provider.GetApiEndpoints());
    }

    [Theory]
    [InlineData("https://cloud.example/v1", true)]
    [InlineData(" http://cloud.example/ ", true)]
    [InlineData("ftp://cloud.example", false)]
    [InlineData("cloud.example", false)]
    public void PrimaryEndpointRequiresAbsoluteHttpUrl(string value, bool valid)
    {
        Assert.Equal(valid, new CloudEndpointEntry(value, 0).IsValid);
    }

    [Fact]
    public void BlankBackupIsValidButInvalidNonBlankBackupIsNot()
    {
        Assert.True(new CloudEndpointEntry("  ", 1).IsValid);
        Assert.False(new CloudEndpointEntry("not-a-url", 1).IsValid);
    }

    [Fact]
    public async Task CloudTranscriptionTriesEndpointsInOrderAndStopsAfterSuccess()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Host switch
        {
            "primary.example" => Response(HttpStatusCode.ServiceUnavailable, "primary unavailable"),
            "backup.example" => Response(HttpStatusCode.OK, "{\"text\":\"Recovered transcription\"}"),
            _ => Response(HttpStatusCode.OK, "{\"text\":\"should not be called\"}")
        });
        var provider = CloudProvider(
            "https://primary.example",
            "https://backup.example",
            "https://later.example");

        using var service = new TranscriptionService(provider, handler);
        var result = await service.Transcribe(new byte[] { 0, 0 });

        Assert.Equal("Recovered transcription.", result);
        Assert.Equal(new[] { "primary.example", "backup.example" }, handler.Hosts);
    }

    [Fact]
    public async Task CloudTranscriptionAttemptsEachEndpointOnceAndThrowsLastFailure()
    {
        var handler = new RecordingHandler(request =>
            Response(HttpStatusCode.BadGateway, $"failed {request.RequestUri!.Host}"));
        var provider = CloudProvider("https://one.example", "https://two.example");

        using var service = new TranscriptionService(provider, handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.Transcribe(new byte[] { 0, 0 }));

        Assert.Equal(new[] { "one.example", "two.example" }, handler.Hosts);
        Assert.Contains("failed two.example", error.Message);
    }

    [Fact]
    public async Task CloudTranscriptionMovesPastConnectionFailure()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Host == "offline.example")
                throw new HttpRequestException("connection refused");
            return Response(HttpStatusCode.OK, "{\"text\":\"Backup worked\"}");
        });
        var provider = CloudProvider("https://offline.example", "https://online.example");

        using var service = new TranscriptionService(provider, handler);
        var result = await service.Transcribe(new byte[] { 0, 0 });

        Assert.Equal("Backup worked.", result);
        Assert.Equal(new[] { "offline.example", "online.example" }, handler.Hosts);
    }

    static ProviderConfig CloudProvider(params string[] endpoints) => new()
    {
        Name = "Cloud",
        Type = "remote",
        ApiEndpoint = endpoints[0],
        ApiEndpoints = endpoints.ToList(),
        Model = "test-model"
    };

    static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    sealed class RecordingHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        public List<string> Hosts { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) =>
            _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(_response(request));
        }
    }
}
