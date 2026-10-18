using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NexZen.Desktop.Helpers;

namespace NexZen.Desktop.Controls;

public partial class PageViewBar : UserControl
{
    public const double BarHeight = 64;

    public event EventHandler? ViewportChanged;
    public event EventHandler? CloseRequested;

    private readonly PageViewPreset[] _presets = PageViewPresets.All;
    private bool _loading = true;
    private string? _presetAgent;
    private int _committedWidth = 1280;
    private int _committedHeight = 800;

    public PageViewBar()
    {
        InitializeComponent();

        var presets = this.FindControl<ComboBox>("PresetBox");
        var scales = this.FindControl<ComboBox>("ScaleBox");
        if (presets != null)
        {
            foreach (var preset in _presets)
                presets.Items.Add(preset.Label);
            presets.SelectedIndex = 0;
            presets.SelectionChanged += OnPresetChanged;
        }

        if (scales != null)
        {
            scales.Items.Add("Fit");
            scales.Items.Add("50%");
            scales.Items.Add("75%");
            scales.Items.Add("100%");
            scales.Items.Add("125%");
            scales.Items.Add("150%");
            scales.Items.Add("200%");
            scales.SelectedIndex = 0;
            scales.SelectionChanged += OnScaleChanged;
        }

        WireSizeBox("WidthBox");
        WireSizeBox("HeightBox");

        var rotate = this.FindControl<Button>("RotateButton");
        if (rotate != null)
            rotate.Click += OnRotate;

        var mobile = this.FindControl<CheckBox>("MobileBox");
        if (mobile != null)
            mobile.IsCheckedChanged += OnMobileChanged;

        var reset = this.FindControl<Button>("ResetButton");
        if (reset != null)
            reset.Click += OnReset;

        var hide = this.FindControl<Button>("HideButton");
        if (hide != null)
            hide.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _loading = false;
    }

    internal void Load(PageViewport? viewport)
    {
        _loading = true;
        var presets = this.FindControl<ComboBox>("PresetBox");
        var scales = this.FindControl<ComboBox>("ScaleBox");
        var width = this.FindControl<TextBox>("WidthBox");
        var height = this.FindControl<TextBox>("HeightBox");
        var mobile = this.FindControl<CheckBox>("MobileBox");

        if (viewport == null)
        {
            if (presets != null)
                presets.SelectedIndex = IndexOf(PageViewPresets.DesktopId);
            _presetAgent = null;
            if (mobile != null)
                mobile.IsChecked = false;
            if (scales != null)
                scales.SelectedIndex = 0;
        }
        else
        {
            int index = IndexOf(viewport.PresetId);
            if (index < 0)
                index = IndexOf(PageViewPresets.ResponsiveId);
            if (presets != null)
                presets.SelectedIndex = index;
            _committedWidth = Clamp(viewport.Width);
            _committedHeight = Clamp(viewport.Height);
            if (width != null)
                width.Text = _committedWidth.ToString(CultureInfo.InvariantCulture);
            if (height != null)
                height.Text = _committedHeight.ToString(CultureInfo.InvariantCulture);
            if (mobile != null)
                mobile.IsChecked = viewport.Mobile;
            _presetAgent = viewport.UserAgent;
            if (scales != null)
                scales.SelectedIndex = ScaleIndex(viewport);
        }

        _loading = false;
    }

    public void ShowCurrentSize(int width, int height)
    {
        _loading = true;
        _committedWidth = Clamp(width);
        _committedHeight = Clamp(height);
        var widthBox = this.FindControl<TextBox>("WidthBox");
        var heightBox = this.FindControl<TextBox>("HeightBox");
        if (widthBox != null)
            widthBox.Text = _committedWidth.ToString(CultureInfo.InvariantCulture);
        if (heightBox != null)
            heightBox.Text = _committedHeight.ToString(CultureInfo.InvariantCulture);
        _loading = false;
    }

    public void SetShowing(int width, int height)
    {
        var label = this.FindControl<TextBlock>("ShowingLabel");
        if (label != null)
            label.Text = $"{width} × {height}";
    }

    public void SetShowingFull()
    {
        var label = this.FindControl<TextBlock>("ShowingLabel");
        if (label != null)
            label.Text = "Full window";
    }

    internal PageViewport? Read()
    {
        var preset = CurrentPreset();
        if (preset.Id == PageViewPresets.DesktopId)
            return null;

        bool mobile = this.FindControl<CheckBox>("MobileBox")?.IsChecked == true;
        string? agent = null;
        if (mobile)
            agent = string.IsNullOrEmpty(_presetAgent) ? PageViewPresets.AndroidUserAgent : _presetAgent;

        var (fit, scale) = ReadScale();
        return new PageViewport
        {
            PresetId = preset.Id,
            Width = _committedWidth,
            Height = _committedHeight,
            Mobile = mobile,
            UserAgent = agent,
            Fit = fit,
            Scale = scale
        };
    }

