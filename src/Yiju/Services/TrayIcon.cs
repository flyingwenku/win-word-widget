using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// 这三个类型名 WPF 与 WinForms/Drawing 都有，全部走别名，绝不裸 using
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using Yiju.Data;
using Yiju.Helpers;

namespace Yiju.Services;

/// <summary>托盘图标与右键菜单（卡片自己没有标题栏和任务栏按钮，菜单只能放托盘）</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly SentenceStore _store;
    private readonly AppConfig _cfg;
    private readonly Forms.ToolStripMenuItem _bootItem;

    /// <param name="newCard">新建一张卡片（由 App 决定怎么排位置）</param>
    /// <param name="nextAll">所有卡片一起换一句</param>
    /// <param name="activeCard">管理页要编辑的那张卡片（没卡片时会先建一张）</param>
    /// <param name="openSync">打开同步设置</param>
    /// <param name="syncNow">立刻同步一次</param>
    /// <param name="exit">退出整个程序</param>
    public TrayIcon(AppConfig cfg, SentenceStore store, Action newCard, Action nextAll,
        Func<CardConfig> activeCard, Action openSync, Action syncNow, Action exit)
    {
        _store = store;
        _cfg = cfg;

        _icon = new Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "一句",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();

        var manageItem = new Forms.ToolStripMenuItem("管理句子…");
        manageItem.Click += (_, __) => ManageWindow.ShowOrActivate(activeCard(), _store);
        menu.Items.Add(manageItem);

        var newCardItem = new Forms.ToolStripMenuItem("新建卡片");
        newCardItem.Click += (_, __) => newCard();
        menu.Items.Add(newCardItem);

        var nextItem = new Forms.ToolStripMenuItem("所有卡片换一句");
        nextItem.Click += (_, __) => nextAll();
        menu.Items.Add(nextItem);

        menu.Items.Add(SchemeMenu(_cfg));

        menu.Items.Add(new Forms.ToolStripSeparator());

        var syncNowItem = new Forms.ToolStripMenuItem("立即同步");
        syncNowItem.Click += (_, __) => syncNow();
        menu.Items.Add(syncNowItem);

        var syncItem = new Forms.ToolStripMenuItem("同步设置…");
        syncItem.Click += (_, __) => openSync();
        menu.Items.Add(syncItem);

        _bootItem = new Forms.ToolStripMenuItem("开机自启") { CheckOnClick = true };
        _bootItem.Checked = AutoStart.IsEnabled;
        _bootItem.CheckedChanged += (_, __) =>
        {
            AutoStart.Set(_bootItem.Checked);
            _cfg.StartOnBoot = _bootItem.Checked;
            _cfg.Save();
        };
        menu.Items.Add(_bootItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, __) => exit();
        menu.Items.Add(exitItem);

        _icon.ContextMenuStrip = menu;
        // 左键也弹菜单（托盘图标默认只有右键弹，很多人会以为点了没反应）
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                var mi = typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                mi?.Invoke(_icon, null);
            }
        };
    }

    /// <summary>配色方案子菜单：全局切换，所有卡片统一</summary>
    private static Forms.ToolStripMenuItem SchemeMenu(AppConfig cfg)
    {
        var item = new Forms.ToolStripMenuItem("配色方案");

        for (var i = 0; i < Palette.Schemes.Length; i++)
        {
            var idx = i;
            var mi = new Forms.ToolStripMenuItem(Palette.Schemes[idx].Name)
            {
                Checked = cfg.ColorScheme == idx
            };
            mi.Click += (_, __) => CardWindow.SetScheme(idx);
            item.DropDownItems.Add(mi);
        }

        return item;
    }

    /// <summary>
    /// 图标用代码画，不塞二进制 ico 文件：
    /// 深色圆角方块 + 三道白色横线（就是"一张卡片"的意思）
    /// </summary>
    private static Drawing.Icon MakeIcon()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), null,
                new Rect(2, 2, 60, 60), 12, 12);

            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0xF4, 0xF3)), 4);
            dc.DrawLine(pen, new Point(14, 22), new Point(50, 22));
            dc.DrawLine(pen, new Point(14, 34), new Point(42, 34));
            dc.DrawLine(pen, new Point(14, 46), new Point(46, 46));
        }

        var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;

        using var bitmap = new Drawing.Bitmap(ms);
        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
