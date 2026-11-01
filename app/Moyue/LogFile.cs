using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace Moyue
{
    /// <summary>
    /// 日志落盘：追加到 LocalState\logs\moyue.log，超过 1 MB 只保留后半段。
    /// 启动时把文件尾部读回内存缓冲，重启 App 后仍能看到之前的现场。
    /// </summary>
    public static class LogFile
    {
        const string FolderName = "logs";
        const string FileName = "moyue.log";
        const long MaxBytes = 1024 * 1024;
        const int ReplayChars = 48 * 1024;

        static StorageFile file;
        static bool initDone;

        public static async Task InitAsync()
        {
            if (initDone)
                return;
            initDone = true;
            try
            {
                var folder = await ApplicationData
                    .Current
                    .LocalFolder
                    .CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists)
                    .AsTask()
                    .ConfigureAwait(false);
                file = await folder
                    .CreateFileAsync(FileName, CreationCollisionOption.OpenIfExists)
                    .AsTask()
                    .ConfigureAwait(false);
                Log.Sink = Append;
                var text = await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
                if (text.Length > ReplayChars)
                    text = text.Substring(text.Length - ReplayChars);
                foreach (var line in text.Split('\n'))
                {
                    var t = line.TrimEnd('\r');
                    if (t.Length > 0)
                        Log.Replay(t);
                }
                Log.I("log", "日志文件就绪，历史 " + text.Length + " 字符");
            }
            catch (Exception ex)
            {
                Log.E("log", "日志文件初始化失败", ex);
            }
        }

        static void Append(string line)
        {
            // 故意不 await：写盘失败最多丢一行日志，不能拖慢主流程。
            var task = AppendAsync(line);
        }

        static async Task AppendAsync(string line)
        {
            try
            {
                if (file == null)
                    return;
                await FileIO.AppendTextAsync(file, line + "\n").AsTask().ConfigureAwait(false);
                var props = await file.GetBasicPropertiesAsync().AsTask().ConfigureAwait(false);
                if ((long)props.Size > MaxBytes)
                    await TrimAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 忽略：日志不是关键路径。
            }
        }

        static async Task TrimAsync()
        {
            try
            {
                var text = await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
                var keep = text.Substring(text.Length / 2);
                await FileIO.WriteTextAsync(file, keep).AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 忽略。
            }
        }

        /// <summary>导出文件路径，供"关于"或日志页展示。</summary>
        public static string Path
        {
            get { return file == null ? "(未初始化)" : file.Path; }
        }
    }
}
