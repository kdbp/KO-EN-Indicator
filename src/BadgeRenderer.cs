using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;

namespace KoEngIndicator;

/// <summary>
/// 커서 아래에 표시하는 흰색 'A' 배지를 지정한 글자 크기(px)로 그린다.
/// 오버레이와 설정 창 미리보기가 동일한 모양을 쓰도록 공용화했다.
/// </summary>
internal static class BadgeRenderer
{
    public const int MinSize = 10;
    public const int MaxSize = 24;
    public const int DefaultSize = 14;

    public static int Clamp(int fontPx) => Math.Max(MinSize, Math.Min(MaxSize, fontPx));

    /// <summary>글자 크기(px)에 맞춘 배지 비트맵(32bpp, 투명 배경)을 만든다.</summary>
    public static Bitmap Render(int fontPx)
    {
        fontPx = Clamp(fontPx);
        int w = fontPx + 8;
        int h = fontPx + 10;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        var rect = new Rectangle(0, 0, w - 1, h - 1);
        int radius = Math.Max(4, fontPx / 2);
        using (var path = RoundedRect(rect, radius))
        {
            using var bg = new SolidBrush(Color.FromArgb(220, 25, 25, 25));
            g.FillPath(bg, path);
            using var border = new Pen(Color.FromArgb(170, 255, 255, 255), 1f);
            g.DrawPath(border, path);
        }

        using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.White);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString("A", font, text, new RectangleF(0, -1, w, h), fmt);

        return bmp;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
