using Crawler_project.Models;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Collections.Concurrent;
namespace Crawler_project.Controllers
{





    [ApiController]
    [Route("api/crawl")]
    public sealed class SitemapAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly Services.SitemapAuditService _svc;

        private readonly Services.SitemapAuditRunner _runner;
        public SitemapAuditController(JobStore store, Services.SitemapAuditService svc, Services.SitemapAuditRunner runner)
        {
            _store = store;
            _svc = svc;
            _runner = runner;
        }

        /// <summary>
        /// Сайтовая проверка sitemap.xml с учётом результатов обхода (jobId).
        /// </summary>
        [HttpGet("{jobId:guid}/sitemap-audit")]
        public async Task<ActionResult<Services.SitemapAuditReport>> Audit(Guid jobId, CancellationToken ct)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            // Собираем все URL, найденные обходом (DiscoveredOrder)
            var crawledUrls = GetAllUrls(jobId);

            var startUri = new Uri(job.StartUrl);
            var report = await _svc.AuditAsync(startUri, crawledUrls, ct);

            return Ok(report);
        }
        [HttpPost("{jobId:guid}/sitemap-audit/start")]
        public ActionResult<Crawler_project.Services.SitemapAuditRunInfo> Start(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var crawledUrls = GetAllUrls(jobId);
            var startUri = new Uri(job.StartUrl);

            var info = _runner.Start(jobId, startUri, crawledUrls);
            return Ok(info);
        }

        [HttpGet("{jobId:guid}/sitemap-audit/status")]
        public ActionResult<Crawler_project.Services.SitemapAuditRunInfo> Status(Guid jobId)
        {
            return Ok(_runner.GetStatus(jobId));
        }

        [HttpGet("{jobId:guid}/sitemap-audit/result")]
        public ActionResult Result(Guid jobId)
        {
            var st = _runner.GetStatus(jobId);

            if (st.Status == Crawler_project.Services.SitemapAuditRunStatus.NotStarted)
                return NotFound(new { message = "Sitemap audit not started", status = st });

            if (st.Status == Crawler_project.Services.SitemapAuditRunStatus.Running)
                return Accepted(new { status = st });

            if (st.Status == Crawler_project.Services.SitemapAuditRunStatus.Failed)
                return Problem(title: "Sitemap audit failed", detail: st.Error);

            if (st.Status == Crawler_project.Services.SitemapAuditRunStatus.Canceled)
                return Ok(new { status = st });

            var rep = _runner.GetResult(jobId);
            return rep is null ? Problem("Report missing") : Ok(rep);
        }

        [HttpDelete("{jobId:guid}/sitemap-audit")]
        public IActionResult Cancel(Guid jobId)
        {
            return _runner.Cancel(jobId) ? Ok(new { canceled = true }) : Conflict(new { canceled = false });
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
    }




}

namespace Crawler_project.Services
{
    public sealed class SitemapAuditOptions
    {
        public int HttpTimeoutSeconds { get; set; } = 20;

        // Ограничения на размер/объём (защита)
        public int MaxSitemapsToProcess { get; set; } = 200;
        public int MaxUrlsToCollect { get; set; } = 200_000;
        public int MaxDownloadBytes { get; set; } = 10 * 1024 * 1024; // 10 MB

        // Индексируемость (для сравнений) — лимиты, чтобы не скачивать всё на больших сайтах
        public int MaxIndexabilityChecksFromSitemap { get; set; } = 3000;
        public int MaxIndexabilityChecksFromCrawl { get; set; } = 3000;
        public int MaxConcurrentRequests { get; set; } = 10;
        public bool FullIndexabilityScan { get; set; } = true; 
        public int ProgressReportEvery { get; set; } = 200;    
        // Порог “похожести” URL при сравнении: мы сравниваем нормализованные URL без query/fragment
        public bool IgnoreQueryAndFragment { get; set; } = true;

        // User-agent для robots.txt (используем ваш же)
        public string UserAgent { get; set; } = "MyCrawler";


    }
    public sealed record SitemapAuditProgress(
        string Stage,
        int Total,
        int Processed,
        int Remaining
        );

