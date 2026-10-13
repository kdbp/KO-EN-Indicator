using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KorEng;

public sealed class ToggleSwitch : Control
{
    private bool _checked;
    private bool _hovered;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Size = new Size(48, 28);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    public event EventHandler? CheckedChanged;

    [DefaultValue(false)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = Math.Max(2f, DeviceDpi / 48f);
        var track = new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
        if (track.Width <= 0 || track.Height <= 0) return;

        using var path = RoundedRectangle(track, track.Height / 2f);
        var fillColor = !Enabled ? Color.FromArgb(47, 52, 64)
            : Checked ? (_hovered ? Color.FromArgb(109, 151, 255) : Color.FromArgb(86, 132, 244))
            : (_hovered ? Color.FromArgb(80, 90, 111) : Color.FromArgb(60, 69, 86));
        using var fill = new SolidBrush(fillColor);
        graphics.FillPath(fill, path);

        var thumbInset = Math.Max(3f, DeviceDpi / 32f);
        var diameter = track.Height - thumbInset * 2;
        var x = Checked ? track.Right - thumbInset - diameter : track.Left + thumbInset;
        using var thumb = new SolidBrush(Enabled ? Color.FromArgb(248, 250, 255) : Color.FromArgb(147, 153, 164));
        graphics.FillEllipse(thumb, x, track.Top + thumbInset, diameter, diameter);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(175, 202, 255), Math.Max(1f, DeviceDpi / 96f));
            graphics.DrawPath(focus, path);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        if (Enabled) Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Focus();
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            Checked = !Checked;
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessibleObject(this);

    internal static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class SwitchAccessibleObject(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.CheckButton;
        public override AccessibleStates State => base.State | AccessibleStates.Focusable |
            (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);
        public override string DefaultAction => "전환";
        public override void DoDefaultAction() { if (owner.Enabled) owner.Checked = !owner.Checked; }
    }
}