    private void WireSizeBox(string name)
    {
        var box = this.FindControl<TextBox>(name);
        if (box == null)
            return;

        box.LostFocus += (_, _) => CommitSize();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            CommitSize();
        };
    }

    private void CommitSize()
    {
        if (_loading)
            return;

        var widthBox = this.FindControl<TextBox>("WidthBox");
        var heightBox = this.FindControl<TextBox>("HeightBox");
        if (!TrySize(widthBox?.Text, out int width) || !TrySize(heightBox?.Text, out int height))
        {
            _loading = true;
            if (widthBox != null)
                widthBox.Text = _committedWidth.ToString(CultureInfo.InvariantCulture);
            if (heightBox != null)
                heightBox.Text = _committedHeight.ToString(CultureInfo.InvariantCulture);
            _loading = false;
            return;
        }

        if (width == _committedWidth && height == _committedHeight)
            return;

        _committedWidth = width;
        _committedHeight = height;
        SelectResponsive();
        Raise();
    }

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;

        var preset = CurrentPreset();
        if (preset.Id == PageViewPresets.DesktopId)
        {
            _presetAgent = null;
            _loading = true;
            var mobile = this.FindControl<CheckBox>("MobileBox");
            if (mobile != null)
                mobile.IsChecked = false;
            var scales = this.FindControl<ComboBox>("ScaleBox");
            if (scales != null)
                scales.SelectedIndex = 0;
            _loading = false;
            Raise();
            return;
        }

        if (preset.Width > 0 && preset.Height > 0)
        {
            _presetAgent = preset.UserAgent;
            _loading = true;
            _committedWidth = preset.Width;
            _committedHeight = preset.Height;
            var widthBox = this.FindControl<TextBox>("WidthBox");
            var heightBox = this.FindControl<TextBox>("HeightBox");
            if (widthBox != null)
                widthBox.Text = preset.Width.ToString(CultureInfo.InvariantCulture);
            if (heightBox != null)
                heightBox.Text = preset.Height.ToString(CultureInfo.InvariantCulture);
            var mobile = this.FindControl<CheckBox>("MobileBox");
            if (mobile != null)
                mobile.IsChecked = preset.Mobile;
            var scales = this.FindControl<ComboBox>("ScaleBox");
            if (scales != null)
                scales.SelectedIndex = 0;
            _loading = false;
        }

        Raise();
    }

    private void OnScaleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        if (CurrentPreset().Id == PageViewPresets.DesktopId)
            SelectResponsive();
        Raise();
    }

    private void OnMobileChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        if (this.FindControl<CheckBox>("MobileBox")?.IsChecked != true)
            _presetAgent = null;

        if (CurrentPreset().Id != PageViewPresets.ResponsiveId)
            SelectResponsive();
        Raise();
    }

    private void OnRotate(object? sender, RoutedEventArgs e)
    {
        if (_loading || CurrentPreset().Id == PageViewPresets.DesktopId)
            return;

        _loading = true;
        (_committedWidth, _committedHeight) = (_committedHeight, _committedWidth);
        var widthBox = this.FindControl<TextBox>("WidthBox");
        var heightBox = this.FindControl<TextBox>("HeightBox");
        if (widthBox != null)
            widthBox.Text = _committedWidth.ToString(CultureInfo.InvariantCulture);
        if (heightBox != null)
            heightBox.Text = _committedHeight.ToString(CultureInfo.InvariantCulture);
        _loading = false;
        Raise();
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        _loading = true;
        var presets = this.FindControl<ComboBox>("PresetBox");
        if (presets != null)
            presets.SelectedIndex = IndexOf(PageViewPresets.DesktopId);
        var mobile = this.FindControl<CheckBox>("MobileBox");
        if (mobile != null)
            mobile.IsChecked = false;
        var scales = this.FindControl<ComboBox>("ScaleBox");
        if (scales != null)
            scales.SelectedIndex = 0;
        _presetAgent = null;
        _loading = false;
        Raise();
    }

    private void SelectResponsive()
    {
        int index = IndexOf(PageViewPresets.ResponsiveId);
        var presets = this.FindControl<ComboBox>("PresetBox");
        if (presets == null || presets.SelectedIndex == index)
            return;

        _loading = true;
        presets.SelectedIndex = index;
        _loading = false;
    }

    private void Raise()
    {
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private PageViewPreset CurrentPreset()
    {
        int index = this.FindControl<ComboBox>("PresetBox")?.SelectedIndex ?? 0;
        if (index < 0 || index >= _presets.Length)
            index = 0;
        return _presets[index];
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < _presets.Length; i++)
        {
            if (_presets[i].Id == id)
                return i;
        }

        return -1;
    }

    private (bool Fit, double Scale) ReadScale()
    {
        return this.FindControl<ComboBox>("ScaleBox")?.SelectedIndex switch
        {
            1 => (false, 0.5),
            2 => (false, 0.75),
            3 => (false, 1),
            4 => (false, 1.25),
            5 => (false, 1.5),
            6 => (false, 2),
            _ => (true, 1)
        };
    }

    private static int ScaleIndex(PageViewport viewport)
    {
        if (viewport.Fit)
            return 0;

        double scale = viewport.Scale;
        if (scale < 0.63)
            return 1;
        if (scale < 0.88)
            return 2;
        if (scale < 1.13)
            return 3;
        if (scale < 1.38)
            return 4;
        if (scale < 1.75)
            return 5;
        return 6;
    }

    private static bool TrySize(string? text, out int value)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= 200
            && parsed <= 3840)
        {
            value = parsed;
            return true;
        }

        value = 0;
        return false;
    }

    private static int Clamp(int value)
    {
        return Math.Clamp(value, 200, 3840);
    }
}
