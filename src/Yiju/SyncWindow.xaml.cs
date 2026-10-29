using System;
using System.Windows;
using Yiju.Data;
using Yiju.Services;

namespace Yiju;

/// <summary>
/// 同步设置：填坚果云账号、开自动同步、手动立刻同步一次。
/// 只开一个（点两次托盘菜单不该弹出两个）。
/// </summary>
public partial class SyncWindow : Window
{
    private static SyncWindow _instance;

    private readonly AppConfig _cfg;
    private readonly SyncService _sync;

    public SyncWindow(AppConfig cfg, SyncService sync)
    {
        InitializeComponent();

        _cfg = cfg;
        _sync = sync;

        TbUrl.Text = cfg.Sync.Url;
        TbUser.Text = cfg.Sync.User;
        TbPass.Password = cfg.Sync.Password;
        CbAuto.IsChecked = cfg.Sync.AutoSync;

        _sync.Status = ShowStatus;
        ShowStatus(cfg.Sync.LastSyncAt == 0 ? "还没同步过" : LastText(cfg.Sync.LastSyncAt));
    }

    public static void ShowOrActivate(AppConfig cfg, SyncService sync)
    {
        if (_instance == null)
        {
            _instance = new SyncWindow(cfg, sync);
            _instance.Closed += (_, __) => { _instance = null; sync.Status = null; };
            _instance.Show();
            return;
        }

        if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
        _instance.Activate();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Sync.Url = TbUrl.Text.Trim();
        _cfg.Sync.User = TbUser.Text.Trim();
        _cfg.Sync.Password = TbPass.Password;
        _cfg.Sync.AutoSync = CbAuto.IsChecked == true;
        _cfg.Save();

        ShowStatus(_cfg.Sync.Ready ? "已保存" : "还差账号或密码，填全了才能同步");
    }

    private async void BtnSync_Click(object sender, RoutedEventArgs e)
    {
        BtnSave_Click(sender, e);
        ShowStatus("正在同步…");
        var msg = await _sync.SyncNow();
        ShowStatus(msg);
    }

    private void ShowStatus(string msg)
    {
        Dispatcher.InvokeAsync(() => LbStatus.Text = msg);
    }

    private static string LastText(long ms) =>
        "上次同步：" + DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("MM-dd HH:mm:ss");
}
