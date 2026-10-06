using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WhisperNote.Services;
using WhisperNote.ViewModels;

namespace WhisperNote;

public partial class SettingsWindow : Window
{
    readonly SettingsViewModel _viewModel;

    public SettingsWindow(MainWindowViewModel mainViewModel)
    {
        InitializeComponent();
        TrayIconService.ApplyWindowIcon(this);
        _viewModel = new SettingsViewModel(mainViewModel);
        DataContext = _viewModel;
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Apply();
        DialogResult = true;
    }

    void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Fires while InitializeComponent() builds the tab bar, before the
        // content panels exist.
        if (PanelModel is null || PanelRecord is null || PanelOutput is null || PanelSystem is null)
            return;

        var tab = (string)((RadioButton)sender).Tag;
        PanelModel.Visibility = TabVisibility(tab == "model");
        PanelRecord.Visibility = TabVisibility(tab == "record");
        PanelOutput.Visibility = TabVisibility(tab == "output");
        PanelSystem.Visibility = TabVisibility(tab == "system");
    }

    static Visibility TabVisibility(bool show) =>
        show ? Visibility.Visible : Visibility.Collapsed;

    void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        DialogResult = false;
    }

    // Custom drag: DragMove() only moves this window and misbehaves inside
    // preview events, so track the mouse manually and move the owner (main
    // window) along with the settings window.
    bool _dragging;
    Point _dragStart;
    double _startLeft, _startTop;
    double _ownerStartLeft, _ownerStartTop;

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        // A combo box dropdown holds the mouse while it is open; its clicks
        // belong to the dropdown, never to the drag logic.
        if (Mouse.Captured is not null and not SettingsWindow)
            return;

        // Interactive controls (buttons, switches, tabs, combo boxes) keep
        // their clicks; everywhere else starts a drag.
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source != this)
        {
            if (source is ButtonBase or ComboBoxItem or ComboBox or TextBox)
                return;
            source = VisualTreeHelper.GetParent(source);
        }

        // The walk stopped at null instead of at this window: the click came
        // from a popup's separate visual tree (e.g. a combo box dropdown),
        // not from this window's surface, so it must not start a drag.
        if (source is null)
            return;

        _dragStart = PointToScreen(e.GetPosition(this));
        _startLeft = Left;
        _startTop = Top;
        _ownerStartLeft = Owner?.Left ?? 0;
        _ownerStartTop = Owner?.Top ?? 0;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;

        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;
        Left = _startLeft + dx;
        Top = _startTop + dy;

        if (Owner is { WindowState: WindowState.Normal })
        {
            Owner.Left = _ownerStartLeft + dx;
            Owner.Top = _ownerStartTop + dy;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
            return;

        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // A closing dropdown can steal capture back before the button-up
        // arrives; without this the window would keep following the mouse.
        _dragging = false;
    }
}
