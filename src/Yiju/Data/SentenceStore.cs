using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Yiju.Data;

/// <summary>
/// 句子库：多个有名字的列表 + 句子增删改查。
///
/// 文件格式与 Android 端 v1.2 完全一致（lists / items：id、t、s），
/// 只多了一个 "v" 版本字段 —— 阶段二要做 WebDAV 同步，没有版本号两端会互相读崩。
/// 旧文件（没有 v 字段）按 v1 处理。
/// </summary>
public sealed class SentenceStore
{
    private const int FormatVersion = 2;
    private readonly string _file;
    private readonly List<SentenceList> _lists;

    /// <summary>内容变了（卡片要跟着重画）</summary>
    public event Action Changed;

    public SentenceStore(string file)
    {
        _file = file;
        _lists = Load();
    }

    public IReadOnlyList<SentenceList> Lists => _lists;
    public SentenceList Get(long id) => _lists.FirstOrDefault(l => l.Id == id);
    public SentenceList First() => _lists.FirstOrDefault();

    public SentenceList AddList(string name)
    {
        var l = new SentenceList { Id = NextListId(), Name = string.IsNullOrWhiteSpace(name) ? "新列表" : name.Trim() };
        _lists.Add(l);
        Save();
        return l;
    }

    public void RenameList(long id, string name)
    {
        var l = Get(id);
        if (l == null || string.IsNullOrWhiteSpace(name)) return;
        l.Name = name.Trim();
        Save();
    }

    /// <summary>删除整个列表。至少保留一个（卡片总要有个数据源）</summary>
    public bool DeleteList(long id)
    {
        if (_lists.Count <= 1) return false;
        var l = Get(id);
        if (l == null) return false;
        _lists.Remove(l);
        Save();
        return true;
    }

    public Sentence AddSentence(long listId, string text, string source)
    {
        var l = Get(listId) ?? First();
        if (l == null) return null;
        var s = new Sentence
        {
            Id = (l.Items.Count == 0 ? 0 : l.Items.Max(i => i.Id)) + 1,
            Text = text.Trim(),
            Source = (source ?? "").Trim()
        };
        l.Items.Add(s);
        Save();
        return s;
    }

    public void UpdateSentence(long listId, long sentenceId, string text, string source)
    {
        var l = Get(listId);
        if (l == null) return;
        var s = l.Items.FirstOrDefault(i => i.Id == sentenceId);
        if (s == null) return;
        s.Text = text.Trim();
        s.Source = (source ?? "").Trim();
        Save();
    }

    public void DeleteSentence(long listId, long sentenceId)
    {
        var l = Get(listId);
        if (l == null) return;
        var s = l.Items.FirstOrDefault(i => i.Id == sentenceId);
        if (s == null) return;
        l.Items.Remove(s);
        Save();
    }

    // ===== 持久化 =====

    private List<SentenceList> Load()
    {
        try
        {
            if (!File.Exists(_file)) return Presets();

            var root = JsonNode.Parse(File.ReadAllText(_file));
            var arr = root?["lists"]?.AsArray();
            if (arr == null) return Presets();

            var lists = new List<SentenceList>();
            foreach (var n in arr)
            {
                var items = new List<Sentence>();
                foreach (var it in n?["items"]?.AsArray() ?? new JsonArray())
                {
                    items.Add(new Sentence
                    {
                        Id = (long)(it?["id"]?.GetValue<double>() ?? 0),
                        Text = it?["t"]?.GetValue<string>() ?? "",
                        Source = it?["s"]?.GetValue<string>() ?? ""
                    });
                }
                lists.Add(new SentenceList
                {
                    Id = (long)(n?["id"]?.GetValue<double>() ?? 0),
                    Name = n?["n"]?.GetValue<string>() ?? "列表",
                    Items = items
                });
            }
            return lists.Count == 0 ? Presets() : lists;
        }
        catch
        {
            // 文件坏了不能直接崩：退回预置，至少程序还能用
            return Presets();
        }
    }

    public void Save()
    {
        try
        {
            var arr = new JsonArray();
            foreach (var l in _lists)
            {
                var items = new JsonArray();
                foreach (var s in l.Items)
                {
                    items.Add(new JsonObject
                    {
                        ["id"] = s.Id,
                        ["t"] = s.Text,
                        ["s"] = s.Source
                    });
                }
                arr.Add(new JsonObject
                {
                    ["id"] = l.Id,
                    ["n"] = l.Name,
                    ["items"] = items
                });
            }

            var root = new JsonObject { ["v"] = FormatVersion, ["lists"] = arr };

            Directory.CreateDirectory(Path.GetDirectoryName(_file));
            // 先写临时文件再覆盖，避免写到一半崩掉把数据弄坏
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString());
            File.Move(tmp, _file, true);

            Changed?.Invoke();
        }
        catch
        {
            // 存盘失败不该让程序挂掉
        }
    }

    private long NextListId() => (_lists.Count == 0 ? 0 : _lists.Max(l => l.Id)) + 1;

    /// <summary>与 Android 端同样预置《论语》14 条，装完立刻能看到效果，也方便对照两端观感</summary>
    private static List<SentenceList> Presets()
    {
        var preset = new[]
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

        var items = new List<Sentence>();
        for (var i = 0; i < preset.Length; i++)
        {
            items.Add(new Sentence { Id = i + 1, Text = preset[i].Item1, Source = preset[i].Item2 });
        }
        return new List<SentenceList> { new() { Id = 1, Name = "论语", Items = items } };
    }
}
