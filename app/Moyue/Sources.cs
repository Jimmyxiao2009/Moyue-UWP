using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jm.Core;
using Newtonsoft.Json.Linq;

namespace Moyue
{
    public sealed class Sources : IContentSource, IDisposable
    {
        readonly Store store;
        readonly HttpClient http;
        readonly SemaphoreSlim syncGate = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim metadataGate = new SemaphoreSlim(3, 3);
        readonly Func<JmClient> jmFactory;

        public Sources(Store s, HttpMessageHandler handler = null, Func<JmClient> jmFactory = null)
        {
            store = s;
            // 把每次 API 尝试（域名 / 状态码 / 异常）接进运行日志，排查"获取失败"用。
            this.jmFactory =
                jmFactory ?? (() => new JmClient { Logger = m => Log.I("jm", m) });
            http = new HttpClient(handler ?? CreateHandler());
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36"
            );
        }

        static HttpClientHandler CreateHandler()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            try
            {
                // netstandard1.4 hides MaxConnectionsPerServer, but the UWP runtime has it and
                // defaults to 6 per host, which would silently cap parallel image downloads.
                var limit = typeof(HttpClientHandler).GetRuntimeProperty("MaxConnectionsPerServer");
                if (limit != null && limit.CanWrite)
                    limit.SetValue(handler, Settings.MaxConcurrency);
            }
            catch (Exception) { }
            return handler;
        }

        public static string Name(string id)
        {
            return id == "jm" ? "JM"
                : id == "boylove" ? "香香腐宅"
                : id == "manwa" ? "漫蛙"
                : id;
        }

        public SourceConfig Config(string id)
        {
            lock (store.SyncRoot)
            {
                var c = store.Data.Sources.FirstOrDefault(s => s.Id == id);
                if (c == null)
                    throw new ArgumentException("未知来源：" + id);
                return c;
            }
        }

        public string Base(string id)
        {
            lock (store.SyncRoot)
            {
                var c = Config(id);
                return c.Active
                    ?? c.Hosts.FirstOrDefault()
                    ?? throw new InvalidOperationException("没有网站线路");
            }
        }

        string ImageHost(string id)
        {
            lock (store.SyncRoot)
                return Config(id).ImageHosts.FirstOrDefault() ?? Base(id);
        }

        public async Task<string> Text(string url, CancellationToken ct)
        {
            var bytes = await Bytes(url, null, ct, 4 * 1024 * 1024).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        public async Task<byte[]> Bytes(
            string url,
            string referer,
            CancellationToken ct,
            int limit = 32 * 1024 * 1024
        )
        {
            Uri address;
            if (
                !Uri.TryCreate(url, UriKind.Absolute, out address)
                || (address.Scheme != "http" && address.Scheme != "https")
            )
                throw new ArgumentException("无效的网络地址");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                if (!string.IsNullOrEmpty(referer))
                    req.Headers.Referrer = new Uri(referer);
                try
                {
                    using (
                        var r = await http.SendAsync(
                                req,
                                HttpCompletionOption.ResponseHeadersRead,
                                deadline.Token
                            )
                            .ConfigureAwait(false)
                    )
                    {
                        r.EnsureSuccessStatusCode();
                        if (r.Content.Headers.ContentLength > limit)
                            throw new InvalidOperationException("响应超过大小限制");
                        using (
                            var input = await r.Content.ReadAsStreamAsync().ConfigureAwait(false)
                        )
                        using (var output = new System.IO.MemoryStream())
                        {
                            var buffer = new byte[16384];
                            int n;
                            while (
                                (
                                    n = await input
                                        .ReadAsync(buffer, 0, buffer.Length, deadline.Token)
                                        .ConfigureAwait(false)
                                ) > 0
                            )
                            {
                                if (output.Length + n > limit)
                                    throw new InvalidOperationException("响应超过大小限制");
                                output.Write(buffer, 0, n);
                            }
                            return output.ToArray();
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("网络请求超时");
                }
            }
        }

        async Task<T> At<T>(string id, Func<string, Task<T>> action, CancellationToken ct)
        {
            await metadataGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool failover;
                lock (store.SyncRoot)
                    failover = store.Data.Settings.Failover;
                try
                {
                    return await TryHosts(id, action, null, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException first)
                    when (id == "jm" && failover && !ct.IsCancellationRequested)
                {
                    // Same recovery as the original client: built-in JM nodes rotate out quickly,
                    // so when every known node fails, pull the live list from the domain servers
                    // and retry once with it.
                    List<string> fresh;
                    try
                    {
                        fresh = await RefreshJmHosts(ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        throw new InvalidOperationException(
                            first.Message + "\n更新线路也失败：" + e.Message,
                            first
                        );
                    }
                    return await TryHosts(id, action, fresh, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                metadataGate.Release();
            }
        }

        /// <summary>
        /// JM's 发布页 is the encrypted domain-server file (newsvr-*.txt), as in the original
        /// client. The configured one goes first; built-in mirrors back it up.
        /// </summary>
        static List<string> JmServers(string publication)
        {
            return new[] { publication }
                .Where(PublicHttps)
                .Concat(JmClient.DomainServerUrls)
                .Distinct()
                .ToList();
        }

        readonly SemaphoreSlim jmRefreshGate = new SemaphoreSlim(1, 1);
        DateTime jmRefreshed;

        async Task<List<string>> RefreshJmHosts(CancellationToken ct)
        {
            await jmRefreshGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Parallel failures share one refresh instead of each hitting the domain servers.
                if (DateTime.UtcNow - jmRefreshed < TimeSpan.FromMinutes(1))
                    lock (store.SyncRoot)
                        return Config("jm").Hosts.Take(4).ToList();
                string publication;
                lock (store.SyncRoot)
                    publication = Config("jm").Publication;
                List<string> fresh;
                using (var jm = jmFactory())
                    fresh = (
                        await jm.FetchApiDomainsAsync(ct, JmServers(publication))
                            .ConfigureAwait(false)
                    )
                        .Select(h => "https://" + h)
                        .Where(PublicHttps)
                        .Distinct()
                        .ToList();
                if (fresh.Count == 0)
                    throw new InvalidOperationException("JM：域名服务器没有返回可用线路");
                jmRefreshed = DateTime.UtcNow;
                lock (store.SyncRoot)
                {
                    var c = Config("jm");
                    c.Hosts = fresh.Concat(c.Hosts).Distinct().Take(24).ToList();
                    c.Message = "已从域名服务器更新线路";
                }
                await store.Save(ct).ConfigureAwait(false);
                return fresh;
            }
            finally
            {
                jmRefreshGate.Release();
            }
        }

        async Task<T> TryHosts<T>(
            string id,
            Func<string, Task<T>> action,
            List<string> only,
            CancellationToken ct
        )
        {
            List<string> hosts;
            lock (store.SyncRoot)
            {
                var c = Config(id);
                hosts = (only ?? new[] { c.Active }.Concat(c.Hosts))
                    .Where(PublicHttps)
                    .Distinct()
                    // Built-in JM nodes go dead in batches; dead ones fail fast
                    // (HTTP error / TLS reset / parked page), so try the whole list.
                    .Take(store.Data.Settings.Failover ? 16 : 1)
                    .ToList();
            }
            Exception last = null;
            foreach (var host in hosts)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Log.I("net", Name(id) + " 尝试 " + host);
                    var result = await action(host).ConfigureAwait(false);
                    lock (store.SyncRoot)
                    {
                        var c = Config(id);
                        c.Active = host;
                        c.Message = "连接正常";
                    }
                    Log.I("net", Name(id) + " 成功 " + host);
                    return result;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    last = e;
                    Log.E("net", Name(id) + " 线路失败 " + host, e);
                }
            }
            throw new InvalidOperationException(
                Name(id) + "：" + (last == null ? "没有可用线路" : last.Message),
                last
            );
        }

        public static bool PublicHttps(string text)
        {
            Uri u;
            return Uri.TryCreate(text, UriKind.Absolute, out u)
                && u.Scheme == "https"
                && u.IsDefaultPort
                && !u.IsLoopback
                && string.IsNullOrEmpty(u.UserInfo)
                && Uri.CheckHostName(u.Host) == UriHostNameType.Dns
                && u.Host.Contains('.')
                && !u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        }

        public async Task AutoSync(CancellationToken ct = default(CancellationToken))
        {
            List<string> ids;
            lock (store.SyncRoot)
            {
                if (!store.Data.Settings.AutoSync)
                    return;
                ids = store
                    .Data.Sources.Where(c =>
                        DateTime.UtcNow - c.Synced.ToUniversalTime() > TimeSpan.FromHours(12)
                        && DateTime.UtcNow - c.LastSyncAttempt.ToUniversalTime()
                            > TimeSpan.FromMinutes(30)
                    )
                    .Select(c => c.Id)
                    .ToList();
            }
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await Sync(id, ct).ConfigureAwait(false);
                    string done;
                    lock (store.SyncRoot)
                        done = Config(id).Message;
                    Log.I("sync", Name(id) + " 同步完成：" + done);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    lock (store.SyncRoot)
                        Config(id).Message = "自动同步失败：" + e.Message;
                    Log.W("sync", "自动同步失败 " + Name(id), e);
                }
            }
            await store.Save(ct).ConfigureAwait(false);
        }

        public async Task Sync(string id, CancellationToken ct)
        {
            await syncGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string publication;
                lock (store.SyncRoot)
                {
                    var c = Config(id);
                    publication = c.Publication;
                    c.LastSyncAttempt = DateTime.UtcNow;
                }
                if (id == "jm")
                {
                    List<string> fresh;
                    using (var jm = jmFactory())
                        fresh = (
                            await jm.FetchApiDomainsAsync(ct, JmServers(publication))
                                .ConfigureAwait(false)
                        )
                            .Select(h => "https://" + h)
                            .Where(PublicHttps)
                            .Distinct()
                            .Take(12)
                            .ToList();
                    // Verify at least one API endpoint before replacing the working list.
                    string working = null;
                    foreach (var h in fresh.Take(4))
                    {
                        try
                        {
                            using (var jm = jmFactory())
                            {
                                jm.SetApiDomains(new[] { new Uri(h).Host });
                                var response = await jm.GetAsync("/categories", null, ct)
                                    .ConfigureAwait(false);
                                if (response.IsOk)
                                {
                                    working = h;
                                    break;
                                }
                            }
                        }
                        catch (Exception) when (!ct.IsCancellationRequested) { }
                    }
                    if (working == null)
                        throw new InvalidOperationException(
                            "发布接口的 API 线路未通过验证，保留原线路"
                        );
                    lock (store.SyncRoot)
                    {
                        var c = Config(id);
                        c.Hosts = fresh;
                        c.Active = working;
                        c.Synced = DateTime.UtcNow;
                        c.Message = "已验证并同步 API 线路";
                    }
                }
                else
                {
                    if (!PublicHttps(publication))
                        throw new ArgumentException("发布页必须是 HTTPS 公网地址");
                    var queue = new Queue<string>(
                        PublicationLinks(
                            await Text(publication, ct).ConfigureAwait(false),
                            publication
                        )
                    );
                    var seen = new HashSet<string>();
                    var valid = new List<string>();
                    while (queue.Count > 0 && seen.Count < 12)
                    {
                        var url = queue.Dequeue();
                        if (!seen.Add(url))
                            continue;
                        try
                        {
                            var body = await Text(url, ct).ConfigureAwait(false);
                            var origin = "https://" + new Uri(url).Authority;
                            if (id == "boylove")
                            {
                                if (body.Contains("/home/book/") && body.Contains("腐宅"))
                                {
                                    SourceParsers.BoySearch(
                                        await Text(origin + "/home/api/getpage/tp/1-topestmh-0", ct)
                                            .ConfigureAwait(false),
                                        ImageHost(id)
                                    );
                                    valid.Add(origin);
                                }
                            }
                            else if (body.Contains("manga-list") || body.Contains("book-list"))
                            {
                                SourceParsers.ManwaSearch(
                                    await Text(origin + "/booklist", ct).ConfigureAwait(false),
                                    origin
                                );
                                valid.Add(origin);
                            }
                            else
                                foreach (var nested in PublicationLinks(body, url))
                                    if (!seen.Contains(nested))
                                        queue.Enqueue(nested);
                        }
                        catch (Exception) when (!ct.IsCancellationRequested) { }
                    }
                    if (valid.Count == 0)
                        throw new InvalidOperationException("发布页未提供可验证的线路，保留原线路");
                    lock (store.SyncRoot)
                    {
                        var c = Config(id);
                        c.Hosts = valid.Concat(c.Hosts).Distinct().Take(12).ToList();
                        c.Active = valid[0];
                        c.Synced = DateTime.UtcNow;
                        c.Message = "同步并验证 " + valid.Distinct().Count() + " 条线路";
                    }
                }
                await store.Save(ct).ConfigureAwait(false);
            }
            finally
            {
                syncGate.Release();
            }
        }

        public static List<string> PublicationLinks(string html, string baseUrl)
        {
            var links = new List<string>();
            var d = SourceParsers.Doc(html);
            foreach (
                var a in (IEnumerable<HtmlAgilityPack.HtmlNode>)
                    d.DocumentNode.SelectNodes("//a[@href]")
                    ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>()
            )
            {
                var href = a.GetAttributeValue("href", "");
                Uri u;
                if (
                    Uri.TryCreate(
                        new Uri(baseUrl),
                        HtmlAgilityPack.HtmlEntity.DeEntitize(href),
                        out u
                    )
                    && PublicHttps(u.ToString())
                    && !new[]
                    {
                        "t.me",
                        "telegram.org",
                        "github.io",
                        "weibo.com",
                        "facebook.com",
                        "discord.com",
                    }.Any(x => u.Host == x || u.Host.EndsWith("." + x))
                )
                    links.Add(u.ToString().TrimEnd('/'));
            }
            // Decode static literals only, including arrays followed by .map(atob).
            // No publication JavaScript is executed.
            foreach (Match m in Regex.Matches(html, @"['""]([A-Za-z0-9+/=]{12,8192})['""]"))
            {
                try
                {
                    var text = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
                    if (PublicHttps(text))
                        links.Add(text.TrimEnd('/'));
                    else if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
                        links.AddRange(JArray.Parse(text).Values<string>().Where(PublicHttps));
                }
                catch (FormatException) { }
                catch (Newtonsoft.Json.JsonException) { }
            }
            return links
                .Where(PublicHttps)
                .Where(x =>
                    !new[]
                    {
                        "t.me",
                        "telegram.org",
                        "github.io",
                        "weibo.com",
                        "facebook.com",
                        "discord.com",
                    }.Any(h => new Uri(x).Host == h || new Uri(x).Host.EndsWith("." + h))
                )
                .Select(x => x.TrimEnd('/'))
                .Distinct()
                .Take(20)
                .ToList();
        }

        public async Task<List<Book>> Search(
            string id,
            string query,
            int page,
            CancellationToken ct
        )
        {
            if (page < 1)
                throw new ArgumentOutOfRangeException(nameof(page));
            query = query ?? "";
            return await At(
                    id,
                    async host =>
                    {
                        if (id == "jm")
                            using (var jm = jmFactory())
                            {
                                jm.SetApiDomains(new[] { new Uri(host).Host });
                                var r = string.IsNullOrWhiteSpace(query)
                                    ? await jm.GetAsync(
                                            "/categories/filter",
                                            new Dictionary<string, string>
                                            {
                                                { "page", page.ToString() },
                                                { "order", "" },
                                                { "c", "0" },
                                                { "o", "mr" },
                                            },
                                            ct
                                        )
                                        .ConfigureAwait(false)
                                    : await jm.SearchAsync(query, page, null, null, ct)
                                        .ConfigureAwait(false);
                                if (!r.IsOk)
                                    throw new InvalidOperationException(
                                        r.ErrorMsg ?? "JM API 返回失败"
                                    );
                                var root = JObject.Parse(r.Data);
                                var redirect = (string)root["redirect_aid"];
                                int aid;
                                if (
                                    !string.IsNullOrEmpty(redirect)
                                    && int.TryParse(redirect, out aid)
                                )
                                {
                                    var album = await jm.GetAlbumAsync(aid, ct)
                                        .ConfigureAwait(false);
                                    if (!album.IsOk)
                                        throw new InvalidOperationException(
                                            album.ErrorMsg ?? "JM ID 重定向失败"
                                        );
                                    return new List<Book>
                                    {
                                        SourceParsers.JmDetail(
                                            new Book { Source = "jm", Id = redirect },
                                            album.Data,
                                            ImageHost(id)
                                        ),
                                    };
                                }
                                return SourceParsers.JmSearch(r.Data, ImageHost(id));
                            }
                        if (id == "boylove")
                        {
                            var path = string.IsNullOrWhiteSpace(query)
                                ? "/home/api/getpage/tp/1-topestmh-" + (page - 1)
                                : "/home/api/searchk?keyword="
                                    + Uri.EscapeDataString(query)
                                    + "&type=1&pageNo="
                                    + page;
                            return SourceParsers.BoySearch(
                                await Text(host + path, ct).ConfigureAwait(false),
                                ImageHost(id)
                            );
                        }
                        var path2 = string.IsNullOrWhiteSpace(query)
                            ? "/booklist?page=" + page
                            : "/search?keyword=" + Uri.EscapeDataString(query) + "&page=" + page;
                        return SourceParsers.ManwaSearch(
                            await Text(host + path2, ct).ConfigureAwait(false),
                            host
                        );
                    },
                    ct
                )
                .ConfigureAwait(false);
        }

        public async Task<Book> Detail(Book input, CancellationToken ct)
        {
            Store.Validate(input);
            int number;
            if (!int.TryParse(input.Id, out number) || number < 1)
                throw new ArgumentException("漫画 ID 必须为正整数");
            var book = await At(
                    input.Source,
                    async host =>
                    {
                        if (input.Source == "jm")
                            using (var jm = jmFactory())
                            {
                                jm.SetApiDomains(new[] { new Uri(host).Host });
                                var r = await jm.GetAlbumAsync(number, ct).ConfigureAwait(false);
                                if (!r.IsOk)
                                    throw new InvalidOperationException(
                                        r.ErrorMsg ?? "JM 详情失败"
                                    );
                                return SourceParsers.JmDetail(
                                    input,
                                    r.Data,
                                    ImageHost(input.Source)
                                );
                            }
                        if (input.Source == "boylove")
                        {
                            var html = await Text(host + "/home/book/index/id/" + input.Id, ct)
                                .ConfigureAwait(false);
                            var chapters = await Text(
                                    host + "/home/api/chapter_list/tp/" + input.Id,
                                    ct
                                )
                                .ConfigureAwait(false);
                            var b = SourceParsers.BoyDetail(input, html, chapters);
                            var results = SourceParsers.BoySearch(
                                await Text(
                                        host
                                            + "/home/api/searchk?keyword="
                                            + Uri.EscapeDataString(b.Title)
                                            + "&type=1&pageNo=1",
                                        ct
                                    )
                                    .ConfigureAwait(false),
                                ImageHost(input.Source)
                            );
                            var summary = results.FirstOrDefault(x => x.Id == input.Id);
                            if (summary != null)
                            {
                                summary.Chapters = b.Chapters;
                                return summary;
                            }
                            return b;
                        }
                        return SourceParsers.ManwaDetail(
                            input,
                            await Text(host + "/book/" + input.Id, ct).ConfigureAwait(false),
                            host
                        );
                    },
                    ct
                )
                .ConfigureAwait(false);
            store.Remember(book);
            await store.Save(ct).ConfigureAwait(false);
            return book;
        }

        public async Task<List<PageInfo>> Pages(Book b, Chapter c, CancellationToken ct)
        {
            Store.Validate(b);
            if (c == null || string.IsNullOrWhiteSpace(c.Id))
                throw new ArgumentException("章节不能为空");
            return await At(
                    b.Source,
                    async host =>
                    {
                        if (b.Source == "jm")
                        {
                            int id;
                            if (!int.TryParse(c.Id, out id) || id < 1)
                                throw new ArgumentException("JM 章节 ID 无效");
                            using (var jm = jmFactory())
                            {
                                jm.SetApiDomains(new[] { new Uri(host).Host });
                                var r = await jm.GetChapterAsync(id, ct).ConfigureAwait(false);
                                if (!r.IsOk)
                                    throw new InvalidOperationException(
                                        r.ErrorMsg ?? "JM 章节失败"
                                    );
                                var t = JObject.Parse(r.Data);
                                var arr = t["images"] as JArray;
                                if (arr == null || arr.Count == 0)
                                    throw new FormatException("JM 图片列表为空或格式变化");
                                List<string> imageHosts;
                                lock (store.SyncRoot)
                                    imageHosts = Config(b.Source).ImageHosts.ToList();
                                int scramble =
                                    (int?)t["scramble_id"]
                                    ?? await jm.GetScrambleIdAsync(id, ct).ConfigureAwait(false);
                                return arr.Select(n =>
                                    {
                                        var name = (string)n;
                                        if (
                                            string.IsNullOrEmpty(name)
                                            || name.Contains("/")
                                            || name.Contains("..")
                                        )
                                            throw new FormatException("图片名称无效");
                                        var path = "/media/photos/" + c.Id + "/" + name;
                                        return new PageInfo
                                        {
                                            Url = ImageHost(b.Source) + path,
                                            Alternatives = imageHosts
                                                .Select(h => h.TrimEnd('/') + path)
                                                .ToList(),
                                            Identity = path,
                                            Referer = host,
                                            Strips = JmImageScramble.GetSegmentCount(
                                                id,
                                                System.IO.Path.GetFileNameWithoutExtension(name),
                                                scramble
                                            ),
                                        };
                                    })
                                    .ToList();
                            }
                        }
                        var path2 = new Uri(SourceParsers.Abs(host, c.Id)).AbsolutePath;
                        // Use the selected site's origin for chapters; never follow a stored mirror's host.
                        var html = await Text(host + path2, ct).ConfigureAwait(false);
                        var pages = SourceParsers.Pages(
                            b.Source,
                            html,
                            host,
                            ImageHost(b.Source),
                            path2
                        );
                        if (b.Source == "boylove")
                            foreach (var p in pages)
                            {
                                List<string> hosts;
                                lock (store.SyncRoot)
                                    hosts = Config(b.Source).ImageHosts.ToList();
                                p.Alternatives = hosts
                                    .Select(h => h.TrimEnd('/') + new Uri(p.Url).AbsolutePath)
                                    .ToList();
                            }
                        return pages;
                    },
                    ct
                )
                .ConfigureAwait(false);
        }

        public async Task SetPublication(string id, string url)
        {
            if (!PublicHttps(url))
                throw new ArgumentException("发布页必须使用 HTTPS");
            lock (store.SyncRoot)
            {
                Config(id).Publication = url;
                Config(id).LastSyncAttempt = default(DateTime);
            }
            await store.Save().ConfigureAwait(false);
        }

        public async Task AddLine(string id, string url, bool image = false)
        {
            if (!PublicHttps(url))
                throw new ArgumentException("线路必须使用 HTTPS");
            var origin = "https://" + new Uri(url).Authority;
            lock (store.SyncRoot)
            {
                var c = Config(id);
                var list = image ? c.ImageHosts : c.Hosts;
                list.Remove(origin);
                list.Insert(0, origin);
                if (!image)
                    c.Active = origin;
            }
            await store.Save().ConfigureAwait(false);
        }

        public void Dispose()
        {
            http.Dispose();
        }
    }
}
