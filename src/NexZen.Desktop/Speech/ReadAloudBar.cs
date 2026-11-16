using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace NexZen.Desktop.Speech;

/// <summary>Small bar at the bottom of the page while NexZen reads aloud: pause, stop and the voice.</summary>
public sealed class ReadAloudBar : Border
{
    private readonly TextBlock _status;
    private readonly Button _pause;
    private readonly Button _download;
    private readonly ComboBox _voice;
    private bool _syncing;

    public ReadAloudBar()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 0, 24);
        Padding = new Thickness(14, 8);
        CornerRadius = new CornerRadius(22);
        Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x22, 0x26));
        BoxShadow = BoxShadows.Parse("0 4 18 0 #55000000");
        ZIndex = 900;
        IsVisible = false;

        _status = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 150,
            MaxWidth = 420,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _pause = MakeButton("⏸", "Pause / resume");
        _pause.Click += (_, _) => ReadAloud.TogglePause();
        var stop = MakeButton("⏹", "Stop");
        stop.Click += (_, _) => ReadAloud.Stop();
        _download = new Button
        {
            Content = "Download",
            Height = 30,
            Padding = new Thickness(14, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(15),
        };
        _download.Click += (_, _) => ReadAloud.Install();
        _voice = new ComboBox
        {
            MinWidth = 110,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_voice, "Voice");
        _voice.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _voice.SelectedItem is ComboBoxItem { Tag: string id })
                ReadAloud.SetVoice(id);
        };

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "🔊", FontSize = 15, VerticalAlignment = VerticalAlignment.Center },
                _status,
                _voice,
                _download,
                _pause,
                stop,
            },
        };

        ReadAloud.StateChanged += OnStateChanged;
        DetachedFromVisualTree += (_, _) => ReadAloud.StateChanged -= OnStateChanged;
    }

    private static Button MakeButton(string glyph, string tip)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 34,
            Height = 30,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(15),
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private void OnStateChanged() => Dispatcher.UIThread.Post(Refresh);

    public void Refresh()
    {
        var state = ReadAloud.State;
        IsVisible = state != ReadAloudState.Idle;
        _status.Text = ReadAloud.Status;
        _pause.Content = state == ReadAloudState.Paused ? "▶" : "⏸";
        _pause.IsEnabled = state is ReadAloudState.Speaking or ReadAloudState.Paused;
        var reading = state is ReadAloudState.Preparing or ReadAloudState.Speaking or ReadAloudState.Paused;
        _pause.IsVisible = reading;
        _voice.IsVisible = reading;
        _download.IsVisible = state == ReadAloudState.OfferDownload;
        _status.TextWrapping = reading ? TextWrapping.NoWrap : TextWrapping.Wrap;

        _syncing = true;
        try
        {
            var voices = ReadAloud.Voices;
            var current = ReadAloud.Voice?.Id;
            if (_voice.ItemCount != voices.Count)
            {
                _voice.Items.Clear();
                foreach (var v in voices)
                    _voice.Items.Add(new ComboBoxItem { Content = v.Name, Tag = v.Id });
            }
            _voice.SelectedItem = _voice.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == current);
        }
        finally
        {
            _syncing = false;
        }
    }
}