    public sealed record SitemapAuditReport(
        string Host,
        bool SitemapFound,
        IReadOnlyList<string> SitemapFiles,                 // реальные URL sitemap файлов, которые обработали
        int SitemapFilesCount,
        int TotalElementsInSitemaps,                        // сумма <url> по всем urlset (и/или ссылок из индексов)
        int HtmlPagesCountEstimated,                        // “только html” (эвристика)
        int ErrorsCount,
        int WarningsCount,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings,


        // сравнения
        int InSitemapButNotFoundByCrawlCount,
        IReadOnlyList<string> InSitemapButNotFoundByCrawlSample,

        int IndexableButNotInSitemapCount,                  // рассчитывается по выборке (см. IndexabilitySamplingNote)
        IReadOnlyList<string> IndexableButNotInSitemapSample,

        int NoindexButInSitemapCount,                       // рассчитывается по выборке
        IReadOnlyList<string> NoindexButInSitemapSample,

        string IndexabilitySamplingNote,
        bool HasSitemapXml
    );

    public sealed class SitemapAuditService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly SitemapAuditOptions _opt;

        public SitemapAuditService(IHttpClientFactory httpFactory, SitemapAuditOptions opt)
        {
            _httpFactory = httpFactory;
            _opt = opt;
        }

        public async Task<SitemapAuditReport> AuditAsync(Uri startUri, string[] crawledUrls, CancellationToken ct, Func<SitemapAuditProgress, Task>? onProgress = null)
        {
            var host = startUri.Host;

            // Нормализуем “найденные обходом”
            var crawledSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in crawledUrls)
            {
                var nu = NormalizeUrl(u, _opt.IgnoreQueryAndFragment);
                if (nu is not null) crawledSet.Add(nu);
            }

            var errors = new List<string>();
            var warnings = new List<string>();

            // 1) Находим кандидаты sitemap (robots.txt + /sitemap.xml)
            var sitemapCandidates = new List<Uri>();

            // robots.txt (Sitemap:)
            try
            {
                var robotSitemaps = await TryGetSitemapsFromRobotsAsync(host, ct);
                sitemapCandidates.AddRange(robotSitemaps);
            }
            catch (Exception ex)
            {
                warnings.Add($"ROBOTS_SITEMAP_PARSE_WARNING: не удалось извлечь Sitemap: из robots.txt: {ex.Message}");
            }

            // стандартные пути
            sitemapCandidates.Add(new Uri($"https://{host}/sitemap.xml"));
            sitemapCandidates.Add(new Uri($"https://{host}/sitemap_index.xml"));
            sitemapCandidates.Add(new Uri($"https://{host}/sitemap-index.xml"));

            // уникализируем
            sitemapCandidates = sitemapCandidates
                .DistinctBy(u => u.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 2) Загружаем/парсим sitemap (включая index)
            var (sitemapsProcessed, sitemapUrls, totalElements, htmlEstimated, parseErrors, parseWarnings, hasSitemapXml) =
                await LoadAndParseSitemapsAsync(host, sitemapCandidates, ct, onProgress);


            errors.AddRange(parseErrors);
            warnings.AddRange(parseWarnings);

            bool sitemapFound = sitemapsProcessed.Count > 0 && sitemapUrls.Count > 0;

            if (!sitemapFound)
            {
                errors.Add("SITEMAP_NOT_FOUND_OR_EMPTY: не удалось найти/распарсить sitemap.xml (или он пуст).");
            }

            // 3) Сравнение: “есть в sitemap, но не найдены при обходе”
            var inSitemapNotInCrawl = new List<string>();
            if (sitemapFound)
            {
                foreach (var u in sitemapUrls)
                {
                    if (!crawledSet.Contains(u))
                        inSitemapNotInCrawl.Add(u);
                }
            }

            // 4) Индексируемость и сравнительные пункты:
            //    - Indexable но не в sitemap (по выборке из crawl)
            //    - Noindex/Blocked но в sitemap (по выборке из sitemap)
            // Делается ограниченно, чтобы не устроить DDoS.
            var http = _httpFactory.CreateClient("crawler");
            http.Timeout = TimeSpan.FromSeconds(_opt.HttpTimeoutSeconds);

            // robots rules (для блокировки)
            RobotsRules? robots = null;
            try
            {
                robots = await RobotsRules.FetchAsync(new Uri($"https://{host}/"), _opt.UserAgent, http);
            }
            catch
            {
                // если robots не смогли — индексируемость только по noindex
                warnings.Add("ROBOTS_FETCH_WARNING: не удалось получить robots.txt для проверки индексируемости (robots).");
            }

            // 4a) Noindex/Blocked в sitemap (выборка из sitemap)
            var noindexButInSitemap = new ConcurrentBag<string>();
            var indexableButNotInSitemap = new ConcurrentBag<string>();
            var crawlSample = crawledSet.Take(_opt.MaxIndexabilityChecksFromCrawl).ToList();
            var blockedOrNoindexButInSitemap = new ConcurrentBag<string>();

