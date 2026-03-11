using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public enum PageSpeedStrategy { Mobile, Desktop }

    public sealed class PageSpeedOptions
    {
        public string? ApiKey { get; set; }
        public bool HostRootOnly { get; set; } = true;
        public int MaxPagesPerHostPerStrategy { get; set; } = 20;

        public int MaxConcurrentRequests { get; set; } = 2;

        public int TimeoutSeconds { get; set; } = 60;

        public int ScoreWarnBelow { get; set; } = 50;
        public int ScoreInfoBelow { get; set; } = 90;

        public int MaxRetries { get; set; } = 3;
        public int RetryBaseDelayMs { get; set; } = 1500;
    }

    public sealed record PageSpeedResult(
        string Url,
        PageSpeedStrategy Strategy,
        bool Success,
        int? PerformanceScore,      
        double? FcpMs,
        double? LcpMs,
        double? SpeedIndexMs,
        double? TbtMs,
        double? Cls,
        double? InpMs,
        string? FinalUrl,
        DateTimeOffset? FetchTimeUtc,
        string? Error
    );

    public sealed record PageSpeedAuditReport(
        string Host,
        PageSpeedStrategy Strategy,
        int Tested,
        int SuccessCount,
        int FailCount,
        double? AvgScore,
        PageSpeedResult[] WorstByScore,
        PageSpeedResult[] All
    );


    public sealed class PageSpeedStore
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<PageSpeedStrategy, ConcurrentDictionary<string, PageSpeedResult>>> _data
            = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<PageSpeedStrategy, int>> _counts
            = new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host)
        {
            _data.TryRemove(host, out _);
            _counts.TryRemove(host, out _);
        }
        public bool HasAny(string host, PageSpeedStrategy strategy)
        {
            if (!_data.TryGetValue(host, out var perHost)) return false;
            if (!perHost.TryGetValue(strategy, out var perStrat)) return false;
            return !perStrat.IsEmpty;
        }


        public bool TryReserveSlot(string host, PageSpeedStrategy strategy, int limit)
        {
            var perHost = _counts.GetOrAdd(host, _ => new ConcurrentDictionary<PageSpeedStrategy, int>());
            var newVal = perHost.AddOrUpdate(strategy, 1, (_, v) => v + 1);
            if (newVal <= limit) return true;

            perHost.AddOrUpdate(strategy, 0, (_, v) => Math.Max(0, v - 1));
            return false;
        }

        public bool Contains(string host, PageSpeedStrategy strategy, string url)
        {
            if (!_data.TryGetValue(host, out var perHost)) return false;
            if (!perHost.TryGetValue(strategy, out var perStrat)) return false;
            return perStrat.ContainsKey(url);
        }

        public void Put(string host, PageSpeedStrategy strategy, PageSpeedResult result)
        {
            var perHost = _data.GetOrAdd(host, _ => new ConcurrentDictionary<PageSpeedStrategy, ConcurrentDictionary<string, PageSpeedResult>>());
            var perStrat = perHost.GetOrAdd(strategy, _ => new ConcurrentDictionary<string, PageSpeedResult>(StringComparer.OrdinalIgnoreCase));
            perStrat[result.Url] = result;
        }

        public PageSpeedAuditReport Build(string host, PageSpeedStrategy strategy)
        {
            var all = _data.TryGetValue(host, out var perHost) &&
                      perHost.TryGetValue(strategy, out var perStrat)
                ? perStrat.Values.ToArray()
                : Array.Empty<PageSpeedResult>();

            var tested = all.Length;
            var success = all.Count(x => x.Success);
            var fail = tested - success;

            double? avg = null;
            var scores = all.Where(x => x.Success && x.PerformanceScore.HasValue).Select(x => x.PerformanceScore!.Value).ToArray();
            if (scores.Length > 0) avg = scores.Average();

            var worst = all
                .Where(x => x.Success && x.PerformanceScore.HasValue)
                .OrderBy(x => x.PerformanceScore!.Value)
                .Take(50)
                .ToArray();

            return new PageSpeedAuditReport(host, strategy, tested, success, fail, avg, worst, all);
        }
    }

    public sealed class PageSpeedClient
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly PageSpeedOptions _opt;
        private readonly SemaphoreSlim _sem;

        public PageSpeedClient(IHttpClientFactory httpFactory, PageSpeedOptions opt)
        {
            _httpFactory = httpFactory;
            _opt = opt;
            _sem = new SemaphoreSlim(Math.Max(1, opt.MaxConcurrentRequests));
        }

        public async Task<PageSpeedResult> RunAsync(string url, PageSpeedStrategy strategy, CancellationToken ct)
        {
            await _sem.WaitAsync(ct);
            try
            {
                var http = _httpFactory.CreateClient("pagespeed");
                http.Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds);

                var strat = strategy == PageSpeedStrategy.Mobile ? "mobile" : "desktop";

      
                var qs = $"runPagespeed?url={Uri.EscapeDataString(url)}&strategy={strat}&category=performance";
                //if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
                //    qs += $"&key={Uri.EscapeDataString(_opt.ApiKey)}";
                //else qs += $"&key={Environment.GetEnvironmentVariable("PAGESPEED_API_KEY_")}";
                var key = !string.IsNullOrWhiteSpace(_opt.ApiKey)? _opt.ApiKey : (Environment.GetEnvironmentVariable("PAGESPEED_API_KEY_") ?? Environment.GetEnvironmentVariable("GOOGLE_PAGESPEED_API_KEY"));
                //
                var envKey = Environment.GetEnvironmentVariable("PAGESPEED_API_KEY");
                Console.WriteLine($"[PSI] optKey={(string.IsNullOrWhiteSpace(_opt.ApiKey) ? "EMPTY" : "SET")}, envKey={(string.IsNullOrWhiteSpace(envKey) ? "EMPTY" : "SET")}");
                key ??= envKey;
                //
                if (!string.IsNullOrWhiteSpace(key))
                    qs += $"&key={Uri.EscapeDataString(key)}";
                else qs += $"&key=AIzaSyBw6emtAfbWkLXY0FUsN_PiPMFbBCnJbAw";

                for (int attempt = 0; attempt <= _opt.MaxRetries; attempt++)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, qs);
                        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                        if (resp.StatusCode == (HttpStatusCode)429 || (int)resp.StatusCode >= 500)
                        {
                            if (attempt == _opt.MaxRetries)
                                return Fail(url, strategy, $"PSI_HTTP_{(int)resp.StatusCode}");

                            await DelayBackoff(attempt, ct);
                            continue;
                        }

                        var body = await resp.Content.ReadAsStringAsync(ct);
                        if (!resp.IsSuccessStatusCode)
                            return Fail(url, strategy, $"PSI_HTTP_{(int)resp.StatusCode}: {Trim(body, 300)}");

                        return Parse(url, strategy, body);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (attempt == _opt.MaxRetries)
                            return Fail(url, strategy, ex.Message);

                        await DelayBackoff(attempt, ct);
                    }
                }

                return Fail(url, strategy, "PSI_UNKNOWN");
            }
            finally
            {
                _sem.Release();
            }
        }

        private async Task DelayBackoff(int attempt, CancellationToken ct)
        {
            var delay = _opt.RetryBaseDelayMs * Math.Pow(2, attempt);
          
            var jitter = Random.Shared.Next(0, 350);
            await Task.Delay(TimeSpan.FromMilliseconds(delay + jitter), ct);
        }

        private static PageSpeedResult Fail(string url, PageSpeedStrategy strategy, string error) =>
            new(url, strategy, false, null, null, null, null, null, null, null, null, null, error);

        private static PageSpeedResult Parse(string url, PageSpeedStrategy strategy, string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

           
            string? finalUrl = TryGetString(root, "lighthouseResult", "finalUrl");
            var fetchTime = TryGetString(root, "lighthouseResult", "fetchTime");
            DateTimeOffset? fetchUtc = null;
            if (DateTimeOffset.TryParse(fetchTime, out var ft)) fetchUtc = ft.ToUniversalTime();

            
            int? score = null;
            var scoreEl = TryGetElement(root, "lighthouseResult", "categories", "performance", "score");
            if (scoreEl.HasValue && scoreEl.Value.ValueKind == JsonValueKind.Number)
            {
                var s = scoreEl.Value.GetDouble();
                score = (int)Math.Round(s * 100.0, MidpointRounding.AwayFromZero);
            }

           
            double? fcp = GetAuditNumeric(root, "first-contentful-paint");
            double? lcp = GetAuditNumeric(root, "largest-contentful-paint");
            double? si = GetAuditNumeric(root, "speed-index");
            double? tbt = GetAuditNumeric(root, "total-blocking-time");
            double? cls = GetAuditNumeric(root, "cumulative-layout-shift");
            double? inp = GetAuditNumeric(root, "interaction-to-next-paint");

            return new PageSpeedResult(
                Url: url,
                Strategy: strategy,
                Success: true,
                PerformanceScore: score,
                FcpMs: fcp,
                LcpMs: lcp,
                SpeedIndexMs: si,
                TbtMs: tbt,
                Cls: cls,
                InpMs: inp,
                FinalUrl: finalUrl,
                FetchTimeUtc: fetchUtc,
                Error: null
            );

            static double? GetAuditNumeric(JsonElement root, string auditId)
            {
                var el = TryGetElement(root, "lighthouseResult", "audits", auditId, "numericValue");
                if (!el.HasValue) return null;
                if (el.Value.ValueKind != JsonValueKind.Number) return null;
                return el.Value.GetDouble();
            }

            static JsonElement? TryGetElement(JsonElement root, params string[] path)
            {
                JsonElement cur = root;
                foreach (var p in path)
                {
                    if (cur.ValueKind != JsonValueKind.Object) return null;
                    if (!cur.TryGetProperty(p, out var next)) return null;
                    cur = next;
                }
                return cur;
            }

            static string? TryGetString(JsonElement root, params string[] path)
            {
                var el = TryGetElement(root, path);
                if (!el.HasValue) return null;
                if (el.Value.ValueKind != JsonValueKind.String) return null;
                return el.Value.GetString();
            }
        }

        private static string Trim(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }


    public abstract class PageSpeedCheckBase : ILinkCheck
    {
        private readonly PageSpeedClient _client;
        private readonly PageSpeedStore _store;
        private readonly PageSpeedOptions _opt;
        private readonly PageSpeedStrategy _strategy;

        protected PageSpeedCheckBase(PageSpeedClient client, PageSpeedStore store, PageSpeedOptions opt, PageSpeedStrategy strategy)
        {
            _client = client;
            _store = store;
            _opt = opt;
            _strategy = strategy;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
 
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400)
                return Array.Empty<LinkIssue>();

            if (string.IsNullOrWhiteSpace(ctx.ContentType) || !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<LinkIssue>();

            var host = ctx.FinalUri.Host;

            // по умолчанию — текущая страница
            var targetUrl = ctx.FinalUrl;

            // если включен HostRootOnly — проверяем только корень хоста (1 раз)
            if (_opt.HostRootOnly)
            {
                // если уже есть результат по этому host+strategy — выходим
                if (_store.HasAny(host, _strategy))
                    return Array.Empty<LinkIssue>();

                // всегда бьём корень (с финальной схемой https/http)
                targetUrl = ctx.FinalUri.GetLeftPart(UriPartial.Authority) + "/";
            }

            // если уже есть именно по этому URL (на всякий)
            if (_store.Contains(host, _strategy, targetUrl))
                return Array.Empty<LinkIssue>();

            // лимит запросов (для HostRootOnly = 1)
            var limit = _opt.HostRootOnly ? 1 : _opt.MaxPagesPerHostPerStrategy;
            if (!_store.TryReserveSlot(host, _strategy, limit))
                return Array.Empty<LinkIssue>();

            var res = await _client.RunAsync(targetUrl, _strategy, ct);
            _store.Put(host, _strategy, res);

            var issues = new List<LinkIssue>();

            if (!res.Success)
            {
                issues.Add(new LinkIssue(
                    Code(_strategy, "PSI_FAILED"),
                    $"{Label(_strategy)}: PageSpeed API не вернул результат ({res.Error}).",
                    IssueSeverity.Warning));
                return issues;
            }

            if (res.PerformanceScore is int sc)
            {
                if (sc < _opt.ScoreWarnBelow)
                {
                    issues.Add(new LinkIssue(
                        Code(_strategy, "PSI_SCORE_LOW"),
                        $"{Label(_strategy)}: низкий Performance score = {sc}/100.",
                        IssueSeverity.Warning));
                }
                else if (sc < _opt.ScoreInfoBelow)
                {
                    issues.Add(new LinkIssue(
                        Code(_strategy, "PSI_SCORE_NEEDS_IMPROVEMENT"),
                        $"{Label(_strategy)}: Performance score = {sc}/100 (можно улучшить).",
                        IssueSeverity.Info));
                }
            }

            return issues;
        }

        private static string Label(PageSpeedStrategy s) => s == PageSpeedStrategy.Mobile ? "Mobile PSI" : "Desktop PSI";
        private static string Code(PageSpeedStrategy s, string tail) => (s == PageSpeedStrategy.Mobile ? "MOBILE_" : "DESKTOP_") + tail;
    }

    public sealed class PageSpeedMobileCheck : PageSpeedCheckBase
    {
        public PageSpeedMobileCheck(PageSpeedClient client, PageSpeedStore store, PageSpeedOptions opt)
            : base(client, store, opt, PageSpeedStrategy.Mobile) { }
    }

    public sealed class PageSpeedDesktopCheck : PageSpeedCheckBase
    {
        public PageSpeedDesktopCheck(PageSpeedClient client, PageSpeedStore store, PageSpeedOptions opt)
            : base(client, store, opt, PageSpeedStrategy.Desktop) { }
    }
}

