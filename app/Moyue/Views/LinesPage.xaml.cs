using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Moyue
{
    public sealed class LineSource : Observable
    {
        string active,
            syncText,
            message,
            imageText;
        List<LineItem> lines = new List<LineItem>();

        public LineSource(string id)
        {
            Id = id;
        }

        public string Id { get; private set; }
        public string Publication { get; set; }
        public string ManualLine { get; set; }

        public string Name
        {
            get { return Sources.Name(Id); }
        }

        public string Note
        {
            get
            {
                return Id == "jm"
                    ? "JM 发布页是加密的域名列表文件（newsvr-2025.txt），返回当前可用的接口地址。保存的地址优先，内置镜像作为备用。"
                    : "只从保存的发布页取得候选地址，页面验证通过后才启用。";
            }
        }

        public string Active
        {
            get { return active; }
            private set { Set(ref active, value); }
        }

        public string SyncText
        {
            get { return syncText; }
            private set { Set(ref syncText, value); }
        }

        public string Message
        {
            get { return message; }
            set
            {
                if (Set(ref message, value))
                {
                    Raise(nameof(MessageVisibility));
                    Raise(nameof(MessageBrush));
                }
            }
        }

        public Visibility MessageVisibility
        {
            get { return string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Brush MessageBrush
        {
            get
            {
                return Ui.Resource(
                    message != null && message.Contains("失败") ? "ErrorBrush" : "SystemControlForegroundBaseMediumBrush"
                );
            }
        }

        public List<LineItem> Lines
        {
            get { return lines; }
            private set { Set(ref lines, value); }
        }

        public string ImageText
        {
            get { return imageText; }
            private set
            {
                if (Set(ref imageText, value))
                    Raise(nameof(ImageVisibility));
            }
        }

        public Visibility ImageVisibility
        {
            get { return string.IsNullOrEmpty(imageText) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public void Refresh()
        {
            lock (App.Store.SyncRoot)
            {
                var c = App.Sources.Config(Id);
                if (Publication == null)
                    Publication = c.Publication;
                var current = c.Active ?? c.Hosts.FirstOrDefault();
                Active = current ?? "没有可用线路";
                SyncText =
                    "最近同步："
                    + (c.Synced == default(DateTime) ? "从未成功同步" : Ui.FriendlyTime(c.Synced.ToLocalTime()));
                Message = c.Message;
                Lines = c
                    .Hosts.Select(
                        (h, i) =>
                            new LineItem
                            {
                                Group = "line-" + Id,
                                Host = h,
                                Active = h == current,
                                Label = h == current ? "当前使用" : i == 0 ? "首选" : "备用",
                            }
                    )
                    .ToList();
                ImageText = string.Join("\n", c.ImageHosts);
            }
        }
    }

    public sealed partial class LinesPage : Page
    {
        readonly List<LineSource> sources = Ui.SourceIds.Select(id => new LineSource(id)).ToList();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();

        public LinesPage()
        {
            InitializeComponent();
            foreach (var s in sources)
                s.Refresh();
            Hub.ItemsSource = sources;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            lifetime.Cancel();
        }

        static LineSource Owner(object sender)
        {
            return ((FrameworkElement)sender).DataContext as LineSource;
        }

        LineSource Selected
        {
            get { return Hub.SelectedItem as LineSource; }
        }

        void PublicationChanged(object sender, TextChangedEventArgs e)
        {
            var s = Owner(sender);
            if (s != null)
                s.Publication = ((TextBox)sender).Text;
        }

        void ManualChanged(object sender, TextChangedEventArgs e)
        {
            var s = Owner(sender);
            if (s != null)
                s.ManualLine = ((TextBox)sender).Text;
        }

        async void LineClick(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as LineItem;
            var s = Selected;
            if (item == null || s == null)
                return;
            lock (App.Store.SyncRoot)
            {
                var c = App.Sources.Config(s.Id);
                if (c.Hosts.Contains(item.Host))
                    c.Active = item.Host;
            }
            try
            {
                await App.Store.Save();
            }
            catch (Exception ex)
            {
                await Ui.Alert("线路没有保存", Ui.Describe(ex));
            }
            s.Refresh();
            s.Message = "已切换到 " + item.Host + "。";
        }

        async void AddLineClick(object sender, RoutedEventArgs e)
        {
            var s = Owner(sender);
            if (s == null)
                return;
            var url = (s.ManualLine ?? "").Trim();
            if (!Sources.PublicHttps(url))
            {
                s.Message = "添加失败：请输入完整的 HTTPS 网址。";
                return;
            }
            try
            {
                await App.Sources.AddLine(s.Id, url);
                s.Refresh();
                s.Message = "已添加并切换到新线路。";
            }
            catch (Exception ex)
            {
                s.Message = "添加失败：" + Ui.Describe(ex);
            }
        }

        async void SavePublicationClick(object sender, RoutedEventArgs e)
        {
            var s = Owner(sender);
            if (s == null)
                return;
            var url = (s.Publication ?? "").Trim();
            try
            {
                await App.Sources.SetPublication(s.Id, url);
                s.Refresh();
                s.Message = "发布页已保存，下次同步时使用。";
            }
            catch (Exception ex)
            {
                s.Message = "保存失败：" + Ui.Describe(ex);
            }
        }

        async void SyncClick(object sender, RoutedEventArgs e)
        {
            var s = Selected;
            if (s == null)
                return;
            SyncCommand.IsEnabled = false;
            Busy.Visibility = Visibility.Visible;
            s.Message = "正在同步 " + s.Name + "…";
            string failure = null;
            try
            {
                await App.Sources.Sync(s.Id, lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                failure = "同步失败：" + Ui.Describe(ex) + "。已保留原来的线路。";
            }
            finally
            {
                SyncCommand.IsEnabled = true;
                Busy.Visibility = Visibility.Collapsed;
            }
            s.Refresh();
            if (failure != null)
                s.Message = failure;
        }

        void OfflineClick(object sender, RoutedEventArgs e)
        {
            MainPage.RequestedSection = 3;
            while (Frame.CanGoBack && !(Frame.Content is MainPage))
                Frame.GoBack();
        }
    }
}
