using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yiju.Services;

/// <summary>
/// 配色：卡片底色取壁纸主色，但**亮度被推到与文字相反的一端**（0.85 / 0.15），
/// 色相跟着壁纸走、明暗我们自己保证 —— 与 Android 端 v1.1 用同一套阈值，两端观感才一致。
///
/// Windows 上没有 Android 那样的 getWallpaperColors() 官方取色接口，要自己读：
///   SPI_GETDESKWALLPAPER 返回的是缓存副本（%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper，
///   没有扩展名，内容是 JPEG），轮播时返回的也是它 —— 正好，我们要的就是"实际显示的那张"。
/// </summary>
internal static class Palette
{
    private const uint SPI_GETDESKWALLPAPER = 0x0073;

    // 卡片亮度目标：亮壁纸 → 卡片推到 0.85 + 深字；暗壁纸 → 推到 0.15 + 浅字
    private const double LightTarget = 0.85;
    private const double DarkTarget = 0.15;

    private static readonly Color PaperBg = Color.FromRgb(0xB8, 0xB6, 0xB5);
    private static readonly Color PaperText = Color.FromRgb(0x2B, 0x2B, 0x2B);
    private static readonly Color DarkText = Color.FromRgb(0x1F, 0x1F, 0x1E);
    private static readonly Color LightText = Color.FromRgb(0xFA, 0xF8, 0xF6);

    private static Color? _wallpaper;
    private static DateTime _stamp;

    /// <summary>取色结果（卡片底色，文字色）</summary>
    public static (Color Bg, Color Text) Get()
    {
        var wall = WallpaperColor();
        if (wall == null) return (PaperBg, PaperText);

        var darkText = Luminance(wall.Value) > 0.5;
        return (ToLuminance(wall.Value, darkText ? LightTarget : DarkTarget),
                darkText ? DarkText : LightText);
    }

    /// <summary>壁纸变了就重新取（卡片自己会定时调它）</summary>
    public static void Refresh() => _wallpaper = null;

    private static Color? WallpaperColor()
    {
        if (_wallpaper != null) return _wallpaper;

        var path = CurrentWallpaperPath();
        if (path == null) return null;

        try
        {
            // 系统写入时也会打开这个文件，必须允许共享读，否则读不到
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            // 缩到 8x8 再取平均色（1x1 会变成只采到一个点，不可靠）
            const int n = 8;
            var scaled = new TransformedBitmap(frame,
                new ScaleTransform((double)n / frame.PixelWidth, (double)n / frame.PixelHeight));
            var bgr = new FormatConvertedBitmap(scaled, PixelFormats.Bgr24, null, 0);

            var stride = n * 3;
            var pixels = new byte[stride * n];
            bgr.CopyPixels(pixels, stride, 0);

            long r = 0, g = 0, b = 0;
            for (var i = 0; i < pixels.Length; i += 3)
            {
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
            }
            var count = n * n;
            _wallpaper = Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
            _stamp = File.GetLastWriteTimeUtc(path);
            return _wallpaper;
        }
        catch
        {
            return null;
        }
    }

    private static string CurrentWallpaperPath()
    {
        // 优先用系统接口（纯色壁纸时它会返回空串，那时再回退）
        var sb = new StringBuilder(260);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && sb.Length > 0)
        {
            var p = sb.ToString();
            if (File.Exists(p)) return p;
        }

        // 缓存副本：轮播时它也会跟着更新，读它就行，不用去解析 slideshow.ini
        var themes = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes");

        var transcoded = Path.Combine(themes, "TranscodedWallpaper");
        if (File.Exists(transcoded)) return transcoded;

        try
        {
            var cached = Path.Combine(themes, "CachedFiles");
            if (Directory.Exists(cached))
            {
                var newest = Directory.GetFiles(cached)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest != null) return newest;
            }
        }
        catch
        {
            // 读不到就算了，回退纸色
        }
        return null;
    }

    /// <summary>壁纸文件是否被更新过（供卡片定时判断要不要重新取色）</summary>
    public static bool WallpaperChanged()
    {
        var path = CurrentWallpaperPath();
        if (path == null) return false;
        try
        {
            return File.GetLastWriteTimeUtc(path) > _stamp;
        }
        catch
        {
            return false;
        }
    }

    private static double Luminance(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>朝白 / 黑混合到目标亮度：色相保留，只改明暗（与 Android 端同一算法）</summary>
    private static Color ToLuminance(Color c, double target)
    {
        var lum = Luminance(c);
        var toWhite = target > lum;
        var ratio = toWhite
            ? (target - lum) / Math.Max(1.0 - lum, 0.001)
            : (lum - target) / Math.Max(lum, 0.001);

        ratio = Math.Clamp(ratio, 0, 1);
        var dst = toWhite ? Colors.White : Colors.Black;

        return Color.FromRgb(
            (byte)Math.Round(c.R + (dst.R - c.R) * ratio),
            (byte)Math.Round(c.G + (dst.G - c.G) * ratio),
            (byte)Math.Round(c.B + (dst.B - c.B) * ratio));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);
}
