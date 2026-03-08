using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Crawler_project.Checks
{
    // =========================
    // Options
    // =========================

    public sealed class HtmlSeoStructureOptions
    {
        public int LargeHtmlCharsThreshold { get; set; } = 2_000_000;     // "Очень большие страницы"
        public int LargeContentLengthThreshold { get; set; } = 2_000_000; // если есть Content-Length
        public bool RequireDoctypeHtml { get; set; } = true;
    }

    public sealed class CanonicalOptions
    {
        public int MaxCanonicalHtmlChars { get; set; } = 400_000;
        public int CanonicalHttpTimeoutSeconds { get; set; } = 20;

        // Ограничение на количество сетевых проверок canonical (чтобы не застрелить сайт)
        public int MaxCanonicalNetworkChecksPerHost { get; set; } = 5000;
    }

    public sealed class ImagesMetaOptions
    {
        public int MaxImagesPerPage { get; set; } = 500;
    }

    public sealed class OutgoingLinksOptions
    {
        public int MaxLinksPerPage { get; set; } = 2000;

        public int InternalTooManyThreshold { get; set; } = 200;
        public int ExternalWarnThreshold { get; set; } = 50; // чтобы не спамить на каждой странице
    }

    public sealed class IndexingNoindexOptions
    {
        public int MaxHtmlCharsForNoindexCheck { get; set; } = 400_000;
    }

    public sealed class PerformanceOptions
    {
        public double SlowPageSecondsThreshold { get; set; } = 3.0;
        public int TopSlowPagesToKeep { get; set; } = 200;
    }

    public sealed class ContentLengthUniformityOptions
    {
        public int MinPagesForUniformityCheck { get; set; } = 50;
        public double UniformityTopShareThreshold { get; set; } = 0.75; // "подозрительно одинаковые"
        public int TopLengthsToReport { get; set; } = 20;
    }

    // =========================
    // Stores
    // =========================

    public sealed class CanonicalSummaryStore
    {
        private sealed class HostCanonicalCounters
        {
            public int PagesSeen;
            public int WithCanonical;
            public int MultipleCanonical;
            public int CanonicalInBody;
            public int CanonicalMissingScheme;
            public int CrossDomainCanonical;
            public int CanonicalTargetHttpErrors;
            public int CanonicalTargetNoindexOrBlocked;
            public int CanonicalInvalidUrl;

            public ConcurrentQueue<string> SamplesInvalid = new();
            public ConcurrentQueue<string> SamplesHttpErr = new();
            public ConcurrentQueue<string> SamplesNoindex = new();
            public ConcurrentQueue<string> SamplesCrossDomain = new();
        }

        private readonly ConcurrentDictionary<string, HostCanonicalCounters> _byHost =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host) => _byHost.TryRemove(host, out _);

        public void AddPageSeen(string host) => Interlocked.Increment(ref _byHost.GetOrAdd(host, _ => new HostCanonicalCounters()).PagesSeen);
        public void AddWithCanonical(string host) => Interlocked.Increment(ref _byHost.GetOrAdd(host, _ => new HostCanonicalCounters()).WithCanonical);
        public void AddMultipleCanonical(string host) => Interlocked.Increment(ref _byHost.GetOrAdd(host, _ => new HostCanonicalCounters()).MultipleCanonical);
        public void AddCanonicalInBody(string host) => Interlocked.Increment(ref _byHost.GetOrAdd(host, _ => new HostCanonicalCounters()).CanonicalInBody);
        public void AddCanonicalMissingScheme(string host) => Interlocked.Increment(ref _byHost.GetOrAdd(host, _ => new HostCanonicalCounters()).CanonicalMissingScheme);
        public void AddCrossDomain(string host, string sampleUrl)
        {
            var c = _byHost.GetOrAdd(host, _ => new HostCanonicalCounters());
            Interlocked.Increment(ref c.CrossDomainCanonical);
            EnqSample(c.SamplesCrossDomain, sampleUrl);
        }
        public void AddTargetHttpError(string host, string sampleUrl)
        {
            var c = _byHost.GetOrAdd(host, _ => new HostCanonicalCounters());
            Interlocked.Increment(ref c.CanonicalTargetHttpErrors);
            EnqSample(c.SamplesHttpErr, sampleUrl);
        }
        public void AddTargetNoindexOrBlocked(string host, string sampleUrl)
        {
            var c = _byHost.GetOrAdd(host, _ => new HostCanonicalCounters());
            Interlocked.Increment(ref c.CanonicalTargetNoindexOrBlocked);
            EnqSample(c.SamplesNoindex, sampleUrl);
        }
        public void AddInvalid(string host, string sampleUrl)
        {
            var c = _byHost.GetOrAdd(host, _ => new HostCanonicalCounters());
            Interlocked.Increment(ref c.CanonicalInvalidUrl);
            EnqSample(c.SamplesInvalid, sampleUrl);
        }

        public CanonicalAuditReport Build(string host)
        {
            _byHost.TryGetValue(host, out var c);
            c ??= new HostCanonicalCounters();

            return new CanonicalAuditReport(
                Host: host,
                PagesSeen: c.PagesSeen,
                WithCanonical: c.WithCanonical,
                MultipleCanonical: c.MultipleCanonical,
                CanonicalInBody: c.CanonicalInBody,
                CanonicalMissingScheme: c.CanonicalMissingScheme,
                CrossDomainCanonical: c.CrossDomainCanonical,
                CanonicalTargetHttpErrors: c.CanonicalTargetHttpErrors,
                CanonicalTargetNoindexOrBlocked: c.CanonicalTargetNoindexOrBlocked,
                CanonicalInvalidUrl: c.CanonicalInvalidUrl,
                SamplesInvalid: c.SamplesInvalid.ToArray(),
                SamplesHttpErrors: c.SamplesHttpErr.ToArray(),
                SamplesNoindexOrBlocked: c.SamplesNoindex.ToArray(),
                SamplesCrossDomain: c.SamplesCrossDomain.ToArray()
            );
        }

        private static void EnqSample(ConcurrentQueue<string> q, string s)
        {
            if (q.Count < 50) q.Enqueue(s);
        }
    }

    public sealed record CanonicalAuditReport(
        string Host,
        int PagesSeen,
        int WithCanonical,
        int MultipleCanonical,
        int CanonicalInBody,
        int CanonicalMissingScheme,
        int CrossDomainCanonical,
        int CanonicalTargetHttpErrors,
        int CanonicalTargetNoindexOrBlocked,
        int CanonicalInvalidUrl,
        string[] SamplesInvalid,
        string[] SamplesHttpErrors,
        string[] SamplesNoindexOrBlocked,
        string[] SamplesCrossDomain
    );

    public sealed class NoindexStore
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _noindex =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host) => _noindex.TryRemove(host, out _);

        public void Add(string host, string url)
        {
            var set = _noindex.GetOrAdd(host, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            set.TryAdd(url, 0);
        }

        public NoindexAuditReport Build(string host)
        {
            var urls = _noindex.TryGetValue(host, out var set) ? set.Keys.ToArray() : Array.Empty<string>();
            return new NoindexAuditReport(host, urls.Length, urls.Take(200).ToArray());
        }
    }

    public sealed record NoindexAuditReport(string Host, int NoindexPagesCount, string[] Sample);

    public sealed class ImagesMetaStore
    {
        private sealed class PageImgStat
        {
            public int Total;
            public int EmptyAlt;
            public int EmptyTitle;
        }

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PageImgStat>> _byHost =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host) => _byHost.TryRemove(host, out _);

        public void Add(string host, string pageUrl, int total, int emptyAlt, int emptyTitle)
        {
            var map = _byHost.GetOrAdd(host, _ => new ConcurrentDictionary<string, PageImgStat>(StringComparer.OrdinalIgnoreCase));
            map[pageUrl] = new PageImgStat { Total = total, EmptyAlt = emptyAlt, EmptyTitle = emptyTitle };
        }

        public ImagesMetaAuditReport Build(string host)
        {
            _byHost.TryGetValue(host, out var map);
            map ??= new ConcurrentDictionary<string, PageImgStat>(StringComparer.OrdinalIgnoreCase);

            int pages = map.Count;
            int totalImgs = map.Values.Sum(v => v.Total);
            int emptyAlt = map.Values.Sum(v => v.EmptyAlt);
            int emptyTitle = map.Values.Sum(v => v.EmptyTitle);

            var pagesWithEmptyAlt = map.Where(kv => kv.Value.EmptyAlt > 0)
                .Select(kv => new PageImageIssue(kv.Key, kv.Value.Total, kv.Value.EmptyAlt, kv.Value.EmptyTitle))
                .OrderByDescending(x => x.EmptyAlt)
                .Take(200)
                .ToArray();

            var pagesWithEmptyTitle = map.Where(kv => kv.Value.EmptyTitle > 0)
                .Select(kv => new PageImageIssue(kv.Key, kv.Value.Total, kv.Value.EmptyAlt, kv.Value.EmptyTitle))
                .OrderByDescending(x => x.EmptyTitle)
                .Take(200)
                .ToArray();

            return new ImagesMetaAuditReport(
                Host: host,
                PagesAnalyzed: pages,
                TotalImages: totalImgs,
                AverageImagesPerPage: pages > 0 ? (double)totalImgs / pages : 0,
                EmptyAltTotal: emptyAlt,
                EmptyTitleTotal: emptyTitle,
                PagesWithEmptyAlt: pagesWithEmptyAlt,
                PagesWithEmptyTitle: pagesWithEmptyTitle
            );
        }
    }

    public sealed record PageImageIssue(string Url, int TotalImages, int EmptyAlt, int EmptyTitle);

    public sealed record ImagesMetaAuditReport(
        string Host,
        int PagesAnalyzed,
        int TotalImages,
        double AverageImagesPerPage,
        int EmptyAltTotal,
        int EmptyTitleTotal,
        PageImageIssue[] PagesWithEmptyAlt,
        PageImageIssue[] PagesWithEmptyTitle
    );

    public sealed class OutgoingLinksStore
    {
        private sealed class PageOutStat
        {
            public int InternalOut;
            public int ExternalOut;
            public bool HasNoInternal;
        }

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PageOutStat>> _byHost =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host) => _byHost.TryRemove(host, out _);

        public void Add(string host, string url, int internalOut, int externalOut)
        {
            var map = _byHost.GetOrAdd(host, _ => new ConcurrentDictionary<string, PageOutStat>(StringComparer.OrdinalIgnoreCase));
            map[url] = new PageOutStat { InternalOut = internalOut, ExternalOut = externalOut, HasNoInternal = internalOut == 0 };
        }

        public OutgoingLinksAuditReport Build(string host, int internalTooManyThreshold)
        {
            _byHost.TryGetValue(host, out var map);
            map ??= new ConcurrentDictionary<string, PageOutStat>(StringComparer.OrdinalIgnoreCase);

            var pagesNoInternal = map.Where(kv => kv.Value.HasNoInternal)
                .Select(kv => kv.Key)
                .Take(200)
                .ToArray();

            var pagesTooManyInternal = map.Where(kv => kv.Value.InternalOut > internalTooManyThreshold)
                .Select(kv => new PageOutgoingStat(kv.Key, kv.Value.InternalOut, kv.Value.ExternalOut))
                .OrderByDescending(x => x.InternalOut)
                .Take(200)
                .ToArray();

            var pagesManyExternal = map.Where(kv => kv.Value.ExternalOut > 0)
                .Select(kv => new PageOutgoingStat(kv.Key, kv.Value.InternalOut, kv.Value.ExternalOut))
                .OrderByDescending(x => x.ExternalOut)
                .Take(200)
                .ToArray();

            return new OutgoingLinksAuditReport(
                Host: host,
                PagesAnalyzed: map.Count,
                PagesWithoutInternalOutCount: pagesNoInternal.Length,
                PagesWithoutInternalOutSample: pagesNoInternal,
                PagesWithTooManyInternalOut: pagesTooManyInternal,
                PagesWithExternalOut: pagesManyExternal
            );
        }
    }

    public sealed record PageOutgoingStat(string Url, int InternalOut, int ExternalOut);

    public sealed record OutgoingLinksAuditReport(
        string Host,
        int PagesAnalyzed,
        int PagesWithoutInternalOutCount,
        string[] PagesWithoutInternalOutSample,
        PageOutgoingStat[] PagesWithTooManyInternalOut,
        PageOutgoingStat[] PagesWithExternalOut
    );

    public sealed class PerformanceStore
    {
        private sealed class HostPerf
        {
            public long Count;
            public double SumSeconds;
            public double MaxSeconds;
            public ConcurrentDictionary<string, double> TopSlow = new(StringComparer.OrdinalIgnoreCase);
        }

        private readonly ConcurrentDictionary<string, HostPerf> _byHost =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host) => _byHost.TryRemove(host, out _);

        public void Add(string host, string url, double seconds, int topKeep)
        {
            var p = _byHost.GetOrAdd(host, _ => new HostPerf());
            Interlocked.Increment(ref p.Count);
            lock (p)
            {
                p.SumSeconds += seconds;
                if (seconds > p.MaxSeconds) p.MaxSeconds = seconds;
            }

            // топ медленных
            p.TopSlow[url] = seconds;
            if (p.TopSlow.Count > topKeep)
            {
                var cut = p.TopSlow.OrderByDescending(x => x.Value).Take(topKeep).ToArray();
                p.TopSlow = new ConcurrentDictionary<string, double>(cut, StringComparer.OrdinalIgnoreCase);
                _byHost[host] = p;
            }
        }

        public PerformanceAuditReport Build(string host)
        {
            _byHost.TryGetValue(host, out var p);
            p ??= new HostPerf();

            var count = p.Count;
            var avg = count > 0 ? p.SumSeconds / count : 0;

            var top = p.TopSlow.OrderByDescending(x => x.Value)
                .Take(200)
                .Select(x => new SlowPage(x.Key, x.Value))
                .ToArray();

            return new PerformanceAuditReport(host, (int)count, avg, p.MaxSeconds, top);
        }
    }

    public sealed record SlowPage(string Url, double Seconds);

    public sealed record PerformanceAuditReport(
        string Host,
        int PagesMeasured,
        double AverageLoadSeconds,
        double MaxLoadSeconds,
        SlowPage[] TopSlowPages
    );

    public sealed class ContentLengthUniformityStore
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, int>> _byHost =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, int> _pagesCount =
            new(StringComparer.OrdinalIgnoreCase);
        public int GetPagesCount(string host) => _pagesCount.TryGetValue(host, out var pc) ? pc : 0;
        public void ResetHost(string host)
        {
            _byHost.TryRemove(host, out _);
            _pagesCount.TryRemove(host, out _);
        }

        public void Add(string host, long? contentLength)
        {
            if (contentLength is null) return;
            if (contentLength <= 0) return;

            _pagesCount.AddOrUpdate(host, 1, (_, v) => v + 1);

            var map = _byHost.GetOrAdd(host, _ => new ConcurrentDictionary<long, int>());
            map.AddOrUpdate(contentLength.Value, 1, (_, v) => v + 1);
        }

        public ContentLengthAuditReport Build(string host, ContentLengthUniformityOptions opt)
        {
            var pages = _pagesCount.TryGetValue(host, out var pc) ? pc : 0;
            _byHost.TryGetValue(host, out var map);
            map ??= new ConcurrentDictionary<long, int>();

            var top = map.OrderByDescending(x => x.Value)
                .Take(Math.Clamp(opt.TopLengthsToReport, 1, 100))
                .Select(x => new LengthStat(x.Key, x.Value))
                .ToArray();

            double topShare = 0;
            if (pages > 0 && top.Length > 0) topShare = (double)top[0].Count / pages;

            bool suspicious = pages >= opt.MinPagesForUniformityCheck && topShare >= opt.UniformityTopShareThreshold;

            return new ContentLengthAuditReport(host, pages, topShare, suspicious, top);
        }
    }

    public sealed record LengthStat(long ContentLength, int Count);

    public sealed record ContentLengthAuditReport(
        string Host,
        int PagesWithKnownContentLength,
        double TopLengthShare,
        bool SuspiciousUniformity,
        LengthStat[] TopLengths
    );

    // =========================
    // Canonical network audit service
    // =========================

    public sealed class CanonicalAuditService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly CanonicalOptions _opt;

        private readonly ConcurrentDictionary<string, Lazy<Task<RobotsRules>>> _robots =
            new(StringComparer.OrdinalIgnoreCase);

        private sealed class CanonicalTarget
        {
            public int Status;
            public bool Noindex;
            public bool BlockedByRobots;
        }

        private readonly ConcurrentDictionary<string, Lazy<Task<CanonicalTarget>>> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, int> _hostNetChecks =
            new(StringComparer.OrdinalIgnoreCase);

        public CanonicalAuditService(IHttpClientFactory httpFactory, CanonicalOptions opt)
        {
            _httpFactory = httpFactory;
            _opt = opt;
        }

        public async Task<(int status, bool noindexOrBlocked)> CheckCanonicalTargetAsync(Uri canonical, string userAgent, CancellationToken ct)
        {
            // лимит на host сетевые проверки
            var host = canonical.Host;
            var used = _hostNetChecks.AddOrUpdate(host, 1, (_, v) => v + 1);
            if (used > _opt.MaxCanonicalNetworkChecksPerHost)
                return (0, false); // silently stop checking to avoid overload

            var key = canonical.AbsoluteUri;

            var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<CanonicalTarget>>(() => FetchTargetAsync(canonical, userAgent, ct)));
            var t = await lazy.Value;

            return (t.Status, t.Noindex || t.BlockedByRobots);
        }

        private async Task<CanonicalTarget> FetchTargetAsync(Uri canonical, string userAgent, CancellationToken ct)
        {
            var http = _httpFactory.CreateClient("crawler");
            http.Timeout = TimeSpan.FromSeconds(_opt.CanonicalHttpTimeoutSeconds);

            // robots для host canonical
            RobotsRules? rules = null;
            try
            {
                rules = await _robots.GetOrAdd(canonical.Host, _ =>
                    new Lazy<Task<RobotsRules>>(() => RobotsRules.FetchAsync(new Uri($"https://{canonical.Host}/"), userAgent, http))).Value;
            }
            catch { rules = null; }

            bool blocked = rules is not null && !rules.IsAllowed(canonical);

            // HEAD for status + X-Robots-Tag
            bool noindex = false;
            int status = 0;

            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, canonical);
                using var headResp = await http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);
                status = (int)headResp.StatusCode;

                if (headResp.Headers.TryGetValues("X-Robots-Tag", out var xrt))
                {
                    var s = string.Join(", ", xrt);
                    if (s.Contains("noindex", StringComparison.OrdinalIgnoreCase))
                        noindex = true;
                }
            }
            catch
            {
                // fallback на GET ниже
            }

            // GET for meta robots (если HTML и статус не 4xx/5xx)
            try
            {
                using var getReq = new HttpRequestMessage(HttpMethod.Get, canonical);
                using var getResp = await http.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, ct);
                status = (int)getResp.StatusCode;

                if (status >= 400)
                    return new CanonicalTarget { Status = status, Noindex = noindex, BlockedByRobots = blocked };

                var ctype = getResp.Content.Headers.ContentType?.MediaType ?? "";
                if (!ctype.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return new CanonicalTarget { Status = status, Noindex = noindex, BlockedByRobots = blocked };

                var html = await getResp.Content.ReadAsStringAsync(ct);
                if (html.Length > _opt.MaxCanonicalHtmlChars) html = html.Substring(0, _opt.MaxCanonicalHtmlChars);

                var doc = new HtmlDocument { OptionCheckSyntax = true, OptionFixNestedTags = true };
                doc.LoadHtml(html);

                if (HasNoindexMeta(doc)) noindex = true;
            }
            catch
            {
                // ignore
            }

            return new CanonicalTarget { Status = status, Noindex = noindex, BlockedByRobots = blocked };
        }

        private static bool HasNoindexMeta(HtmlDocument doc)
        {
            var nodes = doc.DocumentNode.SelectNodes("//meta[@name and @content]");
            if (nodes is null) return false;

            foreach (var n in nodes)
            {
                var name = (n.GetAttributeValue("name", "") ?? "").Trim();
                if (name.Length == 0) continue;

                if (!name.Equals("robots", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("googlebot", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("yandex", StringComparison.OrdinalIgnoreCase))
                    continue;

                var content = (n.GetAttributeValue("content", "") ?? "").Trim();
                if (content.Contains("noindex", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    // =========================
    // Checks
    // =========================

    /// <summary>
    /// HTML: multiple title/description, missing title/h1/description, large pages, invalid doctype
    /// </summary>
    public sealed class HtmlSeoStructureCheck : ILinkCheck
    {
        private readonly HtmlSeoStructureOptions _opt;

        public HtmlSeoStructureCheck(HtmlSeoStructureOptions opt) => _opt = opt;

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400) return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);

            // Large page
            if (ctx.ContentLength is long cl && cl > _opt.LargeContentLengthThreshold)
            {
                issues.Add(new LinkIssue(
                    "HTML_LARGE_PAGE",
                    $"Очень большая страница: Content-Length={cl} > {_opt.LargeContentLengthThreshold}.",
                    IssueSeverity.Warning));
            }
            if (!string.IsNullOrEmpty(ctx.Html) && ctx.Html.Length > _opt.LargeHtmlCharsThreshold)
            {
                issues.Add(new LinkIssue(
                    "HTML_LARGE_PAGE_BODY",
                    $"Очень большая страница: htmlChars={ctx.Html.Length} > {_opt.LargeHtmlCharsThreshold}.",
                    IssueSeverity.Warning));
            }

            // Doctype
            if (_opt.RequireDoctypeHtml && !string.IsNullOrEmpty(ctx.Html))
            {
                var html = ctx.Html;
                var m = Regex.Match(html, @"<!doctype\s+[^>]+>", RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    issues.Add(new LinkIssue(
                        "HTML_DOCTYPE_MISSING",
                        "Неверный HTML DOCTYPE: doctype отсутствует.",
                        IssueSeverity.Warning));
                }
                else
                {
                    var dt = m.Value.Trim();
                    if (!Regex.IsMatch(dt, @"<!doctype\s+html\s*>", RegexOptions.IgnoreCase))
                    {
                        issues.Add(new LinkIssue(
                            "HTML_DOCTYPE_NOT_HTML",
                            $"Неверный HTML DOCTYPE: найден \"{dt}\" (ожидали <!doctype html>).",
                            IssueSeverity.Warning));
                    }
                }
            }

            // Multiple TITLE
            var titles = ctx.Document.DocumentNode.SelectNodes("//title");
            var titleCount = titles?.Count ?? 0;
            if (titleCount == 0)
            {
                issues.Add(new LinkIssue("SEO_MISSING_TITLE", "Страница без TITLE.", IssueSeverity.Warning));
            }
            else if (titleCount > 1)
            {
                issues.Add(new LinkIssue("SEO_MULTIPLE_TITLE", $"Страницa с несколькими TITLE: {titleCount}.", IssueSeverity.Warning));
            }

            // H1 missing
            var h1 = ctx.Document.DocumentNode.SelectSingleNode("//h1");
            if (h1 is null || string.IsNullOrWhiteSpace(h1.InnerText))
            {
                issues.Add(new LinkIssue("SEO_MISSING_H1", "Страница без H1.", IssueSeverity.Warning));
            }

            // Multiple DESCRIPTION
            var descCount = CountMetaDescription(ctx.Document);
            if (descCount == 0)
            {
                issues.Add(new LinkIssue("SEO_MISSING_DESCRIPTION", "Страница без DESCRIPTION.", IssueSeverity.Warning));
            }
            else if (descCount > 1)
            {
                issues.Add(new LinkIssue("SEO_MULTIPLE_DESCRIPTION", $"Страница с несколькими DESCRIPTION: {descCount}.", IssueSeverity.Warning));
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }

        private static int CountMetaDescription(HtmlDocument doc)
        {
            var nodes = doc.DocumentNode.SelectNodes("//meta[@name and @content]");
            if (nodes is null) return 0;

            int c = 0;
            foreach (var n in nodes)
            {
                var name = (n.GetAttributeValue("name", "") ?? "").Trim();
                if (name.Equals("description", StringComparison.OrdinalIgnoreCase))
                    c++;
            }
            return c;
        }
    }

    /// <summary>
    /// Canonical: presence/multiple/body/cross-domain/invalid/missing scheme + target status/noindex/robots
    /// </summary>
    public sealed class CanonicalCheck : ILinkCheck
    {
        private readonly CanonicalOptions _opt;
        private readonly CanonicalAuditService _svc;
        private readonly CanonicalSummaryStore _sum;

        public CanonicalCheck(CanonicalOptions opt, CanonicalAuditService svc, CanonicalSummaryStore sum)
        {
            _opt = opt;
            _svc = svc;
            _sum = sum;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            if (ctx.Document is null) return issues;
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400) return issues;
            if (string.IsNullOrWhiteSpace(ctx.ContentType) || !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return issues;

            var host = ctx.FinalUri.Host;
            _sum.AddPageSeen(host);

            var canonLinks = FindCanonicals(ctx.Document);
            if (canonLinks.Count == 0) return issues;

            _sum.AddWithCanonical(host);

            if (canonLinks.Count > 1)
            {
                _sum.AddMultipleCanonical(host);
                issues.Add(new LinkIssue("CANONICAL_MULTIPLE", $"Страница с несколькими rel=canonical: {canonLinks.Count}.", IssueSeverity.Warning));
            }

            // canonical in body
            if (IsCanonicalInBody(canonLinks))
            {
                _sum.AddCanonicalInBody(host);
                issues.Add(new LinkIssue("CANONICAL_IN_BODY", "rel=canonical найден в <body> (должен быть в <head>).", IssueSeverity.Warning));
            }

            // Проверяем первый canonical (остальные считаем проблемой multiple)
            var href = (canonLinks[0].GetAttributeValue("href", "") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(href))
            {
                _sum.AddInvalid(host, ctx.FinalUrl);
                issues.Add(new LinkIssue("CANONICAL_EMPTY_HREF", "rel=canonical без href.", IssueSeverity.Warning));
                return issues;
            }

            // missing scheme (например "www.site.com/page")
            if (LooksLikeAbsoluteWithoutScheme(href))
            {
                _sum.AddCanonicalMissingScheme(host);
                issues.Add(new LinkIssue("CANONICAL_MISSING_SCHEME", $"В canonical URL отсутствует префикс http/https: \"{TrimTo(href, 120)}\".", IssueSeverity.Warning));
                // попробуем поправить для проверки (как https://)
                href = "https://" + href;
            }

            if (!TryResolveUrl(ctx.FinalUri, href, out var canonicalUri))
            {
                _sum.AddInvalid(host, ctx.FinalUrl);
                issues.Add(new LinkIssue("CANONICAL_INVALID_URL", $"Некорректный canonical URL: \"{TrimTo(href, 200)}\".", IssueSeverity.Warning));
                return issues;
            }

            if (!canonicalUri.IsAbsoluteUri)
            {
                _sum.AddInvalid(host, ctx.FinalUrl);
                issues.Add(new LinkIssue("CANONICAL_INVALID_URL", $"Некорректный canonical URL (не абсолютный): \"{canonicalUri}\".", IssueSeverity.Warning));
                return issues;
            }

            // cross-domain
            if (!canonicalUri.Host.Equals(ctx.FinalUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                _sum.AddCrossDomain(host, ctx.FinalUrl);
                issues.Add(new LinkIssue(
                    "CANONICAL_CROSS_DOMAIN",
                    $"Кросс-доменный canonical: {canonicalUri.Host} (страница: {ctx.FinalUri.Host}).",
                    IssueSeverity.Warning));
            }

            // target checks (status + noindex/robots)
            var (status, noindexOrBlocked) = await _svc.CheckCanonicalTargetAsync(canonicalUri, userAgent: "MyCrawler", ct);

            if (status >= 400)
            {
                _sum.AddTargetHttpError(host, ctx.FinalUrl);
                issues.Add(new LinkIssue(
                    "CANONICAL_TARGET_HTTP_ERROR",
                    $"canonical указывает на недоступную страницу (HTTP {status}).",
                    IssueSeverity.Warning));
            }

            if (noindexOrBlocked)
            {
                _sum.AddTargetNoindexOrBlocked(host, ctx.FinalUrl);
                issues.Add(new LinkIssue(
                    "CANONICAL_TARGET_BLOCKED_OR_NOINDEX",
                    "Канонический URL заблокирован для индексации (robots/noindex на canonical-странице).",
                    IssueSeverity.Warning));
            }

            return issues;
        }

        private static List<HtmlNode> FindCanonicals(HtmlDocument doc)
        {
            var nodes = doc.DocumentNode.SelectNodes("//link[@rel]");
            if (nodes is null) return new List<HtmlNode>();

            var res = new List<HtmlNode>();
            foreach (var n in nodes)
            {
                var rel = (n.GetAttributeValue("rel", "") ?? "").Trim();
                if (rel.Length == 0) continue;

                // rel может быть "canonical" или "alternate canonical"
                var parts = rel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(p => p.Equals("canonical", StringComparison.OrdinalIgnoreCase)))
                    res.Add(n);
            }
            return res;
        }

        private static bool IsCanonicalInBody(List<HtmlNode> canonLinks)
        {
            foreach (var n in canonLinks)
            {
                var cur = n.ParentNode;
                while (cur is not null)
                {
                    if (cur.Name.Equals("body", StringComparison.OrdinalIgnoreCase))
                        return true;
                    cur = cur.ParentNode;
                }
            }
            return false;
        }

        private static bool TryResolveUrl(Uri baseUri, string href, out Uri uri)
        {
            uri = default!;
            if (href.StartsWith("//", StringComparison.Ordinal))
                href = baseUri.Scheme + ":" + href;

            if (Uri.TryCreate(href, UriKind.Absolute, out var abs))
            {
                uri = abs;
                return true;
            }

            try
            {
                uri = new Uri(baseUri, href);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksLikeAbsoluteWithoutScheme(string href)
        {
            // "www.site.com/..." или "site.com/..." — без http/https
            // не считаем относительные (/ ./ ../ #)
            if (href.StartsWith("/") || href.StartsWith("./") || href.StartsWith("../") || href.StartsWith("#") || href.StartsWith("//"))
                return false;

            if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;

            // простая эвристика: содержит точку и не содержит пробелов
            return href.Contains('.') && !href.Contains(' ');
        }

        private static string TrimTo(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }

    /// <summary>
    /// Индексация: noindex (meta / X-Robots-Tag) на каждой странице + store
    /// </summary>
    public sealed class NoindexAllPagesCheck : ILinkCheck
    {
        private readonly NoindexStore _store;

        public NoindexAllPagesCheck(NoindexStore store) => _store = store;

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400) return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
            if (string.IsNullOrWhiteSpace(ctx.ContentType) || !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);

            var noindex = HasNoindexHeader(ctx.Headers) || HasNoindexMeta(ctx.Document);
            if (noindex)
            {
                _store.Add(ctx.FinalUri.Host, ctx.FinalUrl);
                issues.Add(new LinkIssue(
                    "INDEX_NOINDEX",
                    "Страница закрыта от индексации (noindex в X-Robots-Tag или meta robots).",
                    IssueSeverity.Warning));
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }

        private static bool HasNoindexHeader(IReadOnlyDictionary<string, string> headers)
        {
            if (headers is null) return false;
            if (headers.TryGetValue("X-Robots-Tag", out var xrt) && !string.IsNullOrWhiteSpace(xrt))
                return xrt.Contains("noindex", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private static bool HasNoindexMeta(HtmlDocument doc)
        {
            var nodes = doc.DocumentNode.SelectNodes("//meta[@name and @content]");
            if (nodes is null) return false;

            foreach (var n in nodes)
            {
                var name = (n.GetAttributeValue("name", "") ?? "").Trim();
                if (name.Length == 0) continue;

                if (!name.Equals("robots", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("googlebot", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("yandex", StringComparison.OrdinalIgnoreCase))
                    continue;

                var content = (n.GetAttributeValue("content", "") ?? "").Trim();
                if (content.Contains("noindex", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Изображения: число изображений, пустой alt/title + store
    /// </summary>
    public sealed class ImagesMetaCheck : ILinkCheck
    {
        private readonly ImagesMetaOptions _opt;
        private readonly ImagesMetaStore _store;

        public ImagesMetaCheck(ImagesMetaOptions opt, ImagesMetaStore store)
        {
            _opt = opt;
            _store = store;
        }

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400) return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
            if (string.IsNullOrWhiteSpace(ctx.ContentType) || !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);

            var nodes = ctx.Document.DocumentNode.SelectNodes("//img");
            int total = 0, emptyAlt = 0, emptyTitle = 0;

            if (nodes is not null)
            {
                foreach (var n in nodes)
                {
                    if (total >= Math.Clamp(_opt.MaxImagesPerPage, 1, 5000)) break;
                    total++;

                    var alt = (n.GetAttributeValue("alt", null) ?? "").Trim();
                    var title = (n.GetAttributeValue("title", null) ?? "").Trim();
                    var src = (n.GetAttributeValue("src", "") ?? "").Trim();
                    var cls = (n.GetAttributeValue("class", "") ?? "").ToLowerInvariant();
                    var role = (n.GetAttributeValue("role", "") ?? "").ToLowerInvariant();
                    var ariaHidden = (n.GetAttributeValue("aria-hidden", "") ?? "").ToLowerInvariant();

                    bool decorative =
                        role == "presentation" ||
                        ariaHidden == "true" ||
                        cls.Contains("icon") ||
                        src.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                        src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase);


                    if (string.IsNullOrWhiteSpace(alt) && !decorative) emptyAlt++;

                    if (string.IsNullOrWhiteSpace(title)) emptyTitle++;

                }
            }

            _store.Add(ctx.FinalUri.Host, ctx.FinalUrl, total, emptyAlt, emptyTitle);

            //if (emptyAlt > 0)
            //    issues.Add(new LinkIssue("IMG_EMPTY_ALT", $"Найдены изображения с пустым alt: {emptyAlt} (всего img={total}).", IssueSeverity.Warning));

            //if (emptyTitle > 0)
            //    issues.Add(new LinkIssue("IMG_EMPTY_TITLE", $"Найдены изображения с пустым title: {emptyTitle} (всего img={total}).", IssueSeverity.Warning));

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }
    }

    /// <summary>
    /// Outgoing links: внешние ссылки, слишком много внутренних, нет внутренних + store
    /// </summary>
    public sealed class OutgoingLinksCheck : ILinkCheck
    {
        private readonly OutgoingLinksOptions _opt;
        private readonly OutgoingLinksStore _store;

        public OutgoingLinksCheck(OutgoingLinksOptions opt, OutgoingLinksStore store)
        {
            _opt = opt;
            _store = store;
        }

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400) return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
            if (string.IsNullOrWhiteSpace(ctx.ContentType) || !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);

            var host = ctx.FinalUri.Host;
            int internalOut = 0;
            int externalOut = 0;

            var nodes = ctx.Document.DocumentNode.SelectNodes("//a[@href]");
            if (nodes is not null)
            {
                int processed = 0;
                foreach (var a in nodes)
                {
                    if (processed++ >= Math.Clamp(_opt.MaxLinksPerPage, 1, 10000)) break;

                    var href = (a.GetAttributeValue("href", "") ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(href)) continue;
                    if (href.StartsWith("#")) continue;
                    if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;

                    // фиксируем “url без http/https” как отдельную проблему (для абсолютных ссылок вида www.site.com/...)
                    if (LooksLikeAbsoluteWithoutScheme(href))
                    {
                        issues.Add(new LinkIssue("URL_MISSING_SCHEME", $"В ссылке отсутствует префикс http/https: \"{TrimTo(href, 120)}\".", IssueSeverity.Info));
                        href = "https://" + href;
                    }

                    if (!TryResolve(ctx.FinalUri, href, out var u)) continue;

                    if (u.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) internalOut++;
                    else externalOut++;
                }
            }

            _store.Add(host, ctx.FinalUrl, internalOut, externalOut);

            if (internalOut == 0)
                issues.Add(new LinkIssue("OUT_NO_INTERNAL_LINKS", "Страница без исходящих внутренних ссылок.", IssueSeverity.Warning));

            if (internalOut > _opt.InternalTooManyThreshold)
                issues.Add(new LinkIssue("OUT_TOO_MANY_INTERNAL_LINKS", $"Очень много исходящих внутренних ссылок: {internalOut} (> {_opt.InternalTooManyThreshold}).", IssueSeverity.Warning));

            if (externalOut >= _opt.ExternalWarnThreshold)
                issues.Add(new LinkIssue("OUT_MANY_EXTERNAL_LINKS", $"Много исходящих внешних ссылок: {externalOut} (>= {_opt.ExternalWarnThreshold}).", IssueSeverity.Warning));

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }

        private static bool LooksLikeAbsoluteWithoutScheme(string href)
        {
            if (href.StartsWith("/") || href.StartsWith("./") || href.StartsWith("../") || href.StartsWith("#") || href.StartsWith("//"))
                return false;

            if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;

            return href.Contains('.') && !href.Contains(' ');
        }

        private static bool TryResolve(Uri baseUri, string raw, out Uri uri)
        {
            uri = default!;

            if (raw.StartsWith("//", StringComparison.Ordinal))
                raw = baseUri.Scheme + ":" + raw;

            if (Uri.TryCreate(raw, UriKind.Absolute, out var abs))
            {
                uri = abs;
                return true;
            }

            try
            {
                uri = new Uri(baseUri, raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string TrimTo(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }

    /// <summary>
    /// Redirects: переадресация на поддомены (по цепочке ctx.Redirects)
    /// </summary>
    public sealed class RedirectToSubdomainCheck : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var redirects = ctx.Redirects;

            // IReadOnlyList => Count
            if (redirects is null || redirects.Count == 0)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var baseHost = ctx.OriginalUri.Host;
            var baseRoot = GetRoot2(baseHost);

            foreach (var hop in redirects)
            {
                var toUrl = TryGetRedirectToUrl(hop);
                if (string.IsNullOrWhiteSpace(toUrl)) continue;

                if (!Uri.TryCreate(toUrl, UriKind.Absolute, out var toUri)) continue;

                var toHost = toUri.Host;
                if (toHost.Equals(baseHost, StringComparison.OrdinalIgnoreCase)) continue;

                // "на поддомен/вариант" трактуем как тот же root2 (пример: a.site.com -> b.site.com)
                var toRoot = GetRoot2(toHost);
                if (!string.IsNullOrEmpty(baseRoot) &&
                    baseRoot.Equals(toRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                    {
                        new LinkIssue(
                            "REDIRECT_TO_SUBDOMAIN",
                            $"Переадресация на поддомен/вариант хоста: {baseHost} -> {toHost}",
                            IssueSeverity.Warning)
                    });
                }

                else return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]{new LinkIssue("REDIRECT_TO_OTHER_DOMAIN",$"Переадресация на другой домен: {baseHost} -> {toHost}", IssueSeverity.Warning)});
                
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }

        private static string? TryGetRedirectToUrl(object hop)
        {
            // Подберите сюда имя вашего поля, если оно отличается
            var propNames = new[]
            {
                "To", "ToUrl", "Target", "TargetUrl", "Next", "NextUrl", "Location", "RedirectUrl", "Url"
            };

            var t = hop.GetType();

            foreach (var name in propNames)
            {
                var p = t.GetProperty(name);
                if (p is null) continue;
                if (p.PropertyType != typeof(string)) continue;

                var val = p.GetValue(hop) as string;
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }

            return null;
        }

        private static string GetRoot2(string host)
        {
            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return host;
            return parts[^2] + "." + parts[^1];
        }
    }

/// <summary>
/// Performance: среднее время загрузки (store) + предупреждение на медленных страницах
/// </summary>
public sealed class PerformanceCheck : ILinkCheck
    {
        private readonly PerformanceOptions _opt;
        private readonly PerformanceStore _store;

        public PerformanceCheck(PerformanceOptions opt, PerformanceStore store)
        {
            _opt = opt;
            _store = store;
        }

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            // измерение есть даже на не-html, но логичнее считать по успешным
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var sec = ctx.TotalTime.TotalSeconds;
            _store.Add(ctx.FinalUri.Host, ctx.FinalUrl, sec, _opt.TopSlowPagesToKeep);

            if (sec >= _opt.SlowPageSecondsThreshold)
            {
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                {
                    new LinkIssue("PERF_SLOW_PAGE", $"Медленная загрузка: {sec:F2} сек (>= {_opt.SlowPageSecondsThreshold:F2}).", IssueSeverity.Warning)
                });
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }
    }

    /// <summary>
    /// Content-length uniformity collector ("Одинаковый размер документа для всех запросов")
    /// </summary>
    public sealed class ContentLengthUniformityCollectorCheck : ILinkCheck
    {
        private readonly ContentLengthUniformityStore _store;

        public ContentLengthUniformityCollectorCheck(ContentLengthUniformityStore store) => _store = store;

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.FinalStatusCode >= 200 && ctx.FinalStatusCode < 400)
                _store.Add(ctx.FinalUri.Host, ctx.ContentLength);

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }
    }
}
