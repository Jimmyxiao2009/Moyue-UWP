using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Moyue
{
    public sealed partial class Store
    {
        public Database Data { get; private set; } = new Database();
        public readonly object SyncRoot = new object();
        public IBackendStorage Storage { get; private set; }
        readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);
        public string LoadWarning { get; private set; }

        public static async Task<Store> Open(IBackendStorage storage)
        {
            if (storage == null)
                throw new ArgumentNullException(nameof(storage));
            var store = new Store { Storage = storage };
            foreach (var name in new[] { "library.json", "library.backup.json" })
            {
                var bytes = await storage.Read(name, CancellationToken.None).ConfigureAwait(false);
                if (bytes == null)
                    continue;
                try
                {
                    var data = JsonConvert.DeserializeObject<Database>(
                        Encoding.UTF8.GetString(bytes)
                    );
                    if (data != null && data.Version > 2)
                        throw new NotSupportedException("资料来自更新版本的应用，请升级后打开");
                    if (data == null || data.Version < 1)
                        throw new InvalidOperationException("不支持的资料版本");
                    store.Data = data;
                    break;
                }
                catch (NotSupportedException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    store.LoadWarning = "资料读取失败，尝试恢复备份：" + e.Message;
                }
            }
            store.Normalize();
            return store;
        }

        void Normalize()
        {
            Data.Version = 2;
            Data.Settings = Data.Settings ?? new Settings();
            Data.Favorites = Data.Favorites ?? new List<Book>();
            Data.Known = Data.Known ?? new List<Book>();
            Data.History = Data.History ?? new List<Progress>();
            Data.Bookmarks = Data.Bookmarks ?? new List<Bookmark>();
            Data.Bookmarks.RemoveAll(m => m == null || m.Book == null || m.Chapter == null);
            Data.Downloads = Data.Downloads ?? new List<DownloadJob>();
            Data.Queries = Data.Queries ?? new List<string>();
            Data.Sources = Data.Sources ?? new List<SourceConfig>();
            Data.Settings.Concurrency = Math.Max(
                1,
                Math.Min(Settings.MaxConcurrency, Data.Settings.Concurrency)
            );
            Data.Settings.Preload = Math.Max(0, Math.Min(3, Data.Settings.Preload));
            Data.Settings.CacheMb = Math.Max(16, Math.Min(512, Data.Settings.CacheMb));
            Data.Downloads.RemoveAll(j => j == null || j.Book == null || j.Chapter == null);
            foreach (var j in Data.Downloads)
            {
                j.Pages = j.Pages ?? new List<PageInfo>();
                j.Done = j.Done ?? new List<int>();
                j.Saved = j.Saved ?? new Dictionary<int, SavedPage>();
                j.Key = ChapterKey(j.Book, j.Chapter);
                if (
                    j.State == "下载中"
                    || (
                        j.State == "已完成"
                        && (j.Pages.Count == 0 || j.Saved.Count != j.Pages.Count)
                    )
                )
                    j.State = "等待";
            }
            var defaults = new[]
            {
                new SourceConfig
                {
                    Id = "jm",
                    Publication = Jm.Core.JmClient.DomainServerUrls[0],
                    Hosts = Jm.Core.JmClient.DefaultApiDomains.Select(h => "https://" + h).ToList(),
                    ImageHosts = Jm
                        .Core.JmImageUrl.DefaultImageDomains.Select(h => "https://" + h)
                        .ToList(),
                },
                new SourceConfig
                {
                    Id = "boylove",
                    Publication = "https://boylovepage.github.io/",
                    Hosts = new List<string> { "https://boylove.cc" },
                    ImageHosts = new List<string>
                    {
                        "https://img.boylove.cc",
                        "https://blcnimghost2.cc",
                    },
                },
                new SourceConfig
                {
                    Id = "manwa",
                    Publication = "https://manwala.cc/",
                    Hosts = new List<string> { "https://manwa.me", "https://fuwbm.cc" },
                },
            };
            foreach (var fallback in defaults)
            {
                var c = Data.Sources.FirstOrDefault(s => s.Id == fallback.Id);
                if (c == null)
                {
                    Data.Sources.Add(fallback);
                    continue;
                }
                c.Hosts = c.Hosts ?? fallback.Hosts;
                // JM API nodes die often; older libraries kept only the first four defaults.
                // Keep synced/manual hosts first and append any missing built-in candidates.
                if (c.Id == "jm")
                    c.Hosts = c.Hosts.Concat(fallback.Hosts).Distinct().ToList();
                c.ImageHosts = c.ImageHosts ?? fallback.ImageHosts;
                if (c.ImageHosts.Count == 0)
                    c.ImageHosts = fallback.ImageHosts;
                c.WebHosts = c.WebHosts ?? new List<string>();
                if (c.Publication == "https://boylovepage.github.io/boylove_page/")
                    c.Publication = fallback.Publication;
                // The website publication page never listed API nodes; use the domain server.
                if (c.Id == "jm" && c.Publication == "https://jmcomicgo.org/")
                    c.Publication = fallback.Publication;
                if (c.Id == "manwa" && c.Publication == "https://mwmissing3.cc/")
                    c.Publication = fallback.Publication;
            }
        }

        public Database Snapshot()
        {
            lock (SyncRoot)
                return JsonConvert.DeserializeObject<Database>(JsonConvert.SerializeObject(Data));
        }

        public async Task Save(CancellationToken ct = default(CancellationToken))
        {
            await saveGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                byte[] bytes;
                lock (SyncRoot)
                    bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Data));
                var old = await Storage.Read("library.json", ct).ConfigureAwait(false);
                if (old != null)
                {
                    try
                    {
                        if (
                            JsonConvert.DeserializeObject<Database>(Encoding.UTF8.GetString(old))
                            != null
                        )
                            await Storage
                                .WriteAtomic("library.backup.json", old, ct)
                                .ConfigureAwait(false);
                    }
                    catch (JsonException) { }
                }
                await Storage.WriteAtomic("library.json", bytes, ct).ConfigureAwait(false);
            }
            finally
            {
                saveGate.Release();
            }
        }

        public void Remember(Book book)
        {
            Validate(book);
            lock (SyncRoot)
            {
                Data.Known.RemoveAll(b => b.Key == book.Key);
                Data.Known.Insert(0, book);
                if (Data.Known.Count > 1000)
                    Data.Known.RemoveRange(1000, Data.Known.Count - 1000);
            }
        }

        public async Task SetFavorite(Book book, bool favorite)
        {
            Validate(book);
            lock (SyncRoot)
            {
                Data.Favorites.RemoveAll(b => b.Key == book.Key);
                if (favorite)
                    Data.Favorites.Add(book);
            }
            Remember(book);
            await Save().ConfigureAwait(false);
        }

        public async Task Record(Book book, Chapter chapter, int page, double offset)
        {
            Validate(book);
            if (chapter == null || string.IsNullOrEmpty(chapter.Id))
                throw new ArgumentException("章节不能为空");
            if (page < 0 || double.IsNaN(offset) || double.IsInfinity(offset))
                throw new ArgumentOutOfRangeException(nameof(page));
            lock (SyncRoot)
            {
                Data.History.RemoveAll(h => h.Key == book.Key);
                Data.History.Insert(
                    0,
                    new Progress
                    {
                        Key = book.Key,
                        Book = book,
                        Chapter = chapter,
                        Page = page,
                        Offset = Math.Max(0, Math.Min(1, offset)),
                        Updated = DateTime.Now,
                    }
                );
                if (Data.History.Count > 500)
                    Data.History.RemoveRange(500, Data.History.Count - 500);
            }
            Remember(book);
            await Save().ConfigureAwait(false);
        }

        public async Task ClearHistory(string bookKey = null)
        {
            lock (SyncRoot)
            {
                if (bookKey == null)
                    Data.History.Clear();
                else
                    Data.History.RemoveAll(h => h.Key == bookKey);
            }
            await Save().ConfigureAwait(false);
        }

        /// <summary>Adds a bookmark, or returns the existing one for the same chapter page.</summary>
        public async Task<Bookmark> AddBookmark(Book book, Chapter chapter, int page, double offset)
        {
            Validate(book);
            if (chapter == null || string.IsNullOrEmpty(chapter.Id))
                throw new ArgumentException("章节不能为空");
            if (page < 0 || double.IsNaN(offset) || double.IsInfinity(offset))
                throw new ArgumentOutOfRangeException(nameof(page));
            Bookmark mark;
            lock (SyncRoot)
            {
                mark = Data.Bookmarks.FirstOrDefault(m =>
                    m.Key == book.Key && m.Chapter.Id == chapter.Id && m.Page == page
                );
                if (mark != null)
                    return mark;
                mark = new Bookmark
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Key = book.Key,
                    Book = new Book
                    {
                        Source = book.Source,
                        Id = book.Id,
                        Title = book.Title,
                        Cover = book.Cover,
                        Author = book.Author,
                    },
                    Chapter = new Chapter { Id = chapter.Id, Title = chapter.Title },
                    Page = page,
                    Offset = Math.Max(0, Math.Min(1, offset)),
                    Created = DateTime.Now,
                };
                Data.Bookmarks.Insert(0, mark);
            }
            Remember(book);
            await Save().ConfigureAwait(false);
            return mark;
        }

        public async Task RemoveBookmark(string id)
        {
            lock (SyncRoot)
                Data.Bookmarks.RemoveAll(m => m.Id == id);
            await Save().ConfigureAwait(false);
        }

        /// <summary>Most complete stored copy of a book (with chapters), or null.</summary>
        public Book FindBook(string key)
        {
            lock (SyncRoot)
                return Data.Favorites.FirstOrDefault(b => b.Key == key && b.Chapters.Count > 0)
                    ?? Data.History.Select(h => h.Book)
                        .FirstOrDefault(b => b != null && b.Key == key && b.Chapters.Count > 0)
                    ?? Data.Known.FirstOrDefault(b => b.Key == key && b.Chapters.Count > 0);
        }

        public static void Validate(Book b)
        {
            if (
                b == null
                || !new[] { "jm", "boylove", "manwa" }.Contains(b.Source)
                || string.IsNullOrWhiteSpace(b.Id)
            )
                throw new ArgumentException("无效的来源或漫画 ID");
        }

        public static string ChapterKey(Book b, Chapter c)
        {
            return b.Key + ":" + c.Id;
        }

        public static string Safe(string key)
        {
            return Jm.Core.JmCrypto.Md5Hex(key);
        }

        public static string DownloadPath(string chapterKey, int index)
        {
            return "downloads/" + Safe(chapterKey) + "/" + index.ToString("D5") + ".jpg";
        }
    }
}
