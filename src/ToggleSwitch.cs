using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KoEngIndicator;

/// <summary>
/// iOS 스타일의 on/off 토글 스위치. CheckBox를 상속하여 CheckedChanged 등을 그대로 사용한다.
/// </summary>
internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.CheckedBackColor = Color.Transparent;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        Size = new Size(50, 26);
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnPaint(PaintEventArgs pe)
    {
        Graphics g = pe.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);

        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        Color trackColor = Checked
            ? Color.FromArgb(0x34, 0xA8, 0x53)   // 켜짐: 초록
            : Color.FromArgb(0xC2, 0xC2, 0xC2);  // 꺼짐: 회색

        using (var path = RoundedRect(track, Height / 2))
        using (var brush = new SolidBrush(trackColor))
            g.FillPath(brush, path);

        int knob = Height - 8;
        int y = 4;
        int x = Checked ? Width - knob - 4 : 4;
        using (var knobBrush = new SolidBrush(Color.White))
            g.FillEllipse(knobBrush, x, y, knob, knob);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 90, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 0, 90);
        path.CloseFigure();
        return path;
    }
}
