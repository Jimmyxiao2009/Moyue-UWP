using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jm.Core;
using Moyue;

class Program
{
    static int passed;

    static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    static async Task Test(string name, Func<Task> body)
    {
        await body();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    static async Task Throws<T>(Func<Task> body)
        where T : Exception
    {
        try
        {
            await body();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("expected " + typeof(T).Name);
    }

    static Task Sync(Action body)
    {
        body();
        return Task.CompletedTask;
    }

    static Book Book(string id = "1", string source = "jm", string title = "星 海")
    {
        return new Book
        {
            Source = source,
            Id = id,
            Title = title,
            Author = "甲",
            Tags = "冒险",
            State = "连载",
        };
    }

    static Chapter Chapter()
    {
        return new Chapter { Id = "10", Title = "第一章" };
    }

    static PageInfo Page(int i)
    {
        return new PageInfo
        {
            Url = "https://images.example.org/" + i + ".jpg",
            Identity = "/" + i + ".jpg",
        };
    }

    static byte[] Jpeg(int value)
    {
        return new byte[] { 255, 216, 1, (byte)value, 255, 217 };
    }

    static HttpResponseMessage Reply(string text, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(text) };
    }

    static async Task Idle(DownloadEngine engine)
    {
        for (int i = 0; i < 300; i++)
        {
            var task = engine.Completion;
            await task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(10);
            if (ReferenceEquals(task, engine.Completion) && task.IsCompleted)
                return;
        }
        throw new Exception("engine never idle");
    }

    static async Task Main(string[] args)
    {
        if (args.Contains("--live"))
        {
            await Live();
            return;
        }
        if (args.Contains("--jm"))
        {
            await JmLive();
            return;
        }
        if (args.Contains("--boy"))
        {
            await BoyLive();
            return;
        }
        await Test(
            "ALL/ANY/exclude/author/tag/unknown-state",
            () =>
                Sync(() =>
                {
                    var b = Book();
                    var o = new SearchOptions
                    {
                        Query = "星 海",
                        Tags = "冒险",
                        Author = "甲",
                    };
                    Assert(o.Match(b), "ALL");
                    o.Query = "星 月";
                    Assert(!o.Match(b), "missing word");
                    o.Any = true;
                    Assert(o.Match(b), "ANY");
                    o.Exclude = "海";
                    Assert(!o.Match(b), "exclude");
                    o.Exclude = "";
                    o.State = "完结";
                    b.State = null;
                    Assert(!o.Match(b), "unknown state");
                })
        );
        await Test(
            "same ID across sources remains distinct",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                await s.SetFavorite(Book("1", "jm"), true);
                await s.SetFavorite(Book("1", "boylove"), true);
                Assert(s.Snapshot().Favorites.Count == 2, "collision");
            }
        );
        await Test(
            "bookmarks persist, dedupe per page and delete independently of history",
            async () =>
            {
                var disk = new MemoryStorage();
                var s = await Store.Open(disk);
                var book = Book();
                book.Chapters.Add(Chapter());
                var a = await s.AddBookmark(book, Chapter(), 4, 0.5);
                var again = await s.AddBookmark(book, Chapter(), 4, 0.9);
                var b = await s.AddBookmark(book, Chapter(), 9, 0);
                Assert(a.Id == again.Id && s.Snapshot().Bookmarks.Count == 2, "dedupe");
                Assert(a.Book.Chapters.Count == 0 && s.FindBook(book.Key) != null, "light copy");
                await s.ClearHistory();
                var restored = await Store.Open(disk);
                Assert(restored.Snapshot().Bookmarks.Count == 2, "persist");
                await restored.RemoveBookmark(b.Id);
                Assert(
                    restored.Snapshot().Bookmarks.Single().Page == 4,
                    "remove only the chosen bookmark"
                );
            }
        );
        await Test(
            "persistent favorites/history/settings + bounded offset",
            async () =>
            {
                var disk = new MemoryStorage();
                var s = await Store.Open(disk);
                s.Data.Settings.Concurrency = 3;
                await s.SetFavorite(Book(), true);
                await s.Record(Book(), Chapter(), 8, 4);
                var restored = await Store.Open(disk);
                Assert(
                    restored.Data.Favorites.Count == 1
                        && restored.Data.History[0].Page == 8
                        && restored.Data.History[0].Offset == 1
                        && restored.Data.Settings.Concurrency == 3,
                    "round trip"
                );
            }
        );
        await Test(
            "corrupted primary restores backup and preserves good backup",
            async () =>
            {
                var disk = new MemoryStorage();
                var s = await Store.Open(disk);
                await s.SetFavorite(Book(), true);
                await s.Save();
                disk.Files["library.json"] = Encoding.UTF8.GetBytes("broken");
                s = await Store.Open(disk);
                Assert(s.Data.Favorites.Count == 1 && s.LoadWarning != null, "backup restore");
                await s.Save();
                Assert(
                    Encoding
                        .UTF8.GetString(disk.Files["library.backup.json"])
                        .Contains("Favorites"),
                    "good backup lost"
                );
            }
        );
        await Test(
            "v1 queue migration + settings limits",
            async () =>
            {
                var disk = new MemoryStorage();
                disk.Files["library.json"] = Encoding.UTF8.GetBytes(
                    "{\"Version\":1,\"Settings\":{\"Concurrency\":100,\"Preload\":-5},\"Downloads\":[{\"Book\":{\"Source\":\"jm\",\"Id\":\"1\"},\"Chapter\":{\"Id\":\"10\"},\"State\":\"已完成\",\"Pages\":[{\"Url\":\"https://example.org/x.jpg\"}],\"Done\":[0]}]}"
                );
                var s = await Store.Open(disk);
                Assert(
                    s.Data.Downloads[0].State == "等待"
                        && s.Data.Settings.Concurrency == Settings.MaxConcurrency
                        && s.Data.Settings.Preload == 0,
                    "migration"
                );
            }
        );
        await Test(
            "disk-write failure keeps previous primary",
            async () =>
            {
                var disk = new MemoryStorage();
                var s = await Store.Open(disk);
                await s.Save();
                var original = disk.Files["library.json"];
                disk.FailWrite = true;
                await Throws<System.IO.IOException>(() => s.SetFavorite(Book(), true));
                Assert(original.SequenceEqual(disk.Files["library.json"]), "atomic write");
            }
        );
        await Test(
            "partial source failure preserves other sources + arrival callbacks",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                var source = new FakeContent
                {
                    SearchCall = (id, q, p, ct) =>
                        id == "jm"
                            ? Task.FromException<List<Book>>(new Exception("offline"))
                            : Task.FromResult(new List<Book> { Book("1", id) }),
                };
                int count = 0;
                var batch = await new SearchService(s, source).Search(
                    new SearchOptions { Query = "星" },
                    1,
                    CancellationToken.None,
                    r => Interlocked.Increment(ref count)
                );
                Assert(
                    batch.Books.Count == 2
                        && batch.Sources.Single(r => r.Source == "jm").Error != null
                        && count == 3,
                    "partial results"
                );
            }
        );
        await Test(
            "source-matched keywords are not re-checked against titles",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                // JM list rows carry no tags and titles are often traditional/Japanese, so a
                // keyword the server matched (e.g. a tag) is usually absent from the row.
                var source = new FakeContent
                {
                    SearchCall = (id, q, p, ct) =>
                        Task.FromResult(
                            new List<Book>
                            {
                                new Book
                                {
                                    Id = "1",
                                    Source = id,
                                    Title = "無關標題",
                                },
                                new Book
                                {
                                    Id = "2",
                                    Source = id,
                                    Title = "無關標題 排除",
                                },
                            }
                        ),
                };
                var search = new SearchService(s, source);
                var jm = await search.Search(
                    new SearchOptions
                    {
                        Query = "全彩 汉化",
                        Exclude = "排除",
                        Sources = new List<string> { "jm" },
                    },
                    1,
                    CancellationToken.None
                );
                Assert(jm.Books.Count == 1 && jm.Books[0].Id == "1", "JM keyword recheck");
                // Other sources only receive the first ALL keyword; the rest is still verified.
                var manwa = await search.Search(
                    new SearchOptions
                    {
                        Query = "全彩 汉化",
                        Sources = new List<string> { "manwa" },
                    },
                    1,
                    CancellationToken.None
                );
                Assert(manwa.Books.Count == 0, "unsent keyword must still match");
                manwa = await search.Search(
                    new SearchOptions
                    {
                        Query = "全彩",
                        Sources = new List<string> { "manwa" },
                    },
                    1,
                    CancellationToken.None
                );
                Assert(manwa.Books.Count == 2, "sent keyword dropped");
                var tagOnly = await search.Search(
                    new SearchOptions
                    {
                        Tags = "全彩",
                        Sources = new List<string> { "jm" },
                    },
                    1,
                    CancellationToken.None
                );
                Assert(tagOnly.Books.Count == 2, "tag-only search");
            }
        );
        await Test(
            "advanced search enriches missing metadata",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                var source = new FakeContent
                {
                    SearchCall = (id, q, p, ct) =>
                        Task.FromResult(
                            new List<Book>
                            {
                                new Book
                                {
                                    Id = "1",
                                    Source = id,
                                    Title = "星",
                                },
                            }
                        ),
                    DetailCall = (b, ct) =>
                    {
                        b.Author = "甲";
                        b.Tags = "冒险";
                        return Task.FromResult(b);
                    },
                };
                var batch = await new SearchService(s, source).Search(
                    new SearchOptions
                    {
                        Query = "星",
                        Author = "甲",
                        Tags = "冒险",
                        Sources = new List<string> { "manwa" },
                    },
                    1,
                    CancellationToken.None
                );
                Assert(batch.Books.Count == 1, "missing detail enrichment");
            }
        );
        await Test(
            "search cancellation propagates and does not report empty success",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                var source = new FakeContent
                {
                    SearchCall = async (id, q, p, ct) =>
                    {
                        await Task.Delay(3000, ct);
                        return new List<Book>();
                    },
                };
                using (var ct = new CancellationTokenSource(20))
                    await Throws<OperationCanceledException>(
                        () =>
                            new SearchService(s, source).Search(
                                new SearchOptions { Query = "星" },
                                1,
                                ct.Token
                            )
                    );
            }
        );
        await Test(
            "local search includes favorites/history/downloads and honors filters",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                await s.SetFavorite(Book("3", "boylove"), true);
                var source = new FakeContent();
                var batch = await new SearchService(s, source).Search(
                    new SearchOptions
                    {
                        Query = "海",
                        Local = true,
                        Sources = new List<string> { "boylove" },
                    },
                    1,
                    CancellationToken.None
                );
                Assert(batch.Books.Single().Id == "3", "local");
            }
        );
        await Test(
            "BoyLove JSON parser, metadata and reading chapter order",
            () =>
                Sync(() =>
                {
                    var b = SourceParsers
                        .BoySearch(
                            "{\"result\":{\"list\":[{\"id\":7,\"title\":\"星\",\"image\":\"/cover.jpg\",\"auther\":\"甲\",\"mhstatus\":1}]}}",
                            "https://img.example.org"
                        )
                        .Single();
                    Assert(
                        b.State == "完结" && b.Cover == "https://img.example.org/cover.jpg",
                        "metadata"
                    );
                    b = SourceParsers.BoyDetail(
                        b,
                        "<h1>星 &amp; 海</h1>",
                        "{\"result\":{\"list\":[{\"id\":10,\"title\":\"1\"},{\"id\":11,\"title\":\"2\"}]}}"
                    );
                    Assert(b.Chapters[0].Id.EndsWith("10") && b.Title == "星 & 海", "order");
                })
        );
        await Test(
            "Manwa HTML parser + author label + same-name source identity",
            () =>
                Sync(() =>
                {
                    var html =
                        "<ul class='book-list'><li><a href='/book/21?from=x'><img data-original='/a.jpg'/></a><p class='book-list-info-title'>星海</p></li></ul>";
                    var b = SourceParsers.ManwaSearch(html, "https://mw.example.org").Single();
                    Assert(b.Id == "21" && b.Title == "星海", "listing");
                    html =
                        "<h1 class='detail-main-info-title'>星海</h1><p class='detail-main-info-author'>更新状态<span>已完结</span></p><p class='detail-main-info-author'>作者：<a>甲</a></p><ul id='detail-list-select'><li><a href='/chapter/1'>1</a></li><li><a href='/chapter/2'>2</a></li></ul>";
                    b = SourceParsers.ManwaDetail(b, html, "https://mw.example.org");
                    Assert(
                        b.Author == "甲" && b.State == "完结" && b.Chapters[0].Id == "/chapter/1",
                        "detail"
                    );
                })
        );
        await Test(
            "reader page parameters, encrypted query and referer",
            () =>
                Sync(() =>
                {
                    var pages = SourceParsers.Pages(
                        "manwa",
                        "<div id='cp_img'><div class='img-content'><img data-r-src='https://cdn.example.org/x.webp?v=20220724&amp;foo=1'/></div></div>",
                        "https://mw.example.org",
                        "https://mw.example.org",
                        "/chapter/2"
                    );
                    Assert(
                        pages.Single().Encrypted
                            && pages[0].Referer == "https://mw.example.org/chapter/2",
                        "page parameters"
                    );
                    pages = SourceParsers.Pages(
                        "boylove",
                        "<section><div class='reader-cartoon-image'><img src='/load.png' data-original='/x.webp'/></div></section><script>var randomClass = abc + 8; function firstMergeImg(){var imageData=1;} var scrollTop=0;</script>",
                        "https://boy.example.org",
                        "https://img.example.org",
                        "/home/book/capter/id/2"
                    );
                    Assert(pages[0].Strips == 8 && pages[0].Horizontal, "scramble");
                })
        );
        await Test(
            "publication plain/base64 links reject unrelated/social URLs",
            () =>
                Sync(() =>
                {
                    var encoded = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes("[\"https://mw2.example.org/\"]")
                    );
                    var links = Sources.PublicationLinks(
                        "<a href='https://mw.example.org'>line</a><a href='https://t.me/group'>chat</a><script>var lks=JSON.parse(atob('"
                            + encoded
                            + "'));</script>",
                        "https://pub.example.org"
                    );
                    Assert(
                        links.Count == 2 && !links.Any(l => l.Contains("t.me")),
                        "publication links"
                    );
                    Assert(
                        !Sources.PublicHttps("https://127.0.0.1")
                            && !Sources.PublicHttps("https://user:pass@example.org")
                            && !Sources.PublicHttps("http://example.org"),
                        "validation"
                    );
                })
        );
        await Test(
            "static base64 publication arrays decode without executing JavaScript",
            () =>
                Sync(() =>
                {
                    Func<string, string> encode = value =>
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
                    var links = Sources.PublicationLinks(
                        "<script>const e=['"
                            + encode("https://mw.example.org")
                            + "','"
                            + encode("https://t.me/group")
                            + "','"
                            + encode("http://127.0.0.1")
                            + "'].map((e=>atob(e)));</script>",
                        "https://pub.example.org"
                    );
                    Assert(
                        links.SequenceEqual(new[] { "https://mw.example.org" }),
                        "static publication array"
                    );
                })
        );
        await Test(
            "empty lazy reader image cannot fall back to advertisement src",
            () =>
                Sync(() =>
                {
                    var pages = SourceParsers.Pages(
                        "boylove",
                        "<img src='https://ads.example.org/outside.jpg'/><section><div class='reader-cartoon-image'><img src='https://ads.example.org/ad.jpg' data-original=''/><img data-original='/comic.webp'/></div></section>",
                        "https://boy.example.org",
                        "https://img.example.org",
                        "/chapter/1"
                    );
                    Assert(
                        pages.Count == 1 && pages[0].Url == "https://img.example.org/comic.webp",
                        "advertisement leaked into pages"
                    );
                })
        );
        await Test(
            "newer database refuses downgrade instead of replacing user data",
            async () =>
            {
                var disk = new MemoryStorage();
                await disk.WriteAtomic(
                    "library.json",
                    Encoding.UTF8.GetBytes("{\"Version\":99}"),
                    CancellationToken.None
                );
                await Throws<NotSupportedException>(() => Store.Open(disk));
                Assert(
                    Encoding.UTF8.GetString(await disk.Read("library.json", CancellationToken.None))
                        == "{\"Version\":99}",
                    "database overwritten"
                );
            }
        );
        await Test(
            "horizontal uneven-width pixel transform preserves every pixel",
            () =>
                Sync(() =>
                {
                    var src = Enumerable.Range(0, 44).Select(i => (byte)i).ToArray();
                    var dst = PixelTransforms.Reorder(src, 11, 1, 3, true, CancellationToken.None);
                    var expect = src.Skip(32)
                        .Take(12)
                        .Concat(src.Skip(20).Take(12))
                        .Concat(src.Take(20))
                        .ToArray();
                    Assert(dst.SequenceEqual(expect), "horizontal mapping");
                })
        );
        await Test(
            "JM odd-height strip coordinates cover all source/destination rows",
            () =>
                Sync(() =>
                {
                    var strips = JmImageScramble.GetStrips(6, 101);
                    var src = strips
                        .SelectMany(s => Enumerable.Range(s.SrcY, s.Height))
                        .OrderBy(i => i);
                    var dst = strips
                        .SelectMany(s => Enumerable.Range(s.DstY, s.Height))
                        .OrderBy(i => i);
                    Assert(
                        src.SequenceEqual(Enumerable.Range(0, 101))
                            && dst.SequenceEqual(Enumerable.Range(0, 101)),
                        "strip bijection"
                    );
                })
        );
        await Test(
            "AES CBC image decryption + memory budget + malformed cipher",
            () =>
                Sync(() =>
                {
                    var input = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
                    byte[] encoded;
                    using (var a = Aes.Create())
                    {
                        a.Key = Encoding.UTF8.GetBytes("my2ecret782ecret");
                        a.IV = a.Key;
                        a.Padding = PaddingMode.None;
                        a.Mode = CipherMode.CBC;
                        encoded = a.CreateEncryptor().TransformFinalBlock(input, 0, input.Length);
                    }
                    Assert(PixelTransforms.DecryptManwa(encoded).SequenceEqual(input), "AES CBC");
                    try
                    {
                        PixelTransforms.CheckBudget(10000, 10000, 1000, true, true);
                        throw new Exception("budget accepted");
                    }
                    catch (InvalidOperationException) { }
                    try
                    {
                        JmCrypto.DecodeData("AA==", "", JmCrypto.DataSecret);
                        throw new Exception("bad cipher accepted");
                    }
                    catch (CryptographicException) { }
                })
        );
        await SourceTransportTests();
        await DownloadTests();
        Console.WriteLine("TOTAL " + passed + " PASS");
    }

    static async Task SourceTransportTests()
    {
        await Test(
            "web/API failover and malformed response rejection",
            async () =>
            {
                var disk = new MemoryStorage();
                var s = await Store.Open(disk);
                s.Data.Sources.Single(c => c.Id == "boylove").Hosts = new List<string>
                {
                    "https://bad.example.org",
                    "https://good.example.org",
                };
                var h = new Handler
                {
                    Send = (req, ct) =>
                        Task.FromResult(
                            req.RequestUri.Host.StartsWith("bad")
                                ? Reply("Google")
                                : Reply("{\"result\":{\"list\":[{\"id\":1,\"title\":\"星\"}]}}")
                        ),
                };
                using (var sources = new Sources(s, h))
                {
                    Assert(
                        (await sources.Search("boylove", "星", 1, CancellationToken.None)).Count
                            == 1
                            && sources.Base("boylove") == "https://good.example.org",
                        "failover"
                    );
                }
            }
        );
        await Test(
            "failed publication sync keeps old working hosts",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                var before = s.Data.Sources.Single(c => c.Id == "manwa").Hosts.ToArray();
                using (
                    var sources = new Sources(
                        s,
                        new Handler
                        {
                            Send = (r, ct) =>
                                Task.FromResult(Reply("<html><title>Google</title></html>")),
                        }
                    )
                )
                {
                    await Throws<InvalidOperationException>(
                        () => sources.Sync("manwa", CancellationToken.None)
                    );
                    Assert(sources.Config("manwa").Hosts.SequenceEqual(before), "lost lines");
                }
            }
        );
        await Test(
            "validated publication sync updates website only",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                var h = new Handler
                {
                    Send = (r, ct) =>
                        Task.FromResult(
                            Reply(
                                r.RequestUri.Host.Contains("manwala")
                                    ? "<a href='https://mw.example.org'>line</a>"
                                    : "<ul class='manga-list-2'></ul>"
                            )
                        ),
                };
                using (var source = new Sources(s, h))
                {
                    await source.Sync("manwa", CancellationToken.None);
                    Assert(
                        source.Config("manwa").Active == "https://mw.example.org"
                            && source.Config("manwa").Synced != default(DateTime),
                        "validated sync"
                    );
                }
            }
        );
        await Test(
            "JM encrypted API uses same request timestamp for decode",
            async () =>
            {
                var time = 123456789L;
                var h = new Handler
                {
                    Send = (r, ct) =>
                    {
                        Assert(
                            r.Headers.GetValues("token").Single()
                                == JmCrypto.BuildToken(time).Token,
                            "token"
                        );
                        var key = Encoding.UTF8.GetBytes(
                            JmCrypto.Md5Hex(time + JmCrypto.DataSecret)
                        );
                        using (var a = Aes.Create())
                        {
                            a.Key = key;
                            a.Mode = CipherMode.ECB;
                            a.Padding = PaddingMode.PKCS7;
                            var data = Encoding.UTF8.GetBytes("{\"content\":[]}");
                            var encoded = Convert.ToBase64String(
                                a.CreateEncryptor().TransformFinalBlock(data, 0, data.Length)
                            );
                            return Task.FromResult(
                                Reply("{\"code\":200,\"data\":\"" + encoded + "\"}")
                            );
                        }
                    },
                };
                using (var jm = new JmClient(new[] { "api.example.org" }, h, () => time))
                {
                    var result = await jm.SearchAsync("星", ct: CancellationToken.None);
                    Assert(result.IsOk && result.Data.Contains("content"), "decode");
                }
            }
        );
    }

    static async Task DownloadTests()
    {
        await Test(
            "download concurrency bounded, decoder serial, hashes and complete state",
            async () =>
            {
                var disk = new MemoryStorage();
                var s = await Store.Open(disk);
                s.Data.Settings.Concurrency = 3;
                int active = 0,
                    max = 0;
                var h = new Handler
                {
                    Send = async (r, ct) =>
                    {
                        int n = Interlocked.Increment(ref active);
                        max = Math.Max(max, n);
                        try
                        {
                            await Task.Delay(30, ct);
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new ByteArrayContent(
                                    Jpeg(
                                        int.Parse(r.RequestUri.AbsolutePath.Trim('/').Split('.')[0])
                                    )
                                ),
                            };
                        }
                        finally
                        {
                            Interlocked.Decrement(ref active);
                        }
                    },
                };
                using (var sources = new Sources(s, h))
                {
                    var codec = new FakeCodec();
                    var images = new ImageService(s, sources, codec);
                    var content = new FakeContent
                    {
                        PageList = Enumerable.Range(0, 7).Select(Page).ToList(),
                    };
                    using (var engine = new DownloadEngine(s, content, images, new FakeNetwork()))
                    {
                        await engine.Add(Book(), Chapter());
                        await Idle(engine);
                        var j = s.Data.Downloads.Single();
                        Assert(
                            j.State == "已完成"
                                && j.Done.Count == 7
                                && j.Saved.Count == 7
                                && max > 1
                                && max <= 3
                                && codec.MaxActive == 1,
                            "scheduler"
                        );
                    }
                }
            }
        );
        await Test(
            "failed image retries bounded and never marks chapter complete",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                var h = new Handler
                {
                    Send = (r, ct) =>
                        Task.FromResult(
                            new HttpResponseMessage(
                                r.RequestUri.AbsolutePath.Contains("1.jpg")
                                    ? HttpStatusCode.BadGateway
                                    : HttpStatusCode.OK
                            )
                            {
                                Content = new ByteArrayContent(Jpeg(0)),
                            }
                        ),
                };
                using (var sources = new Sources(s, h))
                using (
                    var engine = new DownloadEngine(
                        s,
                        new FakeContent
                        {
                            PageList = new List<PageInfo> { Page(0), Page(1) },
                        },
                        new ImageService(s, sources, new FakeCodec()),
                        new FakeNetwork()
                    )
                )
                {
                    await engine.Add(Book(), Chapter());
                    await Idle(engine);
                    var j = s.Data.Downloads.Single();
                    Assert(
                        j.State == "失败" && j.Done.Count == 1 && h.Counts[Page(1).Url] == 3,
                        "false complete or retry bound"
                    );
                }
            }
        );
        await Test(
            "same-length changed/reordered chapter manifest invalidates stale pages",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                var h = new Handler
                {
                    Send = (r, ct) =>
                        Task.FromResult(
                            new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new ByteArrayContent(
                                    Jpeg(r.RequestUri.AbsolutePath.Contains("1.jpg") ? 1 : 0)
                                ),
                            }
                        ),
                };
                var content = new FakeContent
                {
                    PageList = new List<PageInfo> { Page(0), Page(1) },
                };
                using (var sources = new Sources(s, h))
                using (
                    var engine = new DownloadEngine(
                        s,
                        content,
                        new ImageService(s, sources, new FakeCodec()),
                        new FakeNetwork()
                    )
                )
                {
                    await engine.Add(Book(), Chapter());
                    await Idle(engine);
                    var j = s.Data.Downloads.Single();
                    content.PageList.Reverse();
                    await engine.Resume(j);
                    await Idle(engine);
                    Assert(
                        j.State == "已完成"
                            && j.Saved[0].Resource == "/1.jpg"
                            && (
                                await s.Storage.Read(
                                    Store.DownloadPath(j.Key, 0),
                                    CancellationToken.None
                                )
                            )[3] == 1,
                        "stale manifest"
                    );
                }
            }
        );
        await Test(
            "pause cancels requests, persists paused state; resume fills missing pages",
            async () =>
            {
                var disk = new MemoryStorage();
                var s = await Store.Open(disk);
                var h = new Handler
                {
                    Send = async (r, ct) =>
                    {
                        await Task.Delay(100, ct);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(Jpeg(1)),
                        };
                    },
                };
                using (var sources = new Sources(s, h))
                using (
                    var engine = new DownloadEngine(
                        s,
                        new FakeContent { PageList = Enumerable.Range(0, 5).Select(Page).ToList() },
                        new ImageService(s, sources, new FakeCodec()),
                        new FakeNetwork()
                    )
                )
                {
                    await engine.Add(Book(), Chapter());
                    await Task.Delay(40);
                    var j = s.Data.Downloads.Single();
                    await engine.Pause(s.Snapshot().Downloads.Single());
                    await Idle(engine);
                    Assert(j.State == "已暂停" && j.Done.Count < 5, "pause failed");
                    var restored = await Store.Open(disk);
                    Assert(restored.Data.Downloads[0].State == "已暂停", "pause persistence");
                    await engine.Resume(s.Snapshot().Downloads.Single());
                    await Idle(engine);
                    Assert(j.State == "已完成" && j.Done.Count == 5, "resume failed");
                }
            }
        );
        await Test(
            "network wait automatically wakes; suspended engine stays stopped",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                var network = new FakeNetwork { Available = false };
                var h = new Handler
                {
                    Send = (r, ct) =>
                        Task.FromResult(
                            new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new ByteArrayContent(Jpeg(1)),
                            }
                        ),
                };
                using (var sources = new Sources(s, h))
                using (
                    var engine = new DownloadEngine(
                        s,
                        new FakeContent { PageList = new List<PageInfo> { Page(0) } },
                        new ImageService(s, sources, new FakeCodec()),
                        network
                    )
                )
                {
                    await engine.Add(Book(), Chapter());
                    await Idle(engine);
                    Assert(
                        s.Data.Downloads[0].State == "等待" && h.Counts.Count == 0,
                        "network wait"
                    );
                    await engine.Stop();
                    network.Set(true);
                    await Task.Delay(40);
                    Assert(h.Counts.Count == 0, "suspended woke");
                    engine.Start();
                    await Idle(engine);
                    Assert(s.Data.Downloads[0].State == "已完成", "restart");
                }
            }
        );
        await Test(
            "pause while waiting for reader cannot restart the paused job",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                var h = new Handler
                {
                    Send = (r, ct) =>
                        Task.FromResult(
                            new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new ByteArrayContent(Jpeg(1)),
                            }
                        ),
                };
                using (var source = new Sources(s, h))
                {
                    var images = new ImageService(s, source, new FakeCodec()) { Reading = true };
                    using (
                        var engine = new DownloadEngine(
                            s,
                            new FakeContent { PageList = new List<PageInfo> { Page(0) } },
                            images,
                            new FakeNetwork()
                        )
                    )
                    {
                        await engine.Add(Book(), Chapter());
                        await Task.Delay(50);
                        await engine.Pause(s.Snapshot().Downloads.Single());
                        images.Reading = false;
                        await Idle(engine);
                        Assert(
                            s.Data.Downloads.Single().State == "已暂停" && h.Counts.Count == 0,
                            "paused job restarted after reading"
                        );
                        await engine.Resume(s.Snapshot().Downloads.Single());
                        await Idle(engine);
                        Assert(
                            s.Data.Downloads.Single().State == "已完成",
                            "paused job could not resume"
                        );
                    }
                }
            }
        );
        await Test(
            "image CDN failover + same-page dedup + corrupt file detection",
            async () =>
            {
                var disk = new MemoryStorage();
                var s = await Store.Open(disk);
                var p = Page(0);
                p.Alternatives.Add("https://alternate.example.org/0.jpg");
                var h = new Handler
                {
                    Send = (r, ct) =>
                        Task.FromResult(
                            new HttpResponseMessage(
                                r.RequestUri.Host == "images.example.org"
                                    ? HttpStatusCode.ServiceUnavailable
                                    : HttpStatusCode.OK
                            )
                            {
                                Content = new ByteArrayContent(Jpeg(5)),
                            }
                        ),
                };
                using (var source = new Sources(s, h))
                {
                    var images = new ImageService(s, source, new FakeCodec());
                    var b = Book();
                    var c = Chapter();
                    var job = new DownloadJob
                    {
                        Key = Store.ChapterKey(b, c),
                        Book = b,
                        Chapter = c,
                        Pages = new List<PageInfo> { p },
                    };
                    s.Data.Downloads.Add(job);
                    await Task.WhenAll(
                        images.GetPage(b, c, p, 0, true, CancellationToken.None),
                        images.GetPage(b, c, p, 0, true, CancellationToken.None)
                    );
                    Assert(h.Counts.Values.Sum() == 2 && job.Done.Count == 1, "duplicate pipeline");
                    disk.Files[Store.DownloadPath(job.Key, 0)] = Jpeg(6);
                    Assert(
                        (await images.Reconcile(job, CancellationToken.None)).Count == 0,
                        "same-size corruption accepted"
                    );
                    await images.GetPage(b, c, p, 0, true, CancellationToken.None);
                    Assert(
                        (await images.Reconcile(job, CancellationToken.None)).Count == 1,
                        "repair"
                    );
                }
            }
        );
        await Test(
            "cache cleanup does not delete downloads/favorites/history",
            async () =>
            {
                var s = await Store.Open(new MemoryStorage());
                await s.SetFavorite(Book(), true);
                await s.Record(Book(), Chapter(), 1, 0);
                await s.Storage.WriteAtomic("cache/x.jpg", Jpeg(1), CancellationToken.None);
                await s.Storage.WriteAtomic(
                    "downloads/sample/0.jpg",
                    Jpeg(1),
                    CancellationToken.None
                );
                using (
                    var source = new Sources(
                        s,
                        new Handler { Send = (r, ct) => Task.FromResult(Reply("")) }
                    )
                )
                {
                    await new ImageService(s, source, new FakeCodec()).ClearCache();
                    Assert(
                        await s.Storage.Read("cache/x.jpg", CancellationToken.None) == null
                            && await s.Storage.Read(
                                "downloads/sample/0.jpg",
                                CancellationToken.None
                            ) != null
                            && s.Data.Favorites.Count == 1
                            && s.Data.History.Count == 1,
                        "cache separation"
                    );
                }
            }
        );
    }

    static async Task Live()
    {
        var s = await Store.Open(new MemoryStorage());
        using (var source = new Sources(s))
        {
            foreach (var id in new[] { "boylove", "jm", "manwa" })
                using (var ct = new CancellationTokenSource(TimeSpan.FromSeconds(55)))
                {
                    try
                    {
                        var list = await source.Search(id, "星", 1, ct.Token);
                        Console.WriteLine(id + " search rows=" + list.Count);
                        if (list.Count > 0)
                        {
                            var detail = await source.Detail(list[0], ct.Token);
                            Console.WriteLine(id + " detail chapters=" + detail.Chapters.Count);
                            if (detail.Chapters.Count > 0)
                                Console.WriteLine(
                                    id
                                        + " pages="
                                        + (
                                            await source.Pages(detail, detail.Chapters[0], ct.Token)
                                        ).Count
                                );
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(
                            id + " LIVE BLOCKED: " + e.GetType().Name + " " + e.Message
                        );
                    }
                }
            foreach (var id in new[] { "boylove", "jm", "manwa" })
                using (var ct = new CancellationTokenSource(TimeSpan.FromSeconds(55)))
                {
                    try
                    {
                        await source.Sync(id, ct.Token);
                        Console.WriteLine(id + " synced lines=" + source.Config(id).Hosts.Count);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(
                            id + " SYNC BLOCKED: " + e.GetType().Name + " " + e.Message
                        );
                    }
                }
            using (var ct = new CancellationTokenSource(TimeSpan.FromSeconds(55)))
            {
                try
                {
                    var books = await source.Search("jm", "星", 1, ct.Token);
                    Console.WriteLine("jm after-sync rows=" + books.Count);
                    if (books.Count > 0)
                    {
                        var b = await source.Detail(books[0], ct.Token);
                        Console.WriteLine("jm after-sync chapters=" + b.Chapters.Count);
                        Console.WriteLine(
                            "jm after-sync pages="
                                + (await source.Pages(b, b.Chapters[0], ct.Token)).Count
                        );
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(
                        "jm after-sync BLOCKED: " + e.GetType().Name + " " + e.Message
                    );
                }
            }
        }
    }

    static async Task JmLive()
    {
        var store = await Store.Open(new MemoryStorage());
        using (var source = new Sources(store))
        using (var ct = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
        {
            try
            {
                await source.Sync("jm", ct.Token);
                Console.WriteLine("JM sync verified lines=" + source.Config("jm").Hosts.Count);
                var books = await source.Search("jm", "星", 1, ct.Token);
                Console.WriteLine("JM search rows=" + books.Count);
                if (books.Count == 0)
                {
                    using (var jm = new JmClient(new[] { new Uri(source.Base("jm")).Host }))
                    {
                        var response = await jm.SearchAsync("星", ct: ct.Token);
                        var root = Newtonsoft.Json.Linq.JObject.Parse(response.Data);
                        Console.WriteLine(
                            "JM response fields="
                                + string.Join(",", root.Properties().Select(p => p.Name))
                        );
                        var list = root["content"] as Newtonsoft.Json.Linq.JArray;
                        Console.WriteLine(
                            "JM raw content count=" + (list == null ? -1 : list.Count)
                        );
                        if (list != null && list.Count > 0)
                        {
                            Console.WriteLine(
                                "JM row fields="
                                    + string.Join(
                                        ",",
                                        ((Newtonsoft.Json.Linq.JObject)list[0])
                                            .Properties()
                                            .Select(p => p.Name)
                                    )
                            );
                            books.Add(
                                new Book
                                {
                                    Source = "jm",
                                    Id = (string)list[0]["id"],
                                    Title = (string)list[0]["id"],
                                }
                            );
                        }
                    }
                }
                if (books.Count == 0)
                    books = await source.Search("jm", "", 1, ct.Token);
                if (books.Count > 0)
                {
                    var detail = await source.Detail(books[0], ct.Token);
                    Console.WriteLine("JM chapters=" + detail.Chapters.Count);
                    var pages = await source.Pages(detail, detail.Chapters[0], ct.Token);
                    Console.WriteLine("JM pages=" + pages.Count);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("JM BLOCKED " + e.GetType().Name + " " + e.Message);
                Environment.ExitCode = 2;
            }
        }
    }

    static async Task BoyLive()
    {
        var store = await Store.Open(new MemoryStorage());
        using (var source = new Sources(store))
        using (var ct = new CancellationTokenSource(TimeSpan.FromSeconds(75)))
        {
            try
            {
                var b = await source.Detail(
                    new Book
                    {
                        Source = "boylove",
                        Id = "20743",
                        Title = "20743",
                    },
                    ct.Token
                );
                Console.WriteLine("BOY detail chapters=" + b.Chapters.Count);
                var pages = await source.Pages(b, b.Chapters[0], ct.Token);
                Console.WriteLine("BOY pages=" + pages.Count);
                var search = await source.Search("boylove", "星", 1, ct.Token);
                Console.WriteLine("BOY search rows=" + search.Count);
            }
            catch (Exception e)
            {
                Console.WriteLine("BOY BLOCKED " + e.GetType().Name + " " + e.Message);
                Environment.ExitCode = 2;
            }
        }
    }
}
