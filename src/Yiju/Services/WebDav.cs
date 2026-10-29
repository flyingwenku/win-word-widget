using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Yiju.Services;

/// <summary>
/// 极简 WebDAV 客户端：只做同步需要的四件事（建目录 / 取 ETag / 下载 / 上传）。
///
/// 认证用 Basic：坚果云要用**应用密码**（网页端"账户信息 → 添加应用"里生成），
/// 不是登录密码。
///
/// 为什么不用现成的 WebDAV 库：坚果云对 PROPFIND 的返回比较朴素，
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
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        _http.DefaultRequestHeaders.Add("User-Agent", "Yiju/1.1");
    }

    /// <summary>
    /// 拉远端文件：是否存在 + ETag + 内容。
    ///
    /// 踩过的坑：本来想先 PROPFIND 拿 ETag 再 GET，**坚果云的 PROPFIND 响应不带 ETag 头**
    /// （只有 GET 才有）。于是"拿不到 ETag"被当成"文件不存在"，每次启动都把本地覆盖上去，
    /// 另一端刚加的句子直接被抹掉。所以存在性只看 GET 是不是 404，ETag 只当**可选**的乐观锁。
    /// </summary>
    public async Task<(bool Exists, string Etag, string Content)> FetchAsync()
    {
        using var resp = await _http.GetAsync(FileUrl);
        if (resp.StatusCode == HttpStatusCode.NotFound) return (false, null, null);
        resp.EnsureSuccessStatusCode();

        // 有的服务器带引号（"abc"），有的还带弱标记（W/"abc"），一律剥掉
        var etag = resp.Headers.ETag?.Tag?.Trim('"', 'W', '/');
        return (true, etag, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// 上传。etag 不为 null 时带 If-Match（乐观锁）：
    /// 返回 false 表示 412 —— 别人刚写过，得重新拉取再合并。
    /// </summary>
    public async Task<bool> UploadAsync(string content, string etag)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, FileUrl)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");

        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.PreconditionFailed) return false;
        resp.EnsureSuccessStatusCode();
        return true;
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
