using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moyue;

public sealed class MemoryStorage : IBackendStorage
{
    public readonly ConcurrentDictionary<string, byte[]> Files =
        new ConcurrentDictionary<string, byte[]>();
    public bool FailWrite;

    public Task<byte[]> Read(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] b;
        return Task.FromResult(Files.TryGetValue(key, out b) ? (byte[])b.Clone() : null);
    }

    public Task WriteAtomic(string key, byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FailWrite)
            throw new System.IO.IOException("injected disk failure");
        Files[key] = (byte[])bytes.Clone();
        return Task.CompletedTask;
    }

    public Task Delete(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var k in Files.Keys.Where(k => k == key || k.StartsWith(key + "/")))
        {
            byte[] value;
            Files.TryRemove(k, out value);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BackendFile>> List(string prefix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            (IReadOnlyList<BackendFile>)
                Files
                    .Where(p => p.Key.StartsWith(prefix + "/"))
                    .Select(p => new BackendFile
                    {
                        Key = p.Key,
                        Size = p.Value.Length,
                        Modified = DateTime.UtcNow,
                    })
                    .ToList()
        );
    }
}

public sealed class Handler : HttpMessageHandler
{
    public new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send;
    public readonly ConcurrentDictionary<string, int> Counts =
        new ConcurrentDictionary<string, int>();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req,
        CancellationToken ct
    )
    {
        Counts.AddOrUpdate(req.RequestUri.ToString(), 1, (k, v) => v + 1);
        return Send(req, ct);
    }
}

public sealed class FakeContent : IContentSource
{
    public Func<string, string, int, CancellationToken, Task<List<Book>>> SearchCall;
    public Func<Book, CancellationToken, Task<Book>> DetailCall;
    public List<PageInfo> PageList = new List<PageInfo>();

    public Task<List<Book>> Search(string id, string q, int p, CancellationToken ct)
    {
        return SearchCall(id, q, p, ct);
    }

    public Task<Book> Detail(Book b, CancellationToken ct)
    {
        return DetailCall == null ? Task.FromResult(b) : DetailCall(b, ct);
    }

    public Task<List<PageInfo>> Pages(Book b, Chapter c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(PageList.ToList());
    }
}

public sealed class FakeNetwork : INetworkPolicy
{
    public bool Available = true;
    public event Action Changed;

    public bool CanDownload(bool wifiOnly)
    {
        return Available;
    }

    public void Set(bool value)
    {
        Available = value;
        Changed?.Invoke();
    }
}

// Deliberate codec stand-in: tests below exercise scheduling/storage, not native decoder correctness.
public sealed class FakeCodec : IImageCodec
{
    public int Active,
        MaxActive;

    public async Task<byte[]> ToJpeg(byte[] b, PageInfo p, Settings s, CancellationToken ct)
    {
        int n = Interlocked.Increment(ref Active);
        MaxActive = Math.Max(n, MaxActive);
        try
        {
            await Task.Delay(5, ct);
            if (!PixelTransforms.Jpeg(b))
                throw new FormatException("invalid input");
            return b;
        }
        finally
        {
            Interlocked.Decrement(ref Active);
        }
    }
}
