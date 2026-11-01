using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Moyue
{
    public sealed partial class DownloadEngine : IDisposable
    {
        readonly Store store;
        readonly IContentSource sources;
        readonly ImageService images;
        readonly INetworkPolicy network;
        readonly object gate = new object();
        readonly SemaphoreSlim commands = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, CancellationTokenSource> active =
            new Dictionary<string, CancellationTokenSource>();
        CancellationTokenSource lifetime;
        Task run = Task.FromResult(0);
        bool suspended,
            disposed;
        public event Action Changed;
        public string LastError { get; private set; }

        public DownloadEngine(
            Store s,
            IContentSource sources,
            ImageService images,
            INetworkPolicy network
        )
        {
            store = s;
            this.sources = sources;
            this.images = images;
            this.network = network;
            network.Changed += NetworkChanged;
        }

        public Task Completion
        {
            get
            {
                lock (gate)
                    return run;
            }
        }

        void Notify()
        {
            var handler = Changed;
            if (handler == null)
                return;
            foreach (Action subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber();
                }
                catch (Exception e)
                {
                    System.Diagnostics.Debug.WriteLine(e);
                }
            }
        }

        DownloadJob FindJob(DownloadJob input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            lock (store.SyncRoot)
            {
                var job = store.Data.Downloads.FirstOrDefault(j => j.Key == input.Key);
                if (job == null)
                    throw new KeyNotFoundException("下载任务不存在");
                return job;
            }
        }

        void NetworkChanged()
        {
            lock (gate)
                if (!suspended && !disposed)
                    Start();
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                suspended = false;
                if (!run.IsCompleted)
                    return;
                lifetime?.Dispose();
                lifetime = new CancellationTokenSource();
                var token = lifetime.Token;
                run = Task.Run(() => Run(token));
            }
        }

        public void Suspend()
        {
            lock (gate)
            {
                suspended = true;
                lifetime?.Cancel();
            }
        }

        public async Task Stop()
        {
            Suspend();
            await Completion.ConfigureAwait(false);
        }

        public async Task Add(Book b, Chapter c)
        {
            Store.Validate(b);
            if (c == null || string.IsNullOrWhiteSpace(c.Id))
                throw new ArgumentException("章节不能为空");
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (store.SyncRoot)
                {
                    var key = Store.ChapterKey(b, c);
                    var old = store.Data.Downloads.FirstOrDefault(j => j.Key == key);
                    if (old == null)
                        store.Data.Downloads.Add(
                            new DownloadJob
                            {
                                Book = b,
                                Chapter = c,
                                Key = key,
                            }
                        );
                    else if (old.State == "失败" || old.State == "已暂停")
                        old.State = "等待";
                }
                await store.Save().ConfigureAwait(false);
            }
            finally
            {
                commands.Release();
            }
            Notify();
            Start();
        }

        public async Task Pause(DownloadJob j)
        {
            j = FindJob(j);
            lock (store.SyncRoot)
                if (j.State != "已完成")
                    j.State = "已暂停";
            lock (gate)
            {
                CancellationTokenSource ct;
                if (active.TryGetValue(j.Key, out ct))
                    ct.Cancel();
            }
            await store.Save().ConfigureAwait(false);
            Notify();
        }

        public async Task Resume(DownloadJob j)
        {
            j = FindJob(j);
            await Pause(j).ConfigureAwait(false);
            while (true)
            {
                lock (gate)
                    if (!active.ContainsKey(j.Key))
                        break;
                await Task.Delay(20).ConfigureAwait(false);
            }
            lock (store.SyncRoot)
            {
                j.State = "等待";
                j.Error = null;
            }
            await store.Save().ConfigureAwait(false);
            Notify();
            Start();
        }

        public async Task Remove(DownloadJob j)
        {
            j = FindJob(j);
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                await Pause(j).ConfigureAwait(false);
                while (true)
                {
                    lock (gate)
                        if (!active.ContainsKey(j.Key))
                            break;
                    await Task.Delay(20).ConfigureAwait(false);
                }
                await store
                    .Storage.Delete("downloads/" + Store.Safe(j.Key), CancellationToken.None)
                    .ConfigureAwait(false);
                lock (store.SyncRoot)
                    store.Data.Downloads.RemoveAll(x => x.Key == j.Key);
                await store.Save().ConfigureAwait(false);
            }
            finally
            {
                commands.Release();
            }
            Notify();
        }

        bool Allowed()
        {
            bool wifi;
            lock (store.SyncRoot)
                wifi = store.Data.Settings.WifiOnly;
            return network.CanDownload(wifi);
        }

        bool Reading()
        {
            lock (store.SyncRoot)
                return images.Reading && store.Data.Settings.ReadingPriority;
        }

        async Task Run(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    DownloadJob job;
                    lock (store.SyncRoot)
                        job = store.Data.Downloads.FirstOrDefault(j => j.State == "等待");
                    if (job == null)
                        return;
                    if (!Allowed())
                    {
                        lock (store.SyncRoot)
                            job.Error = "等待符合设置的网络连接";
                        Notify();
                        return;
                    }
                    while (Reading())
                    {
                        lock (store.SyncRoot)
                            if (job.State != "等待")
                                break;
                        await Task.Delay(200, ct).ConfigureAwait(false);
                    }
                    using (var child = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        lock (gate)
                        {
                            lock (store.SyncRoot)
                            {
                                // Pause can run while the selected job waits for the reader.
                                // Claim it atomically with registering its cancellation token.
                                if (job.State != "等待")
                                    continue;
                                job.State = "下载中";
                                job.Error = null;
                                active[job.Key] = child;
                            }
                        }
                        Notify();
                        try
                        {
                            var pages = await sources
                                .Pages(job.Book, job.Chapter, child.Token)
                                .ConfigureAwait(false);
                            if (pages == null || pages.Count == 0)
                                throw new InvalidOperationException("章节图片列表为空");
                            lock (store.SyncRoot)
                                job.Pages = pages;
                            await images.Reconcile(job, child.Token).ConfigureAwait(false);
                            await store.Save(child.Token).ConfigureAwait(false);
                            int next = -1,
                                concurrency;
                            lock (store.SyncRoot)
                                concurrency = Math.Max(
                                    1,
                                    Math.Min(
                                        Settings.MaxConcurrency,
                                        store.Data.Settings.Concurrency
                                    )
                                );
                            var failures = new List<string>();
                            var workers = new List<Task>();
                            for (int w = 0; w < concurrency; w++)
                                workers.Add(
                                    Worker(
                                        job,
                                        () => Interlocked.Increment(ref next),
                                        failures,
                                        child.Token
                                    )
                                );
                            await Task.WhenAll(workers).ConfigureAwait(false);
                            child.Token.ThrowIfCancellationRequested();
                            var valid = await images
                                .Reconcile(job, child.Token)
                                .ConfigureAwait(false);
                            lock (store.SyncRoot)
                            {
                                job.State = valid.Count == pages.Count ? "已完成" : "失败";
                                job.Error = failures.FirstOrDefault();
                                if (job.State == "失败" && job.Error == null)
                                    job.Error = "文件校验失败";
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            lock (store.SyncRoot)
                                if (job.State == "下载中")
                                    job.State = "等待";
                        }
                        catch (Exception e)
                        {
                            lock (store.SyncRoot)
                            {
                                job.State = "失败";
                                job.Error = e.Message;
                            }
                        }
                        finally
                        {
                            try
                            {
                                await store.Save().ConfigureAwait(false);
                            }
                            finally
                            {
                                lock (gate)
                                    active.Remove(job.Key);
                                Notify();
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                LastError = e.Message;
                Notify();
            }
            finally
            {
                // Close the Add/Resume vs. an idle loop race. A network-waiting queue must not spin.
                lock (gate)
                {
                    if (!disposed && !suspended && Allowed())
                    {
                        bool waiting;
                        lock (store.SyncRoot)
                            waiting = store.Data.Downloads.Any(j => j.State == "等待");
                        if (waiting)
                        {
                            lifetime = new CancellationTokenSource();
                            var token = lifetime.Token;
                            run = Task.Run(() => Run(token));
                        }
                    }
                }
            }
        }

        async Task Worker(
            DownloadJob job,
            Func<int> take,
            List<string> failures,
            CancellationToken ct
        )
        {
            int i;
            while ((i = take()) < job.Pages.Count)
            {
                ct.ThrowIfCancellationRequested();
                lock (store.SyncRoot)
                    if (job.Done.Contains(i))
                        continue;
                while (Reading())
                    await Task.Delay(200, ct).ConfigureAwait(false);
                if (!Allowed())
                    throw new OperationCanceledException("下载网络条件变化");
                Exception last = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await images
                            .GetPage(job.Book, job.Chapter, job.Pages[i], i, true, ct)
                            .ConfigureAwait(false);
                        last = null;
                        Notify();
                        break;
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        last = e;
                        if (attempt < 2)
                            await Task.Delay(200 * (attempt + 1), ct).ConfigureAwait(false);
                    }
                }
                if (last != null)
                    lock (failures)
                        failures.Add("第 " + (i + 1) + " 页：" + last.Message);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                lifetime?.Cancel();
            }
            network.Changed -= NetworkChanged;
        }
    }
}
