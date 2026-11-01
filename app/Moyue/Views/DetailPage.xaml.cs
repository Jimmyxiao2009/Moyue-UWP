using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Moyue
{
    public sealed partial class DetailPage : Page, IBackHandler
    {
        readonly ObservableCollection<ChapterItem> chapters = new ObservableCollection<ChapterItem>();
        Book book;
        CancellationTokenSource loading;
        bool favorite,
            selecting,
            synopsisExpanded;

        public DetailPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
            Chapters.ItemsSource = chapters;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            App.Downloads.Changed += DownloadsChanged;
            // Frame caches one instance per page type. Another comic can have reused this
            // instance through bookmarks; restore the journal's comic, not that last comic.
            var requestedBook = e.Parameter as Book;
            if (e.NavigationMode == NavigationMode.Back && book != null && requestedBook != null && book.Key == requestedBook.Key)
            {
                UpdateNotes();
                return;
            }
            book = (Book)e.Parameter;
            ExitSelection();
            synopsisExpanded = false;
            chapters.Clear();
            Fill();
            Load(book.Chapters.Count > 0);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            App.Downloads.Changed -= DownloadsChanged;
            if (e.NavigationMode == NavigationMode.Back)
                loading?.Cancel();
        }

        public bool HandleBack()
        {
            if (!selecting)
                return false;
            ExitSelection();
            return true;
        }

        void DownloadsChanged()
        {
            var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Low, UpdateNotes);
        }

        void Fill()
        {
            Cover.Book = book;
            BookTitle.Text = string.IsNullOrWhiteSpace(book.Title) ? "漫画 " + book.Id : book.Title;
            BookAuthor.Text = string.IsNullOrWhiteSpace(book.Author) ? "作者未知" : book.Author;
            BookSource.Text = Sources.Name(book.Source) + " · ID " + book.Id;
            BookState.Text = string.IsNullOrEmpty(book.State) ? "连载状态未知" : book.State;
            var tags = SearchOptions.Words(book.Tags);
            BookTags.Text = string.Join(" / ", tags);
            BookTags.Visibility = tags.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Synopsis.Text = string.IsNullOrWhiteSpace(book.Description) ? "" : book.Description.Trim();
            Synopsis.Visibility = Synopsis.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Synopsis.MaxLines = synopsisExpanded ? 0 : 4;
            SynopsisToggle.Visibility = Synopsis.Text.Length > 80 ? Visibility.Visible : Visibility.Collapsed;
            SynopsisToggle.Content = synopsisExpanded ? "收起简介" : "展开简介";

            lock (App.Store.SyncRoot)
                favorite = App.Store.Data.Favorites.Any(b => b.Key == book.Key);
            UpdateFavorite();

            if (chapters.Count != book.Chapters.Count || !chapters.Select(c => c.Chapter.Id).SequenceEqual(book.Chapters.Select(c => c.Id)))
            {
                chapters.Clear();
                for (int i = 0; i < book.Chapters.Count; i++)
                    chapters.Add(new ChapterItem(book.Chapters[i], i));
            }
            ChaptersTitle.Text = chapters.Count > 0 ? "章节 · " + chapters.Count : "章节";
            bool usable = chapters.Count > 0;
            ReadButton.IsEnabled = usable;
            DownloadCommand.IsEnabled = usable;
            FavoriteCommand.IsEnabled = !string.IsNullOrEmpty(book.Id);
            UpdateNotes();
        }

        void UpdateNotes()
        {
            if (book == null)
                return;
            Progress progress;
            var states = new Dictionary<string, DownloadJob>();
            Dictionary<string, int> marks;
            lock (App.Store.SyncRoot)
            {
                progress = App.Store.Data.History.FirstOrDefault(h => h.Key == book.Key);
                marks = App
                    .Store.Data.Bookmarks.Where(m => m.Key == book.Key)
                    .GroupBy(m => m.Chapter.Id)
                    .ToDictionary(g => g.Key, g => g.Count());
                var prefix = book.Key + ":";
                foreach (var j in App.Store.Data.Downloads.Where(j => j.Key.StartsWith(prefix)))
                    states[j.Chapter.Id] = new DownloadJob { State = j.State, Pages = j.Pages.ToList(), Done = j.Done.ToList() };
            }
            foreach (var item in chapters)
            {
                var parts = new List<string>();
                if (progress != null && progress.Chapter != null && progress.Chapter.Id == item.Chapter.Id)
                    parts.Add("读到第 " + (progress.Page + 1) + " 页");
                DownloadJob job;
                if (states.TryGetValue(item.Chapter.Id, out job))
                {
                    switch (job.State)
                    {
                        case "已完成":
                            parts.Add("已下载");
                            break;
                        case "下载中":
                            parts.Add("正在下载 " + job.Done.Count + " / " + job.Pages.Count);
                            break;
                        case "失败":
                            parts.Add("下载失败");
                            break;
                        case "已暂停":
                            parts.Add("下载已暂停");
                            break;
                        default:
                            parts.Add("等待下载");
                            break;
                    }
                }
                int count;
                if (marks.TryGetValue(item.Chapter.Id, out count))
                    parts.Add("书签 " + count);
                item.Note = string.Join(" · ", parts);
            }
            var current = progress?.Chapter != null ? book.Chapters.FirstOrDefault(c => c.Id == progress.Chapter.Id) : null;
            ReadLabel.Text =
                current != null
                    ? "继续阅读 · " + current.Title + " · 第 " + (progress.Page + 1) + " 页"
                    : "开始阅读";
            // Starting over is the rare case once a comic has progress, so it lives in the overflow menu.
            FromStartCommand.Visibility = current != null ? Visibility.Visible : Visibility.Collapsed;
            int total = marks.Values.Sum();
            BookmarksCommand.Label = total > 0 ? "书签（" + total + "）" : "书签";
        }

        void FromStartClick(object sender, RoutedEventArgs e)
        {
            var first = book.Chapters.FirstOrDefault();
            if (first != null)
                Frame.Navigate(
                    typeof(ReaderPage),
                    new ReaderContext { Book = book, Chapter = first }
                );
        }

        void BookmarksClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(BookmarksPage), book.Key);
        }

        void UpdateFavorite()
        {
            FavoriteIcon.Glyph = favorite ? "\uE735" : "\uE734";
            FavoriteCommand.Label = favorite ? "已收藏" : "收藏";
        }

        async void Load(bool hasCachedChapters)
        {
            loading?.Cancel();
            var cts = loading = new CancellationTokenSource();
            Busy.Visibility = Visibility.Visible;
            LoadMessage.Visibility = Visibility.Collapsed;
            RetryLink.Visibility = Visibility.Collapsed;
            try
            {
                var fresh = await App.Sources.Detail(book, cts.Token);
                if (cts.IsCancellationRequested)
                    return;
                book = fresh;
                App.Store.Remember(fresh);
                if (favorite)
                    await App.Store.SetFavorite(fresh, true);
                Fill();
                if (fresh.Chapters.Count == 0)
                {
                    LoadMessage.Text = "这个来源没有返回可阅读的章节。";
                    LoadMessage.Visibility = Visibility.Visible;
                    RetryLink.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested)
                    return;
                LoadMessage.Text = hasCachedChapters
                    ? "显示的是已保存的资料。更新失败：" + Ui.Describe(ex)
                    : "详情获取失败：" + Ui.Describe(ex);
                LoadMessage.Visibility = Visibility.Visible;
                RetryLink.Visibility = Visibility.Visible;
            }
            finally
            {
                if (cts == loading)
                    Busy.Visibility = Visibility.Collapsed;
            }
        }

        void RefreshClick(object sender, RoutedEventArgs e)
        {
            Load(book.Chapters.Count > 0);
        }

        void SynopsisToggleClick(object sender, RoutedEventArgs e)
        {
            synopsisExpanded = !synopsisExpanded;
            Synopsis.MaxLines = synopsisExpanded ? 0 : 4;
            SynopsisToggle.Content = synopsisExpanded ? "收起简介" : "展开简介";
        }

        async void FavoriteClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await App.Store.SetFavorite(book, !favorite);
                favorite = !favorite;
                UpdateFavorite();
            }
            catch (Exception ex)
            {
                await Ui.Alert("无法更新书架", Ui.Describe(ex));
            }
        }

        void Read(Chapter chapter)
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

        void ReadClick(object sender, RoutedEventArgs e)
        {
            Progress p;
            lock (App.Store.SyncRoot)
                p = App.Store.Data.History.FirstOrDefault(h => h.Key == book.Key);
            var chapter =
                (p?.Chapter != null ? book.Chapters.FirstOrDefault(c => c.Id == p.Chapter.Id) : null)
                ?? book.Chapters.FirstOrDefault();
            if (chapter != null)
                Read(chapter);
        }

        void ChapterClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ChapterItem;
            if (item != null && !selecting)
                Read(item.Chapter);
        }

        void DownloadModeClick(object sender, RoutedEventArgs e)
        {
            selecting = true;
            Chapters.IsItemClickEnabled = false;
            Chapters.SelectionMode = ListViewSelectionMode.Multiple;
            SelectHint.Text = "选择要下载的章节，然后点“下载所选”。已下载的章节会自动跳过。";
            SelectHint.Visibility = Visibility.Visible;
            SetSelectionCommands(true);
        }

        void ExitSelection()
        {
            selecting = false;
            Chapters.SelectionMode = ListViewSelectionMode.None;
            Chapters.IsItemClickEnabled = true;
            SelectHint.Visibility = Visibility.Collapsed;
            SetSelectionCommands(false);
        }

        void SetSelectionCommands(bool on)
        {
            var normal = on ? Visibility.Collapsed : Visibility.Visible;
            var select = on ? Visibility.Visible : Visibility.Collapsed;
            FavoriteCommand.Visibility = normal;
            DownloadCommand.Visibility = normal;
            RefreshCommand.Visibility = normal;
            ConfirmDownloadCommand.Visibility = select;
            SelectAllCommand.Visibility = select;
            CancelSelectCommand.Visibility = select;
        }

        void SelectAllClick(object sender, RoutedEventArgs e)
        {
            if (Chapters.SelectedItems.Count == chapters.Count)
                Chapters.SelectedItems.Clear();
            else
                Chapters.SelectAll();
        }

        void CancelSelectClick(object sender, RoutedEventArgs e)
        {
            ExitSelection();
        }

        async void ConfirmDownloadClick(object sender, RoutedEventArgs e)
        {
            var selected = Chapters.SelectedItems.Cast<ChapterItem>().ToList();
            if (selected.Count == 0)
            {
                SelectHint.Text = "请至少选择一个章节。";
                return;
            }
            ConfirmDownloadCommand.IsEnabled = false;
            try
            {
                foreach (var item in selected)
                    await App.Downloads.Add(book, item.Chapter);
                ExitSelection();
                LoadMessage.Text = "已加入下载队列：" + selected.Count + " 章。可在“下载”中查看进度。";
                LoadMessage.Visibility = Visibility.Visible;
                UpdateNotes();
            }
            catch (Exception ex)
            {
                await Ui.Alert("无法加入下载", Ui.Describe(ex));
            }
            finally
            {
                ConfirmDownloadCommand.IsEnabled = true;
            }
        }
    }
}
