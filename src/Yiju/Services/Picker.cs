using System;
using System.Linq;
using Yiju.Data;

namespace Yiju.Services;

/// <summary>
/// 选句。与 Android 端同一套四种模式：
/// 随机下一句 / 顺序下一句 / 每日一句 / 固定这一句
/// </summary>
internal static class Picker
{
    /// <summary>当前这句（不推进游标）</summary>
    public static Sentence Current(CardConfig cfg, SentenceStore store)
    {
        var list = store.Get(cfg.ListId) ?? store.First();
        if (list == null || list.Items.Count == 0) return null;

        if (cfg.Mode == Mode.Fixed)
        {
            return list.Items.FirstOrDefault(i => i.Id == cfg.FixedId) ?? list.Items[0];
        }

        var cur = list.Items.FirstOrDefault(i => i.Id == cfg.CurrentId);
        return cur ?? list.Items[0];
    }

    /// <summary>换下一句（会推进游标并落盘）</summary>
    public static Sentence Next(CardConfig cfg, SentenceStore store)
    {
        var list = store.Get(cfg.ListId) ?? store.First();
        if (list == null || list.Items.Count == 0) return null;

        Sentence pick;
        switch (cfg.Mode)
        {
            case Mode.Fixed:
                // 固定模式下换句没有意义
                return list.Items.FirstOrDefault(i => i.Id == cfg.FixedId) ?? list.Items[0];

            case Mode.Sequence:
                if (cfg.Cursor >= list.Items.Count) cfg.Cursor = 0;
                pick = list.Items[cfg.Cursor];
                cfg.Cursor = (cfg.Cursor + 1) % list.Items.Count;
                break;

            case Mode.Daily:
                // 以日期为种子：同一天每次看到的都是同一句，零显式刷新成本
                var day = (int)(DateTime.Now.Date.Ticks / TimeSpan.TicksPerDay);
                var idx = ((day % list.Items.Count) + list.Items.Count) % list.Items.Count;
                pick = list.Items[idx];
                break;

            default:
                // 随机，且尽量不抽到当前这句
                var pool = list.Items.Where(i => i.Id != cfg.CurrentId).ToList();
                if (pool.Count == 0) pool = list.Items;
                pick = pool[Random.Shared.Next(pool.Count)];
                break;
        }

        cfg.CurrentId = pick.Id;
        cfg.Save();
        return pick;
    }
}
