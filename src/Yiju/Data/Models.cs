using System;
using System.Collections.Generic;
using System.Linq;

namespace Yiju.Data;

/// <summary>
/// 一条句子。
///
/// 2026-10-29 起加了三个字段，为多端同步做准备（规格见 docs/sync-spec.md）：
///   Uuid      —— 跨设备**唯一且永久不变**的身份。以前用"列表内 max+1"的数字 id，
///                两端各自记账，必然撞号：手机端的 15 号和电脑端的 15 号是两句不同的话，
///                一合并就互相顶掉。Uuid 才是拿来做对照的那把钥匙。
///   UpdatedAt —— 最后修改时间（Unix 毫秒）。两端都改过同一条时，用它裁决谁留下。
///   Deleted   —— 墓碑。删掉的东西不能直接从文件里抹掉，否则下次同步时另一端
///                （还没删的那一家）会把它原样送回来，删了等于没删。
/// Id 这个数字字段只为读老文件保留，读完即弃。
/// </summary>
public sealed class Sentence
{
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
    public long UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}

public sealed class SentenceList
{
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>包含墓碑在内的全部条目（同步和落盘用这个）</summary>
    public List<Sentence> Items { get; set; } = new();

    public long UpdatedAt { get; set; }
    public bool Deleted { get; set; }

    /// <summary>没被删掉的条目（显示、选句用这个）</summary>
    public List<Sentence> Alive() => Items.Where(i => !i.Deleted).ToList();

    public Sentence FindAlive(string uuid) => Items.FirstOrDefault(i => i.Uuid == uuid && !i.Deleted);

    public override string ToString() => Name;
}
