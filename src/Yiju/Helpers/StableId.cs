using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Yiju.Helpers;

/// <summary>
/// 稳定 UUID：同一个名字永远算出同一个 uuid。
///
/// 为什么需要：两端各自预置了同样的《论语》14 条，如果各自用 Guid.NewGuid()，
/// 第一次同步就会变成 28 条（互相认为对方是"新加的"）。
/// 用"命名空间 + 名字"算出确定性 uuid，两端对同一条预置句得到同一个 id，合并回去还是 14 条。
///
/// 算法（v5 UUID，见 docs/sync-spec.md §2）：
///   SHA1(命名空间16字节 || 名字UTF8) 取前 16 字节，把版本位改成 5、variant 位改成 RFC 4122。
///
/// ⚠️ 字节序：.NET 的 Guid 前三个字段是**小端**，而 Java 的 UUID 全是大端。
///    这里 hash 过之后直接 `new Guid(bytes)` —— 也就是说命名空间用的是
///    `Guid.ToByteArray()` 的小端布局，输出也是 .NET 那套。
///    **Android 端必须照样做一次字节换序才能算出同一个字符串**，
///    照 RFC 4122 直写会算成另一个 uuid（实测见 docs/sync-spec.md §2 的对照值）。
///
///    改这个文件之前，先确认另一端的 StableId 还能对上基准值
///    （"list:论语" → 5cbd5673-d822-a659-84e1-4fadcc544464）。
/// </summary>
internal static class StableId
{
    private static readonly Guid Ns = Guid.Parse("6b5a1f3c-8d2e-4a7b-9c1d-0e2f3a4b5c6d");

    public static string For(string name)
    {
        var bytes = Ns.ToByteArray().Concat(Encoding.UTF8.GetBytes(name)).ToArray();
        var hash = SHA1.HashData(bytes);
        var b = hash.Take(16).ToArray();
        b[6] = (byte)((b[6] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b).ToString();
    }

    public static string List(string name) => For("list:" + name);

    public static string Preset(string listName, int index) => For($"preset:{listName}:{index}");
}
