using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        Closed += (_, _) => _viewModel.Detach();
    }

    // WPF commits a focused TextBox only on focus loss; ClearFocus() flushes it before Close.
    void CloseButton_Click(object sender, RoutedEventArgs e) => CloseSettings();

    void CloseSettings()
    {
        Keyboard.ClearFocus();
        Close();
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" fires this during InitializeComponent(), before the panel fields exist.
        if (PanelModel is null || PanelRecord is null || PanelOutput is null ||
            PanelGrammar is null || PanelSystem is null)
            return;

        var tab = (string)((RadioButton)sender).Tag;
        PanelModel.Visibility = TabVisibility(tab == "model");
        PanelRecord.Visibility = TabVisibility(tab == "record");
        PanelOutput.Visibility = TabVisibility(tab == "output");
        PanelGrammar.Visibility = TabVisibility(tab == "grammar");
        PanelSystem.Visibility = TabVisibility(tab == "system");
    }

    static Visibility TabVisibility(bool show) =>
        show ? Visibility.Visible : Visibility.Collapsed;

    void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        CloseSettings();
    }

    bool _dragging;
    Point _dragStart;
    double _startLeft, _startTop;
    double _ownerStartLeft, _ownerStartTop;

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        // An open combo box dropdown holds mouse capture; without this its clicks would start a drag.
        if (Mouse.Captured is not null and not SettingsWindow)
            return;

        // A click from a popup's separate visual tree (e.g. a dropdown) must not start a drag.
        if (!VisualTree.IsWithin(e.OriginalSource as DependencyObject, this))
            return;

        if (IsInteractiveSource(e.OriginalSource as DependencyObject))
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

    // The open dropdown lives in its own visual tree, so ComboBoxItem needs its own ancestor check.
    static bool IsInteractiveSource(DependencyObject? source) =>
        VisualTree.FindAncestor<ButtonBase>(source) != null ||
        VisualTree.FindAncestor<ComboBoxItem>(source) != null ||
        VisualTree.FindAncestor<ComboBox>(source) != null ||
        VisualTree.FindAncestor<TextBox>(source) != null;

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

        // A closing dropdown can steal capture before button-up; without this the drag never ends.
        _dragging = false;
    }
}
