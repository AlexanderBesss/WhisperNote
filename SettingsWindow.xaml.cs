using System;
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
        Loaded += (_, _) =>
        {
            FitToTallestCard();
            _viewModel.SetPreviewTabVisible(PanelVisualizer.Visibility == Visibility.Visible);
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            // Turning the visualizer off collapses the preview box, which is the tallest
            // thing in the window: the fit has to run again or its space goes dead.
            if (e.PropertyName == nameof(SettingsViewModel.VisualizerEnabled))
                FitToTallestCard();
        };

        // PasswordBox.Password is a plain CLR property, not a DependencyProperty,
        // so the masked key cannot bind: seed it from the view model and write
        // edits back by hand. Subscribe after seeding to skip the seed as an edit.
        ApiKeyBox.Password = _viewModel.GrammarApiKey;
        _apiKeyHint = ApiKeyBox.Tag as string;
        SyncApiKeyHint();
        ApiKeyBox.PasswordChanged += (_, _) =>
        {
            if (_viewModel.GrammarApiKey != ApiKeyBox.Password)
                _viewModel.GrammarApiKey = ApiKeyBox.Password;
            SyncApiKeyHint();
        };
    }

    // The style shows its placeholder out of Tag, which is a DependencyProperty the
    // template can trigger on; Password is not, so Tag is cleared while it has text.
    string? _apiKeyHint;

    void SyncApiKeyHint() => ApiKeyBox.Tag = ApiKeyBox.Password.Length == 0 ? _apiKeyHint : null;

    // The window takes the tallest card's height, so switching tabs never moves the
    // chrome. Only one card may be visible while measuring: the cards share one grid
    // cell and stretch to it, so a sibling left shown would inflate the reading. The
    // ScrollViewer still absorbs display scaling that makes even the tallest card tall.
    void FitToTallestCard()
    {
        var cards = new[] { PanelOutput, PanelVisualizer, PanelModel };
        var states = new Visibility[cards.Length];
        for (int i = 0; i < cards.Length; i++)
        {
            states[i] = cards[i].Visibility;
            cards[i].Visibility = Visibility.Collapsed;
        }

        double tallest = 0;
        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].Visibility = Visibility.Visible;
            UpdateLayout();
            tallest = Math.Max(tallest, cards[i].ActualHeight);
            cards[i].Visibility = Visibility.Collapsed;
        }

        for (int i = 0; i < cards.Length; i++)
            cards[i].Visibility = states[i];

        if (tallest <= 0)
            return;

        Height = Math.Min(MaxHeight, Math.Ceiling(ActualHeight - PanelScroll.ViewportHeight + tallest));
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
        if (PanelModel is null || PanelOutput is null ||
            PanelVisualizer is null)
            return;

        var tab = (string)((RadioButton)sender).Tag;
        PanelModel.Visibility = TabVisibility(tab == "model");
        PanelOutput.Visibility = TabVisibility(tab == "output");
        PanelVisualizer.Visibility = TabVisibility(tab == "visualizer");
        _viewModel.SetPreviewTabVisible(tab == "visualizer");
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
        VisualTree.FindAncestor<TextBox>(source) != null ||
        VisualTree.FindAncestor<PasswordBox>(source) != null;

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
