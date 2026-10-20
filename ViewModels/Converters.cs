using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WhisperNote.ViewModels;

static class StatusBrushes
{
    public static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(128, 128, 128));
    public static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(144, 238, 144));
    public static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(255, 165, 0));
    public static readonly SolidColorBrush RedBrush = new(Color.FromRgb(220, 50, 50));
    public static readonly SolidColorBrush LightBlueBrush = new(Color.FromRgb(173, 216, 230));
}

public class ServerStatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not WhisperNote.Models.ServerStatusKind kind)
            return StatusBrushes.GrayBrush;

        return kind switch
        {
            WhisperNote.Models.ServerStatusKind.Green => StatusBrushes.GreenBrush,
            WhisperNote.Models.ServerStatusKind.Orange => StatusBrushes.OrangeBrush,
            WhisperNote.Models.ServerStatusKind.Red => StatusBrushes.RedBrush,
            _ => StatusBrushes.GrayBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string kind)
            return StatusBrushes.GrayBrush;

        return kind switch
        {
            "Green" => StatusBrushes.GreenBrush,
            "Orange" => StatusBrushes.OrangeBrush,
            "Red" => StatusBrushes.RedBrush,
            "LightBlue" => StatusBrushes.LightBlueBrush,
            _ => StatusBrushes.GrayBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

// Overlay visualizer: visible only while capturing and only if the setting is on.
public class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => values.Length > 0 && Array.TrueForAll(values, value => value is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}
