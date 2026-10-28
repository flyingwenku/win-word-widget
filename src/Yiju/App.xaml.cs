using System.Threading;
using System.Windows;
using Yiju.Data;
using Yiju.Helpers;
using Yiju.Services;
using Application = System.Windows.Application;

namespace Yiju;

public partial class App : Application
{
    private Mutex _mutex;
    private TrayIcon _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：再启动一次会弹出空卡片，且位置配置会互相覆盖
        _mutex = new Mutex(true, "Yiju.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("「一句」已经在运行了 —— 看看桌面上的卡片，或右下角托盘图标。", "一句");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Paths.Ensure();

        var store = new SentenceStore(Paths.ListsFile);
        var cfg = AppConfig.Load(Paths.ConfigFile);

        var card = new CardWindow(cfg, store);
        card.Show();

        _tray = new TrayIcon(card, store, cfg);

        // 之前用户勾了开机自启但 exe 换过位置 → 顺手把注册表里的路径更新掉
        if (cfg.StartOnBoot && !AutoStart.IsEnabled) AutoStart.Set(true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
