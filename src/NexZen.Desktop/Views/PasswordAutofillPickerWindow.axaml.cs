using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NexZen.Application.ViewModels;

namespace NexZen.Desktop.Views;

public partial class PasswordAutofillPickerWindow : Window
{
    public PasswordAutofillPickerWindow()
    {
        InitializeComponent();
    }

    public PasswordAutofillPickerWindow(string host, IReadOnlyList<SavedPasswordAccount> accounts)
        : this()
    {
        this.FindControl<TextBlock>("SiteText")!.Text = $"Saved accounts for {host}";
        var panel = this.FindControl<StackPanel>("AccountsPanel")!;
        foreach (var account in accounts)
        {
            var button = new Button
            {
                Content = account.Username,
                Tag = account.Id,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Avalonia.Thickness(12, 9),
                MinHeight = 42,
                Background = Avalonia.Media.Brushes.White,
                BorderBrush = Avalonia.Media.Brushes.LightGray,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(5)
            };
            button.Click += Account_Click;
            panel.Children.Add(button);
        }

        Height = accounts.Count > 4 ? 360 : 220 + accounts.Count * 48;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public static Task<int> ShowAsync(Window owner, string host, IReadOnlyList<SavedPasswordAccount> accounts)
    {
        var window = new PasswordAutofillPickerWindow(host, accounts);
        return window.ShowDialog<int>(owner);
    }

    private void Account_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id })
            Close(id);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(0);
}
