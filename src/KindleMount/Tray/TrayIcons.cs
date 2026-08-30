using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace KindleMount.Tray;

/// <summary>
/// 外部リソースを持たずに済むよう、トレイアイコンを実行時に描画する。
/// マウント中かどうかで色を変える。
/// </summary>
public static class TrayIcons
{
    private static Icon? _idle;
    private static Icon? _active;
    private static Icon? _busy;

    public static Icon Idle => _idle ??= Create(Color.FromArgb(120, 124, 130), Color.FromArgb(90, 94, 100));

    public static Icon Active => _active ??= Create(Color.FromArgb(56, 142, 60), Color.FromArgb(38, 106, 43));

    public static Icon Busy => _busy ??= Create(Color.FromArgb(230, 145, 30), Color.FromArgb(180, 110, 20));

    /// <summary>電子書籍リーダーを模した単純な図形を描く。</summary>
    private static Icon Create(Color body, Color edge)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var bodyBrush = new SolidBrush(body);
            using var edgePen = new Pen(edge, 2f);
            using var screenBrush = new SolidBrush(Color.FromArgb(245, 245, 240));

            var outer = new Rectangle(6, 2, 20, 28);
            using (var path = RoundedRect(outer, 4))
            {
                graphics.FillPath(bodyBrush, path);
                graphics.DrawPath(edgePen, path);
            }

            graphics.FillRectangle(screenBrush, new Rectangle(9, 6, 14, 17));

            using var linePen = new Pen(Color.FromArgb(150, 150, 150), 1f);
            for (var y = 9; y <= 19; y += 3)
            {
                graphics.DrawLine(linePen, 11, y, 21, y);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
