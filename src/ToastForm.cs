using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KoEngIndicator;

/// <summary>
/// 화면 하단 가운데에 잠깐 떠서 부드럽게 나타났다 사라지는 알림(토스트).
/// 포커스를 빼앗지 않으며 클릭을 받지 않는다.
/// </summary>
internal sealed class ToastForm : Form
{
    private const int FadeIn = 260;
    private const int Hold = 2000;
    private const int FadeOut = 480;
    private const int Total = FadeIn + Hold + FadeOut;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private int _elapsed;
    private readonly string _text;

    public ToastForm(string text, Rectangle anchor)
    {
        _text = text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0x25, 0x25, 0x27);
        Opacity = 0;
        Font = new Font("맑은 고딕", 10f);
        DoubleBuffered = true;

        int w = TextRenderer.MeasureText(text, Font).Width + 52;
        Size = new Size(w, 46);

        int x, y;
        if (anchor.Width > 0 && anchor.Height > 0)
        {
            // 창이 있던 자리(중앙)에 표시
            x = anchor.Left + (anchor.Width - w) / 2;
            y = anchor.Top + (anchor.Height - Height) / 2;
        }
        else
        {
            var pwa = Screen.PrimaryScreen!.WorkingArea;
            x = pwa.Left + (pwa.Width - w) / 2;
            y = pwa.Bottom - Height - 90;
        }

        // 화면 밖으로 나가지 않도록 보정
        var wa = Screen.FromPoint(new Point(x + w / 2, y + Height / 2)).WorkingArea;
        x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - w - 4));
        y = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - Height - 4));
        Location = new Point(x, y);

        _timer.Tick += OnTick;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 배경(알약)은 Region + BackColor로 채워지므로, 여기서는 테두리와 텍스트만 그린다.
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = PillPath(rect))
        using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255), 1.2f))
            g.DrawPath(pen, path);

        TextRenderer.DrawText(g, _text, Font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_TRANSPARENT = 0x00000020;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        using var path = PillPath(new Rectangle(0, 0, Width, Height));
        Region = new Region(path);
    }

    private static GraphicsPath PillPath(Rectangle r)
    {
        int d = r.Height; // 완전한 둥근 양끝(pill)
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    /// <summary>토스트를 표시하고 애니메이션을 시작한다.</summary>
    public void Popup()
    {
        Show();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _elapsed += _timer.Interval;

        double opacity;
        if (_elapsed < FadeIn)
            opacity = (double)_elapsed / FadeIn;
        else if (_elapsed < FadeIn + Hold)
            opacity = 1.0;
        else if (_elapsed < Total)
            opacity = 1.0 - (double)(_elapsed - FadeIn - Hold) / FadeOut;
        else
        {
            _timer.Stop();
            Close();
            return;
        }

        Opacity = Math.Max(0, Math.Min(1, opacity)) * 0.96;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
