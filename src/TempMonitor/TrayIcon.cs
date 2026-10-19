using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace TempMonitor;

/// <summary>
/// Notification-area icon that shows the live CPU temperature as its image, colored by the
/// same thresholds as the CPU card. Double-click shows the window; right-click for Show / Exit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // Same palette as the app (App.xaml).
    private static readonly Color Tile = Color.FromArgb(0x1A, 0x1F, 0x26);
    private static readonly Color TileBorder = Color.FromArgb(0x3A, 0x44, 0x52);
    private static readonly Color Muted = Color.FromArgb(0x8A, 0x94, 0xA3);
    public static readonly Color Cool = Color.FromArgb(0x3D, 0xD6, 0x8C);
    public static readonly Color Warm = Color.FromArgb(0xF5, 0xB8, 0x3D);
    public static readonly Color Hot = Color.FromArgb(0xFF, 0x5C, 0x5C);

    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _appIcon;
    private IntPtr _currentHandle;
    private string _currentKey = "";

    public TrayIcon(Icon appIcon, Action show, Action exit)
    {
        _appIcon = appIcon;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show Temp Monitor", null, (_, _) => show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        menu.Items[0].Font = new Font(menu.Items[0].Font, System.Drawing.FontStyle.Bold);

        _icon = new Forms.NotifyIcon
        {
            Icon = appIcon,
            Text = "Temp Monitor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    /// <summary>Redraws the icon (only when the number or color changes) and updates the tooltip.</summary>
    public void Update(Reading? cpu, Reading? gpu, bool fahrenheit)
    {
        string text;
        Color color;
        if (cpu?.Temperature is { } c)
        {
            var shown = fahrenheit ? c * 9 / 5 + 32 : c;
            text = Math.Round(shown).ToString("0");
            color = c >= cpu.HotAt ? Hot : c >= cpu.WarmAt ? Warm : Cool;
        }
        else
        {
            text = "–";
            color = Muted;
        }

        var key = text + color.ToArgb();
        if (key != _currentKey)
        {
            _currentKey = key;
            SetIcon(text, color);
        }

        var tip = "Temp Monitor\nCPU " +
                  (cpu?.Temperature is { } t ? Reading.FormatTemp(t, fahrenheit) : "temp N/A") +
                  (cpu?.Usage is { } u ? $" · {u:0}%" : "");
        if (gpu?.Temperature is { } g)
            tip += "\nGPU " + Reading.FormatTemp(g, fahrenheit) + (gpu.Usage is { } gu ? $" · {gu:0}%" : "");
        _icon.Text = tip.Length > 127 ? tip[..127] : tip; // NotifyIcon limit
    }

    private void SetIcon(string text, Color color)
    {
        // DPI-scaled tray icon size
        using var bmp = Render(text, color, Forms.SystemInformation.SmallIconSize.Width);
        var handle = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(handle);
        if (_currentHandle != IntPtr.Zero)
            DestroyIcon(_currentHandle); // Icon.FromHandle doesn't own the handle
        _currentHandle = handle;
    }

    /// <summary>Draws the number on a dark rounded tile.</summary>
    public static Bitmap Render(string text, Color color, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Dark tile so the number reads on light and dark taskbars alike.
            var radius = size / 5f;
            using (var tile = RoundedRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), radius))
            using (var fill = new SolidBrush(Tile))
            using (var border = new Pen(TileBorder, Math.Max(1, size / 16f)))
            {
                g.FillPath(fill, tile);
                g.DrawPath(border, tile);
            }

            // Draw the digits as outlines scaled to the exact ink bounds: GDI+ text layout pads
            // and wraps unpredictably at these sizes. Scale is set by digit height so "45",
            // "140" and "–" share one type size unless the text is too wide to fit.
            using var family = new FontFamily("Segoe UI");
            using var path = new GraphicsPath();
            path.AddString(text, family, (int)System.Drawing.FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
            using var digit = new GraphicsPath();
            digit.AddString("0", family, (int)System.Drawing.FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);

            var ink = path.GetBounds();
            var digitHeight = digit.GetBounds().Height;
            var scale = Math.Min(size * 0.62f / digitHeight, size * 0.84f / ink.Width);
            using var m = new Matrix();
            m.Translate(size / 2f, size / 2f);
            m.Scale(scale, scale);
            m.Translate(-(ink.Left + ink.Width / 2), -(ink.Top + ink.Height / 2));
            path.Transform(m);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _icon.Visible = false; // otherwise a ghost icon lingers until the mouse passes over it
        _icon.Icon = _appIcon;
        _icon.Dispose();
        if (_currentHandle != IntPtr.Zero)
            DestroyIcon(_currentHandle);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
