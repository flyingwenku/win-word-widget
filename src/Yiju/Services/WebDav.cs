using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Yiju.Services;

/// <summary>
/// 极简 WebDAV 客户端：只做同步需要的三件事（建目录 / 下载 / 上传）。
///
/// **刻意不带 ETag / If-Match**：坚果云实测 GET 刚拿到的 ETag，原样带回去 PUT
/// 立刻被判 412 `ETag doesn't match`；同一个 ETag 还对应过两份不同的内容。
/// 乐观锁在这套后端上等于废的，重试也只会反复撞。同步层改成
/// "一次 GET → 合并 → 一次 PUT"，并发靠合并的幂等性兜底。
/// 踩坑详见知识库《多端同步踩坑与解法（通用）》。
///
/// 认证用 Basic：坚果云要用**应用密码**（网页端"账户信息 → 添加应用"里生成），
/// 不是登录密码。为什么不引现成的 WebDAV 库：坚果云对 PROPFIND 的返回比较朴素，
/// 自己发这几条请求反而最省事，也不引第三方依赖。
/// </summary>
internal sealed class WebDavClient
{
    private readonly HttpClient _http;

    public string FileUrl { get; }
    public string DirUrl { get; }

    public WebDavClient(string fileUrl, string user, string password)
    {
        FileUrl = fileUrl.Trim();
        DirUrl = FileUrl.Substring(0, FileUrl.LastIndexOf('/'));

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // 坚果云对 GET 返回 Cache-Control: max-age=5，允许中间层缓存 5 秒。
        // 一旦命中缓存，下一次同步拿到的还是上一版内容 → 凭空制造一次覆盖写。
        // 同步这种"写完马上又要读最新"的场景，一律绕开缓存。
        _http.DefaultRequestHeaders.CacheControl =
            System.Net.Http.Headers.CacheControlHeaderValue.Parse("no-cache");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        _http.DefaultRequestHeaders.Add("User-Agent", "Yiju/1.1");
    }

    /// <summary>
    /// 拉远端文件：是否存在 + 内容。存在性**只看 GET 是不是 404** ——
    /// 坚果云的 PROPFIND 响应不带 ETag 头，早期实现把"拿不到 ETag"当成"文件不存在"，
    /// 每次启动都把本地覆盖上去，另一端刚加的句子直接被抹掉。现在完全不碰 ETag。
    /// </summary>
    public async Task<(bool Exists, string Content)> FetchAsync()
    {
        using var resp = await _http.GetAsync(FileUrl);
        if (resp.StatusCode == HttpStatusCode.NotFound) return (false, null);
        resp.EnsureSuccessStatusCode();
        return (true, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>上传。**没有 If-Match、没有 412 分支、没有重试**，非 2xx 直接抛（消息里带状态码）</summary>
    public async Task UploadAsync(string content)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, FileUrl)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>建目录（已经存在时服务器会返回 405，忽略即可）</summary>
    public async Task EnsureDirAsync()
    {
        using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), DirUrl);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // 父目录也没有：一层层建上去
            var parent = DirUrl.Substring(0, DirUrl.LastIndexOf('/'));
            using var p = new HttpRequestMessage(new HttpMethod("MKCOL"), parent);
            using var pr = await _http.SendAsync(p);
            using var again = new HttpRequestMessage(new HttpMethod("MKCOL"), DirUrl);
            using var ar = await _http.SendAsync(again);
        }
    }
}
