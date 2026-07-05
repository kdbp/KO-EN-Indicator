using System.Drawing;
using System.Windows.Forms;
using static KoEngIndicator.Native;

namespace KoEngIndicator;

internal sealed class MainForm : Form
{
    private readonly Overlay _overlay = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private CaretService _caretService = null!;
    private bool _lastShown;
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private NotifyIcon _tray = null!;
    private Icon _appIcon = null!;
    private Icon _trayIcon = null!;
    private ToggleSwitch _switchEnabled = null!;
    private ToggleSwitch _switchAutoStart = null!;
    private Label _lblOperate = null!;
    private Label _lblAutoStart = null!;
    private Label _lblSize = null!;
    private TrackBar _sizeSlider = null!;
    private Panel _sizePreview = null!;
    private int _currentBadgeSize = BadgeRenderer.DefaultSize;
    private ToastForm? _toast;
    private EventWaitHandle? _quitEvent;
    private bool _reallyExit;

    // ----- 개발자 이메일 확장 패널 -----
    private const string DeveloperEmail = "moon.shell581@passinbox.com";
    private const int CollapsedHeight = 256;
    private const int ExpandedHeight = 412; // 접힘(256) + 패널(156)
    private RoundedButton _emailButton = null!;
    private Panel _emailPanel = null!;
    private RoundedButton _copyButton = null!;
    private readonly System.Windows.Forms.Timer _copyResetTimer = new() { Interval = 1300 };
    private bool _emailExpanded;

