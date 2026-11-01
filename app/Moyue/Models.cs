using System;
using System.Collections.Generic;
using System.Linq;

namespace Moyue
{
    public sealed class Book
    {
        public string Source { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Cover { get; set; }
        public string Author { get; set; }
        public string Tags { get; set; }
        public string Description { get; set; }
        public string State { get; set; }
        public List<Chapter> Chapters { get; set; } = new List<Chapter>();
        public string Key
        {
            get { return Source + ":" + Id; }
        }
        public string Caption
        {
            get { return Sources.Name(Source) + "  ·  " + (Author ?? "作者未知"); }
        }

        public override string ToString()
        {
            return Title;
        }
    }

    public sealed class Chapter
    {
        public string Id { get; set; }
        public string Title { get; set; }

        public override string ToString()
        {
            return Title;
        }
    }

    public sealed class PageInfo
    {
        public string Url { get; set; }
        public List<string> Alternatives { get; set; } = new List<string>();
        public string Referer { get; set; }
        public string Identity { get; set; }
        public int Strips { get; set; }
        public bool Horizontal { get; set; }
        public bool Encrypted { get; set; }
        public string Resource
        {
            get
            {
                Uri u;
                return Identity
                    ?? (Uri.TryCreate(Url, UriKind.Absolute, out u) ? u.AbsolutePath : Url);
            }
        }
    }

    public sealed class Progress
    {
        public string Key { get; set; }
        public Book Book { get; set; }
        public Chapter Chapter { get; set; }
        public int Page { get; set; }
        public double Offset { get; set; }
        public DateTime Updated { get; set; }
    }

    public sealed class Bookmark
    {
        public string Id { get; set; }

        /// <summary>Book key (source:id), so bookmarks survive detail refreshes.</summary>
        public string Key { get; set; }

        /// <summary>Lightweight copy without the chapter list; resolve the full book via Store.FindBook.</summary>
        public Book Book { get; set; }
        public Chapter Chapter { get; set; }
        public int Page { get; set; }
        public double Offset { get; set; }
        public DateTime Created { get; set; }
    }

    public sealed class DownloadJob
    {
        public string Key { get; set; }
        public Book Book { get; set; }
        public Chapter Chapter { get; set; }
        public List<PageInfo> Pages { get; set; } = new List<PageInfo>();
        public List<int> Done { get; set; } = new List<int>();
        public string State { get; set; } = "等待";
        public string Error { get; set; }
        public Dictionary<int, SavedPage> Saved { get; set; } = new Dictionary<int, SavedPage>();
        public string Caption
        {
            get
            {
                return State
                    + " · "
                    + Done.Count
                    + " / "
                    + Pages.Count
                    + " 页"
                    + (string.IsNullOrEmpty(Error) ? "" : " · " + Error);
            }
        }
    }

    public sealed class SourceConfig
    {
        public string Id { get; set; }
        public string Publication { get; set; }
        public List<string> Hosts { get; set; } = new List<string>();
        public string Active { get; set; }
        public DateTime Synced { get; set; }
        public string Message { get; set; }
        public List<string> ImageHosts { get; set; } = new List<string>();
        public List<string> WebHosts { get; set; } = new List<string>();
        public DateTime LastSyncAttempt { get; set; }
    }

    public sealed class Settings
    {
        /// <summary>Upper bound for parallel image downloads; decoding stays serial.</summary>
        public const int MaxConcurrency = 32;

        public bool OldDevice { get; set; } = true;
        public bool ReadingPriority { get; set; } = true;
        public bool WifiOnly { get; set; } = true;
        public bool AutoSync { get; set; } = true;
        public bool Failover { get; set; } = true;
        public int Concurrency { get; set; } = 2;
        public int Preload { get; set; } = 2;
        public int CacheMb { get; set; } = 128;
    }

    public sealed class Database
    {
        public int Version { get; set; } = 1;
        public List<Book> Favorites { get; set; } = new List<Book>();
        public List<Book> Known { get; set; } = new List<Book>();
        public List<Progress> History { get; set; } = new List<Progress>();
        public List<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();
        public List<DownloadJob> Downloads { get; set; } = new List<DownloadJob>();
        public List<string> Queries { get; set; } = new List<string>();
        public List<SourceConfig> Sources { get; set; } = new List<SourceConfig>();
        public Settings Settings { get; set; } = new Settings();
    }

    public sealed class SearchOptions
    {
        public string Query = "",
            Exclude = "",
            Author = "",
            Tags = "",
            State = "全部",
            Sort = "相关度";
        public bool Any;
        public bool Local;
        public List<string> Sources = new List<string> { "jm", "boylove", "manwa" };

        public bool Match(Book b)
        {
            return Match(b, null);
        }

        /// <summary>
        /// <paramref name="matchedBySource"/> lists keywords the source already searched for
        /// server-side. Those are not re-checked locally: sources match on tags, authors and
        /// simplified/traditional variants that list rows do not carry, so a local recheck
        /// drops valid results (JM rows have no tags at all).
        /// </summary>
        public bool Match(Book b, ICollection<string> matchedBySource)
        {
            var text = (b.Title ?? "") + " " + (b.Tags ?? "");
            var terms = Words(Query)
                .Where(t => matchedBySource == null || !matchedBySource.Contains(t))
                .ToArray();
            if (Any && matchedBySource != null && matchedBySource.Count > 0)
                terms = new string[0];
            if (
                terms.Length > 0
                && !(Any ? terms.Any(t => Has(text, t)) : terms.All(t => Has(text, t)))
            )
                return false;
            return !Words(Exclude).Any(t => Has(text, t))
                && Words(Author).All(t => Has(b.Author, t))
                && Words(Tags).All(t => Has(b.Tags, t))
                && (State == "全部" || b.State == State);
        }

        public static string[] Words(string s)
        {
            return (s ?? "").Split(
                new[] { ' ', '\t', '\r', '\n', ',', '，' },
                StringSplitOptions.RemoveEmptyEntries
            );
        }

        public static bool Has(string s, string t)
        {
            return (s ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public sealed class ReaderContext
    {
        public Book Book;
        public Chapter Chapter;
        public int Page;
        public double Offset;
    }
}
