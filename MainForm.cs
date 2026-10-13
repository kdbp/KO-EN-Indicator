using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KorEng;

public sealed class MainForm : Form
{
    private readonly ToggleSwitch _enabledSwitch;
    private readonly ToggleSwitch _startupSwitch;
    private readonly Label _notice;
    private readonly Icon _ownedIcon;
    private bool _updating;
    private bool _restoring;

    public MainForm(bool enabled, bool startup)
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("맑은 고딕", 9f);
        BackColor = Color.FromArgb(14, 17, 25);
        ForeColor = Color.FromArgb(235, 240, 250);
        Text = "KorEng v1.0.1";
        _ownedIcon = AppIcon.Create();
        Icon = _ownedIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(410, 310);
        MinimumSize = new Size(426, 349);
        MaximizeBox = false;
        Padding = new Padding(22, 19, 22, 16);
        DoubleBuffered = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 73));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = "입력 상태 표시", Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
            Dock = DockStyle.Fill, Margin = Padding.Empty, AutoEllipsis = true
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "영문 입력 중에는 커서 아래에 A를 표시합니다.",
            ForeColor = Color.FromArgb(159, 170, 192), Dock = DockStyle.Fill,
            Margin = Padding.Empty, AutoEllipsis = true
        }, 0, 1);

        _enabledSwitch = new ToggleSwitch { Checked = enabled, AccessibleName = "작동", TabIndex = 0 };
        _startupSwitch = new ToggleSwitch { Checked = startup, AccessibleName = "윈도우 시작 시 자동 실행", TabIndex = 1 };
        layout.Controls.Add(CreateCard("작동", "영문 상태 표시를 켜거나 끕니다.", _enabledSwitch, 0), 0, 2);
        layout.Controls.Add(CreateCard("윈도우 시작 시 자동 실행", "로그인하면 트레이에서 시작합니다.", _startupSwitch, 11), 0, 3);

        _notice = new Label
        {
            Dock = DockStyle.Fill, Margin = new Padding(1, 5, 0, 0),
            ForeColor = Color.FromArgb(164, 189, 239), AutoEllipsis = true,
            Text = enabled ? "영문 입력 상태를 확인하고 있습니다." : "작동이 중지되었습니다."
        };
        layout.Controls.Add(_notice, 0, 4);
        layout.Controls.Add(new Label
        {
            Text = "최소화하거나 닫으면 트레이에 유지됩니다.", Dock = DockStyle.Fill,
            Margin = new Padding(1, 3, 0, 0), ForeColor = Color.FromArgb(118, 131, 155),
            Font = new Font(Font.FontFamily, 8.25f), AutoEllipsis = true
        }, 0, 5);
        Controls.Add(layout);

        _enabledSwitch.CheckedChanged += (_, _) =>
        {
            if (_updating) return;
            SetNotice(_enabledSwitch.Checked ? "영문 입력 상태를 확인하고 있습니다." : "작동이 중지되었습니다.");
            EnabledChanged?.Invoke(_enabledSwitch.Checked);
        };
        _startupSwitch.CheckedChanged += (_, _) =>
        {
            if (!_updating) StartupChanged?.Invoke(_startupSwitch.Checked);
        };
        ResumeLayout(true);
    }

    public new event Action<bool>? EnabledChanged;
    public event Action<bool>? StartupChanged;
    public bool AllowExit;

    public void SetStartup(bool value)
    {
        _updating = true;
        try { _startupSwitch.Checked = value; }
        finally { _updating = false; }
    }

    public void SetEnabled(bool value)
    {
        _updating = true;
        try { _enabledSwitch.Checked = value; }
        finally { _updating = false; }
        SetNotice(value ? "영문 입력 상태를 확인하고 있습니다." : "작동이 중지되었습니다.");
    }

    public void SetNotice(string message) => _notice.Text = message;

    public void ShowSettingsWindow()
    {
        if (IsDisposed) return;
        _restoring = true;
        try
        {
            // Restore after showing: WinForms may restore its cached minimized state in Show().
            Show();
            WindowState = FormWindowState.Normal;
            if (!Visible) Show();
            BringToFront();
            Activate();
        }
        finally { _restoring = false; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var enabled = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !AllowExit)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && !AllowExit && !_restoring) Hide();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _ownedIcon?.Dispose();
    }

    private static Control CreateCard(string title, string description, ToggleSwitch toggle, int topMargin)
    {
        var card = new RoundedCard(title, description, toggle)
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, topMargin, 0, 0),
            Padding = new Padding(15, 10, 13, 9), TabStop = false
        };
        return card;
    }

    private sealed class RoundedCard : Panel
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly ToggleSwitch _toggle;

        public RoundedCard(string title, string description, ToggleSwitch toggle)
        {
            SuspendLayout();
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            _title = new Label
            {
                Text = title, Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
                Margin = Padding.Empty, AutoEllipsis = true, BackColor = Color.Transparent
            };
            _description = new Label
            {
                Text = description, ForeColor = Color.FromArgb(150, 163, 185),
                Font = new Font("맑은 고딕", 8.25f), Margin = Padding.Empty,
                AutoEllipsis = true, BackColor = Color.Transparent
            };
            _toggle = toggle;
            _toggle.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _toggle.Margin = Padding.Empty;
            Controls.AddRange([_title, _description, _toggle]);
            ResumeLayout(true);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_title is null || _description is null || _toggle is null) return;
            var scale = DeviceDpi / 96f;
            var toggleWidth = (int)Math.Round(48 * scale);
            var toggleHeight = (int)Math.Round(28 * scale);
            var gap = (int)Math.Round(10 * scale);
            var titleHeight = (int)Math.Round(24 * scale);
            var textWidth = Math.Max(0, ClientSize.Width - Padding.Horizontal - toggleWidth - gap);
            _title.SetBounds(Padding.Left, Padding.Top, textWidth, titleHeight);
            _description.SetBounds(Padding.Left, Padding.Top + titleHeight, textWidth,
                Math.Max(0, ClientSize.Height - Padding.Vertical - titleHeight));
            _toggle.SetBounds(ClientSize.Width - Padding.Right - toggleWidth,
                Math.Max(0, (ClientSize.Height - toggleHeight) / 2), toggleWidth, toggleHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = ToggleSwitch.RoundedRectangle(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 11f * DeviceDpi / 96f);
            using var fill = new SolidBrush(Color.FromArgb(24, 30, 43));
            using var outline = new Pen(Color.FromArgb(42, 51, 69));
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(outline, path);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
