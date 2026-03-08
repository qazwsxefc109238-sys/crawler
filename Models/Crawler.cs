using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HtmlAgilityPack;
using static Crawler_project.Models.DTO;
using Crawler_project.Checks;
using Crawler_project.Services;
namespace Crawler_project.Models
{


    public enum CrawlState { Pending, Running, Finished, Canceled, Faulted } //статусы задачи
    public enum CheckState { None, Running, Finished, Canceled, Faulted }

    public sealed class CrawlJob //класс с параметрами нашего кравлера
    {
        public Guid JobId { get; init; }
        public string StartUrl { get; init; } = "";
        public int MaxPages { get; init; }
        public int Workers { get; init; }
        public bool RespectRobots { get; init; }

        public int DiscoveredCount; 
        public CrawlState State { get; set; } = CrawlState.Pending;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string? Error { get; set; }

        public ConcurrentDictionary<string, byte> Discovered { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<string> DiscoveredOrder { get; } = new();
        public ConcurrentDictionary<string, byte> Visited { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int VisitedCount;
        public CancellationTokenSource Cts { get; } = new();
        public Task? RunTask { get; set; }


   

        public CheckState ChecksState { get; set; } = CheckState.None;
        public DateTimeOffset? ChecksStartedAt { get; set; }
        public DateTimeOffset? ChecksFinishedAt { get; set; }
        public string? ChecksError { get; set; }

        public ConcurrentDictionary<string, LinkCheckResult> CheckResults { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task? ChecksTask { get; set; }



    }

    public interface JobStore
    {
        CrawlJob CreateAndStart(CrawlStartRequest req);
        CrawlJob? Get(Guid jobId);
        void Cancel(Guid jobId);
        string[] GetUrls(Guid jobId, int offset, int limit, out int total);
        void StartChecks(Guid jobId);
        LinkCheckResult[] GetCheckResults(Guid jobId, int offset, int limit, out int total);
    }

    public sealed class InMemJobStore : JobStore
    {
        private readonly ConcurrentDictionary<Guid, CrawlJob> _jobs = new();

        private readonly Crawler _crawler = new(); 
        private readonly IHttpClientFactory _httpClientFactory;



        private readonly LinkVerifier _verifier;
        private readonly SeoDuplicatesStore _dup;
        private readonly ContentQualityStore _content;
        private readonly SiteImagesStore _images;
        private readonly InternalLinkGraphStore _graph;
        private readonly CanonicalSummaryStore _canonical;
        private readonly NoindexStore _noindex;
        private readonly ImagesMetaStore _imagesMeta;
        private readonly OutgoingLinksStore _outgoing;
        private readonly PerformanceStore _perf;
        private readonly ContentLengthUniformityStore _len;
        private readonly PageSpeedStore _psi;
        public InMemJobStore(
            IHttpClientFactory httpClientFactory,
            LinkVerifier verifier,
            SeoDuplicatesStore dup,
            ContentQualityStore content,
            SiteImagesStore images,
            InternalLinkGraphStore graph,
            CanonicalSummaryStore canonical,
            NoindexStore noindex,
            ImagesMetaStore imagesMeta,
            OutgoingLinksStore outgoing,
            PerformanceStore perf,
            ContentLengthUniformityStore len,
            PageSpeedStore psi)
        {
            _httpClientFactory = httpClientFactory;
            _verifier = verifier;

            _dup = dup;
            _content = content;
            _images = images;
            _graph = graph;
            _canonical = canonical;
            _noindex = noindex;
            _imagesMeta = imagesMeta;
            _outgoing = outgoing;
            _perf = perf;
            _len = len;
            _psi = psi;
        }

        public void StartChecks(Guid jobId)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return;
            // idempotent: если проверки уже идут/сделаны — ничего не сбрасываем
            if (job.ChecksTask is not null) return;

            // reset всех агрегирующих стораджей под host
            var host = new Uri(job.StartUrl).Host;
            _psi.ResetHost(host);

            _dup.ResetHost(host);
            _content.ResetHost(host);
            _images.ResetHost(host);
            _graph.ResetHost(host);
            _canonical.ResetHost(host);
            _noindex.ResetHost(host);
            _imagesMeta.ResetHost(host);
            _outgoing.ResetHost(host);
            _perf.ResetHost(host);
            _len.ResetHost(host);

            // Важно: запускаем проверки только когда сбор URL завершён
            if (job.State != CrawlState.Finished && job.State != CrawlState.Canceled)
                throw new InvalidOperationException("Нельзя запускать проверки, пока краул не завершён.");

            

            job.ChecksState = CheckState.Running;
            job.ChecksStartedAt = DateTimeOffset.Now;

            var urls = job.DiscoveredOrder.ToArray();

            job.ChecksTask = Task.Run(async () =>
            {
                try
                {
                    await _verifier.VerifyAllAsync(
                        urls: urls,
                        results: job.CheckResults,
                        degreeOfParallelism: job.Workers,
                        ct: job.Cts.Token);

                    job.ChecksState = job.Cts.IsCancellationRequested ? CheckState.Canceled : CheckState.Finished;
                    job.ChecksFinishedAt = DateTimeOffset.Now;
                }
                catch (OperationCanceledException)
                {
                    job.ChecksState = CheckState.Canceled;
                    job.ChecksFinishedAt = DateTimeOffset.Now;
                }
                catch (Exception ex)
                {
                    job.ChecksState = CheckState.Faulted;
                    job.ChecksError = ex.Message;
                    job.ChecksFinishedAt = DateTimeOffset.Now;
                }
            });
        }

        public LinkCheckResult[] GetCheckResults(Guid jobId, int offset, int limit, out int total)
        {
            total = 0;
            if (!_jobs.TryGetValue(jobId, out var job)) return Array.Empty<LinkCheckResult>();

            var arr = job.DiscoveredOrder.ToArray(); // порядок URL
            total = arr.Length;

            offset = Math.Max(0, offset);
            limit = Math.Clamp(limit, 1, 5000);

            var slice = arr.Skip(offset).Take(limit);

            // Возвращаем результаты в том же порядке, если результата нет — пустой объект
            return slice.Select(u =>
                job.CheckResults.TryGetValue(u, out var r)
                    ? r
                    : new LinkCheckResult(
                        Url: u,
                        FinalUrl: u,
                        StatusCode: 0,
                        ContentType: null,
                        ContentLength: null,
                        LoadSeconds: 0,
                        Redirects: Array.Empty<RedirectHop>(),
                        Issues: Array.Empty<LinkIssue>(),
                        Error: "NOT_CHECKED_YET"
                    )
            ).ToArray();
        }

        public CrawlJob CreateAndStart(CrawlStartRequest req)
        {
            var job = new CrawlJob
            {
                JobId = Guid.NewGuid(),
                StartUrl = req.StartUrl,
                MaxPages = req.MaxPages,
                Workers = req.Workers,
                RespectRobots = req.RespectRobots
            };

            _jobs[job.JobId] = job;

            job.RunTask = Task.Run(() => RunJobAsync(job));
            return job;
        }

        public CrawlJob? Get(Guid jobId) => _jobs.TryGetValue(jobId, out var j) ? j : null;

        public void Cancel(Guid jobId)
        {
            if (_jobs.TryGetValue(jobId, out var job))
                job.Cts.Cancel();
        }

        public string[] GetUrls(Guid jobId, int offset, int limit, out int total)
        {
            total = 0;
            if (!_jobs.TryGetValue(jobId, out var job)) return Array.Empty<string>();
            var arr = job.DiscoveredOrder.ToArray();
            total = arr.Length;

            offset = Math.Max(0, offset);
            limit = Math.Clamp(limit, 1, 5000);
            return arr.Skip(offset).Take(limit).ToArray();
        }

        private async Task RunJobAsync(CrawlJob job)
        {
            job.State = CrawlState.Running;
            job.StartedAt = DateTimeOffset.Now;

            try
            {
                var http = _httpClientFactory.CreateClient("crawler");
                try
                {
                    var startUri = new Uri(job.StartUrl);
                    var warmup = startUri.GetLeftPart(UriPartial.Authority) + "/";
                    using var warmReq = new HttpRequestMessage(HttpMethod.Get, warmup);
                    using var warmResp = await http.SendAsync(warmReq, HttpCompletionOption.ResponseHeadersRead, job.Cts.Token);
                }
                catch { /* игнор */ }
                RobotsRules? robots = null;
                if (job.RespectRobots)
                    robots = await RobotsRules.FetchAsync(new Uri(job.StartUrl), "MyCrawler", http);

                var start = _crawler.Normalize(job.StartUrl);
                if (start is null) throw new Exception("Cтартовый url не распознан");

                var channel = Channel.CreateUnbounded<string>();
                int pending = 0;
                int reachedUniqueLimit = 0;

                var maxUnique = job.MaxPages <= 0 ? int.MaxValue : job.MaxPages;

                void Enqueue(string url)
                {
                    if (Volatile.Read(ref reachedUniqueLimit) == 1 && job.DiscoveredCount >= maxUnique)
                        return;

                    if (job.Discovered.TryAdd(url, 0))
                    {
                        var newCount = Interlocked.Increment(ref job.DiscoveredCount);

                        // Если перелетели лимит — откатываем добавление
                        if (newCount > maxUnique)
                        {
                            job.Discovered.TryRemove(url, out _);
                            Interlocked.Decrement(ref job.DiscoveredCount);
                            return;
                        }


                        job.DiscoveredOrder.Enqueue(url);
                        Interlocked.Increment(ref pending);
                        channel.Writer.TryWrite(url);

                        // Ровно достигли лимит — больше новые URL не принимаем
                        if (newCount == maxUnique)
                            Interlocked.Exchange(ref reachedUniqueLimit, 1);
                    }
                }

                Enqueue(start);

                var cts = job.Cts;
                var workers = Math.Max(1, job.Workers);

                Task[] tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
                {
                    while (await channel.Reader.WaitToReadAsync(cts.Token))
                    {
                        while (channel.Reader.TryRead(out var url))
                        {
                            try
                            {
                                if (!job.Visited.TryAdd(url, 0))
                                    continue;

                                // лимит страниц: НЕ cancel токен, а просто перестаём реально парсить
                                Interlocked.Increment(ref job.VisitedCount);

                                var links = await _crawler.ParseRun(url, http, cts.Token);

                                foreach (var link in links)
                                {
                                    var norm = _crawler.Normalize(link);
                                    if (norm is null) continue;

                                    if (!Uri.TryCreate(norm, UriKind.Absolute, out var u)) continue;
                                    if (robots != null && !robots.IsAllowed(u)) continue;

                                    // добавляем только внутренние (у тебя уже фильтр по host внутри ParseRun, но пусть будет)
                                    Enqueue(norm);
                                }
                            }
                            finally
                            {
                                // закрываем канал, когда больше нечего делать
                                if (Interlocked.Decrement(ref pending) == 0)
                                    channel.Writer.TryComplete();
                            }
                        }
                    }
                }, cts.Token)).ToArray();

                await Task.WhenAll(tasks);

                // статус
                job.State = cts.IsCancellationRequested ? CrawlState.Canceled : CrawlState.Finished;
                job.FinishedAt = DateTimeOffset.Now;

                if (job.State == CrawlState.Finished && Volatile.Read(ref reachedUniqueLimit) == 1 && maxUnique != int.MaxValue)
                    job.Error = "MAX_UNIQUE_PAGES_LIMIT_REACHED";

                // Автопереход к этапу проверок
                if (job.State == CrawlState.Finished)
                {
                    // безопасно: StartChecks сам idempotent, а также сам сделает reset стораджей (см. правки выше)
                    StartChecks(job.JobId);
                }

            }
            catch (OperationCanceledException)
            {
                job.State = CrawlState.Canceled;
                job.FinishedAt = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                job.State = CrawlState.Faulted;
                job.Error = ex.Message;
                job.FinishedAt = DateTimeOffset.Now;
            }
        }

    }

