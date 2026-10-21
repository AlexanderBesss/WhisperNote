using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WhisperNote.Config;

namespace WhisperNote;

public sealed class PaletteGradientConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is VisualizerPalette palette ? PaletteBrush(palette) : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    static Brush PaletteBrush(VisualizerPalette palette)
    {
        var stops = new GradientStopCollection();
        foreach (var stop in palette.FillStops())
        {
            stops.Add(new GradientStop(
                Color.FromArgb((byte)(stop.Argb >> 24), (byte)(stop.Argb >> 16),
                              (byte)(stop.Argb >> 8), (byte)stop.Argb),
                stop.Offset));
        }

        var brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0));
        brush.Freeze();
        return brush;
    }
}
