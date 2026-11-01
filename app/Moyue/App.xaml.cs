using System;
using System.Threading;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Moyue
{
    public sealed partial class App : Application
    {
        public static Store Store;
        public static Sources Sources;
        public static SearchService Search;
        public static ImageService Images;
        public static DownloadEngine Downloads;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();

        public App()
        {
            InitializeComponent();
            Suspending += Suspend;
            Resuming += (s, e) => Downloads?.Start();
            UnhandledException += (s, e) =>
            {
                Log.E("app", "未处理异常", e.Exception);
                System.Diagnostics.Debug.WriteLine(e.Exception);
            };
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            if (Store == null)
            {
                Store = await Store.Open();
                Sources = new Sources(Store);
                Search = new SearchService(Store, Sources);
                Images = new ImageService(Store, Sources);
                Downloads = new DownloadEngine(Store, Sources, Images);
                await LogFile.InitAsync();
                var ver = Package.Current.Id.Version;
                Log.I("app", "启动 v" + ver.Major + "." + ver.Minor + "." + ver.Build + " · " + DeviceInfo());
            }
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                frame.Navigated += (s, args) => UpdateBackButton(frame);
                SystemNavigationManager.GetForCurrentView().BackRequested += BackRequested;
                Window.Current.Content = frame;
            }
            if (frame.Content == null)
                frame.Navigate(typeof(MainPage));
            Window.Current.Activate();
            Downloads.Start();
            try
            {
                await Sources.AutoSync(lifetime.Token);
            }
            catch (Exception ex)
            {
                Log.E("app", "启动时同步失败", ex);
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        static string DeviceInfo()
        {
            try
            {
                var di =
                    new Windows.Security.ExchangeActiveSyncProvisioning.EasClientDeviceInformation();
                return
                    di.SystemManufacturer
                    + " "
                    + di.SystemProductName
                    + " / "
                    + di.OperatingSystem;
            }
            catch (Exception)
            {
                return "未知设备";
            }
        }

        static void UpdateBackButton(Frame frame)
        {
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                frame.CanGoBack
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;
        }

        static void BackRequested(object sender, BackRequestedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null || e.Handled)
                return;
            var page = frame.Content as IBackHandler;
            if (page != null && page.HandleBack())
            {
                e.Handled = true;
                return;
            }
            if (frame.CanGoBack)
            {
                e.Handled = true;
                frame.GoBack();
            }
        }

        async void Suspend(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if (Downloads != null)
                    await Downloads.Stop();
                if (Store != null)
                    await Store.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    /// <summary>Pages that consume the system back button before the frame navigates back.</summary>
    public interface IBackHandler
    {
        bool HandleBack();
    }
}
