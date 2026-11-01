using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Moyue
{
    public sealed partial class ImageService
    {
        readonly Store store;
        readonly Sources sources;
        readonly IImageCodec codec;
        readonly SemaphoreSlim pipeline = new SemaphoreSlim(
                Settings.MaxConcurrency,
                Settings.MaxConcurrency
            ),
            decoder = new SemaphoreSlim(1, 1),
            cacheGate = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, PageLock> pageLocks = new Dictionary<string, PageLock>();

        sealed class PageLock
        {
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            public int Users;
        }

        public volatile bool Reading;

        public ImageService(Store store, Sources sources, IImageCodec codec)
        {
            this.store = store;
            this.sources = sources;
            this.codec = codec;
        }

        async Task<IDisposable> LockPage(string key, CancellationToken ct)
        {
            PageLock entry;
            lock (pageLocks)
            {
                if (!pageLocks.TryGetValue(key, out entry))
                    pageLocks[key] = entry = new PageLock();
                entry.Users++;
            }
            try
            {
                await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                lock (pageLocks)
                {
                    if (--entry.Users == 0)
                    {
                        pageLocks.Remove(key);
                        entry.Gate.Dispose();
                    }
                }
                throw;
            }
            return new Release(() =>
            {
                entry.Gate.Release();
                lock (pageLocks)
                {
                    if (--entry.Users == 0)
                    {
                        pageLocks.Remove(key);
                        entry.Gate.Dispose();
                    }
                }
            });
        }

        sealed class Release : IDisposable
        {
            Action action;

            public Release(Action a)
            {
                action = a;
            }

            public void Dispose()
            {
                var a = Interlocked.Exchange(ref action, null);
                if (a != null)
                    a();
            }
        }

        public async Task<string> GetPage(
            Book b,
            Chapter c,
            PageInfo p,
            int index,
            bool download,
            CancellationToken ct
        )
        {
            Store.Validate(b);
            if (c == null || p == null || index < 0)
                throw new ArgumentException("无效的图片请求");
            string chapterKey = Store.ChapterKey(b, c),
                path = Store.DownloadPath(chapterKey, index);
            using (await LockPage(path, ct).ConfigureAwait(false))
            {
                var bytes = await store.Storage.Read(path, ct).ConfigureAwait(false);
                DownloadJob job;
                SavedPage saved;
                lock (store.SyncRoot)
                {
                    job = store.Data.Downloads.FirstOrDefault(j => j.Key == chapterKey);
                    saved = job != null && job.Saved.ContainsKey(index) ? job.Saved[index] : null;
                }
                if (Valid(bytes, saved, p))
                    return path;
                var cacheKey =
                    "cache/"
                    + Store.Safe(
                        chapterKey
                            + ":"
                            + index
                            + ":"
                            + p.Resource
                            + ":"
                            + p.Strips
                            + ":"
                            + p.Horizontal
                            + ":"
                            + p.Encrypted
                    )
                    + ".jpg";
                bytes = await store.Storage.Read(cacheKey, ct).ConfigureAwait(false);
                if (!PixelTransforms.Jpeg(bytes))
                {
                    await pipeline.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        Exception last = null;
                        List<string> urls;
                        bool failover,
                            old;
                        lock (store.SyncRoot)
                        {
                            failover = store.Data.Settings.Failover;
                            old = store.Data.Settings.OldDevice;
                        }
                        urls = new[] { p.Url }
                            .Concat(p.Alternatives ?? new List<string>())
                            .Where(u => !string.IsNullOrEmpty(u))
                            .Distinct()
                            .Take(failover ? 4 : 1)
                            .ToList();
                        foreach (var url in urls)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                var raw = await sources
                                    .Bytes(
                                        url,
                                        p.Referer ?? sources.Base(b.Source),
                                        ct,
                                        old ? 8 * 1024 * 1024 : 32 * 1024 * 1024
                                    )
                                    .ConfigureAwait(false);
                                await decoder.WaitAsync(ct).ConfigureAwait(false);
                                try
                                {
                                    Settings settings;
                                    lock (store.SyncRoot)
                                    {
                                        var current = store.Data.Settings;
                                        settings = new Settings
                                        {
                                            OldDevice = current.OldDevice,
                                            ReadingPriority = current.ReadingPriority,
                                            WifiOnly = current.WifiOnly,
                                            AutoSync = current.AutoSync,
                                            Failover = current.Failover,
                                            Concurrency = current.Concurrency,
                                            Preload = current.Preload,
                                            CacheMb = current.CacheMb,
                                        };
                                    }
                                    bytes = await codec
                                        .ToJpeg(raw, p, settings, ct)
                                        .ConfigureAwait(false);
                                }
                                finally
                                {
                                    decoder.Release();
                                }
                                if (!PixelTransforms.Jpeg(bytes))
                                    throw new FormatException("解码器未返回完整 JPEG 图片");
                                last = null;
                                break;
                            }
                            catch (Exception e) when (!ct.IsCancellationRequested)
                            {
                                last = e;
                                Log.E("img", "图片获取/解码失败 " + url, e);
                            }
                        }
                        if (last != null || !PixelTransforms.Jpeg(bytes))
                        {
                            Log.E("img", "所有图片线路失败 " + (p.Identity ?? ""), last);
                            throw new InvalidOperationException("所有图片线路失败", last);
                        }
                    }
                    finally
                    {
                        pipeline.Release();
                    }
                    await store.Storage.WriteAtomic(cacheKey, bytes, ct).ConfigureAwait(false);
                }
                if (download)
                {
                    await store.Storage.WriteAtomic(path, bytes, ct).ConfigureAwait(false);
                    var commit = new SavedPage
                    {
                        Resource = p.Resource,
                        Hash = PixelTransforms.Hash(bytes),
                        Size = bytes.Length,
                    };
                    lock (store.SyncRoot)
                    {
                        job = store.Data.Downloads.FirstOrDefault(j => j.Key == chapterKey);
                        if (job == null)
                            throw new InvalidOperationException("下载任务已被删除");
                        job.Saved[index] = commit;
                        if (!job.Done.Contains(index))
                            job.Done.Add(index);
                    }
                    await store.Save(ct).ConfigureAwait(false);
                }
                await Trim(ct, cacheKey).ConfigureAwait(false);
                return download ? path : cacheKey;
            }
        }

        public static bool Valid(byte[] bytes, SavedPage saved, PageInfo p)
        {
            return PixelTransforms.Jpeg(bytes)
                && saved != null
                && saved.Resource == p.Resource
                && saved.Size == bytes.Length
                && saved.Hash == PixelTransforms.Hash(bytes);
        }

        public async Task<List<int>> Reconcile(DownloadJob job, CancellationToken ct)
        {
            var done = new List<int>();
            for (int i = 0; i < job.Pages.Count; i++)
            {
                SavedPage saved;
                lock (store.SyncRoot)
                    saved = job.Saved.ContainsKey(i) ? job.Saved[i] : null;
                if (
                    Valid(
                        await store
                            .Storage.Read(Store.DownloadPath(job.Key, i), ct)
                            .ConfigureAwait(false),
                        saved,
                        job.Pages[i]
                    )
                )
                    done.Add(i);
                else
                    lock (store.SyncRoot)
                        job.Saved.Remove(i);
            }
            lock (store.SyncRoot)
                job.Done = done;
            return done;
        }

        async Task Trim(CancellationToken ct, string protectedKey)
        {
            await cacheGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var files = await store.Storage.List("cache", ct).ConfigureAwait(false);
                long total = files.Sum(f => f.Size),
                    limit;
                lock (store.SyncRoot)
                    limit = (long)store.Data.Settings.CacheMb * 1024 * 1024;
                foreach (var file in files.OrderBy(f => f.Modified))
                {
                    if (total <= limit)
                        break;
                    if (file.Key == protectedKey)
                        continue;
                    try
                    {
                        await store.Storage.Delete(file.Key, ct).ConfigureAwait(false);
                        total -= file.Size;
                    }
                    catch (System.IO.IOException) { }
                }
            }
            finally
            {
                cacheGate.Release();
            }
        }

        public async Task ClearCache()
        {
            await cacheGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await store.Storage.Delete("cache", CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                cacheGate.Release();
            }
        }
    }
}
