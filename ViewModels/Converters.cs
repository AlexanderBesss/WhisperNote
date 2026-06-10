using System;
using System.Windows.Data;
using System.Windows.Media;

namespace WhisperNote.ViewModels;

public class ServerStatusKindToBrushConverter : IValueConverter
{
    static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(128, 128, 128));
    static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(144, 238, 144));
    static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(255, 165, 0));
    static readonly SolidColorBrush RedBrush = new(Color.FromRgb(220, 50, 50));

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not WhisperNote.Models.ServerStatusKind kind)
            return GrayBrush;

        return kind switch
        {
            WhisperNote.Models.ServerStatusKind.Green => GreenBrush,
            WhisperNote.Models.ServerStatusKind.Orange => OrangeBrush,
            WhisperNote.Models.ServerStatusKind.Red => RedBrush,
            _ => GrayBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public class StatusKindToBrushConverter : IValueConverter
{
    static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(128, 128, 128));
    static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(144, 238, 144));
    static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(255, 165, 0));
    static readonly SolidColorBrush RedBrush = new(Color.FromRgb(220, 50, 50));
    static readonly SolidColorBrush LightBlueBrush = new(Color.FromRgb(173, 216, 230));

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string kind)
            return GrayBrush;

        return kind switch
        {
            "Green" => GreenBrush,
            "Orange" => OrangeBrush,
            "Red" => RedBrush,
            "LightBlue" => LightBlueBrush,
            _ => GrayBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public class ButtonBackgroundKindToBrushConverter : IValueConverter
{
    static readonly SolidColorBrush DefaultBrush = new(Color.FromArgb(0, 100, 100, 100));
    static readonly SolidColorBrush RecordingBrush = new(Color.FromArgb(100, 220, 50, 50));

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string kind)
            return DefaultBrush;

        return kind switch
        {
            "Recording" => RecordingBrush,
            _ => DefaultBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}
