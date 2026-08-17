using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class RemoteExecutionTests
{
    [Fact]
    public void ExistingSettingsDefaultToDirectApi()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {
              "ActiveProviderIndex": 1,
              "Providers": [
                { "Name": "Local", "Type": "local", "ApiEndpoint": "http://localhost:8082" },
                { "Name": "Existing remote", "Type": "remote", "ApiEndpoint": "https://api.example" }
              ]
            }
            """)!;

        Assert.Equal(RemoteProviderMode.DirectApi, settings.RemoteProviderMode);
        var state = new AppState(settings);
        Assert.Equal("remote", state.ActiveProvider!.Type);
        Assert.Equal("https://api.example", state.ActiveProvider.ApiEndpoint);
    }

    [Fact]
    public void RemoteExecutionClientAndListenerHaveSeparateDefaults()
    {
        var settings = new AppSettings();

        Assert.Equal("http://localhost:8090", settings.RemoteServerEndpoint);
        Assert.Equal("http://0.0.0.0:8090", settings.RemoteListenEndpoint);
        Assert.True(AppSettings.TryNormalizeHttpListenEndpoint(settings.RemoteListenEndpoint, out _));
    }

    [Fact]
    public void RemoteExecutionEndpointStaysSeparateFromDirectApiFailover()
    {
        var settings = Settings();
        settings.RemoteProviderMode = RemoteProviderMode.RemoteExecution;
        settings.RemoteServerEndpoint = "http://whisper-server:8090";
        var state = new AppState(settings);

        Assert.True(state.ActiveProvider!.IsRemoteExecution);
        Assert.Equal("http://whisper-server:8090", state.ActiveProvider.ApiEndpoint);
        Assert.Equal(new[] { "https://primary.example", "https://backup.example" }, state.CloudLlmUrls);
    }

    [Fact]
    public async Task RemoteExecutionClientUsesDedicatedJsonContract()
    {
        RemoteTranscriptionRequest? captured = null;
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal("http://server.example:8090/api/transcriptions", request.RequestUri!.ToString());
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            captured = JsonSerializer.Deserialize<RemoteTranscriptionRequest>(
                request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return Response(HttpStatusCode.OK, "{\"text\":\"Processed by server\"}");
        });
        var provider = new ProviderConfig
        {
            Name = "WhisperNote server",
            Type = ProviderConfig.RemoteExecutionType,
            ApiEndpoint = "http://server.example:8090"
        };

        using var service = new TranscriptionService(provider, handler);
        var result = await service.Transcribe(new byte[] { 1, 2, 3, 4 }, channels: 2);

        Assert.Equal("Processed by server", result);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, captured!.Pcm);
        Assert.Equal(2, captured.Channels);
    }

    [Fact]
    public async Task RemoteExecutionHealthReflectsReachability()
    {
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal("/health", request.RequestUri!.AbsolutePath);
            return Response(HttpStatusCode.OK, "{\"status\":\"ready\"}");
        });
        using var service = new TranscriptionService(new ProviderConfig
        {
            Type = ProviderConfig.RemoteExecutionType,
            ApiEndpoint = "http://server.example:8090"
        }, handler);

        Assert.True(await service.IsServerReady());
    }

    [Fact]
    public async Task ServerAcceptsClientRequestAndReturnsProcessorResult()
    {
        var port = FreeTcpPort();
        var endpoint = $"http://localhost:{port}/whisper";
        byte[]? received = null;
        using var server = new RemoteExecutionServer((pcm, channels, _) =>
        {
            received = pcm;
            Assert.Equal(1, channels);
            return Task.FromResult<string?>("Server transcription");
        });
        await server.StartAsync(endpoint);
        using var service = new TranscriptionService(new ProviderConfig
        {
            Type = ProviderConfig.RemoteExecutionType,
            ApiEndpoint = endpoint
        });

        Assert.True(await service.IsServerReady());
        Assert.Equal("Server transcription", await service.Transcribe(new byte[] { 4, 3, 2, 1 }));
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, received);
    }

    static AppSettings Settings() => new()
    {
        ActiveProviderIndex = 1,
        Providers = new List<ProviderConfig>
        {
            new() { Name = "Local", Type = "local", ApiEndpoint = "http://localhost:8082" },
            new()
            {
                Name = "Direct API",
                Type = "remote",
                ApiEndpoint = "https://primary.example",
                ApiEndpoints = new List<string> { "https://primary.example", "https://backup.example" }
            }
        }
    };

    static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    sealed class DelegateHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, HttpResponseMessage> _handle;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) => _handle = handle;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handle(request));
    }
}
