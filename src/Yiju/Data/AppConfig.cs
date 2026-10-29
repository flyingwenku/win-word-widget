using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yiju.Data;

/// <summary>
/// 同步设置（坚果云 WebDAV）。
///
/// 密码不能明文躺在配置文件里 —— 用 DPAPI 按**当前 Windows 用户**加密：
/// 换了用户或换了机器就解不开（会当成"没设密码"提示重填），但至少不会被人拷走文件就直接看到密码。
/// </summary>
public sealed class SyncConfig
{
    public string Url { get; set; } = "https://dav.jianguoyun.com/dav/Yiju/yiju-sync.json";
    public string User { get; set; } = "";

    /// <summary>DPAPI 加密后的密码（base64）</summary>
    public string EncPassword { get; set; } = "";

    public bool AutoSync { get; set; } = true;
    public long LastSyncAt { get; set; }

    /// <summary>明码密码只存在于内存里，不落盘</summary>
    [JsonIgnore]
    public string Password
    {
        get
        {
            if (string.IsNullOrEmpty(EncPassword)) return "";
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(EncPassword),
                    null, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return "";
            }
        }
        set
        {
            if (string.IsNullOrEmpty(value)) { EncPassword = ""; return; }
            EncPassword = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),
                null, DataProtectionScope.CurrentUser));
        }
    }

    public bool Ready => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(User) && Password.Length > 0;
}

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

    /// <summary>这张卡片用哪个列表（Uuid）。空 = 跟着第一个列表。
    /// 以前这里是数字 ListId —— 那是各端自己记账的，跨设备不是同一个东西。</summary>
    public string ListUuid { get; set; } = "";

    public Mode Mode { get; set; } = Mode.Random;

    /// <summary>固定模式下锁定的是哪一句（Uuid）</summary>
    public string FixedUuid { get; set; } = "";

    public int Cursor { get; set; }

    /// <summary>当前显示到哪一句了（Uuid）</summary>
    public string CurrentUuid { get; set; } = "";

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

    /// <summary>多端同步（坚果云 WebDAV）</summary>
    public SyncConfig Sync { get; set; } = new();

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
                Mode = Mode, Cursor = Cursor,
                RefreshMinutes = RefreshMinutes
                // ListUuid / FixedUuid / CurrentUuid 由 MigrateReferences() 补，
                // 因为要先把数字 id 翻译成 uuid —— 那得等句子库读完才行
            });
        }

        for (var i = 0; i < Cards.Count; i++)
        {
            Cards[i].Owner = this;
            if (Cards[i].Id == 0) Cards[i].Id = i + 1;   // 老文件里可能没有 Id
        }
    }

    /// <summary>
    /// 旧配置里"用哪个列表 / 固定句 / 当前句"存的是数字 id，而句子库升级成 uuid 了，
    /// 必须拿着句子库才能翻译 —— 所以这一步在 App 里、句子库读完之后再调。
    /// </summary>
    public void MigrateReferences(SentenceStore store)
    {
        if (ListId <= 0) return;

        var list = store.Lists.FirstOrDefault(l => l.Id == ListId) ?? store.First();
        if (list == null)
        {
            ClearLegacyRefs();
            Save();
            return;
        }

        var fixedS = list.Items.FirstOrDefault(i => i.Id == FixedId);
        var curS = list.Items.FirstOrDefault(i => i.Id == CurrentId);
        var translated = false;

        foreach (var c in Cards)
        {
            if (!string.IsNullOrEmpty(c.ListUuid)) continue;
            c.ListUuid = list.Uuid;
            if (fixedS != null) c.FixedUuid = fixedS.Uuid;
            if (curS != null) c.CurrentUuid = curS.Uuid;
            translated = true;
        }

        // 翻译完把老字段清掉，免得下次又被当成待迁移
        ClearLegacyRefs();
        if (translated) Save();
    }

    private void ClearLegacyRefs()
    {
        ListId = -1;
        FixedId = 0;
        CurrentId = 0;
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
