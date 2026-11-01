using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Moyue
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class Group<T> : List<T>
    {
        public Group(string title, IEnumerable<T> items)
            : base(items)
        {
            Title = title;
        }

        public string Title { get; private set; }
    }

    public sealed class BookItem
    {
        public BookItem(Book book, string badge = null)
        {
            Book = book;
            Badge = badge ?? Sources.Name(book.Source);
        }

        public Book Book { get; private set; }
        public string Badge { get; private set; }

        public string Title
        {
            get { return string.IsNullOrWhiteSpace(Book.Title) ? Book.Id : Book.Title; }
        }

        public string Meta
        {
            get
            {
                var parts = new List<string> { Book.Author ?? "作者未知" };
                if (!string.IsNullOrEmpty(Book.State))
                    parts.Add(Book.State);
                return string.Join(" · ", parts);
            }
        }

        public string SourceLine
        {
            get { return Sources.Name(Book.Source) + " · " + Meta; }
        }

        public string TagsText
        {
            get
            {
                var tags = SearchOptions.Words(Book.Tags);
                return tags.Length == 0 ? "" : string.Join(" / ", tags.Take(6));
            }
        }

        public Visibility TagsVisibility
        {
            get { return TagsText.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
        }
    }

    public sealed class ChapterItem : Observable
    {
        string note;

        public ChapterItem(Chapter chapter, int index)
        {
            Chapter = chapter;
            Index = index;
        }

        public Chapter Chapter { get; private set; }
        public int Index { get; private set; }

        public string Title
        {
            get { return string.IsNullOrWhiteSpace(Chapter.Title) ? "第 " + (Index + 1) + " 章" : Chapter.Title; }
        }

        public string Note
        {
            get { return note; }
            set
            {
                if (Set(ref note, value))
                    Raise(nameof(NoteVisibility));
            }
        }

        public Visibility NoteVisibility
        {
            get { return string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible; }
        }
    }

    public sealed class HistoryItem
    {
        public HistoryItem(Progress p)
        {
            Progress = p;
        }

        public Progress Progress { get; private set; }

        public Book Book
        {
            get { return Progress.Book; }
        }

        public string Title
        {
            get { return Progress.Book.Title ?? Progress.Book.Id; }
        }

        public string Line
        {
            get
            {
                return Sources.Name(Progress.Book.Source)
                    + " · "
                    + (Progress.Chapter.Title ?? "")
                    + " · 第 "
                    + (Progress.Page + 1)
                    + " 页";
            }
        }

        public string Time
        {
            get { return Ui.FriendlyTime(Progress.Updated); }
        }
    }

    public sealed class BookmarkItem
    {
        public BookmarkItem(Bookmark mark)
        {
            Mark = mark;
        }

        public Bookmark Mark { get; private set; }

        public Book Book
        {
            get { return Mark.Book; }
        }

        public string Title
        {
            get { return Mark.Book.Title ?? Mark.Book.Id; }
        }

        public string ChapterTitle
        {
            get { return Mark.Chapter.Title ?? ""; }
        }

        public string Line
        {
            get { return "第 " + (Mark.Page + 1) + " 页 · " + Ui.FriendlyTime(Mark.Created); }
        }

        public string FullLine
        {
            get { return ChapterTitle + " · " + Line; }
        }
    }

    public sealed class DownloadItem : Observable
    {
        string state, detail, action;
        double percent;
        Brush stateBrush;

        public DownloadItem(string key)
        {
            Key = key;
        }

        public string Key { get; private set; }
        public Book Book { get; set; }
        public Chapter Chapter { get; set; }

        public string ChapterTitle
        {
            get { return Chapter?.Title ?? ""; }
        }

        public string State
        {
            get { return state; }
            private set { Set(ref state, value); }
        }

        public Brush StateBrush
        {
            get { return stateBrush; }
            private set { Set(ref stateBrush, value); }
        }

        public string Detail
        {
            get { return detail; }
            private set { Set(ref detail, value); }
        }

        public double Percent
        {
            get { return percent; }
            private set { Set(ref percent, value); }
        }

        /// <summary>Label of the single contextual action for the current state.</summary>
        public string Action
        {
            get { return action; }
            private set { Set(ref action, value); }
        }

        public bool Completed
        {
            get { return state == "已完成"; }
        }

        public void Update(DownloadJob job)
        {
            Book = job.Book;
            Chapter = job.Chapter;
            var raw = job.State ?? "等待";
            State = raw == "下载中" ? "正在下载" : raw;
            StateBrush = Ui.Resource(
                raw == "失败" ? "ErrorBrush"
                : raw == "已完成" ? "SuccessBrush"
                : raw == "下载中" ? "SystemControlForegroundAccentBrush"
                : "AttentionBrush"
            );
            int total = job.Pages.Count,
                done = job.Done.Count;
            Percent = total == 0 ? 0 : Math.Round(done * 100.0 / total);
            var text = total == 0 ? "尚未取得页数" : "已保存 " + done + " / " + total + " 页";
            if (raw == "失败" && total > 0 && done < total)
                text += " · 缺 " + (total - done) + " 页";
            if (!string.IsNullOrEmpty(job.Error))
                text += "\n" + Ui.Describe(job.Error);
            Detail = text;
            Action =
                raw == "已完成" ? "离线阅读"
                : raw == "下载中" || raw == "等待" ? "暂停"
                : raw == "失败" ? "重试缺页"
                : "继续";
        }
    }

    public sealed class SourceStatus : Observable
    {
        string text;
        bool failed,
            running;

        public SourceStatus(string id)
        {
            Id = id;
        }

        public string Id { get; private set; }

        public string Name
        {
            get { return Sources.Name(Id); }
        }

        public string Text
        {
            get { return text; }
            set { Set(ref text, value); }
        }

        public bool Failed
        {
            get { return failed; }
            set
            {
                if (Set(ref failed, value))
                {
                    Raise(nameof(RetryVisibility));
                    Raise(nameof(TextBrush));
                }
            }
        }

        public bool Running
        {
            get { return running; }
            set
            {
                if (Set(ref running, value))
                    Raise(nameof(RetryVisibility));
            }
        }

        public int Page { get; set; }
        public bool More { get; set; }

        public Visibility RetryVisibility
        {
            get { return failed && !running ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Brush TextBrush
        {
            get { return Ui.Resource(failed ? "ErrorBrush" : "SystemControlForegroundBaseMediumBrush"); }
        }
    }

    public sealed class FilterChip
    {
        public FilterChip(string key, string label)
        {
            Key = key;
            Label = label + "  ×";
        }

        public string Key { get; private set; }
        public string Label { get; private set; }
    }

    public sealed class LineItem
    {
        public string Group { get; set; }
        public string Host { get; set; }
        public bool? Active { get; set; }
        public string Label { get; set; }
    }

    public sealed class ReaderPageItem : Observable
    {
        BitmapImage image;
        bool failed;

        public ReaderPageItem(int index)
        {
            Index = index;
        }

        public int Index { get; private set; }

        public string Number
        {
            get { return (Index + 1).ToString(); }
        }

        public BitmapImage Image
        {
            get { return image; }
            set { Set(ref image, value); }
        }

        public bool Failed
        {
            get { return failed; }
            set
            {
                if (Set(ref failed, value))
                    Raise(nameof(FailedVisibility));
            }
        }

        public Visibility FailedVisibility
        {
            get { return failed ? Visibility.Visible : Visibility.Collapsed; }
        }
    }
}
