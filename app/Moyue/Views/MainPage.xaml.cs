using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Input;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Moyue
{
    public sealed partial class MainPage : Page, IBackHandler
    {
        const int LibrarySection = 0,
            DiscoverSection = 1,
            SearchSection = 2,
            DownloadSection = 3;

        /// <summary>Lets pushed pages (e.g. settings → 管理下载) choose the pivot shown after GoBack.</summary>
        public static int? RequestedSection;

        readonly ObservableCollection<BookItem> library = new ObservableCollection<BookItem>();
        readonly ObservableCollection<BookItem> discover = new ObservableCollection<BookItem>();
        readonly IncrementalCollection<BookItem> results = new IncrementalCollection<BookItem>();
        readonly ObservableCollection<SourceStatus> statuses = new ObservableCollection<SourceStatus>();
        readonly ObservableCollection<FilterChip> chips = new ObservableCollection<FilterChip>();
        readonly HashSet<string> resultKeys = new HashSet<string>();
        readonly Dictionary<string, DownloadItem> downloadItems = new Dictionary<string, DownloadItem>();
        readonly DispatcherTimer downloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        string librarySource = "",
            librarySignature,
            downloadSignature;
        Progress continueProgress;
        string discoverSource = "jm";
        int discoverPage;
        bool discoverLoading;
        CancellationTokenSource discoverCts;
        CancellationTokenSource searchCts = new CancellationTokenSource();
        SearchOptions lastSearch;
        int searchGeneration,
            searchPage,
            busy;
        bool searching,
            syncing,
            showCompleted,
            warned,
            ready,
            statusExpanded,
            selecting;

        public MainPage()
        {
            InitializeComponent();
            LibraryGrid.ItemsSource = library;
            DiscoverGrid.ItemsSource = discover;
            LibraryGrid.SelectionChanged += SelectionChanged;
            ResultsList.SelectionChanged += SelectionChanged;
            results.CanLoadMore = CanLoadMoreResults;
            results.LoadMore = () => SearchAsync(true);
            ResultsList.ItemsSource = results;
            // The page is cached, so Loaded repeats after every back navigation; only apply the
            // saved layout once or the lists would lose their scroll position.
            bool layoutApplied = false;
            Loaded += (s, e) =>
            {
                if (layoutApplied)
                    return;
                layoutApplied = true;
                ApplyLayout(LibraryGrid);
                ApplyLayout(ResultsList);
            };
            StatusList.ItemsSource = statuses;
            Chips.ItemsSource = chips;
            downloadTimer.Tick += (s, e) =>
            {
                downloadTimer.Stop();
                RefreshDownloads();
            };
            App.Downloads.Changed += DownloadsChanged;
            // Checked handlers fire while InitializeComponent applies IsChecked defaults.
            ready = true;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RefreshLibrary();
            RefreshDownloads();
            SyncSearchHeader();
            if (RequestedSection.HasValue)
            {
                Hub.SelectedIndex = RequestedSection.Value;
                RequestedSection = null;
            }
            if (SearchState.RunPending)
            {
                SearchState.RunPending = false;
                Hub.SelectedIndex = SearchSection;
                QueryBox.Text = SearchState.Options.Query ?? "";
                // RadioButton Checked events from SyncSearchHeader are raised asynchronously;
                // start after them so the new conditions are not overridden or searched twice.
                var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => RunSearch(false));
            }
            UpdateCommands();
            if (!warned)
            {
                warned = true;
                if (App.Store.LoadWarning != null)
                    await Ui.Alert(
                        "资料已从备份恢复",
                        App.Store.LoadWarning + "\n最近一次保存后的改动可能没有保留。"
                    );
            }
        }

        void SetBusy(bool on)
        {
            busy = Math.Max(0, busy + (on ? 1 : -1));
            Busy.Visibility = busy > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        static Visibility Show(bool value)
        {
            return value ? Visibility.Visible : Visibility.Collapsed;
        }

        void HubSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Switching pivots drops the selection so each list starts clean.
            if (selecting)
                SetSelecting(false);
            if (Hub.SelectedIndex == DiscoverSection && discover.Count == 0 && !discoverLoading)
                LoadDiscover(false);
            if (Hub.SelectedIndex == DownloadSection)
                RefreshDownloads();
            UpdateCommands();
        }

        void UpdateCommands()
        {
            int i = Hub.SelectedIndex;
            // Multi-select takes over the bar on the two list pivots; normal commands hide.
            bool select = selecting && (i == LibrarySection || i == SearchSection);
            DiscoverRefreshCommand.Visibility = Show(i == DiscoverSection && !select);
            SearchCommand.Visibility = Show(i == SearchSection && !select);
            AdvancedCommand.Visibility = Show(i == SearchSection && !select);
            IdCommand.Visibility = Show(i == SearchSection && !select);
            PauseAllCommand.Visibility = Show(i == DownloadSection && !select);
            ResumeAllCommand.Visibility = Show(i == DownloadSection && !select);
            RetryFailedCommand.Visibility = Show(i == DownloadSection && !select);
            LinesCommand.Visibility = Show(i == DownloadSection && !select);
            bool layoutPivot = i == LibrarySection || i == SearchSection;
            LayoutCommand.Visibility = Show(layoutPivot && !select);
            SizeCommand.Visibility = Show(i == LibrarySection && !select);
            SizeCommand.IsEnabled = !Prefs.LibraryList;
            MultiSelectCommand.Visibility = Show(layoutPivot && !select);
            FavoriteSelectedCommand.Visibility = Show(select);
            DownloadSelectedCommand.Visibility = Show(select);
            SelectAllCommand.Visibility = Show(select);
            CancelSelectCommand.Visibility = Show(select);
            bool list = i == SearchSection ? Prefs.SearchList : Prefs.LibraryList;
            LayoutCommand.Label = list ? "磁贴视图" : "列表视图";
            LayoutIcon.Glyph = list ? "\uE8A9" : "\uE8FD";
            int size = Prefs.TileSize;
            SizeSmall.IsChecked = size == 0;
            SizeMedium.IsChecked = size == 1;
            SizeLarge.IsChecked = size == 2;
            Commands.ClosedDisplayMode = AppBarClosedDisplayMode.Compact;
        }

        // Target tile widths for 小/中/大; on a 360-wide phone that is 3, 2 and 1 columns.
        static readonly double[] TileWidths = { 110, 165, 250 };

        bool IsList(GridView grid)
        {
            return grid == LibraryGrid ? Prefs.LibraryList
                : grid == ResultsList ? Prefs.SearchList
                : false;
        }

        void LayoutGrid(GridView grid)
        {
            var panel = grid.ItemsPanelRoot as ItemsWrapGrid;
            double width = grid.ActualWidth;
            if (panel == null || width <= 0)
                return;
            int columns = IsList(grid)
                ? Math.Max(1, (int)(width / 420))
                : Math.Max(1, (int)(width / TileWidths[grid == LibraryGrid ? Prefs.TileSize : 1]));
            panel.ItemWidth = Math.Floor(width / columns);
        }

        void ApplyLayout(GridView grid)
        {
            bool list = IsList(grid);
            var source = grid.ItemsSource;
            // Rebuild containers so the new template and container style apply to every item.
            grid.ItemsSource = null;
            grid.ItemTemplate = (DataTemplate)Resources[list ? "ResultRowTemplate" : "CoverCardTemplate"];
            grid.ItemContainerStyle = list
                ? (Style)Resources["RowGridItemStyle"]
                : (Style)Application.Current.Resources["CardContainerStyle"];
            grid.ItemsSource = source;
            LayoutGrid(grid);
        }

        void CardGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            LayoutGrid((GridView)sender);
        }

        void LayoutClick(object sender, RoutedEventArgs e)
        {
            if (Hub.SelectedIndex == SearchSection)
            {
                Prefs.SearchList = !Prefs.SearchList;
                ApplyLayout(ResultsList);
            }
            else
            {
                Prefs.LibraryList = !Prefs.LibraryList;
                ApplyLayout(LibraryGrid);
            }
            UpdateCommands();
        }

        void SizeClick(object sender, RoutedEventArgs e)
        {
            Prefs.TileSize = int.Parse((string)((FrameworkElement)sender).Tag);
            LayoutGrid(LibraryGrid);
            UpdateCommands();
        }

        // ---------- 导航 ----------

        void OpenDetail(Book book)
        {
            Frame.Navigate(typeof(DetailPage), book);
        }

        void OpenReader(Book book, Chapter chapter)
        {
            Progress p;
            lock (App.Store.SyncRoot)
                p = App.Store.Data.History.FirstOrDefault(h => h.Key == book.Key);
            bool same = p != null && p.Chapter != null && p.Chapter.Id == chapter.Id;
            Frame.Navigate(
                typeof(ReaderPage),
                new ReaderContext
                {
                    Book = book,
                    Chapter = chapter,
                    Page = same ? p.Page : 0,
                    Offset = same ? p.Offset : 0,
                }
            );
        }

        void BookClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as BookItem;
            if (item != null)
                OpenDetail(item.Book);
        }

        void HistoryClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(HistoryPage));
        }

        void BookmarksClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(BookmarksPage));
        }

        void SettingsClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
        }

        void LinesClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(LinesPage));
        }

        void GoDiscoverClick(object sender, RoutedEventArgs e)
        {
            Hub.SelectedIndex = DiscoverSection;
        }

        void GoSearchClick(object sender, RoutedEventArgs e)
        {
            Hub.SelectedIndex = SearchSection;
        }

        void GoLibraryClick(object sender, RoutedEventArgs e)
        {
            Hub.SelectedIndex = LibrarySection;
        }

        void BookHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started || selecting)
                return;
            e.Handled = true;
            ShowBookMenu((FrameworkElement)sender);
        }

        void BookRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // Touch raises Holding first; avoid showing the menu twice.
            if (e.PointerDeviceType == PointerDeviceType.Touch || selecting)
                return;
            e.Handled = true;
            ShowBookMenu((FrameworkElement)sender);
        }

        void ShowBookMenu(FrameworkElement element)
        {
            var item = element.DataContext as BookItem;
            if (item == null)
                return;
            bool favorite;
            Progress progress;
            lock (App.Store.SyncRoot)
            {
                favorite = App.Store.Data.Favorites.Any(b => b.Key == item.Book.Key);
                progress = App.Store.Data.History.FirstOrDefault(h =>
                    h.Key == item.Book.Key && h.Chapter != null
                );
            }
            var menu = new MenuFlyout();
            if (progress != null)
            {
                var resume = new MenuFlyoutItem
                {
                    Text = "继续阅读 · " + progress.Chapter.Title + " · 第 " + (progress.Page + 1) + " 页",
                };
                resume.Click += (s, e) =>
                    Frame.Navigate(
                        typeof(ReaderPage),
                        new ReaderContext
                        {
                            Book = App.Store.FindBook(progress.Key) ?? progress.Book,
                            Chapter = progress.Chapter,
                            Page = progress.Page,
                            Offset = progress.Offset,
                        }
                    );
                menu.Items.Add(resume);
            }
            var open = new MenuFlyoutItem { Text = "查看详情" };
            open.Click += (s, e) => OpenDetail(item.Book);
            var toggle = new MenuFlyoutItem { Text = favorite ? "移出书架" : "加入书架" };
            toggle.Click += async (s, e) =>
            {
                try
                {
                    await App.Store.SetFavorite(item.Book, !favorite);
                }
                catch (Exception ex)
                {
                    await Ui.Alert("无法更新书架", Ui.Describe(ex));
                }
                RefreshLibrary();
            };
            menu.Items.Add(open);
            menu.Items.Add(toggle);
            menu.ShowAt(element);
        }

        // ---------- 书架 ----------

        void LibraryFilterChecked(object sender, RoutedEventArgs e)
        {
            librarySource = (string)((FrameworkElement)sender).Tag ?? "";
            if (ready)
                RefreshLibrary();
        }

        void RefreshLibrary()
        {
            if (LibraryGrid == null)
                return;
            // Rebuilding the list drops the selection; leave multi-select rather than keep stale state.
            if (selecting)
                SetSelecting(false);
            List<Book> favorites;
            List<Progress> history;
            lock (App.Store.SyncRoot)
            {
                favorites = App.Store.Data.Favorites.ToList();
                history = App.Store.Data.History.Take(300).ToList();
            }
            var progress = new Dictionary<string, Progress>();
            foreach (var h in history)
                if (!progress.ContainsKey(h.Key))
                    progress[h.Key] = h;

            continueProgress = history.FirstOrDefault(h => h.Book != null && h.Chapter != null);
            ContinuePanel.Visibility = Show(continueProgress != null);
            if (continueProgress != null)
            {
                ContinueCover.Book = continueProgress.Book;
                ContinueTitle.Text = continueProgress.Book.Title ?? continueProgress.Book.Id;
                ContinueChapter.Text = continueProgress.Chapter.Title ?? "";
                ContinuePage.Text =
                    Sources.Name(continueProgress.Book.Source)
                    + " · 第 "
                    + (continueProgress.Page + 1)
                    + " 页 · "
                    + Ui.FriendlyTime(continueProgress.Updated);
            }

            var shown = favorites
                .Where(b => librarySource == "" || b.Source == librarySource)
                .Select(b =>
                {
                    Progress p;
                    var state = progress.TryGetValue(b.Key, out p) && p.Chapter != null ? p.Chapter.Title : "未读";
                    return new BookItem(b, Sources.Name(b.Source) + " · " + state);
                })
                .ToList();
            var signature = string.Join("|", shown.Select(x => x.Book.Key + "/" + x.Badge + "/" + x.Title));
            if (signature != librarySignature)
            {
                librarySignature = signature;
                library.Clear();
                foreach (var item in shown)
                    library.Add(item);
            }
            FavoritesTitle.Text = "收藏 · " + shown.Count;
            LibraryEmpty.Visibility = Show(shown.Count == 0);
            LibraryEmptyText.Text =
                favorites.Count == 0
                    ? "书架还是空的。在漫画详情页点“收藏”即可加入书架。"
                    : "这个来源还没有收藏的漫画。";
        }

        void ContinueDetailClick(object sender, RoutedEventArgs e)
        {
            if (continueProgress != null)
                OpenDetail(App.Store.FindBook(continueProgress.Key) ?? continueProgress.Book);
        }

        void ContinueClick(object sender, RoutedEventArgs e)
        {
            if (continueProgress == null)
                return;
            Frame.Navigate(
                typeof(ReaderPage),
                new ReaderContext
                {
                    Book = continueProgress.Book,
                    Chapter = continueProgress.Chapter,
                    Page = continueProgress.Page,
                    Offset = continueProgress.Offset,
                }
            );
        }

        // ---------- 多选 ----------

        public bool HandleBack()
        {
            if (!selecting)
                return false;
            SetSelecting(false);
            return true;
        }

        /// <summary>Grid the multi-select commands act on; only the library and search pivots select.</summary>
        GridView SelectGrid
        {
            get { return Hub.SelectedIndex == SearchSection ? ResultsList : LibraryGrid; }
        }

        void SetSelecting(bool on)
        {
            selecting = on;
            var active = on ? SelectGrid : null;
            foreach (var grid in new[] { LibraryGrid, ResultsList })
            {
                bool mine = grid == active;
                grid.SelectionMode = mine ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
                grid.IsItemClickEnabled = !mine;
            }
            UpdateCommands();
        }

        void MultiSelectClick(object sender, RoutedEventArgs e)
        {
            SetSelecting(true);
        }

        void CancelSelectClick(object sender, RoutedEventArgs e)
        {
            SetSelecting(false);
        }

        void SelectAllClick(object sender, RoutedEventArgs e)
        {
            var grid = SelectGrid;
            if (grid.SelectedItems.Count == grid.Items.Count)
                grid.SelectedItems.Clear();
            else
                grid.SelectAll();
        }

        void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!selecting)
                return;
            var grid = SelectGrid;
            int count = grid.SelectedItems.Count;
            bool allFavorite = false;
            if (count > 0)
            {
                lock (App.Store.SyncRoot)
                    allFavorite = grid
                        .SelectedItems.Cast<BookItem>()
                        .All(i => App.Store.Data.Favorites.Any(b => b.Key == i.Book.Key));
            }
            FavoriteSelectedCommand.Label =
                (allFavorite ? "移出书架" : "加入书架") + (count > 0 ? "（" + count + "）" : "");
            FavoriteSelectedCommand.IsEnabled = count > 0;
            DownloadSelectedCommand.Label = "下载" + (count > 0 ? "（" + count + "）" : "");
            DownloadSelectedCommand.IsEnabled = count > 0;
        }

        async void FavoriteSelectedClick(object sender, RoutedEventArgs e)
        {
            var items = SelectGrid.SelectedItems.Cast<BookItem>().ToList();
            if (items.Count == 0)
                return;
            bool remove;
            lock (App.Store.SyncRoot)
                remove = items.All(i => App.Store.Data.Favorites.Any(b => b.Key == i.Book.Key));
            SetSelecting(false);
            int done = 0;
            var failed = new List<string>();
            SetBusy(true);
            try
            {
                foreach (var item in items)
                {
                    try
                    {
                        await App.Store.SetFavorite(item.Book, !remove);
                        done++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add((item.Title ?? item.Book.Id) + "：" + Ui.Describe(ex));
                    }
                }
            }
            finally
            {
                SetBusy(false);
            }
            RefreshLibrary();
            var text = "已" + (remove ? "移出书架" : "加入书架") + " " + done + " 部漫画。";
            if (failed.Count > 0)
                text += "\n" + failed.Count + " 部没有完成：" + string.Join("；", failed.Take(3));
            await Ui.Alert("书架已更新", text);
        }

        async void DownloadSelectedClick(object sender, RoutedEventArgs e)
        {
            var items = SelectGrid.SelectedItems.Cast<BookItem>().ToList();
            if (items.Count == 0)
                return;
            if (
                !await Ui.Confirm(
                    "下载 " + items.Count + " 部漫画的全部章节？",
                    "会依次获取每部漫画的章节并加入下载队列；已下载或已在队列中的章节会自动跳过。",
                    "下载"
                )
            )
                return;
            SetSelecting(false);
            int books = 0,
                chapters = 0;
            var failed = new List<string>();
            SetBusy(true);
            try
            {
                foreach (var item in items)
                {
                    try
                    {
                        var book = item.Book;
                        if (book.Chapters.Count == 0)
                            book = await App.Sources.Detail(book, CancellationToken.None);
                        foreach (var chapter in book.Chapters)
                            await App.Downloads.Add(book, chapter);
                        books++;
                        chapters += book.Chapters.Count;
                    }
                    catch (Exception ex)
                    {
                        failed.Add((item.Title ?? item.Book.Id) + "：" + Ui.Describe(ex));
                    }
                }
            }
            finally
            {
                SetBusy(false);
            }
            RefreshDownloads();
            var text = "已加入下载队列：" + books + " 部漫画，共 " + chapters + " 章。";
            if (failed.Count > 0)
                text += "\n" + failed.Count + " 部没有完成：" + string.Join("；", failed.Take(3));
            await Ui.Alert("下载已加入队列", text);
        }
        // ---------- 发现 ----------

        void DiscoverSourceChecked(object sender, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)sender).Tag;
            if (id == discoverSource || !ready)
                return;
            discoverSource = id;
            LoadDiscover(false);
        }

        void DiscoverRefreshClick(object sender, RoutedEventArgs e)
        {
            LoadDiscover(false);
        }

        void DiscoverMoreClick(object sender, RoutedEventArgs e)
        {
            LoadDiscover(discover.Count > 0);
        }

        async void LoadDiscover(bool more)
        {
            if (more && discoverLoading)
                return;
            if (!more)
            {
                discoverCts?.Cancel();
                discoverCts = new CancellationTokenSource();
                discover.Clear();
                discoverPage = 0;
            }
            var cts = discoverCts;
            var source = discoverSource;
            discoverLoading = true;
            DiscoverMore.Visibility = Visibility.Collapsed;
            DiscoverRetry.Visibility = Visibility.Collapsed;
            DiscoverStatus.Text = "正在加载 " + Sources.Name(source) + "…";
            SetBusy(true);
            try
            {
                var books = await App.Sources.Search(source, "", discoverPage + 1, cts.Token);
                if (cts.IsCancellationRequested)
                    return;
                discoverPage++;
                int added = 0;
                foreach (var b in books)
                {
                    if (discover.Any(x => x.Book.Key == b.Key))
                        continue;
                    discover.Add(new BookItem(b));
                    added++;
                }
                bool end = added == 0;
                DiscoverStatus.Text = discover.Count == 0 ? "这个来源暂时没有返回漫画。" : "";
                DiscoverMore.Content = "加载更多";
                DiscoverMore.Visibility = Show(!end);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested)
                {
                    DiscoverStatus.Text = Sources.Name(source) + " 加载失败。" + Ui.Describe(ex);
                    // An empty GridView places its footer at the bottom of the viewport, so retry the
                    // first page from the header and later pages from the footer.
                    DiscoverRetry.Visibility = Show(discover.Count == 0);
                    DiscoverMore.Content = "重试加载更多";
                    DiscoverMore.Visibility = Show(discover.Count > 0);
                }
            }
            finally
            {
                if (cts == discoverCts)
                    discoverLoading = false;
                SetBusy(false);
            }
        }

        // ---------- 搜索 ----------

        void SyncSearchHeader()
        {
            var o = SearchState.Options;
            syncing = true;
            ScopeOnline.IsChecked = !o.Local;
            ScopeLocal.IsChecked = o.Local;
            var all = o.Sources.Count == Ui.SourceIds.Length;
            SourceAll.IsChecked = all;
            SourceJm.IsChecked = !all && o.Sources.Count == 1 && o.Sources[0] == "jm";
            SourceBoylove.IsChecked = !all && o.Sources.Count == 1 && o.Sources[0] == "boylove";
            SourceManwa.IsChecked = !all && o.Sources.Count == 1 && o.Sources[0] == "manwa";
            syncing = false;

            chips.Clear();
            if (o.Any)
                chips.Add(new FilterChip("any", "任一关键词"));
            if (!string.IsNullOrWhiteSpace(o.Exclude))
                chips.Add(new FilterChip("exclude", "排除：" + o.Exclude));
            if (!string.IsNullOrWhiteSpace(o.Author))
                chips.Add(new FilterChip("author", "作者：" + o.Author));
            if (!string.IsNullOrWhiteSpace(o.Tags))
                chips.Add(new FilterChip("tags", "标签：" + o.Tags));
            if (o.State != "全部")
                chips.Add(new FilterChip("state", o.State));
            if (o.Sort != "相关度")
                chips.Add(new FilterChip("sort", "按" + o.Sort + "排序"));
            if (o.Sources.Count > 1 && o.Sources.Count < Ui.SourceIds.Length)
                chips.Add(new FilterChip("sources", string.Join(" / ", o.Sources.Select(Sources.Name))));
            ChipsScroller.Visibility = Show(chips.Count > 0);
            FilterNote.Visibility = Show(
                !string.IsNullOrWhiteSpace(o.Author)
                    || !string.IsNullOrWhiteSpace(o.Tags)
                    || o.State != "全部"
            );
            AdvancedLink.Content = chips.Count > 0 ? "高级搜索 · " + chips.Count + " 项条件" : "高级搜索";
            ClearLink.Visibility = Show(chips.Count > 0 || !string.IsNullOrWhiteSpace(QueryBox.Text));
        }

        void ScopeChecked(object sender, RoutedEventArgs e)
        {
            if (syncing || !ready)
                return;
            bool local = ScopeLocal.IsChecked == true;
            if (local == SearchState.Options.Local)
                return;
            SearchState.Options.Local = local;
            if (lastSearch != null)
                RunSearch(false);
        }

        void SearchSourceChecked(object sender, RoutedEventArgs e)
        {
            if (syncing || !ready)
                return;
            var id = (string)((FrameworkElement)sender).Tag;
            var sources = id == "" ? Ui.SourceIds.ToList() : new List<string> { id };
            if (sources.SequenceEqual(SearchState.Options.Sources))
                return;
            SearchState.Options.Sources = sources;
            SyncSearchHeader();
            if (lastSearch != null)
                RunSearch(false);
        }

        void ChipClick(object sender, RoutedEventArgs e)
        {
            var chip = ((FrameworkElement)sender).DataContext as FilterChip;
            if (chip == null)
                return;
            var o = SearchState.Options;
            switch (chip.Key)
            {
                case "any":
                    o.Any = false;
                    break;
                case "exclude":
                    o.Exclude = "";
                    break;
                case "author":
                    o.Author = "";
                    break;
                case "tags":
                    o.Tags = "";
                    break;
                case "state":
                    o.State = "全部";
                    break;
                case "sort":
                    o.Sort = "相关度";
                    break;
                case "sources":
                    o.Sources = Ui.SourceIds.ToList();
                    break;
            }
            SyncSearchHeader();
            if (lastSearch != null)
                RunSearch(false);
        }

        void ClearSearchClick(object sender, RoutedEventArgs e)
        {
            searchCts.Cancel();
            searchGeneration++;
            SearchState.Options = new SearchOptions { Local = SearchState.Options.Local };
            QueryBox.Text = "";
            lastSearch = null;
            results.Clear();
            resultKeys.Clear();
            statuses.Clear();
            ResultsTitle.Visibility = Visibility.Collapsed;
            SearchMessage.Visibility = Visibility.Collapsed;
            MoreResults.Visibility = Visibility.Collapsed;
            PagingStatus.Visibility = Visibility.Collapsed;
            UpdateStatusSummary();
            SyncSearchHeader();
        }

        void AdvancedClick(object sender, RoutedEventArgs e)
        {
            SearchState.Options.Query = QueryBox.Text ?? "";
            Frame.Navigate(typeof(AdvancedSearchPage));
        }

        void SearchCommandClick(object sender, RoutedEventArgs e)
        {
            RunSearch(false);
        }

        void MoreResultsClick(object sender, RoutedEventArgs e)
        {
            RunSearch(true);
        }

        List<string> RecentQueries(string text)
        {
            lock (App.Store.SyncRoot)
                return App
                    .Store.Data.Queries.Where(q => string.IsNullOrEmpty(text) || SearchOptions.Has(q, text))
                    .Take(8)
                    .ToList();
        }

        void QueryGotFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(QueryBox.Text))
            {
                var recent = RecentQueries("");
                QueryBox.ItemsSource = recent;
                QueryBox.IsSuggestionListOpen = recent.Count > 0;
            }
        }

        void QueryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                sender.ItemsSource = RecentQueries(sender.Text.Trim());
            ClearLink.Visibility = Show(chips.Count > 0 || !string.IsNullOrWhiteSpace(sender.Text));
        }

        void QuerySuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            sender.Text = args.SelectedItem as string ?? sender.Text;
        }

        void QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is string)
                sender.Text = (string)args.ChosenSuggestion;
            ResultsList.Focus(FocusState.Programmatic);
            RunSearch(false);
        }

        void RunSearch(bool more)
        {
            var ignore = SearchAsync(more);
        }

        bool CanLoadMoreResults()
        {
            return !searching
                && lastSearch != null
                && !lastSearch.Local
                && statuses.Any(s => s.More && !s.Failed && s.Page == searchPage);
        }

        async Task SearchAsync(bool more)
        {
            var o = SearchState.Options;
            if (!more)
                o.Query = (QueryBox.Text ?? "").Trim();
            if (SearchOptions.Words(o.Query).Length == 0 && SearchOptions.Words(o.Tags).Length == 0)
            {
                SearchMessage.Text = "请输入关键词，或在高级搜索中填写标签。";
                SearchMessage.Visibility = Visibility.Visible;
                return;
            }
            if (more && (searching || lastSearch == null))
                return;
            SearchOptions options;
            int page;
            if (!more)
            {
                searchCts.Cancel();
                searchCts = new CancellationTokenSource();
                lastSearch = Ui.Clone(o);
                options = Ui.Clone(o);
                if (selecting)
                    SetSelecting(false);
                results.Clear();
                resultKeys.Clear();
                statuses.Clear();
                foreach (var id in options.Sources)
                    statuses.Add(new SourceStatus(id) { Running = true, Text = "正在搜索…" });
                searchPage = page = 1;
            }
            else
            {
                page = searchPage + 1;
                options = Ui.Clone(lastSearch);
                options.Sources = statuses
                    .Where(s => s.More && !s.Failed && s.Page == searchPage)
                    .Select(s => s.Id)
                    .ToList();
                if (options.Sources.Count == 0)
                    return;
                foreach (var s in statuses.Where(s => options.Sources.Contains(s.Id)))
                {
                    s.Running = true;
                    s.Text = "正在加载第 " + page + " 页…";
                }
            }
            var cts = searchCts;
            int generation = ++searchGeneration;
            var arrived = new HashSet<string>();
            searching = true;
            SearchMessage.Visibility = Visibility.Collapsed;
            MoreResults.Visibility = Visibility.Collapsed;
            PagingStatus.Text = more ? "正在加载第 " + page + " 页…" : "";
            PagingStatus.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
            ResultsTitle.Visibility = Visibility.Visible;
            ResultsTitle.Text = "结果 · " + results.Count;
            SyncSearchHeader();
            UpdateStatusSummary();
            SetBusy(true);
            try
            {
                var batch = await App.Search.Search(
                    options,
                    page,
                    cts.Token,
                    r =>
                    {
                        var ignore = Dispatcher.RunAsync(
                            CoreDispatcherPriority.Normal,
                            () =>
                            {
                                if (generation != searchGeneration)
                                    return;
                                arrived.Add(r.Source);
                                Arrived(r, options.Local);
                            }
                        );
                    }
                );
                if (generation != searchGeneration)
                    return;
                foreach (var r in batch.Sources.Where(r => !arrived.Contains(r.Source)))
                    Arrived(r, options.Local);
                searchPage = page;
                if (page == 1)
                {
                    // Per-source batches arrive in network order; restore the service's ranking once all are in.
                    var order = batch.Books.Select(b => b.Key).ToList();
                    if (order.Count == results.Count && !order.SequenceEqual(results.Select(x => x.Book.Key)))
                    {
                        var byKey = results.ToDictionary(x => x.Book.Key);
                        results.Clear();
                        foreach (var key in order)
                            results.Add(byKey[key]);
                    }
                }
                FinishSearch();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (generation == searchGeneration)
                {
                    SearchMessage.Text = Ui.Describe(ex);
                    SearchMessage.Visibility = Visibility.Visible;
                    foreach (var s in statuses.Where(s => s.Running))
                    {
                        s.Running = false;
                        s.Text = "未完成";
                    }
                    PagingStatus.Visibility = Visibility.Collapsed;
                    MoreResults.Visibility = Show(more);
                    MoreResults.Content = "重试加载更多";
                }
            }
            finally
            {
                if (generation == searchGeneration)
                {
                    searching = false;
                    UpdateStatusSummary();
                }
                SetBusy(false);
            }
        }

        void UpdateStatusSummary()
        {
            StatusSummaryRow.Visibility = Show(statuses.Count > 0);
            if (statuses.Count == 0)
            {
                StatusList.Visibility = Visibility.Collapsed;
                return;
            }
            int running = statuses.Count(s => s.Running);
            var failed = statuses.Where(s => s.Failed).Select(s => s.Name).ToList();
            int ok = statuses.Count(s => !s.Running && !s.Failed);
            var parts = new List<string>();
            if (running > 0)
                parts.Add("正在搜索 " + running + " 个来源");
            if (ok > 0)
                parts.Add(ok + " 个来源成功");
            if (failed.Count > 0)
                parts.Add(string.Join("、", failed) + " 失败");
            StatusSummary.Text = string.Join(" · ", parts);
            StatusSummary.Foreground = Ui.Resource(
                failed.Count > 0 ? "ErrorBrush" : "SystemControlForegroundBaseMediumBrush"
            );
            StatusList.Visibility = Show(statusExpanded);
            StatusToggle.Content = statusExpanded ? "收起" : failed.Count > 0 ? "详情 · 重试" : "详情";
        }

        void StatusToggleClick(object sender, RoutedEventArgs e)
        {
            statusExpanded = !statusExpanded;
            UpdateStatusSummary();
        }

        void Arrived(SourceSearchResult r, bool local)
        {
            var s = statuses.FirstOrDefault(x => x.Id == r.Source);
            if (s == null)
                return;
            s.Running = false;
            s.Page = r.Page;
            s.Failed = !r.IsSuccess;
            s.More = r.IsSuccess && r.MoreMayExist && !local;
            if (r.IsSuccess)
            {
                foreach (var b in r.Books)
                    if (resultKeys.Add(b.Key))
                        results.Add(new BookItem(b));
                s.Text =
                    (local ? "本地 " : "第 " + r.Page + " 页 · ")
                    + r.Books.Count
                    + " 条"
                    + (r.MetadataUnknown > 0 ? " · " + r.MetadataUnknown + " 条资料不全，未计入" : "");
            }
            else
            {
                s.Text = "获取失败。" + Ui.Describe(r.Error);
            }
            ResultsTitle.Text = "结果 · " + results.Count;
            UpdateStatusSummary();
        }

        void FinishSearch()
        {
            ResultsTitle.Text = "结果 · " + results.Count;
            bool anyFailed = statuses.Any(s => s.Failed);
            if (results.Count == 0)
            {
                SearchMessage.Text =
                    lastSearch != null && lastSearch.Local ? "本地资料中没有匹配的漫画。可以切换到“在线”继续搜索。"
                    : anyFailed ? "成功的来源没有匹配结果，可以重试失败的来源。"
                    : "没有找到匹配的漫画。可以减少关键词或清除条件。";
                SearchMessage.Visibility = Visibility.Visible;
            }
            bool more = statuses.Any(s => s.More && !s.Failed && s.Page == searchPage);
            // Scrolling to the end loads the next page automatically; the link stays as a fallback.
            MoreResults.Content = "加载更多结果";
            MoreResults.Visibility = Show(more);
            PagingStatus.Text = "已显示全部结果";
            PagingStatus.Visibility = Show(!more && results.Count > 0);
            UpdateStatusSummary();
        }

        async void RetrySourceClick(object sender, RoutedEventArgs e)
        {
            var s = ((FrameworkElement)sender).DataContext as SourceStatus;
            if (s == null || lastSearch == null || s.Running)
                return;
            var options = Ui.Clone(lastSearch);
            options.Sources = new List<string> { s.Id };
            var cts = searchCts;
            int generation = searchGeneration;
            s.Running = true;
            s.Text = "正在重试…";
            UpdateStatusSummary();
            SetBusy(true);
            try
            {
                var batch = await App.Search.Search(options, Math.Max(1, s.Page), cts.Token);
                if (generation != searchGeneration)
                    return;
                foreach (var r in batch.Sources)
                    Arrived(r, options.Local);
                FinishSearch();
                if (results.Count > 0)
                    SearchMessage.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (generation == searchGeneration)
                {
                    s.Failed = true;
                    s.Text = "获取失败：" + Ui.Describe(ex);
                }
            }
            finally
            {
                s.Running = false;
                UpdateStatusSummary();
                SetBusy(false);
            }
        }

        async void IdClick(object sender, RoutedEventArgs e)
        {
            var source = new ComboBox
            {
                Header = "来源",
                PlaceholderText = "选择一个来源",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = Ui.SourceIds.Select(Sources.Name).ToList(),
            };
            var o = SearchState.Options;
            if (o.Sources.Count == 1)
                source.SelectedIndex = Array.IndexOf(Ui.SourceIds, o.Sources[0]);
            var scope = new InputScope();
            scope.Names.Add(new InputScopeName(InputScopeNameValue.Number));
            var id = new TextBox { Header = "漫画 ID", InputScope = scope, Margin = new Thickness(0, 12, 0, 0) };
            var error = new TextBlock
            {
                Foreground = Ui.Resource("ErrorBrush"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 0),
            };
            var panel = new StackPanel();
            panel.Children.Add(source);
            panel.Children.Add(id);
            panel.Children.Add(error);
            var dialog = new ContentDialog
            {
                Title = "漫画 ID 直达",
                Content = panel,
                PrimaryButtonText = "打开",
                SecondaryButtonText = "取消",
            };
            dialog.PrimaryButtonClick += (d, args) =>
            {
                if (source.SelectedIndex < 0)
                {
                    error.Text = "ID 直达需要先选择一个来源。";
                    args.Cancel = true;
                }
                else if (!Regex.IsMatch((id.Text ?? "").Trim(), @"^[1-9]\d*$"))
                {
                    error.Text = "请输入来源中的数字 ID。";
                    args.Cancel = true;
                }
            };
            if (await Ui.Show(dialog) != ContentDialogResult.Primary)
                return;
            var value = id.Text.Trim();
            OpenDetail(new Book { Source = Ui.SourceIds[source.SelectedIndex], Id = value, Title = value });
        }

        // ---------- 下载 ----------

        void DownloadsChanged()
        {
            var ignore = Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                () =>
                {
                    if (!downloadTimer.IsEnabled)
                        downloadTimer.Start();
                }
            );
        }

        void DownloadTabChecked(object sender, RoutedEventArgs e)
        {
            if (!ready)
                return;
            showCompleted = DownloadDoneTab.IsChecked == true;
            RefreshDownloads();
        }

        void RefreshDownloads()
        {
            if (DownloadList == null)
                return;
            var order = new List<DownloadItem>();
            lock (App.Store.SyncRoot)
            {
                foreach (var job in App.Store.Data.Downloads)
                {
                    DownloadItem item;
                    if (!downloadItems.TryGetValue(job.Key, out item))
                        downloadItems[job.Key] = item = new DownloadItem(job.Key);
                    item.Update(job);
                    order.Add(item);
                }
            }
            foreach (var key in downloadItems.Keys.Except(order.Select(i => i.Key)).ToList())
                downloadItems.Remove(key);

            int active = order.Count(i => !i.Completed),
                done = order.Count - active;
            var visible = order.Where(i => i.Completed == showCompleted).ToList();
            var signature = string.Join("|", visible.Select(i => i.Key));
            if (signature != downloadSignature)
            {
                downloadSignature = signature;
                DownloadGroups.Source = visible
                    .GroupBy(i => i.Book.Key)
                    .Select(g => new Group<DownloadItem>(g.First().Book.Title ?? g.First().Book.Id, g))
                    .ToList();
            }
            DownloadEmpty.Visibility = Show(visible.Count == 0);
            DownloadEmptyText.Text = showCompleted
                ? "还没有下载完成的章节。"
                : "没有进行中的下载。在漫画详情页点“下载”选择章节。";
            DownloadActiveTab.Content = active > 0 ? "进行中 " + active : "进行中";
            DownloadDoneTab.Content = done > 0 ? "已完成 " + done : "已完成";

            var s = Ui.SettingsSnapshot();
            DownloadSummary.Text =
                "并发 "
                + s.Concurrency
                + " · "
                + (s.ReadingPriority ? "阅读优先" : "不让行阅读")
                + " · "
                + (s.WifiOnly ? "仅 Wi-Fi" : "允许移动网络");
        }

        static DownloadJob Stub(DownloadItem item)
        {
            return new DownloadJob { Key = item.Key };
        }

        void DownloadClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as DownloadItem;
            if (item != null && item.Completed)
                OpenReader(item.Book, item.Chapter);
        }

        async void DownloadActionClick(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as DownloadItem;
            if (item == null)
                return;
            try
            {
                switch (item.Action)
                {
                    case "离线阅读":
                        OpenReader(item.Book, item.Chapter);
                        return;
                    case "暂停":
                        await App.Downloads.Pause(Stub(item));
                        break;
                    default:
                        await App.Downloads.Resume(Stub(item));
                        break;
                }
            }
            catch (Exception ex)
            {
                await Ui.Alert("操作没有完成", Ui.Describe(ex));
            }
            RefreshDownloads();
        }

        async void DownloadRemoveClick(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as DownloadItem;
            if (item == null)
                return;
            if (
                !await Ui.Confirm(
                    "删除这个章节的下载？",
                    "已保存的图片会被删除。收藏、阅读历史和进度保留。",
                    "删除"
                )
            )
                return;
            try
            {
                await App.Downloads.Remove(Stub(item));
            }
            catch (Exception ex)
            {
                await Ui.Alert("删除没有完成", Ui.Describe(ex));
            }
            RefreshDownloads();
        }

        async Task ForJobs(Func<string, bool> match, Func<DownloadJob, Task> action)
        {
            List<string> keys;
            lock (App.Store.SyncRoot)
                keys = App.Store.Data.Downloads.Where(j => match(j.State)).Select(j => j.Key).ToList();
            SetBusy(true);
            try
            {
                foreach (var key in keys)
                {
                    try
                    {
                        await action(new DownloadJob { Key = key });
                    }
                    catch (KeyNotFoundException) { }
                }
            }
            finally
            {
                SetBusy(false);
                RefreshDownloads();
            }
        }

        async void PauseAllClick(object sender, RoutedEventArgs e)
        {
            await ForJobs(s => s == "等待" || s == "下载中", App.Downloads.Pause);
        }

        async void ResumeAllClick(object sender, RoutedEventArgs e)
        {
            await ForJobs(s => s == "已暂停", App.Downloads.Resume);
        }

        async void RetryFailedClick(object sender, RoutedEventArgs e)
        {
            await ForJobs(s => s == "失败", App.Downloads.Resume);
        }
    }
}
