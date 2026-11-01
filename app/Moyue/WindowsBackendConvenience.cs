using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Moyue
{
    // Source-linked UWP convenience overloads. Do not include this file when
    // referencing Moyue.Backend.dll: partial types cannot span assemblies.
    public sealed partial class Store
    {
        public static Task<Store> Open() => Open(new WindowsBackendStorage());

        public Task<StorageFolder> DownloadsFolder(string key) =>
            ((WindowsBackendStorage)Storage).Folder("downloads/" + Safe(key), true);
    }

    public sealed partial class DownloadEngine
    {
        public DownloadEngine(Store store, Sources sources, ImageService images)
            : this(store, sources, images, new WindowsNetworkPolicy()) { }
    }

    public sealed partial class ImageService
    {
        public ImageService(Store store, Sources sources)
            : this(store, sources, new WindowsImageCodec()) { }

        public async Task<StorageFile> Page(
            Book book,
            Chapter chapter,
            PageInfo page,
            int index,
            bool download,
            CancellationToken ct
        )
        {
            var key = await GetPage(book, chapter, page, index, download, ct);
            return await ((WindowsBackendStorage)store.Storage).File(key);
        }
    }
}
