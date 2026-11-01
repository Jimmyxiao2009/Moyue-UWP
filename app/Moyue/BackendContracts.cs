using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Moyue
{
    public sealed class BackendFile
    {
        public string Key;
        public long Size;
        public DateTime Modified;
    }

    public interface IBackendStorage
    {
        Task<byte[]> Read(string key, CancellationToken ct);
        Task WriteAtomic(string key, byte[] bytes, CancellationToken ct);
        Task Delete(string key, CancellationToken ct);
        Task<IReadOnlyList<BackendFile>> List(string prefix, CancellationToken ct);
    }

    public interface IImageCodec
    {
        Task<byte[]> ToJpeg(byte[] bytes, PageInfo page, Settings settings, CancellationToken ct);
    }

    public interface INetworkPolicy
    {
        bool CanDownload(bool wifiOnly);
        event Action Changed;
    }

    public interface IContentSource
    {
        Task<List<Book>> Search(string source, string query, int page, CancellationToken ct);
        Task<Book> Detail(Book book, CancellationToken ct);
        Task<List<PageInfo>> Pages(Book book, Chapter chapter, CancellationToken ct);
    }

    public sealed class SourceSearchResult
    {
        public string Source;
        public int Page;
        public List<Book> Books = new List<Book>();
        public string Error;
        public bool MoreMayExist;
        public int MetadataUnknown;
        public bool IsSuccess
        {
            get { return Error == null; }
        }
    }

    public sealed class SearchBatch
    {
        public List<SourceSearchResult> Sources = new List<SourceSearchResult>();
        public List<Book> Books = new List<Book>();
        public bool FiltersLoadedResultsOnly = true;
    }

    public sealed class SavedPage
    {
        public string Resource { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
    }
}
