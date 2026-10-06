using System;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class BackendDownloaderTests
{
    const string Cuda13Pattern = @"^llama-.*-bin-win-cuda-13.*-x64\.zip$";
    static readonly string[] Cuda12Patterns =
    {
        @"^cudart-llama-bin-win-cuda-12\.4-x64\.zip$",
        @"^llama-.*-bin-win-cuda-12\.4-x64\.zip$"
    };

    static string Release(string tag, params string[] assetNames)
    {
        var assets = Array.ConvertAll(assetNames,
            name => $"{{ \"name\": \"{name}\", \"browser_download_url\": \"https://example.test/{name}\" }}");
        return $"{{ \"tag_name\": \"{tag}\", \"assets\": [ {string.Join(", ", assets)} ] }}";
    }

    [Fact]
    public void SelectAssetsPicksTheNewestReleaseCoveringEveryPattern()
    {
        var json = $"[ {Release("b2", "llama-b2-bin-win-cuda-13.1-x64.zip")}, "
                 + $"{Release("b1", "llama-b1-bin-win-cuda-12.4-x64.zip")}, "
                 + $"{Release("b0", "ignored.zip")} ]";

        var picked = BackendDownloader.SelectAssets(json, new[] { Cuda13Pattern });

        Assert.NotNull(picked);
        Assert.Single(picked!);
        Assert.Equal("llama-b2-bin-win-cuda-13.1-x64.zip", picked![0].Name);
    }

    [Fact]
    public void SelectAssetsRequiresEveryPatternInTheSameRelease()
    {
        // The cudart zip exists only in the older release: the newer release
        // must not be half-selected.
        var json = $"[ {Release("b2", "llama-b2-bin-win-cuda-12.4-x64.zip")}, "
                 + $"{Release("b1", "cudart-llama-bin-win-cuda-12.4-x64.zip", "llama-b1-bin-win-cuda-12.4-x64.zip")} ]";

        var picked = BackendDownloader.SelectAssets(json, Cuda12Patterns);

        Assert.NotNull(picked);
        Assert.Equal(2, picked!.Count);
        Assert.Equal("cudart-llama-bin-win-cuda-12.4-x64.zip", picked[0].Name);
        Assert.Equal("llama-b1-bin-win-cuda-12.4-x64.zip", picked[1].Name);
    }

    [Fact]
    public void SelectAssetsReturnsNullWhenNoReleaseMatches()
    {
        var json = $"[ {Release("b1", "llama-b1-bin-win-vulkan-x64.zip")} ]";

        Assert.Null(BackendDownloader.SelectAssets(json, new[] { Cuda13Pattern }));
    }

    [Fact]
    public void SelectAssetsToleratesMalformedPayloads()
    {
        Assert.Null(BackendDownloader.SelectAssets("[]", new[] { Cuda13Pattern }));
        Assert.Null(BackendDownloader.SelectAssets("[ { } ]", new[] { Cuda13Pattern }));
        Assert.Null(BackendDownloader.SelectAssets("{}", new[] { Cuda13Pattern }));
    }

    [Fact]
    public void CpuPatternMatchesTheOfficialCpuZip()
    {
        var json = $"[ {Release("b9", "llama-b9-bin-win-cpu-x64.zip", "llama-b9-bin-win-cuda-13.1-x64.zip")} ]";

        var picked = BackendDownloader.SelectAssets(json, new[] { @"^llama-.*-bin-win-cpu-x64\.zip$" });

        Assert.NotNull(picked);
        Assert.Equal("llama-b9-bin-win-cpu-x64.zip", picked![0].Name);
    }
}
