using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Input;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Moyue
{
    /// <summary>All bookmarks grouped by comic; pass a book key to show one comic only.</summary>
    public sealed partial class BookmarksPage : Page
    {
        string filter;

        public BookmarksPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            filter = e.Parameter as string;
            Refresh();
        }

        void Refresh()
        {
            List<Bookmark> marks;
            lock (App.Store.SyncRoot)
                marks = App
                    .Store.Data.Bookmarks.Where(m => filter == null || m.Key == filter)
                    .ToList();
            AppName.Text =
                filter != null && marks.Count > 0
                    ? "墨阅 · " + (marks[0].Book.Title ?? marks[0].Book.Id)
                    : "墨阅";
            Groups.Source = marks
                .GroupBy(m => m.Key)
                .Select(g =>
                    new Group<BookmarkItem>(
                        g.First().Book.Title ?? g.First().Book.Id,
                        g.OrderByDescending(m => m.Created).Select(m => new BookmarkItem(m))
                    )
                )
                .ToList();
            Empty.Visibility = marks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearCommand.IsEnabled = marks.Count > 0;
        }

        void Open(BookmarkItem item)
        {
            var mark = item.Mark;
            Frame.Navigate(
                typeof(ReaderPage),
                new ReaderContext
                {
                    Book = App.Store.FindBook(mark.Key) ?? mark.Book,
                    Chapter = mark.Chapter,
                    Page = mark.Page,
                    Offset = mark.Offset,
                }
            );
        }

        void ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as BookmarkItem;
            if (item != null)
                Open(item);
        }

        void ItemHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started)
                return;
            e.Handled = true;
            ShowMenu((FrameworkElement)sender);
        }

        void ItemRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.PointerDeviceType == PointerDeviceType.Touch)
                return;
            e.Handled = true;
            ShowMenu((FrameworkElement)sender);
        }

        void ShowMenu(FrameworkElement element)
        {
            var item = element.DataContext as BookmarkItem;
            if (item == null)
                return;
            var menu = new MenuFlyout();
            var open = new MenuFlyoutItem { Text = "从书签处阅读" };
            open.Click += (s, e) => Open(item);
            var detail = new MenuFlyoutItem { Text = "查看详情" };
            detail.Click += (s, e) =>
                Frame.Navigate(typeof(DetailPage), App.Store.FindBook(item.Mark.Key) ?? item.Book);
            var remove = new MenuFlyoutItem { Text = "删除书签" };
            remove.Click += async (s, e) =>
            {
                await App.Store.RemoveBookmark(item.Mark.Id);
                Refresh();
            };
            menu.Items.Add(open);
            menu.Items.Add(detail);
            menu.Items.Add(remove);
            menu.ShowAt(element);
        }

        async void ClearClick(object sender, RoutedEventArgs e)
        {
            if (
                !await Ui.Confirm(
                    filter == null ? "清空全部书签？" : "清空这部漫画的书签？",
                    "阅读历史、收藏和下载不受影响。",
                    "清空"
                )
            )
                return;
            List<string> ids;
            lock (App.Store.SyncRoot)
                ids = App
                    .Store.Data.Bookmarks.Where(m => filter == null || m.Key == filter)
                    .Select(m => m.Id)
                    .ToList();
            foreach (var id in ids)
                await App.Store.RemoveBookmark(id);
            Refresh();
        }
    }
}
