using System.Drawing;
using System.Text.Json;
using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class TrayTests
{
    [Fact]
    public void MinimizeToTrayDefaultsToTrueForLegacySettings()
    {
        var defaults = new AppSettings();
        Assert.True(defaults.MinimizeToTray);

        // Configs written before the tray existed omit the key: keep tray minimize on.
        var legacy = JsonSerializer.Deserialize<AppSettings>("""
            {
              "ActiveProviderIndex": 0,
              "Providers": [
                { "Name": "Local", "Type": "local", "ApiEndpoint": "http://localhost:8082" }
              ]
            }
            """)!;

        Assert.True(legacy.MinimizeToTray);
    }

    [Fact]
    public void MinimizeToTrayFalseSurvivesRoundTrip()
    {
        var settings = new AppSettings { MinimizeToTray = false };

        var restored = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings))!;

        Assert.False(restored.MinimizeToTray);
    }

    [Fact]
    public void StartInTrayDefaultsToTrueForLegacySettings()
    {
        var defaults = new AppSettings();
        Assert.True(defaults.StartInTray);

        // Configs written before the option existed omit the key: launch hidden in the tray.
        var legacy = JsonSerializer.Deserialize<AppSettings>("""
            {
              "ActiveProviderIndex": 0,
              "Providers": [
                { "Name": "Local", "Type": "local", "ApiEndpoint": "http://localhost:8082" }
              ]
            }
            """)!;

        Assert.True(legacy.StartInTray);
    }

    [Fact]
    public void StartInTrayFalseSurvivesRoundTrip()
    {
        var settings = new AppSettings { StartInTray = false };

        var restored = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings))!;

        Assert.False(restored.StartInTray);
    }

    [Fact]
    public void AutopasteIsDisabledByDefaultAndForLegacySettings()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.AutoPaste);

        var legacy = JsonSerializer.Deserialize<AppSettings>("""
            {
              "ActiveProviderIndex": 0,
              "Providers": [
                { "Name": "Local", "Type": "local", "ApiEndpoint": "http://localhost:8082" }
              ]
            }
            """)!;

        Assert.False(legacy.AutoPaste);
    }

    [Theory]
    [InlineData("WhisperNote.TrayIcon.ico")]
    [InlineData("WhisperNote.TrayIconIdle.ico")]
    public void TrayIconsAreEmbeddedInApplicationAssembly(string resourceName)
    {
        using var stream = typeof(TrayIconService).Assembly.GetManifestResourceStream(resourceName);

        Assert.NotNull(stream);
        using var icon = new Icon(stream!);
        Assert.True(icon.Width > 0);
    }

    [Fact]
    public void TrayIconHasDistinctIdleAndActiveArtwork()
    {
        using var tray = new TrayIconService("WhisperNote");

        Assert.NotEqual(tray.IdleIcon.Handle, tray.ActiveIcon.Handle);
        Assert.NotEqual(tray.IdleIcon.ToBitmap().GetPixel(16, 2).R,
            tray.ActiveIcon.ToBitmap().GetPixel(16, 2).R);
    }
}
