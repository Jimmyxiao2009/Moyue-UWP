using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Jm.Core;
using Newtonsoft.Json.Linq;

namespace Moyue
{
    public static class SourceParsers
    {
        public static HtmlDocument Doc(string html)
        {
            var d = new HtmlDocument();
            d.LoadHtml(html);
            return d;
        }

        static IEnumerable<HtmlNode> Nodes(HtmlNode n, string xpath)
        {
            return (IEnumerable<HtmlNode>)n.SelectNodes(xpath) ?? Enumerable.Empty<HtmlNode>();
        }

        public static string Clean(HtmlNode n)
        {
            return n == null ? "" : HtmlEntity.DeEntitize(n.InnerText).Trim();
        }

        static string Attr(HtmlNode n, params string[] names)
        {
            if (n == null)
                return null;
            foreach (var name in names)
            {
                var v = n.GetAttributeValue(name, "");
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return null;
        }

        public static string Abs(string host, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var u = new Uri(new Uri(host.TrimEnd('/') + "/"), HtmlEntity.DeEntitize(path).Trim());
            if (u.Scheme != "https" && u.Scheme != "http")
                throw new FormatException("图片或章节地址协议无效");
            return u.ToString();
        }

        static string Value(JToken token)
        {
            return token is JArray ? string.Join(" ", token.Values<string>()) : (string)token;
        }

        public static Book BoyBook(JToken t, string imageHost)
        {
            var b = new Book
            {
                Source = "boylove",
                Id = (string)t["id"],
                Title = (string)t["title"],
                Author = (string)t["auther"],
                Description = (string)t["desc"],
                Tags = (string)t["keyword"],
                Cover = Abs(imageHost, (string)t["image"]),
                State =
                    (string)t["mhstatus"] == "0" ? "连载"
                    : (string)t["mhstatus"] == "1" ? "完结"
                    : null,
            };
            CheckBook(b);
            return b;
        }

        public static List<Book> BoySearch(string json, string imageHost)
        {
            var root = JObject.Parse(json);
            var list = root.SelectToken("result.list") as JArray;
            if (list == null)
                throw new FormatException("香香列表格式变化或需要登录");
            return list.Select(t => BoyBook(t, imageHost)).ToList();
        }

        public static Book BoyDetail(Book input, string html, string chapterJson)
        {
            var d = Doc(html);
            var b = Clone(input);
            var title = d.DocumentNode.SelectSingleNode("//h1");
            if (title == null)
                title = d.DocumentNode.SelectSingleNode("//h2[contains(@class,'title')]");
            if (title != null && !string.IsNullOrWhiteSpace(Clean(title)))
                b.Title = Clean(title);
            var list = JObject.Parse(chapterJson).SelectToken("result.list") as JArray;
            if (list == null)
                throw new FormatException("香香章节格式变化或需要登录");
            b.Chapters = list.Select(t => new Chapter
                {
                    Id = "/home/book/capter/id/" + (string)t["id"],
                    Title = (string)t["title"],
                })
                .ToList();
            // The website API emits reading order (first chapter first), unlike Tachiyomi's newest-first contract.
            CheckBook(b);
            return b;
        }

        public static List<Book> JmSearch(string json, string imageHost)
        {
            var t = JObject.Parse(json);
            var list = t["content"] as JArray;
            if (list == null)
                throw new FormatException("JM 列表格式变化");
            return list.Select(x => new Book
                {
                    Source = "jm",
                    Id = (string)x["id"],
                    Title = (string)x["name"],
                    Author = Value(x["author"]),
                    Tags = Value(x["tags"]),
                    Description = (string)x["description"],
                    Cover = Abs(imageHost, "/media/albums/" + (string)x["id"] + "_3x4.jpg"),
                })
                .Where(b => !string.IsNullOrEmpty(b.Id) && !string.IsNullOrEmpty(b.Title))
                .ToList();
        }

        public static Book JmDetail(Book input, string json, string imageHost)
        {
            var t = JObject.Parse(json);
            var b = Clone(input);
            b.Title = (string)t["name"];
            b.Author = Value(t["author"]);
            b.Tags = Value(t["tags"]);
            b.Description = (string)t["description"];
            b.Cover = Abs(imageHost, "/media/albums/" + b.Id + "_3x4.jpg");
            b.Chapters = (t["series"] as JArray ?? new JArray())
                .OrderBy(x => Int((string)x["sort"]))
                .Select(x => new Chapter
                {
                    Id = (string)x["id"],
                    Title = (string)x["name"] ?? "正文",
                })
                .ToList();
            if (b.Chapters.Count == 0)
                b.Chapters.Add(new Chapter { Id = b.Id, Title = "正文" });
            CheckBook(b);
            return b;
        }

        public static List<Book> ManwaSearch(string html, string host)
        {
            var d = Doc(html);
            var lists = Nodes(
                    d.DocumentNode,
                    "//ul[contains(concat(' ',normalize-space(@class),' '),' book-list ') or contains(concat(' ',normalize-space(@class),' '),' manga-list-2 ')]"
                )
                .ToList();
            if (lists.Count == 0)
                throw new FormatException("漫蛙网页格式变化、跳转页或需要浏览器验证");
            var result = new List<Book>();
            foreach (var li in lists.SelectMany(n => Nodes(n, "./li")))
            {
                var a = li.SelectSingleNode(".//a[contains(@href,'/book/')]");
                if (a == null)
                    continue;
                var path = new Uri(Abs(host, Attr(a, "href"))).AbsolutePath;
                var id = path.TrimEnd('/').Split('/').Last();
                var title = li.SelectSingleNode(".//p[contains(@class,'title')]");
                var b = new Book
                {
                    Source = "manwa",
                    Id = id,
                    Title = Clean(title ?? a),
                    Cover = Abs(host, Attr(li.SelectSingleNode(".//img"), "data-original", "src")),
                };
                CheckBook(b);
                result.Add(b);
            }
            return result.GroupBy(b => b.Key).Select(g => g.First()).ToList();
        }

        public static Book ManwaDetail(Book input, string html, string host)
        {
            var d = Doc(html);
            var root = d.DocumentNode;
            var title = root.SelectSingleNode("//*[contains(@class,'detail-main-info-title')]");
            if (title == null)
                throw new FormatException("漫蛙详情格式变化或需要登录");
            var b = Clone(input);
            b.Title = Clean(title);
            b.Cover = Abs(
                host,
                Attr(
                    root.SelectSingleNode("//div[contains(@class,'detail-main-cover')]//img"),
                    "data-original",
                    "src"
                )
            );
            var author = Nodes(root, "//p[contains(@class,'detail-main-info-author')]")
                .FirstOrDefault(n => Clean(n).Contains("作者"));
            b.Author = string.Join(
                " ",
                author == null ? Enumerable.Empty<string>() : Nodes(author, ".//a").Select(Clean)
            );
            b.Tags = string.Join(
                " ",
                Nodes(root, "//a[contains(@class,'info-tag')]").Select(Clean)
            );
            b.Description = Clean(root.SelectSingleNode("//p[contains(@class,'detail-desc')]"));
            var state = Nodes(root, "//p[contains(@class,'detail-main-info-author')]")
                .FirstOrDefault(n => Clean(n).Contains("更新状态"));
            var stateText = Clean(state);
            b.State =
                stateText.Contains("已完结") ? "完结"
                : stateText.Contains("连载中") ? "连载"
                : null;
            b.Chapters = Nodes(root, "//ul[@id='detail-list-select']/li/a")
                .Select(a => new Chapter
                {
                    Id = new Uri(Abs(host, Attr(a, "href"))).AbsolutePath,
                    Title = Clean(a),
                })
                .ToList();
            CheckBook(b);
            return b;
        }

        public static List<PageInfo> Pages(
            string source,
            string html,
            string host,
            string imageHost,
            string chapterPath
        )
        {
            var d = Doc(html);
            IEnumerable<HtmlNode> nodes;
            int parts = 0;
            if (source == "manwa")
                nodes = Nodes(
                    d.DocumentNode,
                    "//*[@id='cp_img']//div[contains(@class,'img-content')]/img[@data-r-src]"
                );
            else
            {
                var section = d.DocumentNode.SelectSingleNode("//section");
                if (section == null)
                    throw new FormatException("香香阅读页格式变化或需要登录");
                var containers = Nodes(
                        section,
                        ".//*[contains(concat(' ',normalize-space(@class),' '),' reader-cartoon-image ')]"
                    )
                    .ToList();
                nodes =
                    containers.Count == 0
                        ? Nodes(section, ".//img")
                        : containers.SelectMany(n => Nodes(n, ".//img[@data-original]"));
                var script = Nodes(d.DocumentNode, "//script")
                    .FirstOrDefault(n =>
                        n.InnerText.Contains("firstMergeImg") && n.InnerText.Contains("imageData")
                    );
                if (script != null)
                {
                    var before = script.InnerText.Split(
                        new[] { "var scrollTop" },
                        StringSplitOptions.None
                    )[0];
                    var matches = Regex.Matches(before, @"var\s+randomClass\s*=\s*([^;]+);");
                    if (matches.Count > 0)
                    {
                        var tail = Regex.Match(
                            matches[matches.Count - 1].Groups[1].Value.Trim(),
                            @"(?:^|\s)(\d+)$"
                        );
                        if (tail.Success)
                            parts = Int(tail.Groups[1].Value);
                        else
                            throw new FormatException("香香图片重排参数无法解析");
                    }
                }
            }
            var result = new List<PageInfo>();
            foreach (var n in nodes)
            {
                var u =
                    source == "boylove" && n.Attributes["data-original"] != null
                        ? Attr(n, "data-original")
                        : Attr(n, source == "manwa" ? "data-r-src" : "data-original", "src");
                if (string.IsNullOrWhiteSpace(u))
                    continue;
                var absolute = Abs(source == "manwa" ? host : imageHost, u);
                var path = new Uri(absolute).AbsolutePath;
                if (
                    path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("load.png")
                )
                    continue;
                result.Add(
                    new PageInfo
                    {
                        Url = absolute,
                        Identity = path,
                        Referer = Abs(host, chapterPath),
                        Strips = parts,
                        Horizontal = source == "boylove",
                        Encrypted = Regex.IsMatch(
                            new Uri(absolute).Query,
                            @"(?:^\?|&)v=20220724(?:&|$)"
                        ),
                    }
                );
            }
            if (result.Count == 0)
                throw new FormatException("没有可读图片：需要登录、权限或接口已变化");
            return result;
        }

        public static Book Clone(Book b)
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<Book>(
                Newtonsoft.Json.JsonConvert.SerializeObject(b)
            );
        }

        static int Int(string text)
        {
            int i;
            return int.TryParse(text, out i) ? i : 0;
        }

        static void CheckBook(Book b)
        {
            if (string.IsNullOrWhiteSpace(b.Id) || string.IsNullOrWhiteSpace(b.Title))
                throw new FormatException("漫画资料缺少 ID 或标题");
        }
    }
}
