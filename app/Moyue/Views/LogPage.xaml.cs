using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Moyue
{
    /// <summary>查看 / 复制 / 清空运行日志。日志同时写入本机 logs\moyue.log。</summary>
    public sealed partial class LogPage : Page
    {
        public LogPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Refresh();
        }

        void Refresh()
        {
            var text = Log.Dump();
            Body.Text = text.Length == 0 ? "（暂无日志）" : text;
            Summary.Text = Log.Count + " 行 · " + LogFile.Path;
        }

        void RefreshClick(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        async void CopyClick(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(Log.Dump());
            Clipboard.SetContent(package);
            await Ui.Alert("已复制", "日志已复制到剪贴板，可以粘贴到任何地方。");
        }

        async void ClearClick(object sender, RoutedEventArgs e)
        {
            if (
                !await Ui.Confirm(
                    "清空日志？",
                    "内存中的显示会清空，本机 logs\\moyue.log 中的历史仍会保留。",
                    "清空"
                )
            )
                return;
            Log.Clear();
            Refresh();
        }
    }
}
