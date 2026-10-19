using System;
using System.ComponentModel;
using System.Threading.Tasks;
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
    SelectionWatcher? _selectionWatcher;
    SelectionToolbarWindow? _selectionToolbar;
    SelectionSnapshot? _lastSelection;
    bool _fixUiBusy;
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
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        ApplySelectionFixState();
    }

    void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectionToolbarEnabled))
            ApplySelectionFixState();
    }

    void ApplySelectionFixState()
    {
        if (_viewModel.SelectionToolbarEnabled && _selectionWatcher == null)
        {
            try
            {
                _selectionWatcher = new SelectionWatcher();
                _selectionWatcher.SelectionChanged += OnSelectionSnapshot;
            }
            catch (Exception ex)
            {
                Logger.Error($"Selection toolbar watcher failed: {ex.Message}");
                _selectionWatcher = null;
            }
        }
        else if (!_viewModel.SelectionToolbarEnabled)
        {
            _selectionWatcher?.Dispose();
            _selectionWatcher = null;
            _selectionToolbar?.HidePopup();
            _lastSelection = null;
        }
    }

    void OnSelectionSnapshot(SelectionSnapshot? snapshot)
    {
        _lastSelection = snapshot;

        // While a fix or its confirmation is playing out the popup belongs to the
        // flow: watcher events (the paste collapses the selection) must not hide it.
        if (_fixUiBusy)
            return;

        if (snapshot == null || !_viewModel.SelectionToolbarEnabled)
        {
            _selectionToolbar?.HidePopup();
            return;
        }

        EnsureToolbar();
        _selectionToolbar!.ShowAt(snapshot.Rects);
    }

    void EnsureToolbar()
    {
        _selectionToolbar ??= new SelectionToolbarWindow(() => _ = RunFixWithConfirmationAsync());
    }

    // Button -> spinner -> green check (on success) or red X (on failure) at the
    // selection's side. _fixUiBusy is UI-thread-only; it fences watcher events out
    // of the popup while a fix flow owns it.
    async Task RunFixWithConfirmationAsync()
    {
        // Single flight: a second press during a running fix must not flash a
        // failure or release the popup guard while the first flow still owns it.
        if (_fixUiBusy)
            return;

        _fixUiBusy = true;
        var rects = _lastSelection?.Rects;
        var showPopup = rects != null && _viewModel.SelectionToolbarEnabled;
        try
        {
            if (!showPopup)
                rects = null;

            if (_selectionToolbar == null && showPopup)
                EnsureToolbar();
            if (_selectionToolbar != null && rects != null)
            {
                _selectionToolbar.ShowAt(rects);
                _selectionToolbar.ShowProcessing();
            }

            var ok = await _viewModel.FixSelectedTextAsync(_lastSelection?.TargetWindow ?? IntPtr.Zero);
            if (_selectionToolbar != null && rects != null)
            {
                if (ok)
                    _selectionToolbar.ShowConfirmation(rects);
                else
                    _selectionToolbar.ShowFailure(rects);
                var visibleMs = ok
                    ? SelectionToolbarWindow.ConfirmationVisibleMs
                    : SelectionToolbarWindow.FailureVisibleMs;
                await Task.Delay(visibleMs + 100);
            }
            else
            {
                _selectionToolbar?.HidePopup();
            }
        }
        finally
        {
            _fixUiBusy = false;
        }
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
        Logger.Info("Exit requested from tray menu");
        _exitRequested = true;
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested || !_viewModel.MinimizeToTray)
        {
            Logger.Info("MainWindow closing for real");
            return;
        }

        Logger.Info("MainWindow close converted to tray minimize");
        e.Cancel = true;
        MinimizeToTray();
    }

    void MainWindow_Closed(object? sender, EventArgs e)
    {
        // ShutdownMode=OnExplicitShutdown (App.xaml): a real exit needs an explicit dispatcher shutdown.
        var shouldExit = _exitRequested || !_viewModel.MinimizeToTray;
        Logger.Info($"MainWindow closed (shutdown={shouldExit})");
        // A fix caught mid-clipboard-window would otherwise strand its probe
        // sentinel: pending awaits die at shutdown, finally-blocks do not run.
        _viewModel.FlushPendingClipboardRestore();
        _selectionWatcher?.Dispose();
        _selectionWatcher = null;
        _selectionToolbar?.Close();
        _selectionToolbar = null;
        _tray?.Dispose();
        _tray = null;
        _overlay?.Close();
        _overlay = null;
        Logger.Info("Disposing view model");
        _viewModel.Dispose();
        Logger.Info("View model disposed; requesting dispatcher shutdown");
        if (shouldExit)
            Application.Current?.Shutdown();
    }

    void TrayStatus_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerStateManager.Status)
            or nameof(RecordingStateManager.IsRecording)
            or nameof(RecordingStateManager.IsProcessing)
            or nameof(RecordingStateManager.QueuedCount))
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

    string TrayTooltip()
    {
        var recording = _viewModel.RecordingManager;
        var waiting = recording.QueuedCount > 0 ? $" · {recording.QueuedCount} waiting" : "";
        return recording.IsRecording
            ? $"WhisperNote · Recording{waiting}"
            : $"WhisperNote · {_viewModel.ServerManager.Status.Message}{waiting}";
    }

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (VisualTree.FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
            return;

        DragMove();
    }
}
