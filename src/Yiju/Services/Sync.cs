using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Yiju.Data;
using Yiju.Helpers;

namespace Yiju.Services;

/// <summary>
/// 多端同步（坚果云 WebDAV 单文件），规则见 docs/sync-spec.md。
///
/// 流程：拉远端 → 按 uuid 合并 → 带 ETag 乐观锁写回；412 说明别人刚写过，重来（最多 3 次）。
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

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await dav.EnsureDirAsync();

                // 存在性只看 GET 是不是 404；ETag 只是可选的乐观锁，没有也能同步
                // （合并本身是幂等的，最坏情况不过是各端多写一次，不会丢数据）
                var (exists, etag, remoteJson) = await dav.FetchAsync();

                // 远端还没有 → 把本地这份直接放上去
                if (!exists)
                {
                    if (!await dav.UploadAsync(_store.Serialize(), null)) continue;
                    return Finish("已上传到云端（首次同步）");
                }

                var remote = SentenceStore.Parse(remoteJson) ?? new List<SentenceList>();
                var merged = Merge(_store.AllIncludingDeleted.ToList(), remote);
                var mergedJson = SentenceStore.ToJson(merged, _store.DeviceId);

                // 合并结果跟远端一样 → 没必要写远端（也避免"同步触发保存再同步"的空转）
                if (string.Equals(mergedJson, remoteJson, StringComparison.Ordinal))
                {
                    var localJson = _store.Serialize();
                    if (!string.Equals(mergedJson, localJson, StringComparison.Ordinal))
                        _store.ReplaceAll(merged);
                    return Finish("已是最新（从云端取回改动）");
                }

                if (!await dav.UploadAsync(mergedJson, etag)) continue;   // 412：别人刚写过，重来

                _store.ReplaceAll(merged);
                return Finish($"同步完成（第 {attempt} 次尝试）");
            }

            return Finish("同步失败：云端一直被别的一端改写，稍后再试");
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

    // ===== 合并（docs/sync-spec.md §3） =====

    /// <summary>按 uuid 对齐合并。幂等：两台设备各跑一次，结果相同</summary>
    public static List<SentenceList> Merge(List<SentenceList> mine, List<SentenceList> theirs)
    {
        var result = new List<SentenceList>();

        foreach (var uuid in mine.Concat(theirs).Select(x => x.Uuid).Where(u => u.Length > 0).Distinct())
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

        foreach (var uuid in mine.Concat(theirs).Select(x => x.Uuid).Where(u => u.Length > 0).Distinct())
        {
            var m = mine.FirstOrDefault(x => x.Uuid == uuid);
            var t = theirs.FirstOrDefault(x => x.Uuid == uuid);

            if (m == null) result.Add(CloneSentence(t));
            else if (t == null) result.Add(CloneSentence(m));
            else result.Add(Newer(m, t) ? CloneSentence(m) : CloneSentence(t));
        }

        return result;
    }

    /// <summary>最后写入者胜；时间戳完全一样时按内容定序，保证两端收敛到同一结果</summary>
    private static bool Newer(SentenceList a, SentenceList b)
    {
        if (a.UpdatedAt != b.UpdatedAt) return a.UpdatedAt > b.UpdatedAt;
        return string.CompareOrdinal(a.Name, b.Name) <= 0;
    }

    private static bool Newer(Sentence a, Sentence b)
    {
        if (a.UpdatedAt != b.UpdatedAt) return a.UpdatedAt > b.UpdatedAt;
        return string.CompareOrdinal(a.Text + "|" + a.Source, b.Text + "|" + b.Source) <= 0;
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
