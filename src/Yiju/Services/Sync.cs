using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Yiju.Data;
using Yiju.Helpers;

namespace Yiju.Services;

/// <summary>
/// 多端同步（坚果云 WebDAV 单文件）。规格见知识库《多端同步规格》，
/// 踩坑见《多端同步踩坑与解法（通用）》。
///
/// 流程：**一次 GET → 按 uuid 合并 → 有变化才一次 PUT**。
/// 不用 ETag / If-Match / 重试：坚果云 ETag 实测不可信（GET 刚拿到的值原样 PUT 会 412），
/// 带锁和重试都只是白绕一圈；并发窗口只有零点几秒，靠合并的幂等性兜底即可。
/// 这与新项目（桌面进度条）的同步策略一致，也与 Android 端逐字对齐。
///
/// 两个刻意的设计：
///   - **内容变了不立刻传**，攒 10 秒再传。坚果云免费账户有流量限制，
///     用户连着加十几句话不该产生十几次上传。
///   - **没有本地改动就不写远端**。合并结果跟远端一字不差时直接不 PUT，
///     否则会陷入"同步 → 触发保存 → 又同步"的空转。
/// </summary>
public sealed class SyncService
{
    private readonly AppConfig _cfg;
    private readonly SentenceStore _store;

    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _periodic;

    private bool _dirty;
    private bool _running;

    /// <summary>同步状态（给设置窗口显示："已上传 xx 条" / "失败：…"）</summary>
    public Action<string> Status;

    public SyncService(AppConfig cfg, SentenceStore store)
    {
        _cfg = cfg;
        _store = store;

        // 攒 10 秒：连着加十几句话不该产生十几次上传（坚果云免费账户有流量限制）
        _debounce = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background,
            (_, __) =>
            {
                if (!_dirty || !_cfg.Sync.AutoSync) return;
                _ = SyncNow();
            }, Dispatcher.CurrentDispatcher);