            ///
            var sitemapSample = _opt.FullIndexabilityScan
    ? sitemapUrls.ToList()
    : sitemapUrls.Take(_opt.MaxIndexabilityChecksFromSitemap).ToList();

            var processedSitemap = 0;
            var totalSitemap = sitemapSample.Count;

            if (onProgress is not null)
                await onProgress(new SitemapAuditProgress("indexability:sitemap", totalSitemap, 0, totalSitemap));

            await ForEachConcurrentAsync(sitemapSample, _opt.MaxConcurrentRequests, ct, async url =>
            {
                var idx = await CheckIndexabilityAsync(http, robots, url, ct);

                if (!idx.IsError && idx.IsBlockedOrNoindex)
                    noindexButInSitemap.Add(url);

                var done = Interlocked.Increment(ref processedSitemap);
                if (onProgress is not null && (done % _opt.ProgressReportEvery == 0 || done == totalSitemap))
                    await onProgress(new SitemapAuditProgress("indexability:sitemap", totalSitemap, done, totalSitemap - done));
            });


            ///
            var processedCrawl = 0;
            var totalCrawl = crawlSample.Count;

            if (onProgress is not null)
                await onProgress(new SitemapAuditProgress("indexability:crawl", totalCrawl, 0, totalCrawl));

            await ForEachConcurrentAsync(crawlSample, _opt.MaxConcurrentRequests, ct, async url =>
            {
                try
                {
                    // URL уже есть в sitemap — просто пропускаем проверку,
                    // но обязательно считаем его как обработанный элемент этапа.
                    if (!(sitemapFound && sitemapUrls.Contains(url)))
                    {
                        var idx = await CheckIndexabilityAsync(http, robots, url, ct);
                        if (idx.IsIndexable)
                            indexableButNotInSitemap.Add(url);
                    }
                }
                finally
                {
                    var done = Interlocked.Increment(ref processedCrawl);
                    if (onProgress is not null && (done % _opt.ProgressReportEvery == 0 || done == totalCrawl))
                        await onProgress(new SitemapAuditProgress("indexability:crawl", totalCrawl, done, totalCrawl - done));
                }
            });



            // 5) Sampling note
            var note = $"Indexability checks are sampled/limited: " +
                       $"checked sitemap URLs={sitemapSample.Count} (limit={_opt.MaxIndexabilityChecksFromSitemap}), " +
                       $"checked crawled URLs={crawlSample.Count} (limit={_opt.MaxIndexabilityChecksFromCrawl}).";

