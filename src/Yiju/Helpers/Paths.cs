using System;
using System.IO;

namespace Yiju.Helpers;

internal static class Paths
{
    /// <summary>数据放 AppData，不放在 exe 旁边（放程序目录会在 UAC / 更新时出各种麻烦）</summary>
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yiju");

    public static readonly string ListsFile = Path.Combine(Root, "lists.json");
    public static readonly string ConfigFile = Path.Combine(Root, "config.json");

    /// <summary>埋点日志（排查"鼠标消息有没有送到卡片"这类问题用）</summary>
    public static readonly string TraceFile = Path.Combine(Root, "trace.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
    }
}
