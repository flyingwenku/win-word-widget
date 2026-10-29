using System.Windows.Media;

namespace Yiju.Services;

/// <summary>
/// 配色。
///
/// 以前是"读壁纸主色 → 把亮度推到文字相反的一端"，看着很美，在 Windows 上不能用：
///   ・Win 没有 Android 那种 getWallpaperColors()，只能自己读壁纸文件
///   ・取值有三条备选路径（SPI 返回的路径 / TranscodedWallpaper / CachedFiles 里最新的那个），
///     这三者不一定是同一张图；再加上 Windows Spotlight 自己会轮换，
///     结果就是颜色毫无征兆地跳变 —— 实测 11 秒里从深绿翻到深紫再翻成浅蓝。
/// 所以换成固定方案表：颜色永远稳定、永远可读。
///
/// 半透明的做法：**只让背景色带 alpha，文字必须完全不透明**。
/// 用 UIElement.Opacity 会把字也一起弄淡 —— 那正是"半透明状态下看不清"的来源。
/// alpha 用 0xF2 ≈ 95%，略微透一点；想要完全不透光就选带"实"的那两个。
/// </summary>
internal static class Palette
{
    public static readonly (string Name, string Bg, string Text)[] Schemes =
    {
        ("纸白",     "#F2FFFFFF", "#FF1F1F1E"),
        ("纸白·实",  "#FFFFFFFF", "#FF1F1F1E"),
        ("深夜",     "#F21F2124", "#FFFAF8F6"),
        ("深夜·实",  "#FF1F2124", "#FFFAF8F6"),
    };

    public static (string Name, Color Bg, Color Text) For(int index)
    {
        if (index < 0 || index >= Schemes.Length) index = 0;
        var s = Schemes[index];
        return (s.Name, Parse(s.Bg), Parse(s.Text));
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Parse(string hex) =>
        ColorConverter.ConvertFromString(hex) is Color c ? c : Colors.White;
}
