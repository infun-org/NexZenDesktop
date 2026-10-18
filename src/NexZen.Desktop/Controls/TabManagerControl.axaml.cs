using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Data.Converters;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NexZen.Application.ViewModels;

namespace NexZen.Desktop.Controls;

public class TabBackgroundConverter : IMultiValueConverter
{
    public static readonly TabBackgroundConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values != null && values.Count >= 2)
        {
            var colorStr = values[0] as string;
            var imagePath = values[1] as string;

            if (!string.IsNullOrEmpty(imagePath))
            {
                try
                {
                    var bitmap = new Bitmap(imagePath);
                    return new ImageBrush(bitmap)
                    {
                        Stretch = Stretch.UniformToFill,
                        AlignmentX = AlignmentX.Center,
                        AlignmentY = AlignmentY.Center
                    };
                }
                catch
                {
                    // Fallback to color
                }
            }

            if (!string.IsNullOrEmpty(colorStr) && Color.TryParse(colorStr, out var color))
            {
                return new SolidColorBrush(color);
            }
        }

        return new SolidColorBrush(Color.Parse("#b9b9b9"));
    }
}

public partial class TabManagerControl : UserControl
{
    public event EventHandler<string>? CloseTabRequested;
    public event EventHandler<string>? SwitchTabRequested;

    public TabManagerControl()
    {
        InitializeComponent();
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tabId)
        {
            CloseTabRequested?.Invoke(this, tabId);
        }
    }

    private void SwitchTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tabId)
        {
            SwitchTabRequested?.Invoke(this, tabId);
        }
    }

    private void ColorPicker_ColorChanged(object? sender, Avalonia.Controls.ColorChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.TabBackgroundColor = e.NewColor.ToString();
        }
    }

    private async void UploadImage_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose wallpaper",
            AllowMultiple = false,
            FileTypeFilter = new[] { Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll }
        });

        if (files != null && files.Count > 0)
        {
            if (DataContext is MainViewModel vm)
            {
                // Uri to local path
                vm.TabBackgroundImagePath = files[0].Path.LocalPath;
            }
        }
    }

    private void RemoveImage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.TabBackgroundImagePath = null;
        }
    }
}
