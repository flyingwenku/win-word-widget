using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
// WPF 与 WinForms 都有的类型名一律走别名，绝不裸 using
using Forms = System.Windows.Forms;
using Yiju.Data;
using Yiju.Helpers;
using Yiju.Native;
using Yiju.Services;

namespace Yiju;

/// <summary>
/// 桌面上的那张卡片。
///
/// 它就是一个**普通顶层窗口**：代码里加上 WS_EX_TOOLWINDOW（不进 Alt+Tab、不进任务栏），
/// 再压到 HWND_BOTTOM（永远待在所有应用窗口下面）—— 这就是"贴桌面"的实现。
///
/// 试过另一种做法：跨进程 SetParent 到 Explorer 的 WorkerW 那层。代价一连串（详见 docs/display-plan.md）：
///   ・铺满整屏的图标层（SHELLDLL_DefView）吃掉鼠标 → 卡片点不到、拖不动
///   ・想把整个图标层压到我们下面时，SetWindowPos 返回 True 但实际不生效（shell 强制维持层级）
///   ・DWM 合成断层：程序算出来的颜色和屏幕上的像素不一致（同一配置的三张卡显示出三种颜色）
///   ・窗口销毁时桌面不重画 → 退出后屏幕上留下点不掉的鬼影
/// 换成普通顶层窗口之后这些都没了。唯一代价是 Win+D 会把它最小化 —— 靠吞掉 SC_MINIMIZE 解决。
///
/// 字号按容器尺寸自动测量：WPF 能测真实文字宽高，比 Android 端写死分档强得多。
/// </summary>
public partial class CardWindow : Window
{
    private const double PadX = 18;
    private const double PadY = 14;

    private readonly AppConfig _app;
    private readonly CardConfig _card;
    private readonly SentenceStore _store;

    /// <summary>所有活着的卡片：句子库改了要通知到每一张</summary>
    private static readonly List<CardWindow> All = new();

    /// <summary>配色是全局的（所有卡片统一），改一次全跟着变</summary>
    private static AppConfig _sharedApp;

    /// <summary>这张卡片自己的配置（给管理页用）</summary>
    public CardConfig Config => _card;

    public static void RefreshAll()
    {
        foreach (var w in All.ToList()) w.Refresh();
    }

    /// <summary>切换配色方案：全局生效，避免一张黑字一张白字那种"颜色乱了"</summary>
    public static void SetScheme(int index)
    {
        if (_sharedApp == null) return;
        _sharedApp.ColorScheme = index;
        _sharedApp.Save();
        RefreshAll();
    }

    private HwndSource _hwndSource;
    private uint _taskbarCreatedMsg;
    private DispatcherTimer _rotateTimer;
    private DispatcherTimer _saveTimer;
    private DispatcherTimer _traceTimer;

    // 埋点：按消息号统计窗口收到了哪些消息（收不到鼠标消息时这是最直接的证据）
    private readonly Dictionary<int, int> _msgCount = new();

    private bool _resizing;
    private bool _moved;
    private Point _pressPos;
    private Point _pressScreen;
    private double _originLeft, _originTop, _originWidth, _originHeight;

    public CardWindow(AppConfig app, CardConfig card, SentenceStore store)
    {
        InitializeComponent();

        _app = app;
        _card = card;
        _store = store;
        _sharedApp = app;

        Left = card.X;
        Top = card.Y;
        Width = card.W;
        Height = card.H;

        All.Add(this);

        SourceInitialized += (_, __) => OnSourceReady();
        ContentRendered += (_, __) => Render();
        SizeChanged += (_, __) => Render();
        LocationChanged += (_, __) => QueueSave();

        _store.Changed += () => Dispatcher.BeginInvoke(new Action(Render));
    }

    // ===== 置底工具窗口 =====

    private void OnSourceReady()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource.AddHook(WndProc);
        _taskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");

        MakeDesktopToolWindow(hwnd);
        Trace.Log($"ready hwnd=0x{hwnd.ToInt64():X} ex=0x{Win32.GetStyleEx(hwnd).ToInt64():X} bottom={IsBottomMost(hwnd)}");

