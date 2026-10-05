using System.IO;
using System.Reflection;

namespace DraftKeeper;

// The icon, from the copy baked into the exe. Missing? Then a drawn stand-in. An icon
// is no reason not to start.
internal static class Brand
{
    private static Image? _mark;
    private static Icon? _icon;

    public static Image? Mark => _mark ??= LoadImage("Resources.draft-keeper.png");

    public static Icon Icon => _icon ??= LoadIcon() ?? Fallback();

    private static Stream? Open(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var full = assembly.GetManifestResourceNames()
                           .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        return full is null ? null : assembly.GetManifestResourceStream(full);
    }

    private static Image? LoadImage(string name)
    {
        try
        {
            using var stream = Open(name);
            if (stream is null) return null;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            return Image.FromStream(copy);
        }
        catch { return null; }
    }

    private static Icon? LoadIcon()
    {
        try
        {
            using var stream = Open("Resources.draft-keeper.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch { return null; }
    }

    // The stand-in: a folded page with a mark on it.
    private static Icon Fallback()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var page = new System.Drawing.Drawing2D.GraphicsPath();
            page.AddLines(new[]
            {
                new PointF(5, 2), new PointF(20, 2), new PointF(27, 9),
                new PointF(27, 30), new PointF(5, 30)
            });
            page.CloseFigure();

            using var paper = new SolidBrush(Color.FromArgb(238, 238, 242));
            g.FillPath(paper, page);
            using var fold = new SolidBrush(Color.FromArgb(168, 168, 178));
            g.FillPolygon(fold, new[] { new PointF(20, 2), new PointF(27, 9), new PointF(20, 9) });
            using var dot = new SolidBrush(Color.FromArgb(217, 119, 87));
            g.FillEllipse(dot, 18, 19, 12, 12);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