            return new SitemapAuditReport(
                Host: host,
                SitemapFound: sitemapFound,
                SitemapFiles: sitemapsProcessed,
                SitemapFilesCount: sitemapsProcessed.Count,
                TotalElementsInSitemaps: totalElements,
                HtmlPagesCountEstimated: htmlEstimated,
                ErrorsCount: errors.Count,
                WarningsCount: warnings.Count,
                Errors: errors,
                Warnings: warnings,
                HasSitemapXml: hasSitemapXml,
                InSitemapButNotFoundByCrawlCount: inSitemapNotInCrawl.Count,
                InSitemapButNotFoundByCrawlSample: inSitemapNotInCrawl.Take(50).ToList(),

                IndexableButNotInSitemapCount: indexableButNotInSitemap.Count,
                IndexableButNotInSitemapSample: indexableButNotInSitemap.Take(50).ToList(),

                NoindexButInSitemapCount: noindexButInSitemap.Count,
                NoindexButInSitemapSample: noindexButInSitemap.Take(50).ToList(),



                IndexabilitySamplingNote: note

            );
        }

        // -------------------- sitemap discovery --------------------

        private async Task<List<Uri>> TryGetSitemapsFromRobotsAsync(string host, CancellationToken ct)
        {
            var list = new List<Uri>();
            var robotsUrl = new Uri($"https://{host}/robots.txt");

            HttpClient http;
            try { http = _httpFactory.CreateClient("crawler"); }
            catch { http = new HttpClient(); }

            http.Timeout = TimeSpan.FromSeconds(_opt.HttpTimeoutSeconds);

            string text;
            try
            {
                using var resp = await http.GetAsync(robotsUrl, ct);
                if (!resp.IsSuccessStatusCode) return list;

                text = await resp.Content.ReadAsStringAsync(ct);
            }
            catch
            {
                return list;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;

                // Sitemap: <url>
                if (line.StartsWith("Sitemap:", StringComparison.OrdinalIgnoreCase))
                {
                    var val = line.Substring("Sitemap:".Length).Trim();
                    if (Uri.TryCreate(val, UriKind.Absolute, out var u))
                        list.Add(u);
                    else if (Uri.TryCreate(new Uri($"https://{host}/"), val, out var rel))
                        list.Add(rel);
                }
            }

            return list.DistinctBy(u => u.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // -------------------- sitemap load/parse --------------------

        private async Task<(List<string> sitemapsProcessed, HashSet<string> urls, int totalElements, int htmlEstimated, List<string> errors, List<string> warnings, bool hasSitemapXml)>
            LoadAndParseSitemapsAsync(string host, List<Uri> candidates, CancellationToken ct, Func<SitemapAuditProgress, Task>? onProgress = null)


        {
            bool hasSitemapXml = false;
            var processedSitemaps = new List<string>();
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var warnings = new List<string>();

            var queue = new Queue<Uri>(candidates);
            var seenSitemaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int totalElements = 0;
            int htmlEstimated = 0;

            var http = _httpFactory.CreateClient("crawler");
            http.Timeout = TimeSpan.FromSeconds(_opt.HttpTimeoutSeconds);

            if (onProgress is not null) await onProgress(new SitemapAuditProgress("sitemap:scan", candidates.Count, 0, candidates.Count));


            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                if (processedSitemaps.Count >= _opt.MaxSitemapsToProcess)
                {
                    warnings.Add($"SITEMAP_LIMIT_REACHED: превышен лимит файлов sitemap ({_opt.MaxSitemapsToProcess}).");
                    break;
                }

                var sitemapUrl = queue.Dequeue();
                var sitemapKey = sitemapUrl.AbsoluteUri;

                if (!seenSitemaps.Add(sitemapKey))
                    continue;

                var (ok, content, err, isGzip) = await DownloadTextAsync(http, sitemapUrl, ct);
                if (!ok)
                {
                    // это “ошибка sitemap”
                    errors.Add($"SITEMAP_FETCH_ERROR: {sitemapUrl} -> {err}");
                    continue;
                }
                if (sitemapUrl.AbsolutePath.Equals("/sitemap.xml", StringComparison.OrdinalIgnoreCase))
                    hasSitemapXml = true;
                processedSitemaps.Add(sitemapKey);
                if (onProgress is not null) await onProgress(new SitemapAuditProgress("sitemap:scan", processedSitemaps.Count + queue.Count, processedSitemaps.Count, queue.Count));

                try
                {
                    // Парсим XML: sitemapindex или urlset
                    using var sr = new StringReader(content!);
                    using var xr = XmlReader.Create(sr, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Ignore,
                        IgnoreComments = true,
                        IgnoreWhitespace = true
                    });

                    // Читаем корневой элемент
                    xr.MoveToContent();
                    if (xr.NodeType != XmlNodeType.Element)
                    {
                        errors.Add($"SITEMAP_XML_ERROR: {sitemapUrl} -> нет корневого XML элемента.");
                        continue;
                    }

                    var rootName = xr.LocalName; // urlset or sitemapindex
                    if (rootName.Equals("sitemapindex", StringComparison.OrdinalIgnoreCase))
                    {
                        // <sitemap><loc>...</loc></sitemap>
                        var locs = ReadLocsFromSitemapIndex(content!);
                        totalElements += locs.Count;

                        foreach (var loc in locs)
                        {
                            if (Uri.TryCreate(loc, UriKind.Absolute, out var u))
                                queue.Enqueue(u);
                            else if (Uri.TryCreate(new Uri($"https://{host}/"), loc, out var rel))
                                queue.Enqueue(rel);
                            else
                                warnings.Add($"SITEMAP_BAD_LOC: {sitemapUrl} содержит некорректный loc: {loc}");
                        }
                    }
                    else if (rootName.Equals("urlset", StringComparison.OrdinalIgnoreCase))
                    {
                        var pageLocs = ReadLocsFromUrlSet(content!);
                        totalElements += pageLocs.Count;

                        foreach (var loc in pageLocs)
                        {
                            var norm = NormalizeUrl(loc, _opt.IgnoreQueryAndFragment);
                            if (norm is null)
                            {
                                warnings.Add($"SITEMAP_BAD_URL: {sitemapUrl} содержит некорректный URL: {loc}");
                                continue;
                            }

                            if (urls.Add(norm))
                            {
                                if (LooksLikeHtml(norm))
                                    htmlEstimated++;
                            }

                            if (urls.Count >= _opt.MaxUrlsToCollect)
                            {
                                warnings.Add($"SITEMAP_URL_LIMIT_REACHED: превышен лимит URL ({_opt.MaxUrlsToCollect}).");
                                return (processedSitemaps, urls, totalElements, htmlEstimated, errors, warnings, hasSitemapXml);
                            }
                        }
                    }
                    else
                    {
                        warnings.Add($"SITEMAP_UNKNOWN_ROOT: {sitemapUrl} корневой элемент \"{rootName}\" (ожидали urlset/sitemapindex).");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"SITEMAP_XML_PARSE_ERROR: {sitemapUrl} -> {ex.Message}");
                }
            }

            // Базовые предупреждения
            if (processedSitemaps.Count > 0 && urls.Count == 0)
                warnings.Add("SITEMAP_PARSED_BUT_NO_URLS: sitemap обработан, но URL не извлечены (возможно, только index без доступных дочерних файлов).");

            return (processedSitemaps, urls, totalElements, htmlEstimated, errors, warnings, hasSitemapXml);
        }

        private static List<string> ReadLocsFromSitemapIndex(string xml)
        {
            var list = new List<string>();
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);

            var nodes = doc.GetElementsByTagName("loc");
            foreach (XmlNode n in nodes)
            {
                // sitemapindex имеет loc тоже; мы не различаем контекстом — этого достаточно
                var v = n.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(v))
                    list.Add(v);
            }
            return list;
        }

        private static List<string> ReadLocsFromUrlSet(string xml)
        {
            var list = new List<string>();
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);

            // <url><loc>...</loc></url>
            var urlNodes = doc.GetElementsByTagName("url");
            foreach (XmlNode u in urlNodes)
            {
                var locNode = u.ChildNodes.Cast<XmlNode>().FirstOrDefault(x => x.LocalName.Equals("loc", StringComparison.OrdinalIgnoreCase));
                var v = locNode?.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(v))
                    list.Add(v);
            }
            return list;
        }

        // -------------------- indexability --------------------

        private sealed record IndexabilityResult(
    bool IsIndexable,
    bool IsBlockedByRobots,
    bool IsNoindex,
    bool IsError
)
        {
            public bool IsBlockedOrNoindex => IsBlockedByRobots || IsNoindex;
        }

        private async Task<IndexabilityResult> CheckIndexabilityAsync(
            HttpClient http,
            RobotsRules? robots,
            string normalizedUrl,
            CancellationToken ct)
        {
            // 0) Валидация URL
            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
                return new IndexabilityResult(IsIndexable: false, IsBlockedByRobots: false, IsNoindex: false, IsError: true);

            // 1) robots.txt
            if (robots is not null && !robots.IsAllowed(uri))
                return new IndexabilityResult(IsIndexable: false, IsBlockedByRobots: true, IsNoindex: false, IsError: false);

            // 2) HEAD -> X-Robots-Tag
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, uri);
                using var headResp = await http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)headResp.StatusCode >= 400)
                    return new IndexabilityResult(IsIndexable: false, IsBlockedByRobots: false, IsNoindex: false, IsError: true);

                if (headResp.Headers.TryGetValues("X-Robots-Tag", out var xrt))
                {
                    var s = string.Join(", ", xrt);
                    if (s.Contains("noindex", StringComparison.OrdinalIgnoreCase))
                        return new IndexabilityResult(IsIndexable: false, IsBlockedByRobots: false, IsNoindex: true, IsError: false);
                }
            }
            catch
            {
                // HEAD может быть запрещён/нестабилен — пробуем GET
            }

            // 3) GET -> meta robots noindex (только для HTML)
            try
            {
                using var getReq = new HttpRequestMessage(HttpMethod.Get, uri);
                using var resp = await http.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)resp.StatusCode >= 400)
                    return new IndexabilityResult(IsIndexable: false, IsBlockedByRobots: false, IsNoindex: false, IsError: true);

                var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";

                // Не HTML — считаем индексируемым (раз robots разрешает и noindex в заголовках не найден)
                if (!ctype.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return new IndexabilityResult(IsIndexable: true, IsBlockedByRobots: false, IsNoindex: false, IsError: false);

                // Читаем ограниченно (достаточно для meta robots)
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 512_000) bytes = bytes.Take(512_000).ToArray();

                var html = Encoding.UTF8.GetString(bytes);

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                bool noindex = HasNoindexMeta(doc);
                if (noindex)
                    return new IndexabilityResult(IsIndexable: false, IsBlockedByRobots: false, IsNoindex: true, IsError: false);

                return new IndexabilityResult(IsIndexable: true, IsBlockedByRobots: false, IsNoindex: false, IsError: false);
            }
            catch
            {
                return new IndexabilityResult(IsIndexable: false, IsBlockedByRobots: false, IsNoindex: false, IsError: true);
            }
        }

        private static bool HasNoindexMeta(HtmlDocument doc)
        {
            var nodes = doc.DocumentNode.SelectNodes(
                "//meta[@name and @content]"
            );

            if (nodes is null) return false;

            foreach (var n in nodes)
            {
                var name = (n.GetAttributeValue("name", "") ?? "").Trim().ToLowerInvariant();
                var content = (n.GetAttributeValue("content", "") ?? "").Trim();

                if (name is "robots" or "googlebot" or "yandex")
                {
                    if (content.Contains("noindex", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        // -------------------- download helpers (incl .gz) --------------------

        private async Task<(bool ok, string? text, string? error, bool isGzip)> DownloadTextAsync(HttpClient http, Uri url, CancellationToken ct)
        {
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    return (false, null, $"HTTP {(int)resp.StatusCode}", false);

                var isGzip = url.AbsoluteUri.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
                if (!isGzip && resp.Content.Headers.ContentEncoding.Any(x => x.Contains("gzip", StringComparison.OrdinalIgnoreCase)))
                    isGzip = true;

                await using var s = await resp.Content.ReadAsStreamAsync(ct);

                // ограничим скачивание
                using var ms = new MemoryStream();
                await CopyToLimitAsync(s, ms, _opt.MaxDownloadBytes, ct);

                ms.Position = 0;

                Stream rs = ms;
                if (isGzip)
                    rs = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true);

                using var sr = new StreamReader(rs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = await sr.ReadToEndAsync();

                return (true, text, null, isGzip);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message, false);
            }
        }

        private static async Task CopyToLimitAsync(Stream input, Stream output, int maxBytes, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            int total = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0) break;

                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException($"Download limit exceeded: {maxBytes} bytes");

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        // -------------------- URL normalization + HTML heuristic --------------------

        private static string? NormalizeUrl(string url, bool ignoreQueryAndFragment)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            var b = new UriBuilder(uri);
            b.Host = b.Host.ToLowerInvariant();

            if (ignoreQueryAndFragment)
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

            if (b.Path.Length > 1 && b.Path.EndsWith("/", StringComparison.Ordinal))
                b.Path = b.Path.TrimEnd('/');

            return b.Uri.AbsoluteUri;
        }

        private static bool LooksLikeHtml(string normalizedUrl)
        {
            // эвристика: считаем “html”, если нет расширения или оно .html/.htm,
            // и не похоже на статический файл (js/css/img/pdf/zip и т.п.)
            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var u)) return false;

            var path = u.AbsolutePath ?? "/";
            var last = path.Split('/').LastOrDefault() ?? "";

            if (!last.Contains('.', StringComparison.Ordinal)) return true;

            var dot = last.LastIndexOf('.');
            if (dot < 0) return true;

            var ext = last.Substring(dot).ToLowerInvariant();

            return ext is ".html" or ".htm" ||
                   // иногда страницы без .html, но с точкой (например, /catalog/item.123) — это риск,
                   // поэтому остальные расширения считаем не-html:
                   false;
        }

        // -------------------- concurrency helper --------------------

        private static async Task ForEachConcurrentAsync<T>(IReadOnlyList<T> items, int maxConcurrency, CancellationToken ct, Func<T, Task> action)
        {
            maxConcurrency = Math.Clamp(maxConcurrency, 1, 50);
            using var sem = new SemaphoreSlim(maxConcurrency);

            var tasks = items.Select(async item =>
            {
                await sem.WaitAsync(ct);
                try { await action(item); }
                finally { sem.Release(); }
            });

            await Task.WhenAll(tasks);
        }
    }

    internal static class LinqCompat
    {
        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IEqualityComparer<TKey>? comparer = null)
        {
            comparer ??= EqualityComparer<TKey>.Default;
            var seen = new HashSet<TKey>(comparer);
            foreach (var element in source)
            {
                if (seen.Add(keySelector(element)))
                    yield return element;
            }
        }
    }
}
