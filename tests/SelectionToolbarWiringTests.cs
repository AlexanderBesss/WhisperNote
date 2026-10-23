using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WhisperNote.Config;
using Xunit;

namespace WhisperNote.Tests;

// The fix pill's XAML wiring rides in the recording overlay's single WpfThread
// fact: one WPF session per process is all the Application allows, and a test
// window collected between facts marks it shut down. See RecordingOverlayWiringTests.
internal static class SelectionToolbarWiringTests
{
    // Returns the window so the caller keeps it rooted: a collected test window
    // flips the WPF shutdown flag for the rest of the fact.
    internal static SelectionToolbarWindow AssertFixPillStyle()
    {
        var window = new SelectionToolbarWindow(() => { });

        var root = (Grid)window.Content;
        var pill = (Border)root.Children[0];
        Assert.Equal(Color.FromArgb(0xE6, 0x1A, 0x1A, 0x1A),
            ((SolidColorBrush)pill.Background).Color);
        Assert.Equal(new CornerRadius(16), pill.CornerRadius);
        Assert.Equal(20, pill.Margin.Left);
        var shadow = Assert.IsType<DropShadowEffect>(pill.Effect);
        Assert.Equal(18, shadow.BlurRadius);
        Assert.Equal(2, shadow.ShadowDepth);
        Assert.Equal(0.55, shadow.Opacity);

        var glow = (GlowBorder)window.FindName("Glow");
        Assert.NotNull(glow);
        Assert.Same(window.Palette, glow.Palette);
        Assert.Equal(window.FrameFps, glow.FrameFps);

        var warm = VisualizerPaletteCatalog.Resolve("Signal Red");
        window.Palette = warm;
        Assert.Same(warm, glow.Palette);
        window.FrameFps = 120;
        Assert.Equal(120, glow.FrameFps);
        return window;
    }
}
