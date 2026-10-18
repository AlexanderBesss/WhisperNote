using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WhisperNote.Services;
using WhisperNote.ViewModels;

namespace WhisperNote;

public partial class MainWindow : Window
{
    readonly MainWindowViewModel _viewModel;
    TrayIconService? _tray;
    RecordingOverlayWindow? _overlay;
    bool _exitRequested;

    public MainWindow()
    {
        _viewModel = new MainWindowViewModel(App.AppState!);
        DataContext = _viewModel;

        InitializeComponent();
        TrayIconService.ApplyWindowIcon(this);

        Activated += (_, _) => _viewModel.SetFocused(true);
        Deactivated += (_, _) => _viewModel.SetFocused(false);
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;

        // Not on Loaded: Loaded never fires while the window starts hidden.
        CreateTrayIcon();
    }

    void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_viewModel)
        {
            Owner = this
        };
        settingsWindow.ShowDialog();
    }

    void CloseButton_Click(object sender, RoutedEventArgs e) => MinimizeToTray();

    public void MinimizeToTray() => Hide();

    void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    void CreateTrayIcon()
    {
        if (_tray != null)
            return;

        _tray = new TrayIconService(TrayTooltip());
        _tray.RestoreRequested += (_, _) => RestoreFromTray();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _overlay = new RecordingOverlayWindow
        {
            DataContext = _viewModel.RecordingManager
        };

        _viewModel.ServerManager.PropertyChanged += TrayStatus_PropertyChanged;
        _viewModel.RecordingManager.PropertyChanged += TrayStatus_PropertyChanged;
        UpdateTrayState();
        Logger.Info("Tray icon created");
    }

    void ExitApplication()
    {
        _exitRequested = true;
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested || !_viewModel.MinimizeToTray)
            return;

        e.Cancel = true;
        MinimizeToTray();
    }

    void MainWindow_Closed(object? sender, EventArgs e)
    {
        // ShutdownMode=OnExplicitShutdown (App.xaml): a real exit needs an explicit dispatcher shutdown.
        var shouldExit = _exitRequested || !_viewModel.MinimizeToTray;
        _tray?.Dispose();
        _tray = null;
        _overlay?.Close();
        _overlay = null;
        _viewModel.Dispose();
        if (shouldExit)
            Application.Current?.Shutdown();
    }

    void TrayStatus_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerStateManager.Status)
            or nameof(RecordingStateManager.IsRecording)
            or nameof(RecordingStateManager.IsProcessing))
            UpdateTrayState();
    }

    void UpdateTrayState()
    {
        if (_tray == null)
            return;

        _tray.SetState(_viewModel.RecordingManager.IsRecording,
                        _viewModel.RecordingManager.IsProcessing);
        _tray.Tooltip = TrayTooltip();
        UpdateOverlayState();
    }

    void UpdateOverlayState()
    {
        if (_overlay == null)
            return;

        if (_viewModel.RecordingManager.IsRecording || _viewModel.RecordingManager.IsProcessing)
            _overlay.ShowRecording();
        else
            _overlay.HideRecording();
    }

    string TrayTooltip() =>
        _viewModel.RecordingManager.IsRecording
            ? "WhisperNote · Recording"
            : $"WhisperNote · {_viewModel.ServerManager.Status.Message}";

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (VisualTree.FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
            return;

        DragMove();
    }
}
