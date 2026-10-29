using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yiju.Data;

public enum Mode
{
    Random,
    Sequence,
    Daily,
    Fixed
}

/// <summary>
/// 一张卡片的全部设置：位置、大小、用哪个列表、显示方式和配色。
/// 手机上可以加好几个桌面小部件，每张各管各的 —— Windows 这边也一样，一卡一份。
/// </summary>
public sealed class CardConfig
{
    public long Id { get; set; }

    public double X { get; set; } = 140;
    public double Y { get; set; } = 140;
    public double W { get; set; } = 380;
    public double H { get; set; } = 190;

    public long ListId { get; set; } = -1;
    public Mode Mode { get; set; } = Mode.Random;
    public long FixedId { get; set; }
    public int Cursor { get; set; }
    public long CurrentId { get; set; }
    public int RefreshMinutes { get; set; } = 30;

    /// <summary>属于哪个配置文件（改了要能落盘）</summary>
    [JsonIgnore]
    public AppConfig Owner { get; set; }

    public void Save() => Owner?.Save();
}

/// <summary>
/// 整体配置：可以有多张卡片。
/// 单文件 JSON，学 Snipaste 那种朴素做法：一个文件搞定，别弄一堆散配置。
/// </summary>
public sealed class AppConfig
{
    public List<CardConfig> Cards { get; set; } = new();
    public bool StartOnBoot { get; set; }

    /// <summary>配色方案：索引到 Palette.Schemes。**全局** —— 所有卡片统一，免得一张黑字一张白字</summary>
    public int ColorScheme { get; set; }

    // 下面这几个是"只有一张卡片"那版的字段。老用户的配置文件里就是这种格式，
    // 读上来之后并进 Cards（见 Migrate），不至于一升级就把位置弄丢。
    public double X { get; set; } = 140;
    public double Y { get; set; } = 140;
    public double W { get; set; } = 380;
    public double H { get; set; } = 190;
    public long ListId { get; set; } = -1;
    public Mode Mode { get; set; } = Mode.Random;
    public long FixedId { get; set; }
    public int Cursor { get; set; }
    public long CurrentId { get; set; }
    public int RefreshMinutes { get; set; } = 30;

    private string _file;

    /// <summary>
    /// 反序列化专用：必须有一个无参构造函数。
    /// 踩过的坑 —— 之前只有一个带 file 参数的构造函数，System.Text.Json 会把它当"参数化构造"，
    /// 而 file 匹配不到任何属性 → 直接抛异常。外面那层 try/catch 一吞，就变成"每次启动都用默认值"，
    /// 表现就是：拖好的位置从来没被记住过。
    /// </summary>
    [JsonConstructor]
    public AppConfig()
    {
    }

    public AppConfig(string file) => _file = file;

    public static AppConfig Load(string file)
    {
        AppConfig c = null;
        try
        {
            if (File.Exists(file)) c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file));
        }
        catch
        {
            // 读不出来就用默认值，别让程序起不来
        }

        c ??= new AppConfig(file);
        c._file = file;
        c.Migrate();
        return c;
    }

    /// <summary>旧的单卡片配置 → 收进 Cards 列表；顺手把每张卡片的 Owner 挂上</summary>
    private void Migrate()
    {
        Cards ??= new List<CardConfig>();

        if (Cards.Count == 0)
        {
            Cards.Add(new CardConfig
            {
                Id = 1,
                X = X, Y = Y, W = W, H = H,
                ListId = ListId, Mode = Mode, FixedId = FixedId,
                Cursor = Cursor, CurrentId = CurrentId,
                RefreshMinutes = RefreshMinutes
            });
        }

        for (var i = 0; i < Cards.Count; i++)
        {
            Cards[i].Owner = this;
            if (Cards[i].Id == 0) Cards[i].Id = i + 1;   // 老文件里可能没有 Id
        }
    }

    public long NewCardId() => Cards.Count == 0 ? 1 : Cards.Max(c => c.Id) + 1;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file));
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, true);
        }
        catch
        {
            // 存盘失败不该让程序挂掉
        }
    }
}
