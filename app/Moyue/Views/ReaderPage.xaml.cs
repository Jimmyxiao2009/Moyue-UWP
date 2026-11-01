using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Display;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Moyue
{
    public sealed partial class ReaderPage : Page, IBackHandler
    {
        const double PlaceholderRatio = 1.42;

        ReaderContext context;
        List<PageInfo> pages = new List<PageInfo>();
        List<ReaderPageItem> items = new List<ReaderPageItem>();
        readonly List<Grid> slots = new List<Grid>();
        readonly List<Image> slotImages = new List<Image>();
        readonly Dictionary<int, BitmapImage> loaded = new Dictionary<int, BitmapImage>();
        HashSet<int> loading = new HashSet<int>();
        readonly SemaphoreSlim loader = new SemaphoreSlim(1, 1);
        readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        readonly double scale;
        CancellationTokenSource lifetime = new CancellationTokenSource();
        int current,
            generation;
        bool paged,
            opening,
            disposed,
            restoring,
            sliderSync,
            toolsVisible = true;

        public ReaderPage()
        {
            InitializeComponent();
            scale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            saveTimer.Tick += async (s, e) =>
            {
                saveTimer.Stop();
                await SaveQuietly();
            };
            SizeChanged += (s, e) => Resize();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            context = (ReaderContext)e.Parameter;
            // Bookmarks and some history entries keep a light book copy; use the stored one
            // with chapters so previous/next chapter and the directory work.
            if (context.Book.Chapters == null || context.Book.Chapters.Count == 0)
                context.Book = App.Store.FindBook(context.Book.Key) ?? context.Book;
            context.Book.Chapters = context.Book.Chapters ?? new List<Chapter>();
            current = Math.Max(0, context.Page);
            paged = Prefs.PagedReading;
            App.Images.Reading = true;
            if (Ui.IsMobile)
                ApplicationView.GetForCurrentView().TryEnterFullScreenMode();
            Hint.Visibility = Prefs.ReaderHintSeen ? Visibility.Collapsed : Visibility.Visible;
            restoring = true;
            ScrollMode.IsChecked = !paged;
            PagedMode.IsChecked = paged;
            restoring = false;
            ApplyMode(false);
            UpdateChapterButtons();
            Open();
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            disposed = true;
            saveTimer.Stop();
            lifetime.Cancel();
            App.Images.Reading = false;
            App.Downloads.Start();
            if (Ui.IsMobile)
                ApplicationView.GetForCurrentView().ExitFullScreenMode();
            await SaveQuietly();
            foreach (var image in slotImages)
                image.Source = null;
            foreach (var item in items)
                item.Image = null;
            loaded.Clear();
        }

        public bool HandleBack()
        {
            if (DirectoryPanel.Visibility == Visibility.Visible)
            {
                DirectoryPanel.Visibility = Visibility.Collapsed;
                return true;
            }
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                CloseSettings();
                return true;
            }
            return false;
        }

        double ViewWidth
        {
            get { return Root.ActualWidth > 0 ? Root.ActualWidth : Window.Current.Bounds.Width; }
        }

        // ---------- 打开章节 ----------

        async void Open()
        {
            int gen = ++generation;
            opening = true;
            Busy.Visibility = Visibility.Visible;
            MessagePanel.Visibility = Visibility.Collapsed;
            BookName.Text = context.Book.Title ?? context.Book.Id;
            ChapterName.Text = context.Chapter.Title ?? "";
            PageLabel.Text = "正在打开章节…";
            try
            {
                DownloadJob job;
                var key = Store.ChapterKey(context.Book, context.Chapter);
                lock (App.Store.SyncRoot)
                {
                    job = App.Store.Data.Downloads.FirstOrDefault(j => j.Key == key && j.State == "已完成");
                    if (job != null)
                        pages = job.Pages.ToList();
                }
                SourceBadge.Text = job != null ? "已下载 · 离线" : Sources.Name(context.Book.Source);
                if (job == null)
                    pages = await App.Sources.Pages(context.Book, context.Chapter, lifetime.Token);
                if (disposed || gen != generation)
                    return;
                if (pages.Count == 0)
                    throw new InvalidOperationException("这一章没有可显示的图片。");
                current = Math.Max(0, Math.Min(current, pages.Count - 1));
                BuildSlots();
                sliderSync = true;
                PageSlider.Maximum = pages.Count;
                PageSlider.Value = current + 1;
                sliderSync = false;
                UpdatePageLabel();
                await LoadWindow();
                if (disposed || gen != generation)
                    return;
                restoring = true;
                if (paged)
                    Flip.SelectedIndex = current;
                else
                {
                    Strip.UpdateLayout();
                    Viewport.ChangeView(null, Top(current) + context.Offset * SlotHeight(current), 1, true);
                }
                restoring = false;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (gen == generation && !disposed)
                {
                    PageLabel.Text = "章节没有打开";
                    ShowMessage("章节打开失败：" + Ui.Describe(ex));
                }
            }
            finally
            {
                if (gen == generation)
                {
                    opening = false;
                    Busy.Visibility = Visibility.Collapsed;
                }
            }
        }

        void BuildSlots()
        {
            Strip.Children.Clear();
            slots.Clear();
            slotImages.Clear();
            loaded.Clear();
            loading = new HashSet<int>();
            items = Enumerable.Range(0, pages.Count).Select(i => new ReaderPageItem(i)).ToList();
            double width = ViewWidth;
            var numberBrush = Ui.Resource("SystemControlForegroundBaseMediumLowBrush");
            for (int i = 0; i < pages.Count; i++)
            {
                var slot = new Grid { Width = width, Height = width * PlaceholderRatio };
                slot.Children.Add(
                    new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        FontSize = 34,
                        FontWeight = Windows.UI.Text.FontWeights.Light,
                        Foreground = numberBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                );
                var image = new Image { Stretch = Stretch.Uniform };
                slot.Children.Add(image);
                slots.Add(slot);
                slotImages.Add(image);
                Strip.Children.Add(slot);
            }
            restoring = true;
            Flip.ItemsSource = items;
            restoring = false;
        }

        // ---------- 图片窗口 ----------

        int DecodeWidth(bool oldDevice)
        {
            int cap = oldDevice ? 1080 : 1440;
            return (int)Math.Min(cap, Math.Max(360, ViewWidth * scale));
        }

        async Task LoadWindow()
        {
            if (pages.Count == 0)
                return;
            int gen = generation,
                selected = current,
                preload;
            bool oldDevice;
            lock (App.Store.SyncRoot)
            {
                preload = Math.Max(0, Math.Min(3, App.Store.Data.Settings.Preload));
                oldDevice = App.Store.Data.Settings.OldDevice;
            }
            // 老设备优化: back off preloading once the system reports memory pressure.
            if (oldDevice && MemoryManager.AppMemoryUsageLevel != AppMemoryUsageLevel.Low)
                preload = Math.Min(preload, 1);
            int from = Math.Max(0, selected - 1),
                to = Math.Min(pages.Count - 1, selected + preload);
            foreach (var old in loaded.Keys.Where(i => i < from || i > to).ToList())
            {
                slotImages[old].Source = null;
                items[old].Image = null;
                loaded.Remove(old);
            }
            var order = Enumerable
                .Range(selected, to - selected + 1)
                .Concat(Enumerable.Range(from, selected - from))
                .ToList();
            var set = loading;
            var token = lifetime.Token;
            foreach (int i in order)
            {
                if (disposed || gen != generation)
                    return;
                if (loaded.ContainsKey(i) || set.Contains(i))
                    continue;
                set.Add(i);
                try
                {
                    await loader.WaitAsync(token);
                    try
                    {
                        if (gen != generation || i < current - 1 || i > current + preload)
                            continue;
                        var file = await App.Images.Page(context.Book, context.Chapter, pages[i], i, false, token);
                        var bitmap = new BitmapImage { DecodePixelWidth = DecodeWidth(oldDevice) };
                        using (var stream = await file.OpenReadAsync())
                            await bitmap.SetSourceAsync(stream);
                        if (disposed || gen != generation)
                            return;
                        if (i < current - 1 || i > current + Math.Max(1, preload))
                            continue;
                        loaded[i] = bitmap;
                        items[i].Failed = false;
                        items[i].Image = bitmap;
                        if (!paged)
                            slotImages[i].Source = bitmap;
                        SizeSlot(i, bitmap);
                        if (i == current)
                            MessagePanel.Visibility = Visibility.Collapsed;
                    }
                    finally
                    {
                        loader.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (gen == generation && i < items.Count)
                    {
                        items[i].Failed = true;
                        if (i == current)
                            ShowMessage("第 " + (i + 1) + " 页加载失败：" + Ui.Describe(ex));
                    }
                }
                finally
                {
                    set.Remove(i);
                }
            }
        }

        async void TryLoad()
        {
            try
            {
                await LoadWindow();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ShowMessage(Ui.Describe(ex));
            }
        }

        void SizeSlot(int i, BitmapImage bitmap)
        {
            var slot = slots[i];
            double width = ViewWidth;
            double height = bitmap != null && bitmap.PixelWidth > 0
                ? width * bitmap.PixelHeight / bitmap.PixelWidth
                : width * PlaceholderRatio;
            double delta = height - slot.Height;
            slot.Width = width;
            slot.Height = height;
            // A page above the reading position changed height; keep the visible content still.
            if (!paged && i < current && Math.Abs(delta) > 0.5 && !opening)
            {
                restoring = true;
                Viewport.ChangeView(null, Viewport.VerticalOffset + delta * Viewport.ZoomFactor, null, true);
                restoring = false;
            }
        }

        void Resize()
        {
            if (slots.Count == 0)
                return;
            double width = ViewWidth;
            for (int i = 0; i < slots.Count; i++)
            {
                BitmapImage bitmap;
                loaded.TryGetValue(i, out bitmap);
                double ratio = slots[i].Width > 0 ? slots[i].Height / slots[i].Width : PlaceholderRatio;
                slots[i].Width = width;
                slots[i].Height = bitmap != null && bitmap.PixelWidth > 0
                    ? width * bitmap.PixelHeight / bitmap.PixelWidth
                    : width * ratio;
            }
            if (!paged)
            {
                Strip.UpdateLayout();
                restoring = true;
                Viewport.ChangeView(null, Top(current) * Viewport.ZoomFactor, null, true);
                restoring = false;
            }
        }

        double SlotHeight(int i)
        {
            return slots[i].ActualHeight > 0 ? slots[i].ActualHeight : slots[i].Height;
        }

        double Top(int page)
        {
            double top = 0;
            for (int i = 0; i < page && i < slots.Count; i++)
                top += SlotHeight(i);
            return top;
        }

        // ---------- 翻页与位置 ----------

        void UpdatePageLabel()
        {
            PageLabel.Text = pages.Count == 0 ? "" : "第 " + (current + 1) + " / " + pages.Count + " 页";
            sliderSync = true;
            PageSlider.Value = current + 1;
            sliderSync = false;
            UpdateBookmarkToggle();
        }

        Bookmark CurrentBookmark()
        {
            lock (App.Store.SyncRoot)
                return App.Store.Data.Bookmarks.FirstOrDefault(m =>
                    m.Key == context.Book.Key && m.Chapter.Id == context.Chapter.Id && m.Page == current
                );
        }

        void UpdateBookmarkToggle()
        {
            BookmarkToggle.IsEnabled = pages.Count > 0;
            bool marked = pages.Count > 0 && CurrentBookmark() != null;
            BookmarkToggle.IsChecked = marked;
            BookmarkToggle.Label = marked ? "已加书签" : "书签";
        }

        async void BookmarkToggleClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var mark = CurrentBookmark();
                if (mark != null)
                    await App.Store.RemoveBookmark(mark.Id);
                else
                {
                    double offset = 0;
                    if (!paged && slots.Count > current)
                    {
                        double height = SlotHeight(current);
                        double y = Viewport.VerticalOffset / Viewport.ZoomFactor;
                        offset = height > 0 ? Math.Max(0, Math.Min(1, (y - Top(current)) / height)) : 0;
                    }
                    await App.Store.AddBookmark(context.Book, context.Chapter, current, offset);
                }
            }
            catch (Exception ex)
            {
                ShowMessage("书签没有保存：" + Ui.Describe(ex));
            }
            UpdateBookmarkToggle();
        }

        void SetCurrent(int page)
        {
            if (page == current)
                return;
            current = page;
            UpdatePageLabel();
            MessagePanel.Visibility = items.Count > page && items[page].Failed ? Visibility.Visible : Visibility.Collapsed;
            TryLoad();
        }

        void ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (disposed || opening || restoring || paged || slots.Count == 0)
                return;
            double y = Viewport.VerticalOffset / Viewport.ZoomFactor,
                top = 0;
            int page = slots.Count - 1;
            for (int i = 0; i < slots.Count; i++)
            {
                double height = SlotHeight(i);
                if (y < top + height * 0.8)
                {
                    page = i;
                    break;
                }
                top += height;
            }
            SetCurrent(page);
            if (!e.IsIntermediate)
            {
                saveTimer.Stop();
                saveTimer.Start();
            }
        }

        void FlipChanged(object sender, SelectionChangedEventArgs e)
        {
            if (restoring || disposed || !paged || Flip.SelectedIndex < 0)
                return;
            SetCurrent(Flip.SelectedIndex);
            saveTimer.Stop();
            saveTimer.Start();
        }

        void Go(int page)
        {
            if (page < 0 || page >= slots.Count)
                return;
            if (paged)
                Flip.SelectedIndex = page;
            else
            {
                restoring = true;
                Viewport.ChangeView(null, Top(page) * Viewport.ZoomFactor, null, true);
                restoring = false;
                SetCurrent(page);
            }
            saveTimer.Stop();
            saveTimer.Start();
        }

        void SliderChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (sliderSync || opening || slots.Count == 0)
                return;
            Go((int)Math.Round(e.NewValue) - 1);
        }

        async Task SaveQuietly()
        {
            try
            {
                if (context == null || pages.Count == 0 || slots.Count == 0)
                    return;
                double offset = 0;
                if (!paged)
                {
                    double height = SlotHeight(current);
                    double y = Viewport.VerticalOffset / Viewport.ZoomFactor;
                    offset = height > 0 ? Math.Max(0, Math.Min(1, (y - Top(current)) / height)) : 0;
                }
                context.Page = current;
                context.Offset = offset;
                await App.Store.Record(context.Book, context.Chapter, current, offset);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        // ---------- 工具栏 ----------

        void ContentTapped(object sender, TappedRoutedEventArgs e)
        {
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                CloseSettings();
                return;
            }
            if (paged)
            {
                double x = e.GetPosition(Root).X,
                    width = Root.ActualWidth;
                if (x < width * 0.25)
                {
                    Go(current - 1);
                    return;
                }
                if (x > width * 0.75)
                {
                    Go(current + 1);
                    return;
                }
            }
            SetTools(!toolsVisible);
        }

        void SetTools(bool visible)
        {
            toolsVisible = visible;
            TopBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            BottomBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible && Hint.Visibility == Visibility.Visible)
            {
                Hint.Visibility = Visibility.Collapsed;
                Prefs.ReaderHintSeen = true;
            }
        }

        void ShowMessage(string text)
        {
            Message.Text = text;
            MessagePanel.Visibility = Visibility.Visible;
            SetTools(true);
        }

        void RetryClick(object sender, RoutedEventArgs e)
        {
            MessagePanel.Visibility = Visibility.Collapsed;
            if (pages.Count == 0 || slots.Count == 0)
            {
                Open();
                return;
            }
            if (current < items.Count)
                items[current].Failed = false;
            TryLoad();
        }

        void ApplyMode(bool keepPosition)
        {
            Viewport.Visibility = paged ? Visibility.Collapsed : Visibility.Visible;
            Flip.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
            ModeLabel.Text = paged ? "横向翻页" : "纵向滚动";
            if (!keepPosition || slots.Count == 0)
                return;
            restoring = true;
            if (paged)
            {
                foreach (var image in slotImages)
                    image.Source = null;
                Flip.SelectedIndex = current;
            }
            else
            {
                foreach (var pair in loaded)
                    slotImages[pair.Key].Source = pair.Value;
                Strip.UpdateLayout();
                Viewport.ChangeView(null, Top(current) * Viewport.ZoomFactor, null, true);
            }
            restoring = false;
        }

        void ModeChecked(object sender, RoutedEventArgs e)
        {
            if (restoring || context == null)
                return;
            bool value = PagedMode.IsChecked == true;
            if (value == paged)
                return;
            paged = value;
            Prefs.PagedReading = value;
            ApplyMode(true);
        }

        void ReaderSettingsClick(object sender, RoutedEventArgs e)
        {
            BottomBar.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
        }

        void CloseSettings()
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            BottomBar.Visibility = toolsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        void CloseSettingsClick(object sender, RoutedEventArgs e)
        {
            CloseSettings();
        }

        // ---------- 章节 ----------

        int ChapterIndex
        {
            get { return context.Book.Chapters.FindIndex(c => c.Id == context.Chapter.Id); }
        }

        void UpdateChapterButtons()
        {
            int index = ChapterIndex;
            PrevChapter.IsEnabled = index > 0;
            NextChapter.IsEnabled = index >= 0 && index < context.Book.Chapters.Count - 1;
        }

        async void SwitchChapter(int index, int page = 0, double offset = 0)
        {
            if (index < 0 || index >= context.Book.Chapters.Count)
                return;
            await SaveQuietly();
            lifetime.Cancel();
            lifetime = new CancellationTokenSource();
            foreach (var image in slotImages)
                image.Source = null;
            foreach (var item in items)
                item.Image = null;
            context.Chapter = context.Book.Chapters[index];
            context.Page = current = Math.Max(0, page);
            context.Offset = offset;
            pages = new List<PageInfo>();
            DirectoryPanel.Visibility = Visibility.Collapsed;
            UpdateChapterButtons();
            restoring = true;
            Viewport.ChangeView(null, 0, 1, true);
            restoring = false;
            Open();
        }

        void PrevChapterClick(object sender, RoutedEventArgs e)
        {
            SwitchChapter(ChapterIndex - 1);
        }

        void NextChapterClick(object sender, RoutedEventArgs e)
        {
            SwitchChapter(ChapterIndex + 1);
        }

        void DirectoryTabChecked(object sender, RoutedEventArgs e)
        {
            if (context == null || BookmarksTab == null)
                return;
            ShowDirectoryTab(BookmarksTab.IsChecked == true);
        }

        void ShowDirectoryTab(bool bookmarks)
        {
            DirectoryList.Visibility = bookmarks ? Visibility.Collapsed : Visibility.Visible;
            DirectoryEmpty.Visibility =
                !bookmarks && context.Book.Chapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BookmarkList.Visibility = bookmarks ? Visibility.Visible : Visibility.Collapsed;
            if (!bookmarks)
            {
                BookmarkEmpty.Visibility = Visibility.Collapsed;
                return;
            }
            List<BookmarkItem> marks;
            lock (App.Store.SyncRoot)
                marks = App
                    .Store.Data.Bookmarks.Where(m => m.Key == context.Book.Key)
                    .Select(m => new BookmarkItem(m))
                    .ToList();
            // Reading order: chapter position first, then page.
            var order = context.Book.Chapters.Select(c => c.Id).ToList();
            BookmarkList.ItemsSource = marks
                .OrderBy(m =>
                {
                    int i = order.IndexOf(m.Mark.Chapter.Id);
                    return i < 0 ? int.MaxValue : i;
                })
                .ThenBy(m => m.Mark.Page)
                .ToList();
            BookmarkEmpty.Visibility = marks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void BookmarkItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as BookmarkItem;
            if (item == null)
                return;
            var mark = item.Mark;
            DirectoryPanel.Visibility = Visibility.Collapsed;
            if (mark.Chapter.Id == context.Chapter.Id && slots.Count > 0)
            {
                Go(Math.Min(mark.Page, slots.Count - 1));
                return;
            }
            int index = context.Book.Chapters.FindIndex(c => c.Id == mark.Chapter.Id);
            if (index >= 0)
                SwitchChapter(index, mark.Page, mark.Offset);
            else
                ShowMessage("这个书签所在的章节不在当前目录里，请回到详情页刷新。");
        }

        async void BookmarkRemoveClick(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as BookmarkItem;
            if (item == null)
                return;
            await App.Store.RemoveBookmark(item.Mark.Id);
            ShowDirectoryTab(true);
            UpdateBookmarkToggle();
        }

        void DirectoryClick(object sender, RoutedEventArgs e)
        {
            DirectoryBook.Text = context.Book.Title ?? context.Book.Id;
            DirectoryList.ItemsSource = context.Book.Chapters;
            ShowDirectoryTab(BookmarksTab.IsChecked == true);
            int index = ChapterIndex;
            DirectoryList.SelectedIndex = index;
            DirectoryPanel.Visibility = Visibility.Visible;
            if (index >= 0)
                DirectoryList.ScrollIntoView(context.Book.Chapters[index]);
        }

        void DirectoryItemClick(object sender, ItemClickEventArgs e)
        {
            var chapter = e.ClickedItem as Chapter;
            if (chapter == null)
                return;
            if (chapter.Id == context.Chapter.Id)
            {
                DirectoryPanel.Visibility = Visibility.Collapsed;
                return;
            }
            SwitchChapter(context.Book.Chapters.IndexOf(chapter));
        }
    }
}
