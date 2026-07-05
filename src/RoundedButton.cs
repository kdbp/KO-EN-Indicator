using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KoEngIndicator;

internal enum ButtonGlyph { None, Envelope, Copy }

/// <summary>
/// 안티에일리어스 둥근 버튼. 왼쪽에 흰색 벡터 아이콘(봉투/복사)을 그리고
/// 아이콘+텍스트를 가운데 정렬한다. 호버/클릭 시 색이 짙어진다.
/// </summary>
internal sealed class RoundedButton : Button
{
    private Color _base = Color.FromArgb(0x2F, 0x6F, 0xED);
    private bool _hover;
    private bool _down;

    private const int GlyphSize = 17;
    private const int GlyphGap = 9;

    public int Radius { get; set; } = 10;
    public ButtonGlyph Glyph { get; set; } = ButtonGlyph.None;

    public Color BaseColor
    {
        get => _base;
        set { _base = value; Invalidate(); }
    }

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        ForeColor = Color.White;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font("맑은 고딕", 10f, FontStyle.Bold);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color fill = _down ? Darken(_base, 0.82f) : _hover ? Darken(_base, 0.90f) : _base;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Rounded(rect, Radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        // 아이콘 + 텍스트를 하나의 그룹으로 가운데 정렬
        Size ts = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        int glyphW = Glyph == ButtonGlyph.None ? 0 : GlyphSize;
        int gap = Glyph == ButtonGlyph.None ? 0 : GlyphGap;
        int contentW = glyphW + gap + ts.Width;
        int startX = Math.Max(6, (Width - contentW) / 2);
        int midY = Height / 2;

        if (Glyph != ButtonGlyph.None)
            DrawGlyph(g, Glyph, new Rectangle(startX, midY - GlyphSize / 2, GlyphSize, GlyphSize), fill);

        int textX = startX + glyphW + gap;
        TextRenderer.DrawText(g, Text, Font, new Point(textX, midY - ts.Height / 2), ForeColor, TextFormatFlags.NoPadding);
    }

    private static void DrawGlyph(Graphics g, ButtonGlyph kind, Rectangle r, Color bg)
    {
        using var pen = new Pen(Color.White, 1.6f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        if (kind == ButtonGlyph.Envelope)
        {
            var body = new Rectangle(r.X, r.Y + 2, r.Width - 1, r.Height - 5);
            using (var p = Rounded(body, 2)) g.DrawPath(pen, p);
            g.DrawLines(pen, new[]
            {
                new Point(body.Left + 1, body.Top + 1),
                new Point(body.Left + body.Width / 2, body.Top + (int)(body.Height * 0.55f)),
                new Point(body.Right - 1, body.Top + 1),
            });
        }
        else if (kind == ButtonGlyph.Copy)
        {
            var back = new Rectangle(r.X + 4, r.Y, r.Width - 5, r.Height - 4);
            var front = new Rectangle(r.X, r.Y + 4, r.Width - 5, r.Height - 4);
            using (var pb = Rounded(back, 2)) g.DrawPath(pen, pb);
            using (var fillBrush = new SolidBrush(bg))
            using (var pf = Rounded(front, 2))
            {
                g.FillPath(fillBrush, pf); // 뒷장과 겹치는 부분을 가림
                g.DrawPath(pen, pf);
            }
        }
    }

    private static Color Darken(Color c, float f)
        => Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        if (d <= 0 || d > r.Width || d > r.Height) { path.AddRectangle(r); path.CloseFigure(); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
