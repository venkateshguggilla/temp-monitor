// Renders the Temp Monitor logo and writes app.ico (16–256 px) plus logo.png.
// Usage: dotnet run -- <output dir>
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Program
{
    // Same palette as the app (App.xaml).
    static readonly Color TileTop = Color.FromRgb(0x23, 0x2B, 0x36);
    static readonly Color TileBottom = Color.FromRgb(0x11, 0x14, 0x18);
    static readonly Color TileBorder = Color.FromRgb(0x2E, 0x37, 0x44);
    static readonly Color Glass = Color.FromRgb(0xE8, 0xEC, 0xF1);
    static readonly Color Channel = Color.FromRgb(0x1A, 0x1F, 0x26);
    static readonly Color Cool = Color.FromRgb(0x3D, 0xD6, 0x8C);
    static readonly Color Warm = Color.FromRgb(0xF5, 0xB8, 0x3D);
    static readonly Color Hot = Color.FromRgb(0xFF, 0x5C, 0x5C);
    static readonly Color Usage = Color.FromRgb(0x4C, 0x9A, 0xFF);

    [STAThread]
    static void Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);

        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = sizes.Select(s => (s, Png(Render(s)))).ToList();
        WriteIco(Path.Combine(outDir, "app.ico"), pngs);
        File.WriteAllBytes(Path.Combine(outDir, "logo.png"), Png(Render(512)));
        Console.WriteLine($"Wrote app.ico ({string.Join(", ", sizes)}) and logo.png to {Path.GetFullPath(outDir)}");

        // Optional: a sheet of the small sizes blown up 6x (nearest neighbour) for checking legibility.
        if (args.Length > 2 && args[1] == "--preview")
            File.WriteAllBytes(args[2], Png(PreviewSheet(new[] { 16, 24, 32, 48 }, 6)));
    }

    static BitmapSource PreviewSheet(int[] sizes, int zoom)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        double x = 10, height = sizes.Max() * zoom + 20;
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, sizes.Sum(s => s * zoom + 10) + 10, height));
            foreach (var s in sizes)
            {
                dc.DrawImage(Render(s), new Rect(x, 10, s * zoom, s * zoom));
                x += s * zoom + 10;
            }
        }
        var bmp = new RenderTargetBitmap((int)x, (int)height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    /// <summary>The logo, drawn on a 256-unit canvas.</summary>
    static Drawing Logo(int pixelSize)
    {
        var g = new DrawingGroup();
        var small = pixelSize <= 24; // simplify for tiny icons

        // Tile
        var tileBrush = new LinearGradientBrush(TileTop, TileBottom, 90);
        var tile = new RectangleGeometry(new Rect(8, 8, 240, 240), 56, 56);
        g.Children.Add(new GeometryDrawing(tileBrush, new Pen(new SolidColorBrush(TileBorder), small ? 0 : 4), tile));

        // Thermometer glass: tube + bulb. Wider on small icons so it doesn't vanish.
        var cx = small ? 128.0 : 100.0;
        var tubeW = small ? 72.0 : 60.0;
        var bulbR = small ? 56.0 : 46.0;
        var bulbY = small ? 178.0 : 186.0;
        var glass = Union(
            new RectangleGeometry(new Rect(cx - tubeW / 2, small ? 28 : 34, tubeW, 150), tubeW / 2, tubeW / 2),
            new EllipseGeometry(new Point(cx, bulbY), bulbR, bulbR));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Glass), null, glass));

        // Inner channel
        var inset = small ? 12.0 : 12.0;
        var channelW = tubeW - inset * 2;
        var channel = Union(
            new RectangleGeometry(new Rect(cx - channelW / 2, (small ? 28 : 34) + inset, channelW, 150), channelW / 2, channelW / 2),
            new EllipseGeometry(new Point(cx, bulbY), bulbR - inset, bulbR - inset));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Channel), null, channel));

        // Mercury: green in the bulb, rising through amber to red.
        var gap = small ? 6.0 : 6.0;
        var mercuryW = channelW - gap * 2;
        var top = small ? 64.0 : 76.0;
        var mercury = Union(
            new RectangleGeometry(new Rect(cx - mercuryW / 2, top, mercuryW, bulbY - top), mercuryW / 2, mercuryW / 2),
            new EllipseGeometry(new Point(cx, bulbY), bulbR - inset - gap, bulbR - inset - gap));
        var mercuryBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Hot, 0.0),
                new GradientStop(Warm, 0.45),
                new GradientStop(Cool, 0.9),
            },
        };
        g.Children.Add(new GeometryDrawing(mercuryBrush, null, mercury));

        // Rising usage bars to the right (dropped on tiny icons, where they'd be mush).
        if (!small)
        {
            var barBrush = new SolidColorBrush(Usage);
            const double barW = 20, barGap = 9, baseline = 210;
            var x0 = cx + tubeW / 2 + 20;
            double[] heights = { 46, 82, 118 };
            for (var i = 0; i < heights.Length; i++)
            {
                var x = x0 + i * (barW + barGap);
                g.Children.Add(new GeometryDrawing(barBrush, null,
                    new RectangleGeometry(new Rect(x, baseline - heights[i], barW, heights[i]), 6, 6)));
            }
        }

        // Clip everything to the tile.
        g.ClipGeometry = tile;
        return g;
    }

    static Geometry Union(Geometry a, Geometry b) => new CombinedGeometry(GeometryCombineMode.Union, a, b);

    static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            dc.DrawDrawing(Logo(size));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    static byte[] Png(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>ICO container with PNG-compressed entries (supported since Windows Vista).</summary>
    static void WriteIco(string path, List<(int size, byte[] png)> images)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type: icon
        w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);            // palette colors
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images)
            w.Write(png);
    }
}
