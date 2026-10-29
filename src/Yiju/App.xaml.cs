using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using Yiju.Data;
using Yiju.Helpers;
using Yiju.Services;
using Application = System.Windows.Application;

namespace Yiju;

public partial class App : Application
{
    private const string MutexName = "Yiju.SingleInstance.v2";

    private Mutex _mutex;
    private TrayIcon _tray;
    private AppConfig _cfg;
    private SentenceStore _store;
    private SyncService _sync;
    private readonly List<CardWindow> _cards = new();

    /// <summary>正在退出整个程序（这时卡片关掉不该被当成"用户删掉了这张卡片"）</summary>
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：再启动一次会弹出空卡片，且位置配置会互相覆盖
        _mutex = new Mutex(true, MutexName, out var created);

        // 互斥体可能被"没退干净的实例"留下的句柄占着：实测出现过——
        // 那个实例窗口都没了，句柄却不放，于是之后每次启动都被判成"已经在运行"。
        // 所以：拿不到互斥体时，再看看是不是真有另一个 Yiju 进程；没有就当它是陈旧的，直接接管。
        if (!created && !OtherInstanceAlive()) created = true;

        if (!created)
        {
            MessageBox.Show("「一句」已经在运行了 —— 看看桌面上的卡片，或右下角托盘图标。", "一句");
            // 必须硬退出：只调 Shutdown() 进程会变成"没有窗口的僵尸"还赖着不走，
            // 而它仍握着互斥体的句柄 —— 之后每次启动都会被判定成"已经在运行"，
            // 弹框 → 关掉 → 又留一个僵尸，无限套下去。
            Shutdown();
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 埋点日志要在一上来就清 —— 放在卡片里清的话，
        // 会把句子库加载/迁移这些更早写的日志一起擦掉（踩过一次）
        Yiju.Helpers.Trace.Reset();

        Paths.Ensure();

        _store = new SentenceStore(Paths.ListsFile);
        _cfg = AppConfig.Load(Paths.ConfigFile);
        // 句子库已经升级成 uuid 引用了，旧配置里那些数字 id 到这一步才翻译得动
        _cfg.MigrateReferences(_store);

        // 手机端加了好几条内容、同步过来之后，卡片得跟着重画
        _store.Changed += () => CardWindow.RefreshAll();

        // 手机上能加好几个桌面小部件，这边也一样：配置里有几张就开几张
        foreach (var cc in _cfg.Cards.ToList()) OpenCard(cc);
        if (_cards.Count == 0) NewCard();

        Yiju.Helpers.Trace.Log($"cfg file={Paths.ConfigFile} cards={_cfg.Cards.Count} " +
                  $"legacy=({_cfg.X},{_cfg.Y},{_cfg.W},{_cfg.H}) " +
                  $"first=({_cfg.Cards[0].X},{_cfg.Cards[0].Y},{_cfg.Cards[0].W},{_cfg.Cards[0].H})");

        _sync = new SyncService(_cfg, _store);
        _sync.Start();

        _tray = new TrayIcon(_cfg, _store, () => NewCard(), NextAll, ActiveCard,
            () => SyncWindow.ShowOrActivate(_cfg, _sync), () => _ = _sync.SyncNow(), ExitApp);

        // 之前用户勾了开机自启但 exe 换过位置 → 顺手把注册表里的路径更新掉
        if (_cfg.StartOnBoot && !AutoStart.IsEnabled) AutoStart.Set(true);
    }

    // ===== 多卡片 =====

    private CardWindow OpenCard(CardConfig cc)
    {
        var w = new CardWindow(_cfg, cc, _store);
        w.Closed += (_, __) =>
        {
            _cards.Remove(w);

            // 用户单独关掉一张 → 从配置里删掉，下次启动不会再冒出来；
            // 整个程序退出时卡片也会关，那时不能删，否则下次启动位置全丢了
            if (!_shuttingDown)
            {
                _cfg.Cards.Remove(w.Config);
                _cfg.Save();
            }
        };

        _cards.Add(w);
        w.Show();
        return w;
    }

    /// <summary>再加一张卡片：沿对角线找第一个不压住别人的位置</summary>
    private CardWindow NewCard()
    {
        var cc = new CardConfig { Id = _cfg.NewCardId(), Owner = _cfg };
        (cc.X, cc.Y) = FindSpot(cc.W, cc.H);

        _cfg.Cards.Add(cc);
        var w = OpenCard(cc);
        _cfg.Save();
        return w;
    }

    private (double, double) FindSpot(double w, double h)
    {
        var maxX = Math.Max(0, SystemParameters.PrimaryScreenWidth - w - 20);
        var maxY = Math.Max(0, SystemParameters.PrimaryScreenHeight - h - 20);

        for (var k = 0; k < 40; k++)
        {
            var x = Math.Min(140 + k * 28, maxX);
            var y = Math.Min(140 + k * 28, maxY);
            if (!Overlaps(x, y, w, h)) return (x, y);
            if (x >= maxX && y >= maxY) break;
        }
        return (140, 140);
    }

    private bool Overlaps(double x, double y, double w, double h)
    {
        const int gap = 12;
        foreach (var c in _cfg.Cards)
        {
            if (x < c.X + c.W + gap && c.X < x + w + gap &&
                y < c.Y + c.H + gap && c.Y < y + h + gap) return true;
        }
        return false;
    }

    private void NextAll()
    {
        foreach (var w in _cards.ToList()) w.NextSentence();
    }

    /// <summary>管理页默认编辑第一张卡片（没卡片就先建一张，不然设置没地方落）</summary>
    private CardConfig ActiveCard() => _cards.Count == 0 ? NewCard().Config : _cards[0].Config;

    private void ExitApp()
    {
        _shuttingDown = true;
        Shutdown();
    }

    /// <summary>除了自己以外，还有没有另一个“一句”进程</summary>
    private static bool OtherInstanceAlive()
    {
        try
        {
            var me = Process.GetCurrentProcess().Id;
            return Process.GetProcessesByName("Yiju")
                .Any(p => p.Id != me);
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
