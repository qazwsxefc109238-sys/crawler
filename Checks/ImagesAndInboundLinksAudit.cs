using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Crawler_project.Models;

namespace Crawler_project.Checks
{
    // =========================
    //  IMAGES (site-level)
    // =========================

    public sealed class SiteImagesOptions
    {
        /// <summary>“Редкое” изображение: встречается на сайте меньше этого числа раз (по умолчанию < 10)</summary>
        public int RareCopiesThreshold { get; set; } = 10;

        /// <summary>Игнорировать query/fragment при учёте изображений</summary>
        public bool IgnoreQueryAndFragment { get; set; } = true;

        /// <summary>Максимум img на страницу (защита)</summary>
        public int MaxImagesPerPage { get; set; } = 300;
    }

    public sealed class SiteImagesStore
    {
        // host -> imageUrl -> count
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _imgCounts =
            new(StringComparer.OrdinalIgnoreCase);

        // host -> pageUrl -> images[]
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string[]>> _pageImages =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host)
        {
            _imgCounts.TryRemove(host, out _);
            _pageImages.TryRemove(host, out _);
        }

        public void AddPageImages(string host, string pageUrl, IReadOnlyList<string> images)
        {
            var pageMap = _pageImages.GetOrAdd(host, _ => new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
            // если страницу уже добавляли — не пересчитываем
            if (!pageMap.TryAdd(pageUrl, images.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
                return;

            var cntMap = _imgCounts.GetOrAdd(host, _ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            foreach (var img in images)
            {
                cntMap.AddOrUpdate(img, 1, (_, v) => v + 1);
            }
        }

        public ImagesAuditReport BuildReport(string host, int rareThreshold, int maxSamples = 100)
        {
            _pageImages.TryGetValue(host, out var pageMap);
            _imgCounts.TryGetValue(host, out var cntMap);

            pageMap ??= new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            cntMap ??= new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int pagesTotal = pageMap.Count;

            var pagesWithoutImages = new List<string>();
            var pagesWithoutUnique = new List<string>();
            var pagesWithoutRare = new List<string>();

            foreach (var kv in pageMap)
            {
                var page = kv.Key;
                var imgs = kv.Value ?? Array.Empty<string>();

                if (imgs.Length == 0)
                {
                    pagesWithoutImages.Add(page);
                    continue;
                }

                bool hasUnique = imgs.Any(i => cntMap.TryGetValue(i, out var c) && c == 1);
                if (!hasUnique) pagesWithoutUnique.Add(page);

                bool hasRare = imgs.Any(i => cntMap.TryGetValue(i, out var c) && c < rareThreshold);
                if (!hasRare) pagesWithoutRare.Add(page);
            }

            var topRepeated = cntMap
                .OrderByDescending(x => x.Value)
                .Take(50)
                .Select(x => new ImageUsage(x.Key, x.Value))
                .ToArray();

            return new ImagesAuditReport(
                Host: host,
                PagesWithKnownImages: pagesTotal,
                PagesWithoutImagesCount: pagesWithoutImages.Count,
                PagesWithoutImagesSample: pagesWithoutImages.Take(maxSamples).ToArray(),

                PagesWithoutUniqueImagesCount: pagesWithoutUnique.Count,
                PagesWithoutUniqueImagesSample: pagesWithoutUnique.Take(maxSamples).ToArray(),

                RareCopiesThreshold: rareThreshold,
                PagesWithoutRareImagesCount: pagesWithoutRare.Count,
                PagesWithoutRareImagesSample: pagesWithoutRare.Take(maxSamples).ToArray(),

                TopRepeatedImages: topRepeated
            );
        }
    }

    public sealed record ImageUsage(string ImageUrl, int PagesCount);

    public sealed record ImagesAuditReport(
        string Host,
        int PagesWithKnownImages,

        int PagesWithoutImagesCount,
        string[] PagesWithoutImagesSample,

        int PagesWithoutUniqueImagesCount,
        string[] PagesWithoutUniqueImagesSample,

        int RareCopiesThreshold,
        int PagesWithoutRareImagesCount,
        string[] PagesWithoutRareImagesSample,

        ImageUsage[] TopRepeatedImages
    );

    /// <summary>
    /// Collector: собирает img[src] и наполняет SiteImagesStore.
    /// Issues не возвращает (это агрегат “по сайту”).
    /// </summary>
    public sealed class ImagesCollectorCheck : ILinkCheck
    {
        private readonly SiteImagesStore _store;
        private readonly SiteImagesOptions _opt;

        public ImagesCollectorCheck(SiteImagesStore store, SiteImagesOptions opt)
        {
            _store = store;
            _opt = opt;
        }

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
            if (string.IsNullOrWhiteSpace(ctx.ContentType) || !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var host = ctx.FinalUri.Host;
            var pageUrl = ctx.FinalUrl;

            var imgs = ExtractImages(ctx.Document, ctx.FinalUri, _opt.IgnoreQueryAndFragment, _opt.MaxImagesPerPage);
            _store.AddPageImages(host, pageUrl, imgs);

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }

        private static List<string> ExtractImages(HtmlDocument doc, Uri baseUri, bool ignoreQueryFragment, int maxPerPage)
        {
            var res = new List<string>(64);
            var nodes = doc.DocumentNode.SelectNodes("//img[@src]");
            if (nodes is null) return res;

            foreach (var n in nodes)
            {
                if (res.Count >= Math.Clamp(maxPerPage, 1, 5000)) break;

                var raw = (n.GetAttributeValue("src", "") ?? "").Trim();
                if (!IsCheckableUrl(raw)) continue;

                if (!TryResolve(baseUri, raw, out var u)) continue;

                var norm = NormalizeUrl(u, ignoreQueryFragment);
                if (norm is null) continue;

                res.Add(norm);
            }

            // unique per page
            return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsCheckableUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (raw.StartsWith("#")) return false;
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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

        private static string? NormalizeUrl(Uri uri, bool ignoreQueryFragment)
        {
            try
            {
                var b = new UriBuilder(uri);
                b.Host = b.Host.ToLowerInvariant();

                if (ignoreQueryFragment)
                {
                    b.Query = "";
                    b.Fragment = "";
                }
                else
                {
                    b.Fragment = "";
                }

                if ((b.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && b.Port == 443) ||
                    (b.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port == 80))
                    b.Port = -1;

                if (string.IsNullOrEmpty(b.Path)) b.Path = "/";

                return b.Uri.AbsoluteUri;
            }
            catch
            {
                return null;
            }
        }
    }

    // =========================
    //  INTERNAL LINK GRAPH (inbound)
    // =========================

    public sealed class InternalLinkGraphOptions
    {
        public bool IgnoreQueryAndFragment { get; set; } = true;

        // landing heuristic (как в вашей логике)
        public int LandingMaxPathSegments { get; set; } = 1;
        public bool TreatIndexFilesAsLanding { get; set; } = true;

        // аудит
        public int InboundThreshold { get; set; } = 5;

        public int MaxLinksPerPage { get; set; } = 800;
    }

    public sealed class InternalLinkGraphStore
    {
        // host -> targetUrl -> inboundCount
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _inbound =
            new(StringComparer.OrdinalIgnoreCase);

        // host -> pages seen (чтобы понимать объём данных)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _pages =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host)
        {
            _inbound.TryRemove(host, out _);
            _pages.TryRemove(host, out _);
        }

        public void AddPageAndOutgoing(string host, string pageUrl, IReadOnlyList<string> outgoingInternal)
        {
            var pages = _pages.GetOrAdd(host, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            pages.TryAdd(pageUrl, 0);

            var inbound = _inbound.GetOrAdd(host, _ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase));

            foreach (var target in outgoingInternal.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                inbound.AddOrUpdate(target, 1, (_, v) => v + 1);
            }
        }

        public int GetInboundCount(string host, string normalizedUrl)
        {
            if (_inbound.TryGetValue(host, out var map) && map.TryGetValue(normalizedUrl, out var c))
                return c;
            return 0;
        }

        public int PagesSeenCount(string host)
        {
            return _pages.TryGetValue(host, out var p) ? p.Count : 0;
        }
    }

    /// <summary>
    /// Collector: собирает внутренние ссылки (a[href]) и строит входящие.
    /// Issues не возвращает (агрегат “по сайту”).
    /// </summary>
    public sealed class InternalLinkGraphCollectorCheck : ILinkCheck
    {
        private readonly InternalLinkGraphStore _store;
        private readonly InternalLinkGraphOptions _opt;

        public InternalLinkGraphCollectorCheck(InternalLinkGraphStore store, InternalLinkGraphOptions opt)
        {
            _store = store;
            _opt = opt;
        }

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
            if (string.IsNullOrWhiteSpace(ctx.ContentType) || !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var host = ctx.FinalUri.Host;

            // нормализованный URL текущей страницы (ключ графа)
            var pageNorm = NormalizeUrl(ctx.FinalUri, _opt.IgnoreQueryAndFragment, forceHttpsForInternal: true);
            if (pageNorm is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var outgoing = ExtractInternalLinks(ctx.Document, ctx.FinalUri, host, _opt.IgnoreQueryAndFragment, _opt.MaxLinksPerPage);
            _store.AddPageAndOutgoing(host, pageNorm, outgoing);

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }

        private static List<string> ExtractInternalLinks(HtmlDocument doc, Uri baseUri, string host, bool ignoreQueryFragment, int maxPerPage)
        {
            var res = new List<string>(128);
            var nodes = doc.DocumentNode.SelectNodes("//a[@href]");
            if (nodes is null) return res;

            foreach (var n in nodes)
            {
                if (res.Count >= Math.Clamp(maxPerPage, 1, 5000)) break;

                var raw = (n.GetAttributeValue("href", "") ?? "").Trim();
                if (!IsCheckableUrl(raw)) continue;

                if (!TryResolve(baseUri, raw, out var u)) continue;

                if (!u.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                    continue;

                var norm = NormalizeUrl(u, ignoreQueryFragment, forceHttpsForInternal: true);
                if (norm is null) continue;

                res.Add(norm);
            }

            return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsCheckableUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (raw.StartsWith("#")) return false;
            if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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

        private static string? NormalizeUrl(Uri uri, bool ignoreQueryFragment, bool forceHttpsForInternal)
        {
            try
            {
                var b = new UriBuilder(uri);
                b.Host = b.Host.ToLowerInvariant();

                if (forceHttpsForInternal)
                    b.Scheme = "https";

                if (ignoreQueryFragment)
                {
                    b.Query = "";
                    b.Fragment = "";
                }
                else
                {
                    b.Fragment = "";
                }

                // default ports
                if ((b.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && b.Port == 443) ||
                    (b.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port == 80))
                    b.Port = -1;

                if (string.IsNullOrEmpty(b.Path))
                    b.Path = "/";

                if (b.Path.Length > 1 && b.Path.EndsWith("/"))
                    b.Path = b.Path.TrimEnd('/');

                return b.Uri.AbsoluteUri;
            }
            catch
            {
                return null;
            }
        }
    }
}

namespace Crawler_project.Controllers
{
    using Crawler_project.Checks;

    [ApiController]
    [Route("api/crawl")]
    public sealed class ImagesAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly SiteImagesStore _images;
        private readonly SiteImagesOptions _opt;

        public ImagesAuditController(JobStore store, SiteImagesStore images, SiteImagesOptions opt)
        {
            _store = store;
            _images = images;
            _opt = opt;
        }

        [HttpGet("{jobId:guid}/images-audit")]
        public ActionResult<ImagesAuditReport> ImagesAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;
            var report = _images.BuildReport(host, _opt.RareCopiesThreshold);

            return Ok(report);
        }
    }

    [ApiController]
    [Route("api/crawl")]
    public sealed class InboundLinksAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly InternalLinkGraphStore _graph;
        private readonly InternalLinkGraphOptions _opt;

        public InboundLinksAuditController(JobStore store, InternalLinkGraphStore graph, InternalLinkGraphOptions opt)
        {
            _store = store;
            _graph = graph;
            _opt = opt;
        }

        [HttpGet("{jobId:guid}/inbound-links-audit")]
        public ActionResult<InboundLinksAuditReport> InboundLinksAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var start = new Uri(job.StartUrl);
            var host = start.Host;

            // Берём список URL из job (discovered)
            var all = GetAllUrls(jobId);
            var landing = new List<InboundPageStat>();

            foreach (var u in all)
            {
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) continue;
                if (!uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) continue;

                // нормализуем так же, как в графе (https, без query/fragment по opt)
                var norm = NormalizeForGraph(uri, _opt.IgnoreQueryAndFragment);
                if (norm is null) continue;

                if (!IsLanding(uri, _opt)) continue;

                var inbound = _graph.GetInboundCount(host, norm);
                if (inbound < _opt.InboundThreshold)
                {
                    landing.Add(new InboundPageStat(norm, inbound));
                }
            }

            var report = new InboundLinksAuditReport(
                Host: host,
                PagesDiscovered: all.Length,
                PagesSeenInGraph: _graph.PagesSeenCount(host),
                LandingCandidatesWithLowInboundCount: landing.Count,
                InboundThreshold: _opt.InboundThreshold,
                Sample: landing
                    .OrderBy(x => x.InboundCount)
                    .Take(200)
                    .ToArray()
            );

            return Ok(report);
        }

        private string[] GetAllUrls(Guid jobId)
        {
            var all = new List<string>(capacity: 10_000);
            int offset = 0;

            while (true)
            {
                var batch = _store.GetUrls(jobId, offset, limit: 5000, out var total);
                if (batch.Length == 0) break;

                all.AddRange(batch);
                offset += batch.Length;
                if (offset >= total) break;
            }

            return all.ToArray();
        }

        private static bool IsLanding(Uri uri, InternalLinkGraphOptions opt)
        {
            var path = uri.AbsolutePath ?? "/";
            if (string.IsNullOrEmpty(path)) path = "/";
            if (path == "/") return true;

            if (opt.TreatIndexFilesAsLanding)
            {
                var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                if (last.StartsWith("index.", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            return segments <= Math.Max(0, opt.LandingMaxPathSegments);
        }

        private static string? NormalizeForGraph(Uri uri, bool ignoreQueryFragment)
        {
            try
            {
                var b = new UriBuilder(uri)
                {
                    Scheme = "https",
                    Host = uri.Host.ToLowerInvariant()
                };

                if (ignoreQueryFragment)
                {
                    b.Query = "";
                    b.Fragment = "";
                }
                else
                {
                    b.Fragment = "";
                }

                if (b.Port == 443) b.Port = -1;
                if (string.IsNullOrEmpty(b.Path)) b.Path = "/";

                if (b.Path.Length > 1 && b.Path.EndsWith("/"))
                    b.Path = b.Path.TrimEnd('/');

                return b.Uri.AbsoluteUri;
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed record InboundPageStat(string Url, int InboundCount);

    public sealed record InboundLinksAuditReport(
        string Host,
        int PagesDiscovered,
        int PagesSeenInGraph,
        int LandingCandidatesWithLowInboundCount,
        int InboundThreshold,
        InboundPageStat[] Sample
    );
}
