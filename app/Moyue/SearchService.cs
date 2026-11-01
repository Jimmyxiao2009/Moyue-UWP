using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Moyue
{
    public sealed class SearchService
    {
        readonly Store store;
        readonly IContentSource sources;
        readonly SemaphoreSlim detailsGate = new SemaphoreSlim(2, 2);

        public SearchService(Store store, IContentSource sources)
        {
            this.store = store;
            this.sources = sources;
        }

        public async Task<SearchBatch> Search(
            SearchOptions options,
            int page,
            CancellationToken ct,
            Action<SourceSearchResult> arrived = null
        )
        {
            if (options == null || page < 1)
                throw new ArgumentException("无效的搜索条件");
            // Capture independent immutable-in-practice query state; editing a form cannot change an in-flight search.
            var o = new SearchOptions
            {
                Query = options.Query,
                Exclude = options.Exclude,
                Author = options.Author,
                Tags = options.Tags,
                State = options.State,
                Sort = options.Sort,
                Any = options.Any,
                Local = options.Local,
                Sources =
                    options.Sources == null
                        ? new List<string>()
                        : options.Sources.Distinct().ToList(),
            };
            if (
                o.Sources.Count == 0
                || o.Sources.Any(id => !new[] { "jm", "boylove", "manwa" }.Contains(id))
            )
                throw new ArgumentException("至少选择一个有效来源");
            var words = SearchOptions.Words(o.Query);
            // Tag-only searches send the tags as the source query; the local tag filter still applies.
            bool tagsAsQuery = words.Length == 0;
            if (tagsAsQuery)
                words = SearchOptions.Words(o.Tags);
            if (words.Length == 0)
                throw new ArgumentException("请输入关键词或标签");
            if (o.Any && words.Length > 4)
                throw new ArgumentException("任一关键词最多支持四个查询词");
            if (o.State != "全部" && o.State != "连载" && o.State != "完结")
                throw new ArgumentException("无效的状态条件");
            if (o.Sort != "相关度" && o.Sort != "标题")
                throw new ArgumentException("不支持该排序");
            var snapshot = store.Snapshot();
            if (o.Local)
            {
                var books = snapshot
                    .Known.Concat(snapshot.Favorites)
                    .Concat(snapshot.History.Select(h => h.Book))
                    .Concat(snapshot.Downloads.Select(j => j.Book))
                    .Where(b => b != null && o.Sources.Contains(b.Source))
                    .GroupBy(b => b.Key)
                    .Select(g => g.First())
                    .Where(o.Match)
                    .ToList();
                return new SearchBatch
                {
                    Books = Sort(books, o),
                    Sources = o
                        .Sources.Select(id => new SourceSearchResult
                        {
                            Source = id,
                            Page = page,
                            Books = books.Where(b => b.Source == id).ToList(),
                        })
                        .ToList(),
                };
            }
            if (!tagsAsQuery)
                lock (store.SyncRoot)
                {
                    store.Data.Queries.Remove(o.Query);
                    store.Data.Queries.Insert(0, o.Query);
                    store.Data.Queries = store.Data.Queries.Take(20).ToList();
                }
            await store.Save(ct).ConfigureAwait(false);
            var results = await Task.WhenAll(
                    o.Sources.Select(async id =>
                    {
                        var result = new SourceSearchResult { Source = id, Page = page };
                        try
                        {
                            var found = new List<Book>();
                            var queries = o.Any
                                ? words
                                : new[] { id == "jm" ? string.Join(" ", words) : words[0] };
                            foreach (var query in queries)
                            {
                                var native = query;
                                if (id == "jm")
                                    native +=
                                        " "
                                        + (tagsAsQuery ? "" : o.Tags)
                                        + " "
                                        + string.Join(
                                            " ",
                                            SearchOptions.Words(o.Exclude).Select(t => "-" + t)
                                        );
                                var items = await sources
                                    .Search(id, native.Trim(), page, ct)
                                    .ConfigureAwait(false);
                                found.AddRange(items);
                                result.MoreMayExist |= items.Count > 0;
                            }
                            var candidates = found
                                .GroupBy(b => b.Key)
                                .Select(g => g.First())
                                .ToList();
                            // Keywords (and JM's natively pushed tags) were already matched by the
                            // source; only re-check the conditions it could not apply.
                            var served = o.Any || id == "jm" ? words : new[] { words[0] };
                            var check = id == "jm" ? WithoutTags(o) : o;
                            int budget = 16;
                            foreach (var b in candidates.ToList())
                            {
                                ct.ThrowIfCancellationRequested();
                                var candidate = b;
                                bool unknown =
                                    (
                                        !string.IsNullOrWhiteSpace(o.Author)
                                        && string.IsNullOrWhiteSpace(b.Author)
                                    )
                                    || (
                                        !string.IsNullOrWhiteSpace(check.Tags)
                                        && string.IsNullOrWhiteSpace(b.Tags)
                                    )
                                    || (o.State != "全部" && b.State == null);
                                if (unknown && budget-- > 0)
                                {
                                    await detailsGate.WaitAsync(ct).ConfigureAwait(false);
                                    try
                                    {
                                        candidate = await sources
                                            .Detail(b, ct)
                                            .ConfigureAwait(false);
                                    }
                                    catch (Exception) when (!ct.IsCancellationRequested) { }
                                    finally
                                    {
                                        detailsGate.Release();
                                    }
                                }
                                if (
                                    unknown
                                    && (
                                        (
                                            !string.IsNullOrWhiteSpace(o.Author)
                                            && string.IsNullOrWhiteSpace(candidate.Author)
                                        )
                                        || (
                                            !string.IsNullOrWhiteSpace(check.Tags)
                                            && string.IsNullOrWhiteSpace(candidate.Tags)
                                        )
                                        || (o.State != "全部" && candidate.State == null)
                                    )
                                )
                                    result.MetadataUnknown++;
                                if (check.Match(candidate, served))
                                    result.Books.Add(candidate);
                                store.Remember(candidate);
                            }
                            result.Books = Sort(result.Books, o);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            result.Error = e.Message;
                        }
                        if (arrived != null)
                            arrived(result);
                        return result;
                    })
                )
                .ConfigureAwait(false);
            await store.Save(ct).ConfigureAwait(false);
            return new SearchBatch
            {
                Sources = results.ToList(),
                Books = Sort(
                    results
                        .SelectMany(r => r.Books)
                        .GroupBy(b => b.Key)
                        .Select(g => g.First())
                        .ToList(),
                    o
                ),
            };
        }

        static SearchOptions WithoutTags(SearchOptions o)
        {
            return new SearchOptions
            {
                Query = o.Query,
                Exclude = o.Exclude,
                Author = o.Author,
                Tags = "",
                State = o.State,
                Sort = o.Sort,
                Any = o.Any,
                Local = o.Local,
                Sources = o.Sources,
            };
        }

        static List<Book> Sort(List<Book> books, SearchOptions o)
        {
            if (o.Sort == "标题")
                return books.OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase).ToList();
            // Only a local title relevance score, never fabricated cross-site popularity.
            return books
                .OrderByDescending(b =>
                    SearchOptions.Has(b.Title, o.Query)
                        ? 2
                        : SearchOptions.Words(o.Query).Count(w => SearchOptions.Has(b.Title, w))
                )
                .ToList();
        }

        public Task<Book> ById(string source, string id, CancellationToken ct)
        {
            Store.Validate(new Book { Source = source, Id = id });
            return sources.Detail(
                new Book
                {
                    Source = source,
                    Id = id,
                    Title = id,
                },
                ct
            );
        }
    }
}
