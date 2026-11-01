using System;
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
    public sealed partial class HistoryPage : Page, IBackHandler
    {
        static readonly string[] GroupOrder = { "今天", "昨天", "本周", "更早" };
        bool selecting;

        public HistoryPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Refresh();
        }

        public bool HandleBack()
        {
            if (!selecting)
                return false;
            SetSelecting(false);
            return true;
        }

        void Refresh()
        {
            List<Progress> history;
            lock (App.Store.SyncRoot)
                history = App.Store.Data.History.Where(h => h.Book != null && h.Chapter != null).ToList();
            Groups.Source = history
                .Select(h => new HistoryItem(h))
                .GroupBy(i => Ui.DayGroup(i.Progress.Updated))
                .OrderBy(g => Array.IndexOf(GroupOrder, g.Key))
                .Select(g => new Group<HistoryItem>(g.Key, g))
                .ToList();
            Empty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectCommand.IsEnabled = history.Count > 0;
            ClearCommand.IsEnabled = history.Count > 0;
        }

        void Resume(HistoryItem item)
        {
            Frame.Navigate(
                typeof(ReaderPage),
                new ReaderContext
                {
                    Book = item.Progress.Book,
                    Chapter = item.Progress.Chapter,
                    Page = item.Progress.Page,
                    Offset = item.Progress.Offset,
                }
            );
        }

        void ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as HistoryItem;
            if (item != null && !selecting)
                Resume(item);
        }

        void ItemHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started || selecting)
                return;
            e.Handled = true;
            ShowMenu((FrameworkElement)sender);
        }

        void ItemRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.PointerDeviceType == PointerDeviceType.Touch || selecting)
                return;
            e.Handled = true;
            ShowMenu((FrameworkElement)sender);
        }

        void ShowMenu(FrameworkElement element)
        {
            var item = element.DataContext as HistoryItem;
            if (item == null)
                return;
            var menu = new MenuFlyout();
            var resume = new MenuFlyoutItem { Text = "继续阅读" };
            resume.Click += (s, e) => Resume(item);
            var detail = new MenuFlyoutItem { Text = "查看详情" };
            detail.Click += (s, e) => Frame.Navigate(typeof(DetailPage), item.Book);
            var remove = new MenuFlyoutItem { Text = "删除记录" };
            remove.Click += async (s, e) =>
            {
                await App.Store.ClearHistory(item.Progress.Key);
                Refresh();
            };
            menu.Items.Add(resume);
            menu.Items.Add(detail);
            menu.Items.Add(remove);
            menu.ShowAt(element);
        }

        void SetSelecting(bool on)
        {
            selecting = on;
            List.SelectionMode = on ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
            List.IsItemClickEnabled = !on;
            SelectCommand.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            ClearCommand.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            DeleteCommand.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            CancelCommand.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            DeleteCommand.IsEnabled = false;
        }

        void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DeleteCommand.IsEnabled = selecting && List.SelectedItems.Count > 0;
        }

        void SelectClick(object sender, RoutedEventArgs e)
        {
            SetSelecting(true);
        }

        void CancelClick(object sender, RoutedEventArgs e)
        {
            SetSelecting(false);
        }

        async void DeleteClick(object sender, RoutedEventArgs e)
        {
            var keys = List.SelectedItems.Cast<HistoryItem>().Select(i => i.Progress.Key).ToList();
            if (keys.Count == 0)
                return;
            if (!await Ui.Confirm("删除 " + keys.Count + " 条记录？", "收藏和下载不受影响。", "删除"))
                return;
            foreach (var key in keys)
                await App.Store.ClearHistory(key);
            SetSelecting(false);
            Refresh();
        }

        async void ClearClick(object sender, RoutedEventArgs e)
        {
            if (!await Ui.Confirm("清空阅读历史？", "所有阅读位置都会被移除。收藏和已下载的章节保留。", "清空"))
                return;
            await App.Store.ClearHistory();
            Refresh();
        }
    }
}
