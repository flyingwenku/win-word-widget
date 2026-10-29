using System;
using System.Linq;
using Yiju.Data;

namespace Yiju.Services;

/// <summary>
/// 选句。与 Android 端同一套四种模式：
/// 随机下一句 / 顺序下一句 / 每日一句 / 固定这一句
///
/// 注意这里一律用 **Uuid** 而不是数字 id 指向列表和句子 ——
/// 数字 id 是各端自己记账的，跨设备不是同一个东西（见 docs/sync-spec.md）。
/// </summary>
internal static class Picker
{
    /// <summary>当前这句（不推进游标）</summary>
    public static Sentence Current(CardConfig cfg, SentenceStore store)
    {
        var list = store.Get(cfg.ListUuid) ?? store.First();
        if (list == null) return null;

        var items = list.Alive();
        if (items.Count == 0) return null;

        if (cfg.Mode == Mode.Fixed)
            return list.FindAlive(cfg.FixedUuid) ?? items[0];

        return items.FirstOrDefault(i => i.Uuid == cfg.CurrentUuid) ?? items[0];
    }

    /// <summary>换下一句（会推进游标并落盘）</summary>
    public static Sentence Next(CardConfig cfg, SentenceStore store)
    {
        var list = store.Get(cfg.ListUuid) ?? store.First();
        if (list == null) return null;

        var items = list.Alive();
        if (items.Count == 0) return null;

        Sentence pick;
        switch (cfg.Mode)
        {
            case Mode.Fixed:
                // 固定模式下换句没有意义
                return list.FindAlive(cfg.FixedUuid) ?? items[0];

            case Mode.Sequence:
                if (cfg.Cursor >= items.Count) cfg.Cursor = 0;
                pick = items[cfg.Cursor];
                cfg.Cursor = (cfg.Cursor + 1) % items.Count;
                break;

            case Mode.Daily:
                // 以日期为种子：同一天每次看到的都是同一句，零显式刷新成本
                var day = (int)(DateTime.Now.Date.Ticks / TimeSpan.TicksPerDay);
                var idx = ((day % items.Count) + items.Count) % items.Count;
                pick = items[idx];
                break;

            default:
                // 随机，且尽量不抽到当前这句
                var pool = items.Where(i => i.Uuid != cfg.CurrentUuid).ToList();
                if (pool.Count == 0) pool = items;
                pick = pool[Random.Shared.Next(pool.Count)];
                break;
        }

        cfg.CurrentUuid = pick.Uuid;
        cfg.Save();
        return pick;
    }
}
