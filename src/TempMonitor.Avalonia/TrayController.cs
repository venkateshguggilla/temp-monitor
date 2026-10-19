using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TempMonitor.App;

/// <summary>
/// Notification-area icon showing the live CPU temperature as its image, coloured by the
/// CPU card's thresholds. Click shows the window; the menu has Show and Exit.
/// Drawn with Avalonia so the same code works on Linux (AppIndicator) later.
/// </summary>
public sealed class TrayController : IDisposable
{
    private static readonly Color Tile = Color.Parse("#1A1F26");
    private static readonly Color TileBorder = Color.Parse("#3A4452");
    private static readonly Color Muted = Color.Parse("#8A94A3");
    private static readonly Color Cool = Color.Parse("#3DD68C");
    private static readonly Color Warm = Color.Parse("#F5B83D");
    private static readonly Color Hot = Color.Parse("#FF5C5C");
    private const int IconPixels = 32;

    private readonly TrayIcon _icon;
    private string _currentKey = "";

    public TrayController(Action show, Action exit)
    {
        var showItem = new NativeMenuItem("Show Temp Monitor");
        showItem.Click += (_, _) => show();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => exit();

        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetStream("app.ico")),
            ToolTipText = "Temp Monitor",
            Menu = new NativeMenu { Items = { showItem, new NativeMenuItemSeparator(), exitItem } },
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => show();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
    }

    private static Stream AssetStream(string name) =>
        Avalonia.Platform.AssetLoader.Open(new Uri($"avares://TempMonitor/Assets/{name}"));

    /// <summary>Redraws the icon (only when the number or colour changes) and updates the tooltip.</summary>
    public void Update(Reading? cpu, Reading? gpu, bool fahrenheit)
    {
        string text;
        Color color;
        if (cpu?.Temperature is { } c)
        {
            var shown = fahrenheit ? c * 9 / 5 + 32 : c;
            text = Math.Round(shown).ToString("0");
            color = cpu.Level(c) switch { TempLevel.Hot => Hot, TempLevel.Warm => Warm, _ => Cool };
        }
        else
        {
            text = "–";
            color = Muted;
        }

        var key = text + color;
        if (key != _currentKey)
        {
            _currentKey = key;
            _icon.Icon = new WindowIcon(Render(text, color));
        }

        var tip = "Temp Monitor\nCPU " +
                  (cpu?.Temperature is { } t ? Reading.FormatTemp(t, fahrenheit) : "temp N/A") +
                  (cpu?.Usage is { } u ? $" · {u:0}%" : "");
        if (gpu?.Temperature is { } g)
            tip += "\nGPU " + Reading.FormatTemp(g, fahrenheit) + (gpu.Usage is { } gu ? $" · {gu:0}%" : "");
        _icon.ToolTipText = tip.Length > 127 ? tip[..127] : tip; // Windows tooltip limit
    }

    /// <summary>The number on a dark rounded tile, so it reads on light and dark taskbars alike.</summary>
    private static Bitmap Render(string text, Color color)
    {
        var tile = new Border
        {
            Width = IconPixels,
            Height = IconPixels,
            CornerRadius = new CornerRadius(IconPixels / 5.0),
            Background = new SolidColorBrush(Tile),
            BorderBrush = new SolidColorBrush(TileBorder),
            BorderThickness = new Thickness(Math.Max(1, IconPixels / 16.0)),
            Child = new Viewbox
            {
                Margin = new Thickness(IconPixels * 0.08),
                Stretch = Stretch.Uniform,
                Child = new TextBlock
                {
                    Text = text,
                    FontWeight = FontWeight.Bold,
                    FontSize = 100,
                    Foreground = new SolidColorBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        var size = new Size(IconPixels, IconPixels);
        tile.Measure(size);
        tile.Arrange(new Rect(size));
        var bitmap = new RenderTargetBitmap(new PixelSize(IconPixels, IconPixels), new Vector(96, 96));
        bitmap.Render(tile);
        return bitmap;
    }

    public void Dispose()
    {
        _icon.IsVisible = false; // otherwise a ghost icon lingers until the mouse passes over it
        _icon.Dispose();
    }
}