    public MainForm(bool startMinimized)
    {
        BuildUi();

        // 저장된 상태 복원
        _switchEnabled.Checked = AppSettings.GetEnabled();
        _switchAutoStart.Checked = AppSettings.IsAutoStartEnabled();

        _switchEnabled.CheckedChanged += (_, _) =>
        {
            AppSettings.SetEnabled(_switchEnabled.Checked);
            if (!_switchEnabled.Checked) _overlay.HideOverlay();
        };
        _switchAutoStart.CheckedChanged += (_, _) =>
        {
            try { AppSettings.SetAutoStart(_switchAutoStart.Checked); }
            catch (Exception ex) { MessageBox.Show(this, "자동 실행 설정을 변경하지 못했습니다.\n" + ex.Message, "오류"); }
        };

        // 저장된 배지 크기 복원 (슬라이더 값 설정 → ValueChanged로 미리보기·오버레이 반영)
        _sizeSlider.Value = AppSettings.GetBadgeSize();

        _caretService = new CaretService(() => _switchEnabled.Checked);
        _caretService.Start();

        _timer.Interval = 33; // 약 30fps로 오버레이 위치 갱신
        _timer.Tick += OnTick;
        _timer.Start();

        SetupQuitListener();

        if (startMinimized)
        {
            // 트레이로 조용히 시작
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Load += (_, _) => Hide();
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        AlignRows();
    }

    /// <summary>
    /// 각 행에서 오른쪽 컨트롤(토글/슬라이더/미리보기)의 세로 중심을
    /// 왼쪽 라벨 텍스트의 세로 중심에 정확히 맞춘다. (DPI/테마에 무관)
    /// </summary>
    private void AlignRows()
    {
        // 작동 · 자동 실행 토글: 라벨 중심에 맞춤
        CenterVertically(_switchEnabled, _lblOperate);
        CenterVertically(_switchAutoStart, _lblAutoStart);

        // 크기 행: 슬라이더는 실제 썸(thumb) 중심을, 미리보기는 자기 중심을 라벨 중심에 맞춤
        int sizeLabelCenter = _lblSize.Top + _lblSize.Height / 2;
        var thumb = new Native.RECT();
        Native.SendMessage(_sizeSlider.Handle, Native.TBM_GETTHUMBRECT, IntPtr.Zero, ref thumb);
        int thumbCenterInControl = (thumb.Top + thumb.Bottom) / 2;
        _sizeSlider.Top = thumbCenterInControl > 0
            ? sizeLabelCenter - thumbCenterInControl
            : sizeLabelCenter - _sizeSlider.Height / 2;
        _sizePreview.Top = sizeLabelCenter - _sizePreview.Height / 2;
    }

    private static void CenterVertically(Control ctrl, Control label)
        => ctrl.Top = (label.Top + label.Height / 2) - ctrl.Height / 2;

    /// <summary>
    /// 나중에 실행된 새 인스턴스가 보내는 종료 신호를 대기한다.
    /// 신호를 받으면(자동 교체) 스스로 정상 종료한다.
    /// </summary>
    private void SetupQuitListener()
    {
        try
        {
            _ = Handle; // 핸들을 미리 만들어 BeginInvoke가 가능하도록
            _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
            var t = new Thread(() =>
            {
                try
                {
                    _quitEvent.WaitOne();
                    if (!IsDisposed) BeginInvoke(new Action(ExitApp));
                }
                catch { /* 무시 */ }
            })
            { IsBackground = true, Name = "QuitListener" };
            t.Start();
        }
        catch { /* 이벤트 생성 실패 시 자동 교체만 비활성 */ }
    }

    private void BuildUi()
    {
        Text = "한/영 입력 표시기";
        Font = new Font("맑은 고딕", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, CollapsedHeight);
        BackColor = Color.White;

        // 임베드된 앱 아이콘을 폼(큰 크기)과 트레이(작은 크기)에 각각 로드한다.
        _appIcon = LoadAppIcon(Size.Empty);
        _trayIcon = LoadAppIcon(new Size(16, 16));
        Icon = _appIcon;

        // ----- 작동 on/off 행 -----
        _lblOperate = new Label
        {
            Text = "작동",
            Location = new Point(24, 28),
            AutoSize = true,
            Font = new Font("맑은 고딕", 11f, FontStyle.Bold),
        };
        _switchEnabled = new ToggleSwitch { Location = new Point(386, 25), Anchor = AnchorStyles.Top | AnchorStyles.Right };

        // ----- 배지 크기 행 (라벨 · 슬라이더 · 실시간 미리보기) : 세 요소의 세로 중심을 맞춤 -----
        _lblSize = new Label
        {
            Text = "크기",
            Location = new Point(24, 88),
            AutoSize = true,
            Font = new Font("맑은 고딕", 10.5f),
        };
        _sizeSlider = new TrackBar
        {
            Location = new Point(60, 80),
            Size = new Size(300, 34),
            Minimum = BadgeRenderer.MinSize,
            Maximum = BadgeRenderer.MaxSize,
            TickStyle = TickStyle.None,
            SmallChange = 1,
            LargeChange = 2,
            AutoSize = false,
            BackColor = Color.White,
        };
        _sizePreview = new Panel
        {
            Location = new Point(390, 76),
            Size = new Size(44, 44),
            BackColor = Color.White,
        };
        _sizePreview.Paint += (_, e) =>
        {
            using var bmp = BadgeRenderer.Render(_currentBadgeSize);
            e.Graphics.DrawImageUnscaled(bmp,
                (_sizePreview.Width - bmp.Width) / 2, (_sizePreview.Height - bmp.Height) / 2);
        };
        _sizeSlider.ValueChanged += (_, _) =>
        {
            _currentBadgeSize = _sizeSlider.Value;
            _sizePreview.Invalidate();
            _overlay.SetSize(_currentBadgeSize);
            _lastX = int.MinValue; _lastY = int.MinValue; // 표시 중이면 다음 틱에 새 크기로 다시 그리도록
        };
        // 드래그를 놓으면(또는 키 조작 후) 자동 저장
        _sizeSlider.MouseUp += (_, _) => AppSettings.SetBadgeSize(_sizeSlider.Value);
        _sizeSlider.KeyUp += (_, _) => AppSettings.SetBadgeSize(_sizeSlider.Value);

        // ----- 자동 실행 on/off 행 -----
        _lblAutoStart = new Label
        {
            Text = "윈도우 시작 시 자동 실행",
            Location = new Point(24, 148),
            AutoSize = true,
            Font = new Font("맑은 고딕", 10.5f),
        };
        _switchAutoStart = new ToggleSwitch { Location = new Point(386, 145), Anchor = AnchorStyles.Top | AnchorStyles.Right };

        var separator = new Label
        {
            BorderStyle = BorderStyle.Fixed3D,
            Location = new Point(20, 192),
            Size = new Size(420, 2),
        };

        Controls.AddRange([_lblOperate, _switchEnabled, _lblSize, _sizeSlider, _sizePreview,
            _lblAutoStart, _switchAutoStart, separator]);

        BuildEmailSection();

        // ----- 트레이 아이콘 -----
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("열기", null, (_, _) => RestoreFromTray());
        var exitItem = new ToolStripMenuItem("종료", null, (_, _) => ExitApp());
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "한/영 입력 표시기",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void BuildEmailSection()
    {
        _emailButton = new RoundedButton
        {
            Text = "개발자 이메일  ▾",
            Location = new Point(24, 206),
            Size = new Size(412, 36),
            Radius = 9,
            Glyph = ButtonGlyph.Envelope,
        };
        _emailButton.Click += (_, _) => ToggleEmailPanel();

        _emailPanel = new Panel
        {
            Location = new Point(0, CollapsedHeight),
            Size = new Size(ClientSize.Width, ExpandedHeight - CollapsedHeight),
            BackColor = Color.FromArgb(0xF5, 0xF7, 0xFB),
            Visible = false,
        };
        _emailPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(0xE0, 0xE4, 0xEA));
            e.Graphics.DrawLine(pen, 0, 0, _emailPanel.Width, 0); // 상단 구분선
        };

        var title = new Label
        {
            Text = "개발자 이메일",
            Location = new Point(24, 18),
            AutoSize = true,
            Font = new Font("맑은 고딕", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x22, 0x22, 0x22),
        };
        var email = new Label
        {
            Text = DeveloperEmail,
            Location = new Point(24, 52),
            AutoSize = true,
            Font = new Font("맑은 고딕", 11f),
            ForeColor = Color.FromArgb(0x2F, 0x6F, 0xED),
            Cursor = Cursors.Hand,
        };
        // 파란 이메일 주소 클릭 → 기본 메일 프로그램 실행. 마우스 오버 시 밑줄로 링크 표시.
        var emailNormal = new Font("맑은 고딕", 11f);
        var emailHover = new Font("맑은 고딕", 11f, FontStyle.Underline);
        email.MouseEnter += (_, _) => email.Font = emailHover;
        email.MouseLeave += (_, _) => email.Font = emailNormal;
        email.Click += (_, _) => OpenMailClient();

        _copyButton = new RoundedButton
        {
            Text = "이메일 주소 복사",
            Location = new Point(24, 100),
            Size = new Size(240, 36),
            Radius = 8,
            Glyph = ButtonGlyph.Copy,
            Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
        };
        _copyButton.Click += (_, _) => CopyEmail();

        _emailPanel.Controls.AddRange([title, email, _copyButton]);
        Controls.Add(_emailButton);
        Controls.Add(_emailPanel);

        _copyResetTimer.Tick += (_, _) =>
        {
            _copyResetTimer.Stop();
            _copyButton.Text = "이메일 주소 복사";
            _copyButton.BaseColor = Color.FromArgb(0x2F, 0x6F, 0xED);
        };
    }

