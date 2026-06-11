using System.Windows;
using System.Windows.Input;
using WhisperNote.ViewModels;

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
    }

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
