using Crawler_project.Checks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Models.LinkChecks
{
    /// <summary>
    /// Ресурсы:
    /// - битые ссылки (a[href])
    /// - потерянные JS (script[src])
    /// - потерянные CSS (link rel~stylesheet[href])
    /// - потерянные изображения (img[src])
    /// 
    /// Лимиты + кэш статусов, чтобы не взорвать количество запросов.
    /// </summary>
    public sealed class ResourcesIntegrityChecks : ILinkCheck
    {
        private readonly ResourceIntegrityAuditService _svc;
        private readonly ResourceIntegrityOptions _opt;

        public ResourcesIntegrityChecks(ResourceIntegrityAuditService svc, ResourceIntegrityOptions opt)
        {
            _svc = svc;
            _opt = opt;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300) return Array.Empty<LinkIssue>();
            if (string.IsNullOrWhiteSpace(ctx.ContentType) || !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<LinkIssue>();


            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return Array.Empty<LinkIssue>();

            if (ctx.Document is null)
                return Array.Empty<LinkIssue>();

            if (string.IsNullOrWhiteSpace(ctx.ContentType) ||
                !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<LinkIssue>();
            var issues = new List<LinkIssue>();

            // 1) Собираем URL-ы
            var pageUri = ctx.FinalUri;

            var links = ExtractAnchors(ctx, pageUri)
                .Take(_opt.MaxLinksPerPage)
                .ToList();

            var js = ExtractJs(ctx, pageUri)
                .Take(_opt.MaxJsPerPage)
                .ToList();

            var css = ExtractCss(ctx, pageUri)
                .Take(_opt.MaxCssPerPage)
                .ToList();

            var imgs = ExtractImages(ctx, pageUri)
                .Take(_opt.MaxImagesPerPage)
                .ToList();

            // 2) Проверяем
            if (links.Count > 0)
            {
                var broken = await _svc.FindBrokenAsync(links, _opt.MaxConcurrentRequestsPerPage, ct);
                if (broken.Count > 0)
                {
                    issues.Add(new LinkIssue(
                        "BROKEN_LINKS",
                        $"Битые ссылки: {broken.Count} из {links.Count}. Примеры: {FormatSamples(broken, _opt.SampleUrlsInIssue)}",
                        IssueSeverity.Warning));
                }
            }

            if (js.Count > 0)
            {
                var missing = await _svc.FindBrokenAsync(js, _opt.MaxConcurrentRequestsPerPage, ct);
                if (missing.Count > 0)
                {
                    issues.Add(new LinkIssue(
                        "MISSING_JS",
                        $"Потерянные JS: {missing.Count} из {js.Count}. Примеры: {FormatSamples(missing, _opt.SampleUrlsInIssue)}",
                        IssueSeverity.Error));
                }
            }

            if (css.Count > 0)
            {
                var missing = await _svc.FindBrokenAsync(css, _opt.MaxConcurrentRequestsPerPage, ct);
                if (missing.Count > 0)
                {
                    issues.Add(new LinkIssue(
                        "MISSING_CSS",
                        $"Потерянные CSS: {missing.Count} из {css.Count}. Примеры: {FormatSamples(missing, _opt.SampleUrlsInIssue)}",
                        IssueSeverity.Error));
                }
            }

            if (imgs.Count > 0)
            {
                var missing = await _svc.FindBrokenAsync(imgs, _opt.MaxConcurrentRequestsPerPage, ct);
                if (missing.Count > 0)
                {
                    issues.Add(new LinkIssue(
                        "MISSING_IMAGES",
                        $"Потерянные изображения: {missing.Count} из {imgs.Count}. Примеры: {FormatSamples(missing, _opt.SampleUrlsInIssue)}",
                        IssueSeverity.Error));
                }
            }

            return issues;
        }

        // -------------------- extractors --------------------

        private IEnumerable<Uri> ExtractAnchors(LinkCheckContext ctx, Uri pageUri)
        {
            var nodes = ctx.Document!.DocumentNode.SelectNodes("//a[@href]");
            if (nodes is null) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var n in nodes)
            {
                var raw = (n.GetAttributeValue("href", "") ?? "").Trim();
                if (!IsCheckableUrl(raw)) continue;

                if (!TryResolve(pageUri, raw, out var u)) continue;

                // по умолчанию проверяем и внутренние, и внешние (с лимитом).
                // если нужно только внутренние — включите опцию.
                if (_opt.OnlyInternalLinks && !u.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (seen.Add(u.AbsoluteUri))
                    yield return u;
            }
        }

        private IEnumerable<Uri> ExtractJs(LinkCheckContext ctx, Uri pageUri)
        {
            var nodes = ctx.Document!.DocumentNode.SelectNodes("//script[@src]");
            if (nodes is null) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var n in nodes)
            {
                var raw = (n.GetAttributeValue("src", "") ?? "").Trim();
                if (!IsCheckableUrl(raw)) continue;

                if (!TryResolve(pageUri, raw, out var u)) continue;
                if (seen.Add(u.AbsoluteUri))
                    yield return u;
            }
        }

        private IEnumerable<Uri> ExtractCss(LinkCheckContext ctx, Uri pageUri)
        {
            // rel может быть "stylesheet preload" и т.п.
            var nodes = ctx.Document!.DocumentNode.SelectNodes(
                "//link[@href and contains(translate(@rel,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'stylesheet')]");

            if (nodes is null) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var n in nodes)
            {
                var raw = (n.GetAttributeValue("href", "") ?? "").Trim();
                if (!IsCheckableUrl(raw)) continue;

                if (!TryResolve(pageUri, raw, out var u)) continue;
                if (seen.Add(u.AbsoluteUri))
                    yield return u;
            }
        }

        private IEnumerable<Uri> ExtractImages(LinkCheckContext ctx, Uri pageUri)
        {
            var nodes = ctx.Document!.DocumentNode.SelectNodes("//img[@src]");
            if (nodes is null) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var n in nodes)
            {
                var raw = (n.GetAttributeValue("src", "") ?? "").Trim();
                if (!IsCheckableUrl(raw)) continue;

                if (!TryResolve(pageUri, raw, out var u)) continue;
                if (seen.Add(u.AbsoluteUri))
                    yield return u;
            }
        }

        private static bool IsCheckableUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;

            // якоря, js, почта/телефон, data-uri не проверяем
            if (raw.StartsWith("#")) return false;
            if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("tg:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("viber:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("skype:", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw.StartsWith("intent:", StringComparison.OrdinalIgnoreCase)) return false;


            return true;
        }

        private static bool TryResolve(Uri baseUri, string raw, out Uri uri)
        {
            uri = default!;

            // protocol-relative: //cdn.site.com/file.js
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

        private static string FormatSamples(IReadOnlyList<ResourceProbeResult> broken, int sample)
        {
            return string.Join(" | ", broken
                .Take(Math.Clamp(sample, 1, 20))
                .Select(b => $"{b.Url} -> {(b.StatusCode == 0 ? "ERR" : b.StatusCode.ToString())}"));
        }
    }

    // -------------------- options + service --------------------

    public sealed class ResourceIntegrityOptions
    {
        // Ограничения на объём проверок на одну страницу
        public int MaxLinksPerPage { get; set; } = 30;
        public int MaxJsPerPage { get; set; } = 30;
        public int MaxCssPerPage { get; set; } = 30;
        public int MaxImagesPerPage { get; set; } = 30;

        // Сколько одновременных запросов делать на одну страницу
        public int MaxConcurrentRequestsPerPage { get; set; } = 10;

        // Для выдачи в одном issue (примеров битых)
        public int SampleUrlsInIssue { get; set; } = 5;

        // Если нужно проверять только внутренние ссылки (a[href])
        public bool OnlyInternalLinks { get; set; } = false;

        // Использовать HEAD перед GET
        public bool PreferHead { get; set; } = true;

        // Если HEAD вернул 405/501 — делаем GET
        public bool FallbackToGet { get; set; } = true;
    }

    public sealed record ResourceProbeResult(string Url, int StatusCode, string? Error);

    public sealed class ResourceIntegrityAuditService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly ResourceIntegrityOptions _opt;

        // Кэш статуса по URL, чтобы не дергать одинаковые ресурсы на многих страницах
        private readonly ConcurrentDictionary<string, Lazy<Task<ResourceProbeResult>>> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public ResourceIntegrityAuditService(IHttpClientFactory httpFactory, ResourceIntegrityOptions opt)
        {
            _httpFactory = httpFactory;
            _opt = opt;
        }

        public async Task<List<ResourceProbeResult>> FindBrokenAsync(
            IReadOnlyList<Uri> uris,
            int maxConcurrency,
            CancellationToken ct)
        {
            var broken = new ConcurrentBag<ResourceProbeResult>();
            using var sem = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 50));

            var tasks = uris.Select(async u =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var r = await ProbeAsync(u, ct);
                    if (IsBroken(r)) broken.Add(r);
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);
            return broken.ToList();
        }

        private bool IsBroken(ResourceProbeResult r)
        {
            if (r.StatusCode == 0) return false; // сетевые/таймаут — не “потеряно”
            if (r.StatusCode == 404 || r.StatusCode == 410) return true;
            return false; // 401/403/429/5xx — это “недоступно сейчас”, но не факт что потеряно
        }


        private static bool IsTransient(int status) => status == 0 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504;

        public Task<ResourceProbeResult> ProbeAsync(Uri uri, CancellationToken ct)
        {
            var key = uri.AbsoluteUri;

            var lazy = _cache.GetOrAdd(key, _ =>
                new Lazy<Task<ResourceProbeResult>>(() => ProbeCoreAsync(uri, CancellationToken.None)));

            return lazy.Value.WaitAsync(ct);
        }


        private async Task<ResourceProbeResult> ProbeCoreAsync(Uri uri, CancellationToken ct)
        {
            var http = _httpFactory.CreateClient("crawler"); 

            try
            {
                if (_opt.PreferHead)
                {
                    var head = await SendAsync(http, HttpMethod.Head, uri, ct);
                    if (head.StatusCode != 0)
                    {

                        if (!_opt.FallbackToGet)
                            return head;

                        // если HEAD не поддержан или нас режут/лимитят — пробуем GET
                        if (head.StatusCode != 405 &&
                            head.StatusCode != 501 &&
                            head.StatusCode != 401 &&
                            head.StatusCode != 403 &&
                            head.StatusCode != 429 &&
                            head.StatusCode < 500)
                        {
                            return head;
                        }

                    }
                }

                var get = await SendAsync(http, HttpMethod.Get, uri, ct);
                return get;
            }
            catch (Exception ex)
            {
                return new ResourceProbeResult(uri.AbsoluteUri, 0, ex.Message);
            }
        }

        private static async Task<ResourceProbeResult> SendAsync(HttpClient http, HttpMethod method, Uri uri, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(method, uri);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                return new ResourceProbeResult(uri.AbsoluteUri, (int)resp.StatusCode, null);
            }
            catch (Exception ex)
            {
                return new ResourceProbeResult(uri.AbsoluteUri, 0, ex.Message);
            }
        }
    }
}