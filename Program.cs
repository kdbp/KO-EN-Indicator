using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace KorEng;

internal static class Program
{
    internal static readonly uint ShowMessage = Native.RegisterWindowMessage("KorEng.ShowSettings.1");

    [STAThread]
    private static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--caret-worker") >= 0)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            int result = 1;
            var thread = new Thread(() => result = CaretWorker.Run());
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start(); thread.Join();
            return result;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 1 && args[0] == "--caret-fixture") return SelfTests.RunCaretFixture();
        if (args.Length == 2 && args[0] == "--diagnose" && int.TryParse(args[1], out int seconds))
            return CaretDiagnostics.Run(Math.Clamp(seconds, 5, 60));
        if (args.Length == 1 && args[0] == "--preview-fix") return CaretDiagnostics.Run(60, true);
        if (args.Length == 2 && args[0] == "--write-icon")
        {
            using var icon = AppIcon.Create();
            using var output = File.Create(args[1]);
            icon.Save(output); return 0;
        }
        if (args.Length == 2 && args[0] == "--measure-ui")
        {
            using var preview = new MainForm(true, true);
            preview.Show();
            Application.DoEvents();
            File.WriteAllText(args[1], Environment.TickCount64.ToString(System.Globalization.CultureInfo.InvariantCulture));
            preview.AllowExit = true; preview.Close();
            return 0;
        }
        if (Array.IndexOf(args, "--self-test") >= 0) return SelfTests.Run();

        using var singleInstance = new Mutex(true, @"Local\KorEng.Main.1", out bool created);
        if (!created)
        {
            if (Array.IndexOf(args, "--startup") < 0) Native.PostMessage(new nint(0xffff), ShowMessage, 0, 0);
            return 0;
        }
        try
        {
            using var app = new KorEngContext(Array.IndexOf(args, "--startup") >= 0);
            Application.Run(app);
            return 0;
        }
        finally { singleInstance.ReleaseMutex(); }
    }
}

internal sealed class KorEngContext : ApplicationContext
{
    private readonly Settings settings;
    private readonly MainForm window;
    private readonly OverlayForm overlay;
    private readonly InputMonitor monitor;
    private readonly Icon icon;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem enabledMenu;
    private readonly WindowMessages messages;
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 400 };
    private bool exiting;

    internal KorEngContext(bool startup)
    {
        settings = Settings.Load(Settings.FilePath);
        window = new MainForm(settings.Enabled, settings.StartWithWindows);
        settings.RestoreBounds(window);
        overlay = new OverlayForm();
        monitor = new InputMonitor(overlay);
        icon = AppIcon.Create();
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(24, 30, 43), ForeColor = Color.FromArgb(235, 240, 250),
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()), ShowImageMargin = false
        };
        menu.Items.Add("설정 열기", null, (_, _) => ShowSettings());
        enabledMenu = new ToolStripMenuItem("작동", null, (_, _) => ChangeEnabled(!settings.Enabled)) { Checked = settings.Enabled };
        menu.Items.Add(enabledMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApplication());
        tray = new NotifyIcon { Icon = icon, Text = "KorEng v1.0.1", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        messages = new WindowMessages(window, ShowSettings);
        window.EnabledChanged += ChangeEnabled;
        window.StartupChanged += ChangeStartup;
        window.LocationChanged += (_, _) => ScheduleSave();
        window.SizeChanged += (_, _) => ScheduleSave();
        window.FormClosing += (_, e) =>
        {
            Save();
            if (e.CloseReason != CloseReason.UserClosing) ExitApplication();
        };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        if (!startup) ShowSettings();
        // The first normal launch enables the requested current-user startup entry.
        // Startup launches re-register the stable executable path after an update.
        if (!StartupRegistration.Apply(settings.StartWithWindows, out string error))
        {
            settings.StartWithWindows = false;
            window.SetStartup(false);
            window.SetNotice(error);
        }
        Save();
        monitor.SetEnabled(settings.Enabled);
    }

    internal void ShowSettings()
    {
        if (exiting) return;
        window.ShowSettingsWindow();
    }
    private void ChangeEnabled(bool enabled)
    {
        settings.Enabled = enabled; window.SetEnabled(enabled);
        enabledMenu.Checked = enabled;
        tray.Text = enabled ? "KorEng v1.0.1 · 작동 중" : "KorEng v1.0.1 · 작동 중지";
        monitor.SetEnabled(enabled); Save();
    }
    private void ChangeStartup(bool enabled)
    {
        if (StartupRegistration.Apply(enabled, out string error))
        {
            settings.StartWithWindows = enabled;
            Save();
            window.SetNotice(enabled ? "윈도우 로그인 시 자동으로 시작합니다." : "윈도우 시작 시 자동 실행을 해제했습니다.");
        }
        else { window.SetStartup(settings.StartWithWindows); window.SetNotice(error); }
    }
    private void ScheduleSave()
    {
        if (exiting) return;
        saveTimer.Stop(); saveTimer.Start();
    }
    private void Save()
    {
        settings.CaptureBounds(window);
        if (!settings.Save(Settings.FilePath)) window.SetNotice("설정을 저장할 수 없습니다. 쓰기 가능한 폴더로 옮겨 주세요.");
    }
    internal void ExitApplication()
    {
        if (exiting) return;
        exiting = true; Save();
        saveTimer.Stop(); monitor.Dispose();
        tray.Visible = false;
        window.AllowExit = true;
        window.Close(); overlay.Close();
        ExitThread();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            saveTimer.Dispose(); messages.Dispose(); monitor.Dispose();
            tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose();
            overlay.Dispose(); window.Dispose(); icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class WindowMessages : NativeWindow, IDisposable
    {
        private readonly Action show;
        internal WindowMessages(Form form, Action show)
        {
            this.show = show;
            AssignHandle(form.Handle);
        }
        protected override void WndProc(ref Message m)
        {
            if ((uint)m.Msg == Program.ShowMessage) { show(); return; }
            base.WndProc(ref m);
        }
        public void Dispose() => ReleaseHandle();
    }
    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(45, 63, 95);
        public override Color MenuItemBorder => Color.FromArgb(78, 113, 171);
        public override Color ToolStripDropDownBackground => Color.FromArgb(24, 30, 43);
        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override Color MenuBorder => Color.FromArgb(55, 66, 87);
    }
}
