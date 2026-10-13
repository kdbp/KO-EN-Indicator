using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KorEng;

internal sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool HasWindowBounds { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "KorEng.settings.json");

    internal static Settings Load(string path)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > 0 and < 16384 })
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    internal bool Save(string path)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal void RestoreBounds(Form form)
    {
        if (!HasWindowBounds) return;
        var saved = new Rectangle(X, Y, Math.Clamp(Width, form.MinimumSize.Width, 1200), Math.Clamp(Height, form.MinimumSize.Height, 1000));
        Rectangle work = Screen.FromRectangle(saved).WorkingArea;
        saved.Width = Math.Min(saved.Width, work.Width);
        saved.Height = Math.Min(saved.Height, work.Height);
        saved.X = Math.Clamp(saved.X, work.Left, Math.Max(work.Left, work.Right - saved.Width));
        saved.Y = Math.Clamp(saved.Y, work.Top, Math.Max(work.Top, work.Bottom - saved.Height));
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = saved;
    }

    internal void CaptureBounds(Form form)
    {
        Rectangle rect = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X <= -30000 || rect.Y <= -30000) return;
        HasWindowBounds = true; X = rect.X; Y = rect.Y; Width = rect.Width; Height = rect.Height;
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KorEng";
    internal static string Command(string executable) => "\"" + executable + "\" --startup";

    internal static bool Apply(bool enabled, out string error)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled) key.SetValue(ValueName, Command(Environment.ProcessPath!), RegistryValueKind.String);
            else key.DeleteValue(ValueName, false);
            error = ""; return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = "자동 실행 설정을 저장하지 못했습니다. 접근 권한을 확인해 주세요.";
            return false;
        }
    }
}
