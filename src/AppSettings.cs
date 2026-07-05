using Microsoft.Win32;

namespace KoEngIndicator;

/// <summary>
/// 자동 실행(레지스트리 Run 키)과 앱 설정(작동 on/off) 저장/조회.
/// </summary>
internal static class AppSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "한영표시기";

    private const string SettingsKey = @"Software\한영표시기";
    private const string EnabledValue = "Enabled";
    private const string BadgeSizeValue = "BadgeSize";

    public static string ExePath =>
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;

    // ---- 작동 on/off (마지막 상태 기억) ----
    public static bool GetEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            object? v = key?.GetValue(EnabledValue);
            return v is null || Convert.ToInt32(v) != 0; // 기본값: 켜짐
        }
        catch
        {
            return true; // 레지스트리 읽기 실패 시 안전하게 '켜짐' 기본값
        }
    }

    public static void SetEnabled(bool on)
    {
        // 상태 저장은 부가 기능이므로 실패해도 앱이 죽지 않도록 조용히 무시한다.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue(EnabledValue, on ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // 보안 정책/백신 차단 등으로 쓰기 실패 → 무시
        }
    }

    // ---- 배지 크기(글자 px) ----
    public static int GetBadgeSize()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            object? v = key?.GetValue(BadgeSizeValue);
            return v is null ? BadgeRenderer.DefaultSize : BadgeRenderer.Clamp(Convert.ToInt32(v));
        }
        catch
        {
            return BadgeRenderer.DefaultSize;
        }
    }

    public static void SetBadgeSize(int fontPx)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue(BadgeSizeValue, BadgeRenderer.Clamp(fontPx), RegistryValueKind.DWord);
        }
        catch
        {
            // 무시
        }
    }

    // ---- 윈도우 시작 시 자동 실행 ----
    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValueName) is string s && s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
            // 부팅 시에는 창을 띄우지 않고 트레이로 조용히 시작
            key.SetValue(RunValueName, $"\"{ExePath}\" --minimized");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
