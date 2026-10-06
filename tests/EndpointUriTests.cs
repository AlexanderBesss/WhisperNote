using System;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class EndpointUriTests
{
    [Theory]
    [InlineData("http://localhost:8082", "/health", "http://127.0.0.1:8082/health")]
    [InlineData("http://LOCALHOST:8082/", "/v1/audio/transcriptions", "http://127.0.0.1:8082/v1/audio/transcriptions")]
    [InlineData("http://127.0.0.1:8082", "/health", "http://127.0.0.1:8082/health")]
    [InlineData("http://192.168.0.96:8082", "/health", "http://192.168.0.96:8082/health")]
    [InlineData("https://api.example.com/v1", "/chat", "https://api.example.com/v1/chat")]
    public void LocalhostIsPinnedToIpv4Loopback(string endpoint, string path, string expected)
    {
        // "localhost" makes .NET probe IPv6 [::1] first and stall ~2 s before
        // falling back to IPv4, delaying every health check and transcription.
        var uri = TranscriptionService.BuildEndpointUri(endpoint, path);
        Assert.Equal(expected, uri.ToString());
    }
}
