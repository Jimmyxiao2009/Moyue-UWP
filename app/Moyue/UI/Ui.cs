using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System.Profile;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Moyue
{
    static class Ui
    {
        static bool dialogOpen;

        public static readonly string[] SourceIds = { "jm", "boylove", "manwa" };

        public static bool IsMobile
        {
            get { return AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Mobile"; }
        }

        public static Brush Resource(string key)
        {
            return (Brush)Application.Current.Resources[key];
        }

        /// <summary>ContentDialog throws if a second one opens; ignore overlapping requests.</summary>
        public static async Task<bool> Confirm(string title, string content, string primary)
        {
            if (dialogOpen)
                return false;
            dialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = primary,
                    SecondaryButtonText = "取消",
                };
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            finally
            {
                dialogOpen = false;
            }
        }

        public static async Task Alert(string title, string content)
        {
            if (dialogOpen)
                return;
            dialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = "确定",
                };
                await dialog.ShowAsync();
            }
            finally
            {
                dialogOpen = false;
            }
        }

        public static async Task<ContentDialogResult> Show(ContentDialog dialog)
        {
            if (dialogOpen)
                return ContentDialogResult.None;
            dialogOpen = true;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                dialogOpen = false;
            }
        }

        public static async Task UpdateSettings(Action<Settings> change, bool restartDownloads = false)
        {
            lock (App.Store.SyncRoot)
                change(App.Store.Data.Settings);
            await App.Store.Save();
            if (restartDownloads)
                App.Downloads.Start();
        }

        public static Settings SettingsSnapshot()
        {
            lock (App.Store.SyncRoot)
            {
                var s = App.Store.Data.Settings;
                return new Settings
                {
                    OldDevice = s.OldDevice,
                    ReadingPriority = s.ReadingPriority,
                    WifiOnly = s.WifiOnly,
                    AutoSync = s.AutoSync,
                    Failover = s.Failover,
                    Concurrency = s.Concurrency,
                    Preload = s.Preload,
                    CacheMb = s.CacheMb,
                };
            }
        }

        public static SearchOptions Clone(SearchOptions o)
        {
            return new SearchOptions
            {
                Query = o.Query,
                Exclude = o.Exclude,
                Author = o.Author,
                Tags = o.Tags,
                State = o.State,
                Sort = o.Sort,
                Any = o.Any,
                Local = o.Local,
                Sources = o.Sources.ToList(),
            };
        }

        /// <summary>
        /// Backend errors carry exception chains and host names; users only need what happened
        /// and what they can do next.
        /// </summary>
        public static string Describe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "发生未知错误。";
            var text = raw.Trim();
            if (
                text.Contains("HttpRequestException")
                || text.Contains("COMException")
                || text.Contains("无法与服务器建立连接")
                || text.Contains("所有 API 域名都失败")
                || text.Contains("SocketException")
                || text.Contains("WebException")
            )
                return "无法连接到来源服务器。请检查网络，或在“来源与线路”中换一条线路。";
            if (text.Contains("TaskCanceled") || text.Contains("超时") || text.Contains("timed out"))
                return "连接超时。网络较慢时可以稍后重试。";
            var line = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
            int arrow = line.IndexOf(" <- ", StringComparison.Ordinal);
            if (arrow > 0)
                line = line.Substring(0, arrow);
            return line.Length > 80 ? line.Substring(0, 80) + "…" : line;
        }

        public static string Describe(Exception e)
        {
            return Describe(e == null ? null : e.Message);
        }

        public static string FriendlyTime(DateTime time)
        {
            var day = time.Date;
            if (day == DateTime.Today)
                return "今天 " + time.ToString("HH:mm");
            if (day == DateTime.Today.AddDays(-1))
                return "昨天 " + time.ToString("HH:mm");
            return time.ToString("M月d日 HH:mm");
        }

        public static string DayGroup(DateTime time)
        {
            var day = time.Date;
            if (day == DateTime.Today)
                return "今天";
            if (day == DateTime.Today.AddDays(-1))
                return "昨天";
            if (day > DateTime.Today.AddDays(-7))
                return "本周";
            return "更早";
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024 * 1024)
                return Math.Max(0, bytes / 1024) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
        }

        public static IEnumerable<T> Descendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T)
                    yield return (T)child;
                foreach (var d in Descendants<T>(child))
                    yield return d;
            }
        }
    }

    /// <summary>Interface-only preferences that do not belong in the synced library file.</summary>
    static class Prefs
    {
        static readonly ApplicationDataContainer values = ApplicationData.Current.LocalSettings;

        static bool Get(string key, bool fallback)
        {
            object v;
            return values.Values.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }

        public static bool PagedReading
        {
            get { return Get("PagedReading", false); }
            set { values.Values["PagedReading"] = value; }
        }

        public static bool ReaderHintSeen
        {
            get { return Get("ReaderHintSeen", false); }
            set { values.Values["ReaderHintSeen"] = value; }
        }

        public static bool LibraryList
        {
            get { return Get("LibraryList", false); }
            set { values.Values["LibraryList"] = value; }
        }

        public static bool SearchList
        {
            get { return Get("SearchList", true); }
            set { values.Values["SearchList"] = value; }
        }

        /// <summary>Library tile size: 0 small, 1 medium, 2 large.</summary>
        public static int TileSize
        {
            get
            {
                object v;
                return values.Values.TryGetValue("TileSize", out v) && v is int
                    ? Math.Max(0, Math.Min(2, (int)v))
                    : 1;
            }
            set { values.Values["TileSize"] = value; }
        }
    }

    /// <summary>Search conditions shared between the search pivot and the advanced search page.</summary>
    static class SearchState
    {
        public static SearchOptions Options = new SearchOptions();
        public static bool RunPending;
    }
}
