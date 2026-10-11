using Microsoft.Win32;

namespace G49BatteryMonitor;

/// <summary>
/// 开机自启：写当前用户的 Run 注册表项，不需要管理员权限。
///
/// 重要：程序不会自己勾上这一项。只有用户在界面上主动勾选，才会写注册表。
/// 另外每次启动会自检：如果这一项已经存在但路径不对（比如文件夹被搬走了），就把它修正。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "G49BatteryMonitor";

    private static string ExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "G49BatteryMonitor.exe");

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool Enable(out string error)
    {
        error = "";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key == null) { error = "打不开注册表项"; return false; }
            key.SetValue(ValueName, $"\"{ExePath}\" --background", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Disable(out string error)
    {
        error = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>自启项已存在但指向别处时修正它。没有自启项就什么都不做。</summary>
    public static void RepairIfNeeded()
    {
        try
        {
            if (!IsEnabled()) return;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            string? current = key?.GetValue(ValueName) as string;
            string expected = $"\"{ExePath}\" --background";
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                using var write = Registry.CurrentUser.OpenSubKey(RunKey, true);
                write?.SetValue(ValueName, expected, RegistryValueKind.String);
            }
        }
        catch
        {
            // 自检失败无所谓
        }
    }
}
