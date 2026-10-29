using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yiju.Helpers;

namespace Yiju.Data;

/// <summary>
/// 句子库：多个有名字的列表 + 句子增删改查。
///
/// 文件格式 v3（多端同步的地基，规格见 docs/sync-spec.md）：
///   {"v":3, "deviceId":…, "lists":[ {"uuid":…,"n":…,"updatedAt":…,"deleted":…,
///                                    "items":[{"uuid":…,"t":…,"s":…,"updatedAt":…,"deleted":…}]} ]}
///
/// 关键改动（对比 v2）：
///   - 身份从"列表内 max+1 的数字 id"换成 Uuid —— 否则两端必然撞号
///   - 每条都带 UpdatedAt（Unix 毫秒）—— 不然没法判断两边谁更新
///   - 删除改成写墓碑（Deleted=true）而不是真的删掉
/// 旧的 v2 文件会在 Load 时自动补上这些字段并升级。
/// </summary>
public sealed class SentenceStore
{
    private const int FormatVersion = 3;

    /// <summary>墓碑保留多久：过期就彻底丢掉，免得文件无限膨胀</summary>
    private static readonly long TombstoneKeepMs = TimeSpan.FromDays(30).Ticks / TimeSpan.TicksPerMillisecond;

    private readonly string _file;
    private readonly List<SentenceList> _lists;

    /// <summary>本机身份（同步时写进远端文件，方便排查是谁改的）</summary>
    public string DeviceId { get; private set; } = Guid.NewGuid().ToString();

    /// <summary>内容变了（卡片要跟着重画）</summary>
    public event Action Changed;

    public SentenceStore(string file)
    {
        _file = file;
        _lists = Load(out var needUpgrade);

        // 升级完立刻落盘。注意必须放在这里 —— 不能在 Load() 里调 Save()：
        // 那时 `_lists = Load()` 还没赋值，-lists 是 null，一写就 NullReferenceException。
        if (needUpgrade)
        {
            Yiju.Helpers.Trace.Log($"lists migrating to v{FormatVersion}");
            Save();
        }
    }

    /// <summary>没被删掉的列表</summary>
    public IReadOnlyList<SentenceList> Lists => _lists.Where(l => !l.Deleted).ToList();

    /// <summary>含墓碑在内的全部列表（同步层用）</summary>
    public IReadOnlyList<SentenceList> AllIncludingDeleted => _lists;

    public SentenceList Get(string uuid) => _lists.FirstOrDefault(l => l.Uuid == uuid && !l.Deleted);
    public SentenceList First() => Lists.FirstOrDefault();

    public SentenceList AddList(string name)
    {
        var l = new SentenceList
        {
            Uuid = Guid.NewGuid().ToString(),
            Name = string.IsNullOrWhiteSpace(name) ? "新列表" : name.Trim(),
            UpdatedAt = Now()
        };
        _lists.Add(l);
        Save();
        return l;
    }

    public void RenameList(string uuid, string name)
    {
        var l = Get(uuid);
        if (l == null || string.IsNullOrWhiteSpace(name)) return;
        l.Name = name.Trim();
        l.UpdatedAt = Now();
        Save();
    }

    /// <summary>删除整个列表（写墓碑）。至少留一个活着的（卡片总要有个数据源）</summary>
    public bool DeleteList(string uuid)
    {
        if (Lists.Count <= 1) return false;
        var l = Get(uuid);
        if (l == null) return false;
        l.Deleted = true;
        l.UpdatedAt = Now();
        Save();
        return true;
    }

    public Sentence AddSentence(string listUuid, string text, string source)
    {
        var l = Get(listUuid) ?? First();
        if (l == null) return null;
        var s = new Sentence
        {
            Uuid = Guid.NewGuid().ToString(),
            Text = text.Trim(),
            Source = (source ?? "").Trim(),
            UpdatedAt = Now()
        };
        l.Items.Add(s);
        l.UpdatedAt = Now();
        Save();
        return s;
    }

    public void UpdateSentence(string listUuid, string sentenceUuid, string text, string source)
    {
        var l = Get(listUuid);
        if (l == null) return;
        var s = l.FindAlive(sentenceUuid);
        if (s == null) return;
        s.Text = text.Trim();
        s.Source = (source ?? "").Trim();
        s.UpdatedAt = Now();
        l.UpdatedAt = Now();
        Save();
    }

