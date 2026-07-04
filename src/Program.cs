using System.Windows.Forms;

namespace KoEngIndicator;

internal static class Program
{
    // 중복 실행 방지용 뮤텍스
    private static Mutex? _mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        _mutex = new Mutex(true, "KoEngIndicator_SingleInstance_2A9F", out bool createdNew);
        if (!createdNew) return; // 이미 실행 중이면 종료

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool startMinimized = args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(startMinimized));

        GC.KeepAlive(_mutex);
    }
}
