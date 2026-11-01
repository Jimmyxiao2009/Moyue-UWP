using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Storage;

namespace Moyue
{
    public sealed class WindowsBackendStorage : IBackendStorage
    {
        internal async Task<StorageFolder> Folder(string key, bool create)
        {
            Validate(key);
            var folder = ApplicationData.Current.LocalFolder;
            foreach (var part in key.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (create)
                    folder = await folder.CreateFolderAsync(
                        part,
                        CreationCollisionOption.OpenIfExists
                    );
                else
                {
                    folder = await folder.TryGetItemAsync(part) as StorageFolder;
                    if (folder == null)
                        return null;
                }
            }
            return folder;
        }

        static void Validate(string key)
        {
            if (
                key == null
                || key.Contains("..")
                || key.Contains("\\")
                || key.Contains(":")
                || key.StartsWith("/")
            )
                throw new ArgumentException("存储路径必须位于应用目录内");
        }

        public async Task<StorageFile> File(string key)
        {
            Validate(key);
            int slash = key.LastIndexOf('/');
            var folder = await Folder(slash < 0 ? "" : key.Substring(0, slash), false);
            return folder == null
                ? null
                : await folder.TryGetItemAsync(key.Substring(slash + 1)) as StorageFile;
        }

        public async Task<byte[]> Read(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var f = await File(key);
            if (f == null)
                return null;
            var buffer = await FileIO.ReadBufferAsync(f);
            ct.ThrowIfCancellationRequested();
            return buffer.ToArray();
        }

        public async Task WriteAtomic(string key, byte[] bytes, CancellationToken ct)
        {
            Validate(key);
            ct.ThrowIfCancellationRequested();
            int slash = key.LastIndexOf('/');
            var folder = await Folder(slash < 0 ? "" : key.Substring(0, slash), true);
            var name = key.Substring(slash + 1);
            var temp = await folder.CreateFileAsync(
                name + "." + Guid.NewGuid().ToString("N") + ".pending",
                CreationCollisionOption.FailIfExists
            );
            try
            {
                await FileIO.WriteBytesAsync(temp, bytes);
                ct.ThrowIfCancellationRequested();
                await temp.RenameAsync(name, NameCollisionOption.ReplaceExisting);
            }
            catch
            {
                try
                {
                    await temp.DeleteAsync();
                }
                catch { }
                throw;
            }
        }

        public async Task Delete(string key, CancellationToken ct)
        {
            Validate(key);
            ct.ThrowIfCancellationRequested();
            int slash = key.LastIndexOf('/');
            var folder = await Folder(slash < 0 ? "" : key.Substring(0, slash), false);
            var item =
                folder == null ? null : await folder.TryGetItemAsync(key.Substring(slash + 1));
            if (item != null)
                await item.DeleteAsync();
        }

        public async Task<IReadOnlyList<BackendFile>> List(string prefix, CancellationToken ct)
        {
            var result = new List<BackendFile>();
            var folder = await Folder(prefix.TrimEnd('/'), false);
            if (folder != null)
                foreach (var f in await folder.GetFilesAsync())
                {
                    ct.ThrowIfCancellationRequested();
                    var props = await f.GetBasicPropertiesAsync();
                    result.Add(
                        new BackendFile
                        {
                            Key = prefix.TrimEnd('/') + "/" + f.Name,
                            Size = (long)props.Size,
                            Modified = props.DateModified.UtcDateTime,
                        }
                    );
                }
            return result;
        }
    }

    public sealed class WindowsNetworkPolicy : INetworkPolicy
    {
        public event Action Changed;

        public WindowsNetworkPolicy()
        {
            NetworkInformation.NetworkStatusChanged += n => Changed?.Invoke();
        }

        public bool CanDownload(bool wifiOnly)
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            return p != null
                && p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess
                && (!wifiOnly || p.IsWlanConnectionProfile);
        }
    }
}
