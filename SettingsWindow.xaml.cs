using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhisperNote.ViewModels;

namespace WhisperNote;

public partial class SettingsWindow : Window
{
    readonly SettingsViewModel _viewModel;

    public SettingsWindow(MainWindowViewModel mainViewModel)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(mainViewModel);
        DataContext = _viewModel;
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryApply())
            return;

        DialogResult = true;
    }

    void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        DialogResult = false;
    }

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) != null)
            return;

        DragMove();
    }

    static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match)
                return match;

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}
