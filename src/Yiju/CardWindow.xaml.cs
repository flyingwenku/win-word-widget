using System;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Yiju.Data;
using Yiju.Native;
using Yiju.Services;

namespace Yiju;

/// <summary>
/// 桌面上的那张卡片。
///
/// 三件关键事：
///   1) 挂到"壁纸与桌面图标之间"那一层（24H2 前后结构不同，两种都要认）
///   2) 资源管理器重启后要能自己挂回去（收 TaskbarCreated + 定时检查）
///   3) 字号按容器尺寸自动测量 —— WPF 能测真实文字宽高，比 Android 端写死分档强得多
/// </summary>
public partial class CardWindow : Window
{
    private const double CornerRadius = 14;
    private const double PadX = 18;
    private const double PadY = 14;

    private readonly AppConfig _cfg;
    private readonly SentenceStore _store;

    private IntPtr _workerw = IntPtr.Zero;
    private HwndSource _hwndSource;
    private uint _taskbarCreatedMsg;
    private DispatcherTimer _attachTimer;
    private DispatcherTimer _rotateTimer;
    private DispatcherTimer _wallpaperTimer;
    private DispatcherTimer _saveTimer;

    private bool _resizing;
    private bool _moved;
    private Point _pressPos;
    private Point _pressScreen;
    private double _originLeft, _originTop, _originWidth, _originHeight;

    public CardWindow(AppConfig cfg, SentenceStore store)
    {
        InitializeComponent();

        _cfg = cfg;
        _store = store;

        Left = cfg.X;
        Top = cfg.Y;
        Width = cfg.W;
        Height = cfg.H;

        SourceInitialized += (_, __) => OnSourceReady();
        ContentRendered += (_, __) => { ApplyRegion(); Render(); };
        SizeChanged += (_, __) => { ApplyRegion(); Render(); };
        LocationChanged += (_, __) => QueueSave();

        _store.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyRegion();
            Render();
        }));
    }

    // ===== 挂载到壁纸层 =====

    private void OnSourceReady()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource.AddHook(WndProc);
        _taskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");

        Attach();

        // 资源管理器可能被重启/崩溃，5 秒查一次挂在不在
        _attachTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, __) => EnsureAttached(), Dispatcher);
        _attachTimer.Start();

        // 定时换句（Windows 侧没有 Android 那个 30 分钟下限，想多快都行）
        _rotateTimer = new DispatcherTimer(TimeSpan.FromMinutes(Math.Max(1, _cfg.RefreshMinutes)),
            DispatcherPriority.Background, (_, __) => NextSentence(), Dispatcher);
        _rotateTimer.Start();

        // 壁纸轮播变化时重新取色
        _wallpaperTimer = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background,
            (_, __) => { if (Palette.WallpaperChanged()) { Palette.Refresh(); Render(); } }, Dispatcher);
        _wallpaperTimer.Start();
    }

    private void Attach()
    {
        var host = DesktopHost.FindWallpaperHost();
        if (host == IntPtr.Zero) return;

        _workerw = host;
        DesktopHost.Attach(new WindowInteropHelper(this).Handle, host);
        ApplyRegion();
    }

    private void EnsureAttached()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_workerw == IntPtr.Zero || !Win32.IsWindow(_workerw))
        {
            Attach();
            return;
        }

        // 两种情况都要整个重挂：父窗口丢了（Explorer 重启），
        // 或者 WPF 自己把窗口风格改回了 WS_POPUP（改窗口状态时会重写风格）
        var parentOk = Win32.GetParent(hwnd) == _workerw;
        var styleOk = (Win32.GetStyle(hwnd).ToInt64() & Win32.WS_CHILD) != 0;
        if (!parentOk || !styleOk) Attach();
    }

    private void ApplyRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || ActualWidth < 10 || ActualHeight < 10) return;

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        DesktopHost.ApplyRoundCorners(hwnd, ActualWidth, ActualHeight, CornerRadius, dpi);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 资源管理器重启 → 桌面层被重建，必须重新挂
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            Attach();
        }
        // 改壁纸时系统发这条 → 重新取色
        else if (msg == Win32.WM_SETTINGCHANGE)
        {
            Palette.Refresh();
            Render();
        }
        return IntPtr.Zero;
    }

    // ===== 渲染 =====

    private void Render()
    {
        if (!IsLoaded) return;

        var (bg, text) = Palette.Get();
        Card.Background = new SolidColorBrush(bg);
        TbSentence.Foreground = new SolidColorBrush(text);
        TbSource.Foreground = new SolidColorBrush(Color.FromArgb(0xB3, text.R, text.G, text.B));

        var s = Picker.Current(_cfg, _store);
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
        _pressPos = e.GetPosition(this);
        _pressScreen = PointToScreen(_pressPos);
        _resizing = _pressPos.X >= ActualWidth - 24 && _pressPos.Y >= ActualHeight - 24;
        _originLeft = Left;
        _originTop = Top;
        _originWidth = ActualWidth;
        _originHeight = ActualHeight;
        _moved = false;

        CaptureMouse();
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(System.Windows.Input.MouseEventArgs e)
    {
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

            // 没移动过 = 是一次点击 = 换一句（与 Android 端一致的交互）
            if (!_moved && !_resizing) NextSentence();
            else QueueSave();
        }

        base.OnPreviewMouseLeftButtonUp(e);
    }

    public void NextSentence()
    {
        Picker.Next(_cfg, _store);
        Render();
    }

    /// <summary>内容或配置变了，让卡片重画一次</summary>
    public void Refresh()
    {
        ApplyRegion();
        Render();
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
        // 卡片是壁纸层的子窗口，坐标只在主屏范围内有效 —— 越界会被裁掉
        var limitW = Math.Max(0, SystemParameters.PrimaryScreenWidth - ActualWidth);
        var limitH = Math.Max(0, SystemParameters.PrimaryScreenHeight - ActualHeight);

        _cfg.X = Math.Clamp(Left, 0, limitW);
        _cfg.Y = Math.Clamp(Top, 0, limitH);
        _cfg.W = ActualWidth;
        _cfg.H = ActualHeight;
        _cfg.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveGeometry();
        _hwndSource?.RemoveHook(WndProc);
        base.OnClosed(e);
    }
}