        // 每秒记一次状态快照：还在最底吗？鼠标这个位置到底命中谁（只在埋点打开时才跑）
        if (Trace.Enabled)
        {
            _traceTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                (_, __) => TraceState(), Dispatcher);
            _traceTimer.Start();
        }

        // 注意：这里刻意不做周期性的强制压底 —— 多张卡片会互相抢最底下那个位置，白折腾。
        // 资源管理器重启之类会重排 Z 序的场合，走 WndProc 里的 TaskbarCreated 处理。

        // 定时换句（Windows 侧没有 Android 那个 30 分钟下限，想多快都行）
        _rotateTimer = new DispatcherTimer(TimeSpan.FromMinutes(Math.Max(1, _card.RefreshMinutes)),
            DispatcherPriority.Background, (_, __) => NextSentence(), Dispatcher);
        _rotateTimer.Start();
    }

    /// <summary>把窗口变成"贴桌面"的工具窗口：不进 Alt+Tab，且永远在最底下</summary>
    private void MakeDesktopToolWindow(IntPtr hwnd)
    {
        var ex = Win32.GetStyleEx(hwnd).ToInt64();

        // WS_EX_TOOLWINDOW：不显示在任务栏，也不出现在 Alt+Tab 里
        Win32.SetStyleEx(hwnd, (IntPtr)(ex | Win32.WS_EX_TOOLWINDOW));

        // 改完扩展样式要让系统重读（SWP_FRAMECHANGED），顺手压到底
        PushToBottom();
    }

    /// <summary>压到 Z 序最底 —— 所有应用窗口都在它上面（"贴桌面"，不是"浮在窗口上"）</summary>
    private void PushToBottom()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || IsBottomMost(hwnd)) return;

        Win32.SetWindowPos(hwnd, Win32.HWND_BOTTOM, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
    }

    private static bool IsBottomMost(IntPtr hwnd) => Win32.GetWindow(hwnd, Win32.GW_HWNDLAST) == hwnd;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 埋点：先计数，再看要不要逐条记
        _msgCount.TryGetValue(msg, out var n);
        _msgCount[msg] = n + 1;
        LogMouseMsg(hwnd, msg, lParam);

        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            // 资源管理器重启过了，Z 序可能被重排
            PushToBottom();
        }
        // 卡片不许最小化：Win+D（显示桌面）会把它一起收起来，
        // 而我们要的是"跟着桌面留着"。直接把这个命令吞掉。
        else if (msg == Win32.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == Win32.SC_MINIMIZE)
        {
            handled = true;
            return IntPtr.Zero;
        }
        // 点击卡片不要激活它：一激活就会被抬到别的窗口上面去
        else if (msg == Win32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return (IntPtr)Win32.MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    // ===== 埋点 =====

    /// <summary>鼠标相关消息记一笔 —— 判断"消息到底有没有送到卡片"的主证据</summary>
    private void LogMouseMsg(IntPtr hwnd, int msg, IntPtr lParam)
    {
        static (int, int) Split(IntPtr l) =>
            ((short)(l.ToInt64() & 0xFFFF), (short)((l.ToInt64() >> 16) & 0xFFFF));

        switch (msg)
        {
            case Win32.WM_NCHITTEST:
            case Win32.WM_SETCURSOR:
                // 收到命中测试，说明系统确实把鼠标"指向"了本窗口（收不到 = 被别的窗口挡着）
                if (Trace.Every("ht" + msg, 1000))
                {
                    var (x, y) = Split(lParam);
                    Trace.Log($"msg 0x{msg:X4} hit/cursor lParam=({x},{y})");
                }
                break;

            case Win32.WM_MOUSEMOVE:
            case Win32.WM_NCMOUSEMOVE:
                if (Trace.Every("mv" + msg, 1000))
                {
                    var (x, y) = Split(lParam);
                    Trace.Log($"msg 0x{msg:X4} mouseMove client=({x},{y})");
                }
                break;

            case Win32.WM_LBUTTONDOWN:
            case Win32.WM_LBUTTONUP:
            case Win32.WM_LBUTTONDBLCLK:
            case Win32.WM_RBUTTONDOWN:
            case Win32.WM_RBUTTONUP:
            {
                var (x, y) = Split(lParam);
                Trace.Log($"msg 0x{msg:X4} BUTTON client=({x},{y})");
                break;
            }

            case Win32.WM_MOUSELEAVE:
                Trace.Log("msg 0x02A3 mouseLeave");
                break;
        }
    }

    /// <summary>每秒一次状态快照：还在最底吗？鼠标这个位置命中的是谁？窗口收到了哪些消息？</summary>
    private void TraceState()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        Win32.GetWindowRect(hwnd, out var r);
        Win32.GetCursorPos(out var cur);

        var center = new Win32.POINT { X = (r.L + r.R) / 2, Y = (r.T + r.B) / 2 };
        var hitC = Win32.WindowFromPoint(center);
        var hitP = Win32.WindowFromPoint(cur);
        var inside = cur.X >= r.L && cur.X <= r.R && cur.Y >= r.T && cur.Y <= r.B;

        Trace.Log(
            $"state hwnd=0x{hwnd.ToInt64():X} bottom={IsBottomMost(hwnd)} " +
            $"ex=0x{Win32.GetStyleEx(hwnd).ToInt64():X} rect=({r.L},{r.T})-({r.R},{r.B}) " +
            $"vis={IsVisible} en={Win32.IsWindowEnabled(hwnd)}");

        Trace.Log(
            $"      hit(center)=0x{hitC.ToInt64():X} cls={Win32.ClassNameOf(hitC)} isUs={hitC == hwnd} | " +
            $"cursor=({cur.X},{cur.Y}) hit(cursor)=0x{hitP.ToInt64():X} cls={Win32.ClassNameOf(hitP)} " +
            $"isUs={hitP == hwnd} insideCard={inside}");

        if (Trace.Every("msgs", 5000))
        {
            var top = _msgCount.OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => $"0x{kv.Key:X4}x{kv.Value}");
            Trace.Log("      msgs: " + string.Join(" ", top));
        }
    }

    // ===== 渲染 =====

    private void Render()
    {
        if (!IsLoaded) return;

        // 配色：固定方案表，不再读壁纸（Windows Spotlight 轮换会让颜色毫无征兆跳变）。
        // 半透明写在**背景色的 alpha** 里 —— 文字永远完全不透明，保证可读。
        var scheme = Palette.For(_app.ColorScheme);
        Trace.Log($"render card#{_card.Id} scheme={_app.ColorScheme}({scheme.Name}) bg={scheme.Bg} text={scheme.Text}");

        Card.Background = new SolidColorBrush(scheme.Bg);
        TbSentence.Foreground = new SolidColorBrush(scheme.Text);
        TbSource.Foreground = new SolidColorBrush(Color.FromArgb(0xB3, scheme.Text.R, scheme.Text.G, scheme.Text.B));

        var s = Picker.Current(_card, _store);
        if (s == null)
        {
            TbSentence.Text = "还没有句子 —— 右键托盘图标 → 管理句子";
            TbSource.Visibility = Visibility.Collapsed;
            TbSentence.FontSize = 14;
            return;
        }

        TbSentence.Text = s.Text;
        if (string.IsNullOrWhiteSpace(s.Source))
        {
            TbSource.Visibility = Visibility.Collapsed;
        }
        else
        {
            TbSource.Visibility = Visibility.Visible;
            TbSource.Text = s.Source;
        }

        FitText();
    }

    /// <summary>
    /// 自动选字号：从大到小试，算出能装下的最大字号（顺带定最多几行）。
    /// 这正是 WPF 比 RemoteViews 强的地方 —— 我们能量出真实文字宽高，不用写死分档。
    /// </summary>
    private void FitText()
    {
        if (TbSentence.Text.Length == 0) return;

        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var availWidth = Math.Max(40, ActualWidth - PadX * 2);
        var availHeight = Math.Max(30, ActualHeight - PadY * 2 - (TbSource.Visibility == Visibility.Visible ? 26 : 0));

        var typeface = new Typeface(TbSentence.FontFamily, TbSentence.FontStyle,
            TbSentence.FontWeight, TbSentence.FontStretch);

        for (var size = 34.0; size >= 11; size -= 0.5)
        {
            var maxLines = Math.Clamp((int)Math.Floor(availHeight / (size * 1.55)), 1, 3);
            var ft = new FormattedText(
                TbSentence.Text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                Brushes.Black,
                ppd)
            {
                MaxTextWidth = availWidth,
                MaxLineCount = maxLines,
                Trimming = TextTrimming.CharacterEllipsis
            };

            if (ft.Height <= availHeight)
            {
                TbSentence.FontSize = size;
                TbSource.FontSize = Math.Round(size * 0.6);
                return;
            }
        }

        // 再小也装不下就取最小号（极端窄卡片）
        TbSentence.FontSize = 11;
        TbSource.FontSize = 8;
    }

    // ===== 交互：拖动 / 缩放 / 点击换句 =====

    protected override void OnPreviewMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        Trace.Log($"WPF down src={e.OriginalSource} pos={e.GetPosition(this)}");
        _pressPos = e.GetPosition(this);
        _pressScreen = PointToScreen(_pressPos);
        _resizing = _pressPos.X >= ActualWidth - 24 && _pressPos.Y >= ActualHeight - 24;
        _originLeft = Left;
        _originTop = Top;
        _originWidth = ActualWidth;
        _originHeight = ActualHeight;
        _moved = false;

        CaptureMouse();
        Trace.Log($"     resizing={_resizing} captured={IsMouseCaptured}");
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (Trace.Every("wpfmove", 1000))
            Trace.Log($"WPF move captured={IsMouseCaptured} btn={e.LeftButton} pos={e.GetPosition(this)}");

        if (!IsMouseCaptured) return;

        var pos = e.GetPosition(this);

        if (_resizing)
        {
            Width = Math.Max(160, _originWidth + (pos.X - _pressPos.X));
            Height = Math.Max(70, _originHeight + (pos.Y - _pressPos.Y));
            return;
        }

        var screen = PointToScreen(pos);
        if (Math.Abs(screen.X - _pressScreen.X) > 3 || Math.Abs(screen.Y - _pressScreen.Y) > 3)
        {
            _moved = true;
            Left = _originLeft + (screen.X - _pressScreen.X);
            Top = _originTop + (screen.Y - _pressScreen.Y);
        }

        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            Trace.Log($"WPF up moved={_moved} resizing={_resizing}");

            // 没移动过 = 是一次点击 = 换一句（与 Android 端一致的交互）
            if (!_moved && !_resizing) NextSentence();
            else QueueSave();
        }

        base.OnPreviewMouseLeftButtonUp(e);
    }

    public void NextSentence()
    {
        Trace.Log("NextSentence");
        Picker.Next(_card, _store);
        Render();
    }

    /// <summary>内容或配置变了，让卡片重画一次</summary>
    public void Refresh() => Render();

    // ===== 右键菜单 =====

    protected override void OnPreviewMouseRightButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        // 卡片没有标题栏也没有任务栏按钮，设置只能走右键。
        // 菜单用 WinForms 那套（和托盘一致），省得 WPF Popup 再开一个顶层窗口。
        e.Handled = true;
        ShowContextMenu();
        base.OnPreviewMouseRightButtonUp(e);
    }

    private void ShowContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();

        var next = new Forms.ToolStripMenuItem("换一句");
        next.Click += (_, __) => NextSentence();
        menu.Items.Add(next);

        menu.Items.Add(SchemeMenu());

        var manage = new Forms.ToolStripMenuItem("卡片设置…");
        manage.Click += (_, __) => ManageWindow.ShowOrActivate(_card, _store);
        menu.Items.Add(manage);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var close = new Forms.ToolStripMenuItem("关闭这张卡片");
        close.Click += (_, __) => Close();
        menu.Items.Add(close);

        menu.Show(Forms.Control.MousePosition);
    }

    /// <summary>配色方案子菜单：全局切换（所有卡片统一）</summary>
    private Forms.ToolStripMenuItem SchemeMenu()
    {
        var item = new Forms.ToolStripMenuItem("配色方案");

        for (var i = 0; i < Palette.Schemes.Length; i++)
        {
            var idx = i;
            var mi = new Forms.ToolStripMenuItem(Palette.Schemes[idx].Name)
            {
                Checked = _app.ColorScheme == idx
            };
            mi.Click += (_, __) => SetScheme(idx);
            item.DropDownItems.Add(mi);
        }

        return item;
    }

    // ===== 位置持久化 =====

    private void QueueSave()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
            (_, __) => { _saveTimer.Stop(); SaveGeometry(); }, Dispatcher);

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveGeometry()
    {
        var limitW = Math.Max(0, SystemParameters.PrimaryScreenWidth - ActualWidth);
        var limitH = Math.Max(0, SystemParameters.PrimaryScreenHeight - ActualHeight);

        _card.X = Math.Clamp(Left, 0, limitW);
        _card.Y = Math.Clamp(Top, 0, limitH);
        _card.W = ActualWidth;
        _card.H = ActualHeight;
        _card.Save();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveGeometry();

        _traceTimer?.Stop();
        _rotateTimer?.Stop();
        _saveTimer?.Stop();

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        All.Remove(this);
        if (All.Count == 0) _sharedApp = null;
        _hwndSource?.RemoveHook(WndProc);
        base.OnClosed(e);
    }
}
