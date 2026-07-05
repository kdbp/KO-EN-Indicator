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
    private Label _statusLabel = null!;
    private EventWaitHandle? _quitEvent;
    private bool _reallyExit;

    public MainForm(bool startMinimized)
    {
        BuildUi();

        // 저장된 상태 복원
        _switchEnabled.Checked = AppSettings.GetEnabled();
        _switchAutoStart.Checked = AppSettings.IsAutoStartEnabled();

        _switchEnabled.CheckedChanged += (_, _) =>
        {
            AppSettings.SetEnabled(_switchEnabled.Checked);
            UpdateStatusLabel();
            if (!_switchEnabled.Checked) _overlay.HideOverlay();
        };
        _switchAutoStart.CheckedChanged += (_, _) =>
        {
            try { AppSettings.SetAutoStart(_switchAutoStart.Checked); }
            catch (Exception ex) { MessageBox.Show(this, "자동 실행 설정을 변경하지 못했습니다.\n" + ex.Message, "오류"); }
        };

        _caretService = new CaretService(() => _switchEnabled.Checked);
        _caretService.Start();

        _timer.Interval = 33; // 약 30fps로 오버레이 위치 갱신
        _timer.Tick += OnTick;
        _timer.Start();

        UpdateStatusLabel();
        SetupQuitListener();

        if (startMinimized)
        {
            // 트레이로 조용히 시작
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Load += (_, _) => Hide();
        }
    }

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
        ClientSize = new Size(460, 184);
        BackColor = Color.White;

        // 임베드된 앱 아이콘을 폼(큰 크기)과 트레이(작은 크기)에 각각 로드한다.
        _appIcon = LoadAppIcon(Size.Empty);
        _trayIcon = LoadAppIcon(new Size(16, 16));
        Icon = _appIcon;

        // ----- 작동 on/off 행 -----
        var lbl1 = new Label
        {
            Text = "작동",
            Location = new Point(24, 30),
            AutoSize = true,
            Font = new Font("맑은 고딕", 11f, FontStyle.Bold),
        };
        _switchEnabled = new ToggleSwitch { Location = new Point(386, 27), Anchor = AnchorStyles.Top | AnchorStyles.Right };

        // ----- 자동 실행 on/off 행 -----
        var lbl2 = new Label
        {
            Text = "윈도우 시작 시 자동 실행",
            Location = new Point(24, 81),
            AutoSize = true,
            Font = new Font("맑은 고딕", 10.5f),
        };
        _switchAutoStart = new ToggleSwitch { Location = new Point(386, 77), Anchor = AnchorStyles.Top | AnchorStyles.Right };

        var separator = new Label
        {
            BorderStyle = BorderStyle.Fixed3D,
            Location = new Point(20, 124),
            Size = new Size(420, 2),
        };

        _statusLabel = new Label
        {
            Location = new Point(24, 140),
            AutoSize = true,
            ForeColor = Color.DimGray,
            Font = new Font("맑은 고딕", 9f),
        };

        Controls.AddRange([lbl1, _switchEnabled, lbl2, _switchAutoStart, separator, _statusLabel]);

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

    private void UpdateStatusLabel()
    {
        _statusLabel.Text = _switchEnabled.Checked
            ? "동작 중 · 영어 입력일 때 커서 아래 'A' 표시"
            : "꺼짐 · 표시하지 않음";
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
        Hide();
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
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