    private void ToggleEmailPanel()
    {
        _emailExpanded = !_emailExpanded;
        _emailPanel.Visible = _emailExpanded;
        _emailButton.Text = _emailExpanded ? "개발자 이메일  ▴" : "개발자 이메일  ▾";
        ClientSize = new Size(ClientSize.Width, _emailExpanded ? ExpandedHeight : CollapsedHeight);
    }

    /// <summary>이메일 패널을 접힌 상태로 되돌린다(트레이로 숨기거나 최소화할 때).</summary>
    private void CollapseEmailPanel()
    {
        if (!_emailExpanded) return;
        _emailExpanded = false;
        _emailPanel.Visible = false;
        _emailButton.Text = "개발자 이메일  ▾";
        ClientSize = new Size(ClientSize.Width, CollapsedHeight);
    }

    private void CopyEmail()
    {
        try
        {
            Clipboard.SetText(DeveloperEmail);
            _copyButton.Text = "복사됨 ✓";
            _copyButton.BaseColor = Color.FromArgb(0x2E, 0xA0, 0x43); // 초록 피드백
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
        }
        catch { /* 클립보드 사용 불가 시 무시 */ }
    }

    /// <summary>기본 메일 프로그램으로 개발자 이메일 작성 창을 연다.</summary>
    private void OpenMailClient()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mailto:" + DeveloperEmail)
            {
                UseShellExecute = true,
            });
        }
        catch { /* 기본 메일 앱이 없거나 실행 실패 → 무시 */ }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        int x = 0, y = 0;
        bool show = _switchEnabled.Checked && _caretService.TryGet(out x, out y);

        if (!show)
        {
            if (_lastShown) { _overlay.HideOverlay(); _lastShown = false; }
            return;
        }

        // 위치가 바뀌었거나 새로 표시될 때만 갱신(불필요한 GDI 호출 방지)
        if (!_lastShown || x != _lastX || y != _lastY)
        {
            _overlay.ShowAt(x, y);
            _lastX = x; _lastY = y; _lastShown = true;
        }
    }

    // ----- 닫기/최소화 시 트레이로 -----
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
            HideToTray();
    }

    private void HideToTray()
    {
        // 숨기기 전에 창이 있던 화면 위치를 기억(토스트를 그 자리에 띄우기 위해)
        Rectangle anchor = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        CollapseEmailPanel();
        Hide();
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        ShowTrayToast(anchor);
    }

    /// <summary>트레이로 숨길 때 창이 있던 자리에 잠깐 뜨는 안내 토스트.</summary>
    private void ShowTrayToast(Rectangle anchor)
    {
        try
        {
            _toast?.Close();
            _toast = new ToastForm("작업표시줄 트레이 아이콘으로 최소화합니다", anchor);
            _toast.Popup();
        }
        catch { /* 무시 */ }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        _timer.Stop();
        _caretService.Dispose();
        _overlay.HideOverlay();
        _overlay.Dispose();
        _tray.Visible = false;
        _tray.Dispose();

        _appIcon?.Dispose();
        _trayIcon?.Dispose();

        Application.Exit();
    }

    /// <summary>
    /// 실행파일에 임베드된 appicon.ico를 불러온다.
    /// size가 비어 있으면 다중 해상도 아이콘(창/작업표시줄용),
    /// 지정되면 해당 크기(트레이용)로 로드한다.
    /// </summary>
    private static Icon LoadAppIcon(Size size)
    {
        var stream = typeof(MainForm).Assembly.GetManifestResourceStream("appicon.ico");
        if (stream is null) return (Icon)SystemIcons.Application.Clone();
        using (stream)
            return size.IsEmpty ? new Icon(stream) : new Icon(stream, size);
    }
}
