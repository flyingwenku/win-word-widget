using System;
using Microsoft.Win32;

namespace Yiju.Helpers;

/// <summary>开机自启：写 HKCU 的 Run 键，不需要管理员权限</summary>
internal static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Yiju";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue(Name) != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true);
            if (k == null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) k.SetValue(Name, "\"" + exe + "\"");
            }
            else
            {
                k.DeleteValue(Name, false);
            }
        }
        catch
        {
            // 写不了就算了，不该影响主功能
        }
    }
}
