using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace NexZen.Desktop.Controls;

public partial class FindBar : UserControl
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush NoMatchBrush = new SolidColorBrush(Color.Parse("#C0392B"));

    public event EventHandler<string>? QueryChanged;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? CloseRequested;

    public FindBar()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnBarKeyDown, RoutingStrategies.Tunnel);
    }

    public string Query => QueryBox.Text ?? "";

    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    public void ShowMatch(int ordinal, int count)
    {
        if (string.IsNullOrEmpty(QueryBox.Text))
        {
            MatchLabel.Text = "";
            return;
        }

        if (count <= 0)
        {
            MatchLabel.Text = "0/0";
            MatchLabel.Foreground = NoMatchBrush;
            return;
        }

        MatchLabel.Text = ordinal <= 0 ? count.ToString() : $"{ordinal}/{count}";
        MatchLabel.Foreground = MatchBrush;
    }

    public void ClearMatch()
    {
        MatchLabel.Text = "";
    }

    private void QueryBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        QueryChanged?.Invoke(this, QueryBox.Text ?? "");
    }

    private void OnBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsFindChord(e))
        {
            e.Handled = true;
            FocusQuery();
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Up:
                e.Handled = true;
                PreviousRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Enter:
            case Key.Down:
                e.Handled = true;
                NextRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void UpButton_Click(object? sender, RoutedEventArgs e)
    {
        PreviousRequested?.Invoke(this, EventArgs.Empty);
        QueryBox.Focus();
    }

    private void DownButton_Click(object? sender, RoutedEventArgs e)
    {
        NextRequested?.Invoke(this, EventArgs.Empty);
        QueryBox.Focus();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsFindChord(KeyEventArgs e)
    {
        if (e.Key != Key.F)
            return false;
        var mods = e.KeyModifiers;
        return mods.HasFlag(KeyModifiers.Control)
               && !mods.HasFlag(KeyModifiers.Shift)
               && !mods.HasFlag(KeyModifiers.Alt)
               && !mods.HasFlag(KeyModifiers.Meta);
    }
}
