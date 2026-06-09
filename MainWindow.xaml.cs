using System.Windows;
using System.Windows.Input;
using WhisperNote.Services;

namespace WhisperNote;

public partial class MainWindow : Window
{
    readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        _viewModel = new MainWindowViewModel(App.AppState!);
        DataContext = _viewModel;

        InitializeComponent();

        Closed += (_, _) => _viewModel.Dispose();
        Deactivated += (_, _) => Opacity = 0.3;
        Activated += (_, _) => Opacity = 1.0;
    }

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    void StatusScrollViewer_MouseEnter(object sender, MouseEventArgs e)
    {
        StatusScrollViewer.Focus();
    }
}
