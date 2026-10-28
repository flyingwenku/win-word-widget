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
    private readonly CardWindow _card;
    private readonly SentenceStore _store;
    private readonly AppConfig _cfg;
    private readonly Forms.ToolStripMenuItem _bootItem;

    public TrayIcon(CardWindow card, SentenceStore store, AppConfig cfg)
    {
        _card = card;
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
        manageItem.Click += (_, __) => ManageWindow.ShowOrActivate(_card, _store, _cfg);
        menu.Items.Add(manageItem);

        var nextItem = new Forms.ToolStripMenuItem("换一句");
        nextItem.Click += (_, __) => _card.NextSentence();
        menu.Items.Add(nextItem);

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
        exitItem.Click += (_, __) => Wpf.Application.Current.Shutdown();
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
