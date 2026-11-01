using System;
using System.Linq;
using System.Threading;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Moyue
{
    public sealed partial class SettingsPage : Page
    {
        static readonly int[] ConcurrencyValues = { 1, 2, 3, 4, 6, 8, 12, 16, 24, Settings.MaxConcurrency };
        static readonly int[] CacheValues = { 64, 128, 256, 512 };
        bool ready;

        public SettingsPage()
        {
            InitializeComponent();
            var v = Package.Current.Id.Version;
            About.Text = "墨阅 " + v.Major + "." + v.Minor + "." + v.Build + "\n不提供账号或云同步，所有资料保存在本机。";
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ready = false;
            var s = Ui.SettingsSnapshot();
            OldDevice.IsOn = s.OldDevice;
            ReadingPriority.IsOn = s.ReadingPriority;
            PagedDefault.IsOn = Prefs.PagedReading;
            WifiOnly.IsOn = s.WifiOnly;
            AutoSync.IsOn = s.AutoSync;
            Failover.IsOn = s.Failover;
            Preload.SelectedIndex = Math.Max(0, Math.Min(3, s.Preload));
            // Values saved by older builds may sit between options; show the nearest lower one.
            Concurrency.SelectedIndex = Math.Max(0, Array.FindLastIndex(ConcurrencyValues, v => v <= s.Concurrency));
            CacheLimit.SelectedIndex = Math.Max(0, Array.IndexOf(CacheValues, s.CacheMb));
            ready = true;
            UpdateStorage();
        }

        async void UpdateStorage()
        {
            int downloaded;
            lock (App.Store.SyncRoot)
                downloaded = App.Store.Data.Downloads.Count(j => j.State == "已完成");
            DownloadedCount.Text = downloaded + " 个章节 · 不受缓存清理影响";
            try
            {
                var files = await App.Store.Storage.List("cache", CancellationToken.None);
                long total = files.Sum(f => f.Size);
                CacheSize.Text = Ui.Size(total) + " · 清理后会按需重新获取";
                ClearCacheLink.IsEnabled = files.Count > 0;
            }
            catch (Exception)
            {
                CacheSize.Text = "无法读取缓存大小";
            }
        }

        async void SettingToggled(object sender, RoutedEventArgs e)
        {
            if (!ready)
                return;
            var toggle = (ToggleSwitch)sender;
            bool on = toggle.IsOn;
            try
            {
                if (toggle == PagedDefault)
                    Prefs.PagedReading = on;
                else if (toggle == OldDevice)
                    await Ui.UpdateSettings(s => s.OldDevice = on);
                else if (toggle == ReadingPriority)
                    await Ui.UpdateSettings(s => s.ReadingPriority = on, true);
                else if (toggle == WifiOnly)
                    await Ui.UpdateSettings(s => s.WifiOnly = on, true);
                else if (toggle == AutoSync)
                    await Ui.UpdateSettings(s => s.AutoSync = on);
                else if (toggle == Failover)
                    await Ui.UpdateSettings(s => s.Failover = on);
            }
            catch (Exception ex)
            {
                await Ui.Alert("设置没有保存", Ui.Describe(ex));
            }
        }

        async void ChoiceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ready)
                return;
            try
            {
                if (sender == Preload && Preload.SelectedIndex >= 0)
                {
                    int value = Preload.SelectedIndex;
                    await Ui.UpdateSettings(s => s.Preload = value);
                }
                else if (sender == Concurrency && Concurrency.SelectedIndex >= 0)
                {
                    int value = ConcurrencyValues[Concurrency.SelectedIndex];
                    await Ui.UpdateSettings(s => s.Concurrency = value, true);
                }
                else if (sender == CacheLimit && CacheLimit.SelectedIndex >= 0)
                {
                    int value = CacheValues[CacheLimit.SelectedIndex];
                    await Ui.UpdateSettings(s => s.CacheMb = value);
                }
            }
            catch (Exception ex)
            {
                await Ui.Alert("设置没有保存", Ui.Describe(ex));
            }
        }

        void LinesClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(LinesPage));
        }

        void LogClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(LogPage));
        }

        void ManageDownloadsClick(object sender, RoutedEventArgs e)
        {
            MainPage.RequestedSection = 3;
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        async void ClearCacheClick(object sender, RoutedEventArgs e)
        {
            if (
                !await Ui.Confirm(
                    "清理图片缓存？",
                    "将移除临时保存的图片和封面。已下载的章节、收藏、历史和阅读进度都会保留。",
                    "清理"
                )
            )
                return;
            ClearCacheLink.IsEnabled = false;
            try
            {
                await App.Images.ClearCache();
                CoverImage.ClearCache();
            }
            catch (Exception ex)
            {
                await Ui.Alert("缓存没有清理完", Ui.Describe(ex));
            }
            UpdateStorage();
        }
    }
}
