using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TempMonitor.App;

/// <summary>A simple OK message box (Avalonia has none built in).</summary>
public static class MessageDialog
{
    public static Task Show(Window owner, string title, string message)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, Classes = { "toolbar" }, IsDefault = true };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Res("WindowBg"),
            Icon = owner.Icon,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextPrimary") },
                    ok,
                },
            },
        };
        ok.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(owner);
    }

    internal static IBrush Res(string key) => (IBrush)Application.Current!.FindResource(key)!;
}

/// <summary>Read-only monospace text, for the "All sensors" snapshot.</summary>
public sealed class TextWindow : Window
{
    public TextWindow(string title, string text)
    {
        Title = title;
        Width = 760;
        Height = 700;
        Background = MessageDialog.Res("WindowBg");
        Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 12,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Background = MessageDialog.Res("WindowBg"),
            Foreground = MessageDialog.Res("TextPrimary"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10),
        };
    }
}
