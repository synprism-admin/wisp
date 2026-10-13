using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Wisp.Native;

namespace Wisp.Core;

public static class ScreenCapture
{
    /// <summary>Captures a rectangle in physical screen pixels as PNG, scaled down to fit maxEdge.</summary>
    public static byte[] Capture(Rectangle rect, int maxEdge = 1568)
    {
        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        return ToPng(bmp, maxEdge);
    }

    /// <summary>Captures the monitor the mouse is on.</summary>
    public static byte[] CaptureCursorScreen(int maxEdge = 1568) => Capture(CursorScreenBounds(), maxEdge);

    public static Rectangle CursorScreenBounds()
    {
        Win32.GetCursorPos(out var p);
        return System.Windows.Forms.Screen.FromPoint(new Point(p.X, p.Y)).Bounds;
    }

    public static byte[] ToPng(Bitmap bmp, int maxEdge)
    {
        Bitmap? scaled = null;
        try
        {
            var src = bmp;
            int edge = Math.Max(bmp.Width, bmp.Height);
            if (edge > maxEdge)
            {
                double f = (double)maxEdge / edge;
                scaled = new Bitmap(Math.Max(1, (int)(bmp.Width * f)), Math.Max(1, (int)(bmp.Height * f)));
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, scaled.Width, scaled.Height);
                src = scaled;
            }
            using var ms = new MemoryStream();
            src.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally { scaled?.Dispose(); }
    }

    /// <summary>Grabs every monitor at once, so the overlay's tint never ends up in the picture.</summary>
    public static (Bitmap Image, Point Origin) CaptureVirtualScreen()
    {
        var v = System.Windows.Forms.SystemInformation.VirtualScreen;
        var bmp = new Bitmap(v.Width, v.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(v.Left, v.Top, 0, 0, v.Size, CopyPixelOperation.SourceCopy);
        return (bmp, v.Location);
    }

    /// <summary>
    /// Crops a frozen virtual-screen shot to the lasso's bounds (plus padding). Pixels outside the drawn
    /// path are dimmed so the model sees the surrounding context but knows where the focus is.
    /// With no lasso, returns the monitor the cursor is on.
    /// </summary>
    public static byte[] CropLasso(Bitmap shot, Point origin, Rectangle? bounds, IReadOnlyList<Point> path, int padding = 24, int maxEdge = 1568)
    {
        var r = bounds is { } b ? Rectangle.Inflate(b, padding, padding) : CursorScreenBounds();
        r.Intersect(new Rectangle(origin, shot.Size));
        if (r.Width < 8 || r.Height < 8) r = CursorScreenBounds();

        using var crop = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
        {
            g.DrawImage(shot, new Rectangle(0, 0, r.Width, r.Height),
                new Rectangle(r.Left - origin.X, r.Top - origin.Y, r.Width, r.Height), GraphicsUnit.Pixel);
            if (bounds is not null && path.Count > 2)
            {
                using var gp = new GraphicsPath();
                gp.AddPolygon(path.Select(p => new Point(p.X - r.Left, p.Y - r.Top)).ToArray());
                using var outside = new Region(new Rectangle(0, 0, r.Width, r.Height));
                outside.Exclude(gp);
                using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
                g.FillRegion(dim, outside);
            }
        }
        return ToPng(crop, maxEdge);
    }
}
