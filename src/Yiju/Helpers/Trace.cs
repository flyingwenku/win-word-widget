using System;
using System.Collections.Generic;
using System.IO;

namespace Yiju.Helpers;

/// <summary>
/// 埋点日志。排查"卡片为什么收不到鼠标"这类问题时才用得上：
/// 卡片钉在桌面层之后，肉眼看不出消息到底有没有送到，只能靠日志。
/// 每次启动清空重写，写不写失败都不影响主流程。
/// </summary>
internal static class Trace
{
    /// <summary>
    /// 默认关着：排查问题时在 %LOCALAPPDATA%\Yiju\ 里放一个空的 trace.on 就打开
    /// （再删掉就关）。不然每秒写日志纯属浪费。
    /// </summary>
    public static readonly bool Enabled = File.Exists(Path.Combine(Paths.Root, "trace.on"));

    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> LastAt = new();

    public static void Reset()
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try { File.WriteAllText(Paths.TraceFile, string.Empty); } catch { }
        }
    }

    public static void Log(string line)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Paths.TraceFile,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
            catch
            {
                // 日志写不了就算了，不能因为它把程序搞崩
            }
        }
    }

    /// <summary>同一个 key 在 ms 毫秒内只放行一次 —— 否则 WM_MOUSEMOVE 这类消息会把日志冲爆</summary>
    public static bool Every(string key, int ms)
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (LastAt.TryGetValue(key, out var t) && now - t < ms) return false;
            LastAt[key] = now;
            return true;
        }
    }
}