    class Crawler //Тут все шаблоны, фильтры и нормализация ссылок
    {
        public int MaxPages { get; set; } = 1000;

        public async Task<string[]> ParseRun(string url, HttpClient http, CancellationToken ct)
        {
            var normalized = Normalize(url);
            if (normalized is null) return Array.Empty<string>();

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, normalized);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                // если режут (403/503) — просто не вытаскиваем ссылки с этой страницы
                if (!resp.IsSuccessStatusCode)
                    return Array.Empty<string>();

                var ctHeader = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!ctHeader.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return Array.Empty<string>();

                var html = await resp.Content.ReadAsStringAsync(ct);

                // защита от очень больших HTML
                if (html.Length > 1_500_000)
                    html = html.Substring(0, 1_500_000);

                var doc = new HtmlDocument
                {
                    OptionFixNestedTags = true,
                    OptionCheckSyntax = false
                };
                doc.LoadHtml(html);

                var baseUri = new Uri(normalized);

                var links = doc.DocumentNode
                    .SelectNodes("//a[@href]")
                    ?.Select(a => a.GetAttributeValue("href", "").Trim())
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Where(h => !h.StartsWith("#"))
                    .Where(h => !h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    .Where(h => !h.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    .Where(h => !h.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                    .Select(h => Uri.TryCreate(baseUri, h, out var abs) ? abs : null)
                    .Where(u => u is not null)
                    .Where(u => u!.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
                    .Where(u => string.IsNullOrEmpty(u!.Query))
                    .Select(u => u!.AbsoluteUri)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                    ?? Array.Empty<string>();

                return links;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"__Error__ {normalized} --> {ex.Message}");
                return Array.Empty<string>();
            }
        }



        public string? Normalize(string input) //ссылки могут быть разными, нам нужно нормализовать, тобишь добавить https:// если нет, убрать порты, и т.д.
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();
            if (!input.Contains("://", StringComparison.Ordinal))
                input = "https://" + input;

            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
                return null;

            var b = new UriBuilder(uri);
            b.Host = b.Host.ToLowerInvariant();
            b.Fragment = "";

            if ((b.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && b.Port == 443) ||
                (b.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port == 80))
            {
                b.Port = -1;
            }

            if (string.IsNullOrEmpty(b.Path))
                b.Path = "/";

            if (b.Path.Length > 1 && b.Path.EndsWith("/"))
                b.Path = b.Path.TrimEnd('/');

            return b.Uri.AbsoluteUri;
        }
    }

}
