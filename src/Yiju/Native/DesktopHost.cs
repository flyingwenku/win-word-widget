using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yiju.Native;

/// <summary>
/// 把窗口挂到"壁纸与桌面图标之间"那一层。
///
/// 桌面结构（两种都要认，否则 24H2 上卡片会直接不显示）：
///   老结构（Win10 / 24H2 之前）：顶层 WorkerW(含 SHELLDLL_DefView=图标) | 顶层 WorkerW(空的，挂这个) | Progman
///   24H2 及以后：Progman → [SHELLDLL_DefView(图标), WorkerW(挂这个)]
///
/// 另有两个必须做的校验，来自实踩经验：
///   1) 按类名找 WorkerW 不可靠（窗口类按进程注册，会重名）→ 还要"可见 + 属于 Explorer"
///   2) 只找"不含 SHELLDLL_DefView 子窗口"的那个 WorkerW → 那才是图标后面的一层
/// </summary>
internal static class DesktopHost
{
    private const string Progman = "Progman";
    private const string WorkerW = "WorkerW";
    private const string DefView = "SHELLDLL_DefView";

    /// <summary>找到可以挂载的那一层；找不到返回 0</summary>
    public static IntPtr FindWallpaperHost()
    {
        // 实测：Progman 并不是每台机器都有（有机器 FindWindow("Progman") 直接返回 0），
        // 所以它只能"有则用之"，绝不能当作前提条件 —— 否则整个挂载逻辑直接失效。
        var progman = Win32.FindWindow(Progman, null);

        if (progman != IntPtr.Zero)
        {
            // 老系统上不给 Progman 发这个消息，空的 WorkerW 就不会存在
            Win32.SendMessageTimeout(progman, Win32.WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero,
                Win32.SMTO_NORMAL, 1000, out _);
        }

        var candidates = new List<IntPtr>();

        // 候选来源一：Progman 的子窗口（24H2 新结构）
        if (progman != IntPtr.Zero)
        {
            var h = IntPtr.Zero;
            while (true)
            {
                h = Win32.FindWindowEx(progman, h, WorkerW, null);
                if (h == IntPtr.Zero) break;
                candidates.Add(h);
            }
        }

        // 候选来源二：顶层窗口（老结构）
        Win32.EnumWindows((hwnd, _) =>
        {
            if (Win32.ClassNameOf(hwnd) == WorkerW) candidates.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        foreach (var c in candidates)
        {
            if (!Win32.IsWindowVisible(c)) continue;                                // 不可见的容器挂上去也白挂
            if (Win32.FindWindowEx(c, IntPtr.Zero, DefView, null) != IntPtr.Zero) continue; // 含图标的是另一层
            if (!BelongsToExplorer(c)) continue;                                    // 类名会重名，按进程再验一次
            return c;
        }
        return IntPtr.Zero;
    }

    /// <summary>桌面图标所在那一层（SHELLDLL_DefView）</summary>
    private static IntPtr FindDefView()
    {
        var found = IntPtr.Zero;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (found != IntPtr.Zero) return true;
            var d = Win32.FindWindowEx(hwnd, IntPtr.Zero, DefView, null);
            if (d != IntPtr.Zero) found = d;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>把窗口挂到 host 下，并把 host 压到图标那一层之下（否则卡片会盖住桌面图标）</summary>
    public static void Attach(IntPtr window, IntPtr host)
    {
        // 关键一步：WPF 的无边框窗口是 WS_POPUP 风格。
        // SetParent 对 WS_POPUP 窗口会"返回成功、实际没挂上"（GetParent 之后是 0）——
        // 换父窗口时系统不会自动改这两个风格位，必须自己先把 WS_POPUP 换成 WS_CHILD。
        var style = Win32.GetStyle(window).ToInt64();
        Win32.SetStyle(window, (IntPtr)((style | Win32.WS_CHILD) & ~Win32.WS_POPUP));

        Win32.SetParent(window, host);

        var defView = FindDefView();
        if (defView != IntPtr.Zero)
        {
            // 把 host 排到"图标那一层"之下：
            //   老结构：DefView 在另一个顶层 WorkerW 里 → 排到那个 WorkerW 之下（两者是顶层兄弟）
            //   24H2：DefView 与我们的 host 同在 Progman 下 → 排到 DefView 之下（兄弟）
            var holder = Win32.GetParent(defView);
            var behind = holder == Win32.GetParent(host) ? defView : holder;

            if (behind != IntPtr.Zero && behind != host)
            {
                Win32.SetWindowPos(host, behind, 0, 0, 0, 0,
                    Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE);
            }
        }

        Win32.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    /// <summary>给窗口切圆角（不靠 WS_EX_LAYERED —— 跨进程 SetParent 之后分层窗口不可靠）</summary>
    public static void ApplyRoundCorners(IntPtr window, double widthDip, double heightDip, double radiusDip, double dpiScale)
    {
        var rgn = Win32.CreateRoundRectRgn(
            0, 0,
            (int)Math.Round(widthDip * dpiScale) + 1,
            (int)Math.Round(heightDip * dpiScale) + 1,
            (int)Math.Round(radiusDip * dpiScale * 2),
            (int)Math.Round(radiusDip * dpiScale * 2));
        if (rgn != IntPtr.Zero)
        {
            Win32.SetWindowRgn(window, rgn, true);
            Win32.DeleteObject(rgn);
        }
    }

    private static bool BelongsToExplorer(IntPtr hwnd)
    {
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
