using System;
using System.IO;
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
/// 卡片自己的配置（位置、大小、用哪个列表、显示方式）。
/// 单文件 JSON，学 Snipaste 那种朴素做法：一个文件搞定，别弄一堆散配置。
/// </summary>
public sealed class AppConfig
{
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
    public bool StartOnBoot { get; set; }

    private string _file;

    public AppConfig(string file) => _file = file;

    public static AppConfig Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file));
                if (c != null) { c._file = file; return c; }
            }
        }
        catch
        {
            // 读不出来就用默认值，别让程序起不来
        }
        return new AppConfig(file);
    }

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