    /// <summary>删除一条句子（写墓碑，不是真删）</summary>
    public void DeleteSentence(string listUuid, string sentenceUuid)
    {
        var l = Get(listUuid);
        if (l == null) return;
        var s = l.FindAlive(sentenceUuid);
        if (s == null) return;
        s.Deleted = true;
        s.UpdatedAt = Now();
        l.UpdatedAt = Now();
        Save();
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ===== 持久化 =====

    private List<SentenceList> Load(out bool needUpgrade)
    {
        List<SentenceList> lists = null;
        needUpgrade = false;

        try
        {
            if (File.Exists(_file))
            {
                var root = JsonNode.Parse(File.ReadAllText(_file));
                DeviceId = root?["deviceId"]?.GetValue<string>() ?? DeviceId;

                var version = (int)(root?["v"]?.GetValue<double>() ?? 1);
                needUpgrade = version < FormatVersion;

                var arr = root?["lists"]?.AsArray();
                if (arr != null)
                {
                    lists = new List<SentenceList>();
                    foreach (var n in arr)
                    {
                        var items = new List<Sentence>();
                        foreach (var it in n?["items"]?.AsArray() ?? new JsonArray())
                        {
                            items.Add(new Sentence
                            {
                                Uuid = it?["uuid"]?.GetValue<string>() ?? "",
                                Id = (long)(it?["id"]?.GetValue<double>() ?? 0),
                                Text = it?["t"]?.GetValue<string>() ?? "",
                                Source = it?["s"]?.GetValue<string>() ?? "",
                                UpdatedAt = ReadTs(it as JsonObject),
                                Deleted = it?["deleted"]?.GetValue<bool>() ?? false
                            });
                        }
                        lists.Add(new SentenceList
                        {
                            Uuid = n?["uuid"]?.GetValue<string>() ?? "",
                            Id = (long)(n?["id"]?.GetValue<double>() ?? 0),
                            Name = n?["n"]?.GetValue<string>() ?? "列表",
                            UpdatedAt = ReadTs(n as JsonObject),
                            Deleted = n?["deleted"]?.GetValue<bool>() ?? false,
                            Items = items
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 文件坏了不能直接崩：退回预置，至少程序还能用
            Yiju.Helpers.Trace.Log($"lists Load failed: {ex.GetType().Name}: {ex.Message}");
            lists = null;
        }

        lists ??= Presets();

        if (needUpgrade) Migrate(lists, SafeFileTimeUtc());

        DropOldTombstones(lists);
        return lists;
    }

    private static long ReadTs(JsonObject o)
    {
        if (o == null) return 0;
        var v = o["updatedAt"]?.GetValue<double>();
        return v.HasValue ? (long)v.Value : 0;
    }

    private DateTime SafeFileTimeUtc()
    {
        try { return File.GetLastWriteTimeUtc(_file); } catch { return DateTime.UtcNow; }
    }

    /// <summary>
    /// v2 → v3：给每条数据补 uuid / updatedAt / deleted。
    /// 预置的《论语》14 条走 **稳定 uuid**，这样两端 migrating 完还是同一批 id，
    /// 第一次同步合并得到 14 条而不是 28 条。
    /// </summary>
    private void Migrate(List<SentenceList> lists, DateTime stamp)
    {
        var ms = new DateTimeOffset(stamp).ToUnixTimeMilliseconds();
        var preset = PresetTexts();

        foreach (var l in lists)
        {
            // 只有"确实是预置原句"的列表才用稳定 uuid；用户自己建的列表用随机 uuid ——
            // 否则两端各自建了个同名列表（比如都叫"333"），会被当成同一个列表合并掉，丢数据
            if (string.IsNullOrEmpty(l.Uuid))
                l.Uuid = isPreset(l) ? StableId.List(l.Name) : Guid.NewGuid().ToString();
            if (l.UpdatedAt <= 0) l.UpdatedAt = ms;

            var presetList = isPreset(l);

            for (var i = 0; i < l.Items.Count; i++)
            {
                var s = l.Items[i];
                if (string.IsNullOrEmpty(s.Uuid))
                    s.Uuid = presetList ? StableId.Preset(l.Name, i) : Guid.NewGuid().ToString();
                if (s.UpdatedAt <= 0) s.UpdatedAt = ms;
            }
        }

        // 只有内容确实是预置原句，才拿得到和另一端一致的稳定 uuid
        bool isPreset(SentenceList l) =>
            l.Name == "论语" &&
            l.Items.Count == preset.Count &&
            l.Items.Select(i => i.Text).SequenceEqual(preset.Select(p => p.Item1));
    }

    private static void DropOldTombstones(List<SentenceList> lists)
    {
        var deadline = Now() - TombstoneKeepMs;
        foreach (var l in lists)
        {
            l.Items.RemoveAll(s => s.Deleted && s.UpdatedAt < deadline);
        }
        lists.RemoveAll(l => l.Deleted && l.UpdatedAt < deadline);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file));
            // 先写临时文件再覆盖，避免写到一半崩掉把数据弄坏
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, Serialize());
            File.Move(tmp, _file, true);

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // 存盘失败不该让程序挂掉，但得留下线索
            Yiju.Helpers.Trace.Log($"lists Save failed: {ex}");
        }
    }

    /// <summary>落成同步用的那份文本（远端传的就是这个）</summary>
    public string Serialize() => ToJson(_lists, DeviceId);

    /// <summary>把任意一份列表（比如合并结果）序列化成同一格式，同步层要用</summary>
    public static string ToJson(IEnumerable<SentenceList> lists, string deviceId) =>
        Root(lists, deviceId).ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    private static JsonObject Root(IEnumerable<SentenceList> lists, string deviceId)
    {
        var arr = new JsonArray();
        foreach (var l in lists)
        {
            var items = new JsonArray();
            foreach (var s in l.Items)
            {
                items.Add(new JsonObject
                {
                    ["uuid"] = s.Uuid,
                    ["id"] = s.Id,
                    ["t"] = s.Text,
                    ["s"] = s.Source,
                    ["updatedAt"] = s.UpdatedAt,
                    ["deleted"] = s.Deleted
                });
            }
            arr.Add(new JsonObject
            {
                ["uuid"] = l.Uuid,
                ["id"] = l.Id,
                ["n"] = l.Name,
                ["updatedAt"] = l.UpdatedAt,
                ["deleted"] = l.Deleted,
                ["items"] = items
            });
        }

        return new JsonObject
        {
            ["v"] = FormatVersion,
            ["deviceId"] = deviceId,
            ["lists"] = arr
        };
    }

    /// <summary>解析另一端传来的文本（认不出就返回 null，交给调用方当"没有远端数据"处理）</summary>
    public static List<SentenceList> Parse(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var arr = root?["lists"]?.AsArray();
            if (arr == null) return null;

            var lists = new List<SentenceList>();
            foreach (var n in arr)
            {
                var items = new List<Sentence>();
                foreach (var it in n?["items"]?.AsArray() ?? new JsonArray())
                {
                    items.Add(new Sentence
                    {
                        Uuid = it?["uuid"]?.GetValue<string>() ?? "",
                        Id = (long)(it?["id"]?.GetValue<double>() ?? 0),
                        Text = it?["t"]?.GetValue<string>() ?? "",
                        Source = it?["s"]?.GetValue<string>() ?? "",
                        UpdatedAt = ReadTs(it as JsonObject),
                        Deleted = it?["deleted"]?.GetValue<bool>() ?? false
                    });
                }
                lists.Add(new SentenceList
                {
                    Uuid = n?["uuid"]?.GetValue<string>() ?? "",
                    Id = (long)(n?["id"]?.GetValue<double>() ?? 0),
                    Name = n?["n"]?.GetValue<string>() ?? "列表",
                    UpdatedAt = ReadTs(n as JsonObject),
                    Deleted = n?["deleted"]?.GetValue<bool>() ?? false,
                    Items = items
                });
            }
            return lists;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>同步合并完之后整体换掉（会落盘并通知卡片重画）</summary>
    public void ReplaceAll(List<SentenceList> lists)
    {
        _lists.Clear();
        _lists.AddRange(lists);
        Save();
    }

    /// <summary>与 Android 端同样预置《论语》14 条，装完立刻能看到效果，也方便对照两端观感</summary>
    private static List<(string, string)> PresetTexts() => new()
    {
        ("学而时习之，不亦说乎？", "《论语·学而》"),
        ("温故而知新，可以为师矣。", "《论语·为政》"),
        ("学而不思则罔，思而不学则殆。", "《论语·为政》"),
        ("知之为知之，不知为不知，是知也。", "《论语·为政》"),
        ("三人行，必有我师焉。", "《论语·述而》"),
        ("知之者不如好之者，好之者不如乐之者。", "《论语·雍也》"),
        ("己所不欲，勿施于人。", "《论语·颜渊》"),
        ("君子坦荡荡，小人长戚戚。", "《论语·述而》"),
        ("工欲善其事，必先利其器。", "《论语·卫灵公》"),
        ("吾日三省吾身。", "《论语·学而》"),
        ("岁寒，然后知松柏之后凋也。", "《论语·子罕》"),
        ("见贤思齐焉，见不贤而内自省也。", "《论语·里仁》"),
        ("三军可夺帅也，匹夫不可夺志也。", "《论语·子罕》"),
        ("不患人之不己知，患不知人也。", "《论语·学而》")
    };

    private static List<SentenceList> Presets()
    {
        var preset = PresetTexts();
        var items = new List<Sentence>();
        for (var i = 0; i < preset.Count; i++)
        {
            items.Add(new Sentence
            {
                Uuid = StableId.Preset("论语", i),
                Id = i + 1,
                Text = preset[i].Item1,
                Source = preset[i].Item2,
                UpdatedAt = Now()
            });
        }
        return new List<SentenceList>
        {
            new() { Uuid = StableId.List("论语"), Id = 1, Name = "论语", Items = items, UpdatedAt = Now() }
        };
    }
}
