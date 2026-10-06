using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        _viewModel.MinimizeRequested += (_, _) => MinimizeToTray();

        // Created here rather than on Loaded so the tray exists even when the window
        // starts hidden in the notification area.
        CreateTrayIcon();

        if (_viewModel.StartInTray)
            Visibility = Visibility.Hidden;
    }

    void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_viewModel)
        {
            Owner = this
        };
        settingsWindow.ShowDialog();
    }

    void MinimizeButton_Click(object sender, RoutedEventArgs e) => MinimizeToTray();

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
        _tray?.Dispose();
        _tray = null;
        _overlay?.Close();
        _overlay = null;
        _viewModel.Dispose();
    }

    void TrayStatus_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerStateManager.Status)
            or nameof(RecordingStateManager.IsRecording)
            or nameof(RecordingStateManager.IsProcessing))
            UpdateTrayState();
    }

    // Red ring when nobody is speaking, green while listening or processing.
    void UpdateTrayState()
    {
        if (_tray == null)
            return;

        _tray.SetActive(_viewModel.RecordingManager.IsRecording ||
                        _viewModel.RecordingManager.IsProcessing);
        _tray.Tooltip = TrayTooltip();
        UpdateOverlayState();
    }

    // The pill stays up through recording and processing, and only hides once
    // the request is fully done, so a hotkey- or button-started recording never
    // looks finished while the LLM is still working.
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