        _periodic = new DispatcherTimer(TimeSpan.FromMinutes(30), DispatcherPriority.Background,
            (_, __) => _ = SyncNow(), Dispatcher.CurrentDispatcher);
    }

    public void Start()
    {
        // 句子库一改就记一笔，10 秒后统一传一次（别拖慢启动）
        _store.Changed += Kick;

        if (_cfg.Sync.AutoSync && _cfg.Sync.Ready) Kick();
        _periodic.Start();
    }

    /// <summary>内容变了（或需要拉一次远端）：攒 10 秒再传</summary>
    public void Kick()
    {
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    public async Task<string> SyncNow()
    {
        if (_running) return "同步进行中…";

        var s = _cfg.Sync;
        if (!s.Ready) return "还没填同步账号（右键托盘 → 同步设置）";

        _running = true;
        try
        {
            var dav = new WebDavClient(s.Url, s.User, s.Password);
            await dav.EnsureDirAsync();

            var (exists, remoteJson) = await dav.FetchAsync();
            var local = _store.AllIncludingDeleted.ToList();

            // 远端还没有 → 把本地这份直接放上去（本地也是空库就别写一个空文件）
            if (!exists)
            {
                if (local.Count == 0) return Finish("云端还没有数据（本地也是空的）");
                await dav.UploadAsync(SentenceStore.ToJson(SortedForWire(local), _store.DeviceId));
                return Finish("已上传到云端（首次同步）");
            }

            var remote = SentenceStore.Parse(remoteJson) ?? new List<SentenceList>();
            var merged = Merge(local, remote);

            // 只比 lists，**不要把整个文件拿去比**：文件里的 deviceId 是"最后谁写的"，
            // 两端各不相同，比它会导致一来一回互相覆盖、来回空转。
            var mergedPayload = SentenceStore.ToJsonLists(merged);
            var remotePayload = SentenceStore.ToJsonLists(remote);
            var localPayload = SentenceStore.ToJsonLists(local);

            if (string.Equals(mergedPayload, remotePayload, StringComparison.Ordinal))
            {
                if (!string.Equals(mergedPayload, localPayload, StringComparison.Ordinal))
                    _store.ReplaceAll(merged);
                return Finish("已是最新（从云端取回改动）");
            }

            await dav.UploadAsync(SentenceStore.ToJson(merged, _store.DeviceId));
            _store.ReplaceAll(merged);
            return Finish("同步完成");
        }
        catch (Exception ex)
        {
            Trace.Log($"sync failed: {ex}");
            return Finish("同步失败：" + ex.Message);
        }
        finally
        {
            _running = false;
            _dirty = false;
            _debounce.Stop();
        }
    }

    private string Finish(string msg)
    {
        _cfg.Sync.LastSyncAt = SentenceStore.Now();
        _cfg.Save();
        Trace.Log($"sync: {msg}");
        Status?.Invoke(msg);
        return msg;
    }

    // ===== 合并（知识库《多端同步规格》§3） =====

    /// <summary>写出去之前固定按 uuid 升序（列表层 + 条目层），保证 wire format 字节级确定</summary>
    private static List<SentenceList> SortedForWire(IEnumerable<SentenceList> lists) =>
        lists.OrderBy(l => l.Uuid, StringComparer.Ordinal)
            .Select(l =>
            {
                var c = CloneList(l);
                c.Items = l.Items.OrderBy(i => i.Uuid, StringComparer.Ordinal).ToList();
                return c;
            })
            .ToList();

    /// <summary>
    /// 按 uuid 对齐合并。**幂等**，且结果固定按 uuid 升序：
    /// 如果按"我这边原来的顺序"排，两端会各自认为跟对方不一样，
    /// 轮流把同样的数据按自己的顺序重写一遍，永远在空转。
    /// </summary>
    public static List<SentenceList> Merge(List<SentenceList> mine, List<SentenceList> theirs)
    {
        var result = new List<SentenceList>();

        var uuids = mine.Concat(theirs).Select(x => x.Uuid)
            .Where(u => u.Length > 0).Distinct().OrderBy(u => u, StringComparer.Ordinal);
        foreach (var uuid in uuids)
        {
            var m = mine.FirstOrDefault(x => x.Uuid == uuid);
            var t = theirs.FirstOrDefault(x => x.Uuid == uuid);

            SentenceList winner;
            if (m == null) winner = CloneList(t);
            else if (t == null) winner = CloneList(m);
            else winner = Newer(m, t) ? CloneList(m) : CloneList(t);

            // 条目要合并**两边**的，不能只留胜者那边的：
            // 你在电脑上加了 A、手机上加了 B，两边都得在
            var items = MergeItems(m?.Items ?? new List<Sentence>(), t?.Items ?? new List<Sentence>());
            winner.Items = items;
            winner.UpdatedAt = Math.Max(winner.UpdatedAt, items.Count == 0 ? 0 : items.Max(i => i.UpdatedAt));

            result.Add(winner);
        }

        return result;
    }

    private static List<Sentence> MergeItems(List<Sentence> mine, List<Sentence> theirs)
    {
        var result = new List<Sentence>();

        var uuids = mine.Concat(theirs).Select(x => x.Uuid)
            .Where(u => u.Length > 0).Distinct().OrderBy(u => u, StringComparer.Ordinal);
        foreach (var uuid in uuids)
        {
            var m = mine.FirstOrDefault(x => x.Uuid == uuid);
            var t = theirs.FirstOrDefault(x => x.Uuid == uuid);

            if (m == null) result.Add(CloneSentence(t));
            else if (t == null) result.Add(CloneSentence(m));
            else result.Add(Newer(m, t) ? CloneSentence(m) : CloneSentence(t));
        }

        return result;
    }

    /// <summary>
    /// 时间戳完全相同时的定序键。**两端必须逐字一致**：Android 端用
    /// `text + "|" + source + "|" + (deleted ? "1" : "0")`，并挑**较大**的那个。
    /// 否则同一毫秒两端各改一次会各选各的、永远收敛不了。
    /// </summary>
    public static string TieKeyList(SentenceList x) => x.Name + "|" + (x.Deleted ? "1" : "0");

    public static string TieKeySentence(Sentence x) =>
        x.Text + "|" + x.Source + "|" + (x.Deleted ? "1" : "0");

    /// <summary>最后写入者胜；时间戳完全一样时按上面的键挑较大者</summary>
    private static bool Newer(SentenceList a, SentenceList b)
    {
        if (a.UpdatedAt != b.UpdatedAt) return a.UpdatedAt > b.UpdatedAt;
        return string.CompareOrdinal(TieKeyList(a), TieKeyList(b)) >= 0;
    }

    private static bool Newer(Sentence a, Sentence b)
    {
        if (a.UpdatedAt != b.UpdatedAt) return a.UpdatedAt > b.UpdatedAt;
        return string.CompareOrdinal(TieKeySentence(a), TieKeySentence(b)) >= 0;
    }

    private static SentenceList CloneList(SentenceList x) => new()
    {
        Uuid = x.Uuid,
        Id = x.Id,
        Name = x.Name,
        UpdatedAt = x.UpdatedAt,
        Deleted = x.Deleted
    };

    private static Sentence CloneSentence(Sentence x) => new()
    {
        Uuid = x.Uuid,
        Id = x.Id,
        Text = x.Text,
        Source = x.Source,
        UpdatedAt = x.UpdatedAt,
        Deleted = x.Deleted
    };
}
