using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Crawler_project.Models;

namespace Crawler_project.Checks
{
    public sealed class SeoHtmlCriticalOptions
    {
        public int TitleMinWords { get; set; } = 2;
        public int DescriptionMinWords { get; set; } = 5;
        public int DescriptionMaxChars { get; set; } = 320;

        // Ограничения хранилища дубликатов
        public int MaxUrlsPerDuplicateGroup { get; set; } = 10;
        public int MaxGroupsInReport { get; set; } = 200;
        public int MaxTextPreviewChars { get; set; } = 200;
        public int MaxKeyChars { get; set; } = 1000;
    }

    /// <summary>
    /// Page-level: сравнения TITLE/H1/DESCRIPTION + длины.
    /// Site-level: сбор дубликатов TITLE/DESCRIPTION в SeoDuplicatesStore.
    /// </summary>
    public sealed class SeoHtmlCriticalCheck : ILinkCheck
    {
        private readonly SeoHtmlCriticalOptions _opt;
        private readonly SeoDuplicatesStore _store;

        public SeoHtmlCriticalCheck(SeoHtmlCriticalOptions opt, SeoDuplicatesStore store)
        {
            _opt = opt;
            _store = store;
        }

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            // ✅ иначе 403/404 дают ложные дубли TITLE/короткие TITLE
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (string.IsNullOrWhiteSpace(ctx.ContentType) ||
                !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (ctx.Document is null)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            // ✅ SEO-сравнения и дубли Title/Description считаем только на успешных HTML
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (string.IsNullOrWhiteSpace(ctx.ContentType) ||
                !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (string.IsNullOrWhiteSpace(ctx.ContentType) ||
                !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());


            var doc = ctx.Document;
            var issues = new List<LinkIssue>();

            // Извлекаем
            var title = ExtractTitle(doc);
            var h1 = ExtractFirstH1(doc);
            var description = ExtractMetaDescription(doc);

            // Нормализуем для сравнений
            var nt = NormalizeForCompare(title);
            var nh = NormalizeForCompare(h1);
            var nd = NormalizeForCompare(description);

            // --- сравнения (только если обе стороны есть) ---
            if (!string.IsNullOrEmpty(nt) && !string.IsNullOrEmpty(nh) && string.Equals(nt, nh, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new LinkIssue(
                    "SEO_TITLE_EQUALS_H1",
                    "TITLE совпадает с H1 (грубая SEO-ошибка: отсутствие уникализации заголовков).",
                    IssueSeverity.Warning));
            }

            if (!string.IsNullOrEmpty(nt) && !string.IsNullOrEmpty(nd) && string.Equals(nt, nd, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new LinkIssue(
                    "SEO_TITLE_EQUALS_DESCRIPTION",
                    "TITLE совпадает с meta description (грубая SEO-ошибка).",
                    IssueSeverity.Warning));
            }

            if (!string.IsNullOrEmpty(nd) && !string.IsNullOrEmpty(nh) && string.Equals(nd, nh, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new LinkIssue(
                    "SEO_DESCRIPTION_EQUALS_H1",
                    "meta description совпадает с H1 (грубая SEO-ошибка).",
                    IssueSeverity.Warning));
            }

            // --- длины ---
            if (!string.IsNullOrWhiteSpace(title))
            {
                var wc = WordCount(title);
                if (wc > 0 && wc < _opt.TitleMinWords)
                {
                    issues.Add(new LinkIssue(
                        "SEO_TITLE_TOO_SHORT",
                        $"Слишком короткий TITLE: {wc} слов(а), порог < {_opt.TitleMinWords}.",
                        IssueSeverity.Warning));
                }
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                var wc = WordCount(description);
                if (wc > 0 && wc < _opt.DescriptionMinWords)
                {
                    issues.Add(new LinkIssue(
                        "SEO_DESCRIPTION_TOO_SHORT",
                        $"Слишком короткий DESCRIPTION: {wc} слов(а), порог < {_opt.DescriptionMinWords}.",
                        IssueSeverity.Warning));
                }

                var len = NormalizeWhitespace(WebUtility.HtmlDecode(description)).Length;
                if (len > _opt.DescriptionMaxChars)
                {
                    issues.Add(new LinkIssue(
                        "SEO_DESCRIPTION_TOO_LONG",
                        $"Слишком длинный DESCRIPTION: {len} символов, порог > {_opt.DescriptionMaxChars}.",
                        IssueSeverity.Warning));
                }
            }

            // --- Site-level дубли (собираем в store) ---
            // Ключуем по host, чтобы дубли считались “по сайту”
            var host = ctx.FinalUri.Host;
            var pageUrl = ctx.FinalUrl;

            if (!string.IsNullOrWhiteSpace(title))
            {
                // добавляем и, если уже есть дубль, помечаем текущую страницу
                var cnt = _store.AddTitle(host, title, pageUrl, _opt.MaxKeyChars, _opt.MaxUrlsPerDuplicateGroup);
                if (cnt >= 2)
                {
                    issues.Add(new LinkIssue(
                        "SEO_DUPLICATE_TITLE",
                        "TITLE не уникален в пределах сайта (дубликат).",
                        IssueSeverity.Warning));
                }
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                var cnt = _store.AddDescription(host, description, pageUrl, _opt.MaxKeyChars, _opt.MaxUrlsPerDuplicateGroup);
                if (cnt >= 2)
                {
                    issues.Add(new LinkIssue(
                        "SEO_DUPLICATE_DESCRIPTION",
                        "DESCRIPTION не уникален в пределах сайта (дубликат).",
                        IssueSeverity.Warning));
                }
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }

        // ---------- extractors ----------

        private static string? ExtractTitle(HtmlDocument doc)
        {
            var n = doc.DocumentNode.SelectSingleNode("//title");
            var t = n?.InnerText;
            return string.IsNullOrWhiteSpace(t) ? null : WebUtility.HtmlDecode(t).Trim();
        }

        private static string? ExtractFirstH1(HtmlDocument doc)
        {
            var n = doc.DocumentNode.SelectSingleNode("//h1");
            var t = n?.InnerText;
            return string.IsNullOrWhiteSpace(t) ? null : WebUtility.HtmlDecode(t).Trim();
        }

        private static string? ExtractMetaDescription(HtmlDocument doc)
        {
            // meta name="description" content="..."
            var nodes = doc.DocumentNode.SelectNodes("//meta[@name and @content]");
            if (nodes is null) return null;

            foreach (var n in nodes)
            {
                var name = (n.GetAttributeValue("name", "") ?? "").Trim();
                if (name.Equals("description", StringComparison.OrdinalIgnoreCase))
                {
                    var content = (n.GetAttributeValue("content", "") ?? "").Trim();
                    return string.IsNullOrWhiteSpace(content) ? null : WebUtility.HtmlDecode(content).Trim();
                }
            }
            return null;
        }

        // ---------- normalization ----------

        private static string NormalizeForCompare(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = WebUtility.HtmlDecode(s);
            s = NormalizeWhitespace(s).Trim();
            return s;
        }

        private static string NormalizeWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace('\u00A0', ' ');
            var arr = s.ToCharArray();

            var sb = new System.Text.StringBuilder(arr.Length);
            bool prevWs = false;

            foreach (var ch in arr)
            {
                var isWs = char.IsWhiteSpace(ch);
                if (isWs)
                {
                    if (!prevWs)
                        sb.Append(' ');
                    prevWs = true;
                }
                else
                {
                    sb.Append(ch);
                    prevWs = false;
                }
            }

            return sb.ToString();
        }

        private static int WordCount(string s)
        {
            s = NormalizeWhitespace(WebUtility.HtmlDecode(s)).Trim();
            if (s.Length == 0) return 0;
            return s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }

    // -------------------- Store (дубликаты по сайту) --------------------

    public sealed class SeoDuplicatesStore
    {
        private sealed class CounterWithSamples
        {
            private int _count;
            private int _samplesCount;
            private readonly ConcurrentQueue<string> _urls = new();

            public int Increment(string url, int maxSamples)
            {
                var c = Interlocked.Increment(ref _count);

                // ограничиваем число sample-URL
                var sc = Volatile.Read(ref _samplesCount);
                if (sc < maxSamples)
                {
                    _urls.Enqueue(url);
                    Interlocked.Increment(ref _samplesCount);
                }

                return c;
            }

            public int Count => Volatile.Read(ref _count);
            public string[] Samples => _urls.ToArray();
        }

        // host -> map(titleKey -> counter)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CounterWithSamples>> _titles =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CounterWithSamples>> _descriptions =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host)
        {
            _titles.TryRemove(host, out _);
            _descriptions.TryRemove(host, out _);
        }

        public int AddTitle(string host, string title, string url, int maxKeyChars, int maxSamples)
        {
            var key = MakeKey(title, maxKeyChars);
            var map = _titles.GetOrAdd(host, _ => new ConcurrentDictionary<string, CounterWithSamples>(StringComparer.OrdinalIgnoreCase));
            var ctr = map.GetOrAdd(key, _ => new CounterWithSamples());
            return ctr.Increment(url, maxSamples);
        }

        public int AddDescription(string host, string desc, string url, int maxKeyChars, int maxSamples)
        {
            var key = MakeKey(desc, maxKeyChars);
            var map = _descriptions.GetOrAdd(host, _ => new ConcurrentDictionary<string, CounterWithSamples>(StringComparer.OrdinalIgnoreCase));
            var ctr = map.GetOrAdd(key, _ => new CounterWithSamples());
            return ctr.Increment(url, maxSamples);
        }

        public SeoDuplicatesReport BuildReportForHost(string host, int maxGroups, int maxTextPreviewChars)
        {
            var dupTitles = new List<DuplicateGroup>();
            var dupDesc = new List<DuplicateGroup>();

            if (_titles.TryGetValue(host, out var tmap))
            {
                dupTitles = tmap
                    .Where(kv => kv.Value.Count >= 2)
                    .OrderByDescending(kv => kv.Value.Count)
                    .Take(Math.Clamp(maxGroups, 1, 2000))
                    .Select(kv => new DuplicateGroup(
                        TextPreview: Preview(kv.Key, maxTextPreviewChars),
                        Count: kv.Value.Count,
                        UrlSamples: kv.Value.Samples))
                    .ToList();
            }

            if (_descriptions.TryGetValue(host, out var dmap))
            {
                dupDesc = dmap
                    .Where(kv => kv.Value.Count >= 2)
                    .OrderByDescending(kv => kv.Value.Count)
                    .Take(Math.Clamp(maxGroups, 1, 2000))
                    .Select(kv => new DuplicateGroup(
                        TextPreview: Preview(kv.Key, maxTextPreviewChars),
                        Count: kv.Value.Count,
                        UrlSamples: kv.Value.Samples))
                    .ToList();
            }

            return new SeoDuplicatesReport(
                Host: host,
                DuplicateTitlesCount: dupTitles.Count,
                DuplicateTitles: dupTitles,
                DuplicateDescriptionsCount: dupDesc.Count,
                DuplicateDescriptions: dupDesc
            );
        }

        private static string MakeKey(string s, int maxChars)
        {
            s = WebUtility.HtmlDecode(s ?? "");
            s = NormalizeWs(s).Trim().ToLowerInvariant();
            if (s.Length > maxChars) s = s.Substring(0, maxChars);
            return s;
        }

        private static string NormalizeWs(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace('\u00A0', ' ');
            var sb = new System.Text.StringBuilder(s.Length);
            bool prev = false;
            foreach (var ch in s)
            {
                var ws = char.IsWhiteSpace(ch);
                if (ws)
                {
                    if (!prev) sb.Append(' ');
                    prev = true;
                }
                else
                {
                    sb.Append(ch);
                    prev = false;
                }
            }
            return sb.ToString();
        }

        private static string Preview(string key, int max)
        {
            if (string.IsNullOrEmpty(key)) return key;
            key = key.Trim();
            return key.Length <= max ? key : key.Substring(0, max) + "…";
        }
    }

    public sealed record DuplicateGroup(string TextPreview, int Count, string[] UrlSamples);

    public sealed record SeoDuplicatesReport(
        string Host,
        int DuplicateTitlesCount,
        IReadOnlyList<DuplicateGroup> DuplicateTitles,
        int DuplicateDescriptionsCount,
        IReadOnlyList<DuplicateGroup> DuplicateDescriptions
    );
}

namespace Crawler_project.Controllers
{
    using Crawler_project.Checks;

    [ApiController]
    [Route("api/crawl")]
    public sealed class SeoAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly SeoDuplicatesStore _dup;
        private readonly SeoHtmlCriticalOptions _opt;

        public SeoAuditController(JobStore store, SeoDuplicatesStore dup, SeoHtmlCriticalOptions opt)
        {
            _store = store;
            _dup = dup;
            _opt = opt;
        }

        /// <summary>
        /// Site-level агрегаторы: дубли TITLE и DESCRIPTION по результатам верификации страниц.
        /// </summary>
        [HttpGet("{jobId:guid}/seo-duplicates")]
        public ActionResult<SeoDuplicatesReport> GetDuplicates(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;
            var report = _dup.BuildReportForHost(host, _opt.MaxGroupsInReport, _opt.MaxTextPreviewChars);

            return Ok(report);
        }
    }
}
