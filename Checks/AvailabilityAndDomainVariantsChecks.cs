using Crawler_project.Checks;
using Crawler_project.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    /// <summary>
    /// Безопасность / анализ доступности / доменные варианты:
    /// - доступность (серия запросов к корню сайта)
    /// - тест доменных вариантов (http/https, www/non-www, slash/no-slash)
    /// - единый canonical для вариантов
    /// - стабильность размера документа (повторные GET)
    /// - корректная обработка несуществующего URL (404/410 vs soft-404)
    /// </summary>
    public sealed class AvailabilityAndDomainVariantsChecks : ILinkCheck
    {
        private readonly DomainVariantAuditService _svc;

        public AvailabilityAndDomainVariantsChecks(DomainVariantAuditService svc) => _svc = svc;

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            // 0) Сырой URL без схемы (на случай, если попадёт в список)
            if (!StartsWithHttpScheme(ctx.OriginalUrl))
            {
                issues.Add(new LinkIssue(
                    "URL_NO_SCHEME",
                    $"В URL отсутствует префикс http/https: {ctx.OriginalUrl}",
                    IssueSeverity.Warning));
            }

            // 1) Host-level аудит доменных вариантов + доступность + fake 404
            var hostAudit = await _svc.GetHostAuditAsync(ctx.FinalUri, ct);
            issues.AddRange(hostAudit.ToIssues());

            // 2) Per-page: стабильность размера HTML (2-3 запроса) — кэш по URL
            var sizeAudit = await _svc.GetSizeStabilityAsync(ctx.FinalUri, ct);
            issues.AddRange(sizeAudit.ToIssues());

            return issues;
        }

        private static bool StartsWithHttpScheme(string url) =>
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    #region Service + Models

    public sealed class DomainVariantAuditOptions
    {
        public int MaxRedirects { get; set; } = 10;

        // Анализ доступности (пробы к корню сайта)
        public int AvailabilityProbes { get; set; } = 3;
        public int AvailabilityErrorThresholdPercent { get; set; } = 60;  // ниже -> Error
        public int AvailabilityWarnThresholdPercent { get; set; } = 100;  // ниже -> Warning (т.е. не 100%)

        // Стабильность размера документа
        public int SizeStabilityRequests { get; set; } = 2;               // 2 или 3
        public int SizeStabilityToleranceBytes { get; set; } = 2048;      // допуск, чтобы не ловить “шум”

        // Проверка fake URL (404/410)
        public string Fake404PathPrefix { get; set; } = "/__crawler_nonexistent__";
    }

    public sealed class DomainVariantAuditService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly DomainVariantAuditOptions _opt;

        private readonly ConcurrentDictionary<string, Lazy<Task<HostAudit>>> _hostCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<Task<SizeAudit>>> _sizeCache = new(StringComparer.OrdinalIgnoreCase);

        public DomainVariantAuditService(IHttpClientFactory httpFactory, DomainVariantAuditOptions opt)
        {
            _httpFactory = httpFactory;
            _opt = opt;
        }

        public Task<HostAudit> GetHostAuditAsync(Uri anyUriOnHost, CancellationToken ct)
        {
            var host = anyUriOnHost.Host;
            var key = host;

            var lazy = _hostCache.GetOrAdd(key, _ => new Lazy<Task<HostAudit>>(() => AuditHostAsync(host, ct)));
            return lazy.Value;
        }

        public Task<SizeAudit> GetSizeStabilityAsync(Uri pageUrl, CancellationToken ct)
        {
            var key = NormalizeForCache(pageUrl);
            var lazy = _sizeCache.GetOrAdd(key, _ => new Lazy<Task<SizeAudit>>(() => AuditSizeAsync(pageUrl, ct)));
            return lazy.Value;
        }

        private async Task<HostAudit> AuditHostAsync(string host, CancellationToken ct)
        {
            var noredirect = _httpFactory.CreateClient("crawler_noredirect");

            // Варианты домена: base / www (если применимо)
            var hostBase = StripWww(host);
            var hostWww = "www." + hostBase;

            // Собираем варианты URL (корень) для теста написаний
            var candidates = new List<Uri>
            {
                new Uri($"http://{hostBase}/"),
                new Uri($"https://{hostBase}/"),
                new Uri($"http://{hostBase}"),
                new Uri($"https://{hostBase}"),
            };

            // www-ветка (только если текущий host уже не www.* или если хотим всегда проверять)
            candidates.Add(new Uri($"http://{hostWww}/"));
            candidates.Add(new Uri($"https://{hostWww}/"));
            candidates.Add(new Uri($"http://{hostWww}"));
            candidates.Add(new Uri($"https://{hostWww}"));

            // Убираем дубли (на случай совпадений)
            candidates = candidates.DistinctBy(u => u.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToList();

            // 1) Прогон вариантов (получаем финальный URL + статус + цепочку)
            var variantResults = new List<VariantResult>();
            foreach (var u in candidates)
            {
                var r = await FollowAsync(noredirect, u, _opt.MaxRedirects, ct);
                variantResults.Add(r);
            }

            // 2) Выбираем “канонический” результат:
            // приоритет: https base -> https www -> любой успешный
            var canonical = PickCanonical(variantResults);

            // 3) Проверка “все варианты приводят к одному URL”
            var successful = variantResults
                .Where(v => v.Success && v.FinalUri is not null)
                .ToList();

            var distinctCanonKeys = successful
                .Select(v => CanonKey(v.FinalUri!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool variantsConsistent = distinctCanonKeys.Count <= 1;

            // 4) Анализ доступности сайта: серия проб к каноническому корню
            var availability = canonical.FinalUri is null
                ? HostAvailabilityAudit.Failed("NO_CANONICAL")
                : await ProbeAvailabilityAsync(noredirect, canonical.FinalUri, ct);

            // 5) Fake URL должен отдавать 404/410 (или хотя бы не 200/редирект-на-главную)
            var fake404 = canonical.FinalUri is null
                ? Fake404Audit.Failed("NO_CANONICAL")
                : await ProbeFake404Async(noredirect, canonical.FinalUri, ct);

            // 6) Вывод по HTTPS-доступности: если https варианты не дают успех, но http даёт — HTTPS фактически недоступен
            var httpsOk = successful.Any(v => v.OriginalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
            var httpOk = successful.Any(v => v.OriginalUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase));

            // 7) Редирект HTTP->HTTPS (host-level): смотрим любой http-variant, который уходит в https
            bool hasHttpToHttps = successful.Any(v =>
                v.OriginalUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                v.FinalUri is not null &&
                v.FinalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));

            return new HostAudit(
                Host: host,
                CanonicalFinalUrl: canonical.FinalUri?.AbsoluteUri ?? "",
                CanonicalStatus: canonical.FinalStatusCode,
                HttpsAvailable: httpsOk,
                HttpAvailable: httpOk,
                HasHttpToHttpsRedirect: hasHttpToHttps,
                VariantsConsistent: variantsConsistent,
                Variants: variantResults,
                Availability: availability,
                Fake404: fake404
            );
        }

        private async Task<SizeAudit> AuditSizeAsync(Uri pageUrl, CancellationToken ct)
        {
            var http = _httpFactory.CreateClient("crawler");

            // Если страница не html — смысла сравнивать “размер документа” может не быть
            // но все равно попробуем по Content-Length/тексту.
            var sizes = new List<long>();

            int n = Math.Clamp(_opt.SizeStabilityRequests, 2, 3);

            for (int i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();

                var sw = Stopwatch.StartNew();
                try
                {
                    using var resp = await http.GetAsync(pageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    var ctHeader = resp.Content.Headers.ContentType?.MediaType ?? "";
                    var lenHeader = resp.Content.Headers.ContentLength;

                    long size;
                    if (lenHeader.HasValue)
                    {
                        size = lenHeader.Value;
                    }
                    else
                    {
                        // fallback: читаем тело (ограничений тут нет — но в LinkVerifier вы уже ограничиваете html;
                        // здесь берём как есть, т.к. нужен размер).
                        var body = await resp.Content.ReadAsStringAsync();
                        ct.ThrowIfCancellationRequested();
                        size = body.Length;
                    }

                    // если не html, всё равно фиксируем — полезно для “динамических заглушек”
                    sizes.Add(size);
                }
                catch
                {
                    // если один из запросов упал — считаем аудит “неопределённым”
                    return new SizeAudit(
                        Url: pageUrl.AbsoluteUri,
                        Sizes: sizes,
                        IsStable: true,
                        Error: "SIZE_CHECK_FAILED"
                    );
                }
                finally
                {
                    sw.Stop();
                }
            }

            if (sizes.Count < 2)
            {
                return new SizeAudit(pageUrl.AbsoluteUri, sizes, true, "INSUFFICIENT_SAMPLES");
            }

            var min = sizes.Min();
            var max = sizes.Max();
            var delta = max - min;

            var stable = delta <= _opt.SizeStabilityToleranceBytes;

            return new SizeAudit(
                Url: pageUrl.AbsoluteUri,
                Sizes: sizes,
                IsStable: stable,
                Error: null
            );
        }

        private async Task<HostAvailabilityAudit> ProbeAvailabilityAsync(HttpClient noredirect, Uri canonicalFinal, CancellationToken ct)
        {
            // Пробиваем корень (authority + "/"), т.к. canonicalFinal может быть не корень.
            var root = new Uri(canonicalFinal.GetLeftPart(UriPartial.Authority) + "/");

            int probes = Math.Clamp(_opt.AvailabilityProbes, 1, 10);
            int ok = 0;
            var times = new List<double>();

            for (int i = 0; i < probes; i++)
            {
                ct.ThrowIfCancellationRequested();

                var sw = Stopwatch.StartNew();
                try
                {
                    var r = await FollowAsync(noredirect, root, _opt.MaxRedirects, ct);
                    sw.Stop();

                    times.Add(sw.Elapsed.TotalSeconds);

                    // success = финальный статус < 400
                    if (r.Success && r.FinalStatusCode > 0 && r.FinalStatusCode < 400)
                        ok++;
                }
                catch
                {
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalSeconds);
                }
            }

            var percent = (int)Math.Round(ok * 100.0 / probes);
            var avg = times.Count > 0 ? times.Average() : 0.0;

            return new HostAvailabilityAudit(root.AbsoluteUri, probes, ok, percent, avg, Error: null);
        }

        private async Task<Fake404Audit> ProbeFake404Async(HttpClient noredirect, Uri canonicalFinal, CancellationToken ct)
        {
            // Генерим заведомо несуществующий URL на том же хосте
            var baseAuth = canonicalFinal.GetLeftPart(UriPartial.Authority);
            var fake = new Uri(baseAuth + _opt.Fake404PathPrefix + Guid.NewGuid().ToString("N") + "/");

            var r = await FollowAsync(noredirect, fake, _opt.MaxRedirects, ct);

            // Ожидаем: 404 или 410
            if (r.FinalStatusCode == 404 || r.FinalStatusCode == 410)
                return new Fake404Audit(fake.AbsoluteUri, r.FinalStatusCode, IsOk: true, IsSoft404: false, FinalUrl: r.FinalUri?.AbsoluteUri ?? "");

            // “soft 404”: 200 на несуществующем URL или редирект на главную (часто 301/302/200)
            bool soft404 = false;

            if (r.FinalStatusCode == 200)
                soft404 = true;

            if (r.FinalUri is not null)
            {
                var final = r.FinalUri;
                // если ушли на корень ("/") — обычно soft404
                if (final.AbsolutePath == "/" && !fake.AbsolutePath.Equals("/", StringComparison.OrdinalIgnoreCase))
                    soft404 = true;
            }

            return new Fake404Audit(fake.AbsoluteUri, r.FinalStatusCode, IsOk: false, IsSoft404: soft404, FinalUrl: r.FinalUri?.AbsoluteUri ?? "");
        }

        private static VariantResult PickCanonical(List<VariantResult> variants)
        {
            // Успешные
            var ok = variants.Where(v => v.Success && v.FinalUri is not null && v.FinalStatusCode > 0 && v.FinalStatusCode < 400).ToList();

            if (ok.Count == 0) return variants.OrderByDescending(v => v.FinalStatusCode).FirstOrDefault() ?? VariantResult.Failed(new Uri("https://invalid.local/"), "NO_VARIANTS");

            // Приоритет: https base -> https www -> любой
            VariantResult? best = ok.FirstOrDefault(v =>
                v.OriginalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                !v.OriginalUri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
                v.OriginalUri.AbsolutePath == "/");

            best ??= ok.FirstOrDefault(v =>
                v.OriginalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                v.OriginalUri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
                v.OriginalUri.AbsolutePath == "/");

            best ??= ok.First();
            return best;
        }

        private static string CanonKey(Uri u)
        {
            // canonical key: scheme + host + normalized path (без trailing slash кроме корня)
            var b = new UriBuilder(u);
            b.Fragment = "";
            b.Host = b.Host.ToLowerInvariant();

            // default ports -> убираем
            if ((b.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && b.Port == 443) ||
                (b.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port == 80))
                b.Port = -1;

            // normalize path
            var path = b.Path ?? "/";
            if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
                path = path.TrimEnd('/');
            b.Path = path;

            b.Query = ""; // для сравнения доменных вариантов query не нужен
            return b.Uri.AbsoluteUri;
        }

        private static string StripWww(string host)
        {
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                return host.Substring(4);
            return host;
        }

        private static string NormalizeForCache(Uri u)
        {
            // кэш по “каноническому” представлению url (без фрагмента)
            var b = new UriBuilder(u);
            b.Fragment = "";
            return b.Uri.AbsoluteUri;
        }

        private static async Task<VariantResult> FollowAsync(HttpClient noredirect, Uri start, int maxRedirects, CancellationToken ct)
        {
            var redirects = new List<RedirectHop>();
            Uri current = start;

            for (int i = 0; i <= maxRedirects; i++)
            {
                ct.ThrowIfCancellationRequested();

                HttpResponseMessage? resp = null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, current);
                    resp = await noredirect.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                    var status = (int)resp.StatusCode;

                    if (status >= 300 && status < 400 && resp.Headers.Location is not null)
                    {
                        var loc = resp.Headers.Location;
                        var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);

                        redirects.Add(new RedirectHop(current.AbsoluteUri, status, next.AbsoluteUri));

                        resp.Dispose();
                        current = next;
                        continue;
                    }

                    // финал
                    var final = current;
                    resp.Dispose();

                    return new VariantResult(
                        OriginalUri: start,
                        Success: true,
                        FinalUri: final,
                        FinalStatusCode: status,
                        Redirects: redirects,
                        Error: null
                    );
                }
                catch (Exception ex)
                {
                    resp?.Dispose();
                    return VariantResult.Failed(start, ex.Message);
                }
            }

            return VariantResult.Failed(start, "TOO_MANY_REDIRECTS");
        }
    }

    public sealed record HostRedirectHop(string FromUrl, int StatusCode, string Location);

    public sealed record VariantResult(
        Uri OriginalUri,
        bool Success,
        Uri? FinalUri,
        int FinalStatusCode,
        IReadOnlyList<RedirectHop> Redirects,
        string? Error
    )
    {
        public static VariantResult Failed(Uri original, string error) =>
            new VariantResult(original, Success: false, FinalUri: null, FinalStatusCode: 0, Redirects: Array.Empty<RedirectHop>(), Error: error);
    }

    public sealed record HostAvailabilityAudit(
        string Url,
        int Probes,
        int OkCount,
        int OkPercent,
        double AvgSeconds,
        string? Error
    )
    {
        public static HostAvailabilityAudit Failed(string error) =>
            new HostAvailabilityAudit("", 0, 0, 0, 0, error);
    }

    public sealed record Fake404Audit(
        string FakeUrl,
        int StatusCode,
        bool IsOk,
        bool IsSoft404,
        string FinalUrl
    )
    {
        public static Fake404Audit Failed(string error) =>
            new Fake404Audit("", 0, false, false, error);
    }

    public sealed record HostAudit(
        string Host,
        string CanonicalFinalUrl,
        int CanonicalStatus,
        bool HttpsAvailable,
        bool HttpAvailable,
        bool HasHttpToHttpsRedirect,
        bool VariantsConsistent,
        IReadOnlyList<VariantResult> Variants,
        HostAvailabilityAudit Availability,
        Fake404Audit Fake404
    )
    {
        public IEnumerable<LinkIssue> ToIssues()
        {
            var issues = new List<LinkIssue>();

            // HTTPS недоступен (но HTTP есть)
            if (!HttpsAvailable && HttpAvailable)
            {
                issues.Add(new LinkIssue(
                    "HTTPS_UNAVAILABLE",
                    "HTTPS-варианты домена недоступны, но HTTP доступен",
                    IssueSeverity.Error));
            }

            // Нет редиректа HTTP->HTTPS (если HTTP доступен)
            if (HttpAvailable && !HasHttpToHttpsRedirect)
            {
                issues.Add(new LinkIssue(
                    "NO_HTTP_TO_HTTPS_REDIRECT_HOST",
                    "Для доменных вариантов не обнаружен редирект HTTP → HTTPS",
                    IssueSeverity.Warning));
            }

            // Непоследовательность доменных вариантов (разные конечные URL)
            if (!VariantsConsistent)
            {
                issues.Add(new LinkIssue(
                    "DOMAIN_VARIANTS_INCONSISTENT",
                    "Варианты написания домена приводят к разным конечным URL (нет единой канонизации)",
                    IssueSeverity.Warning));
            }

            // Доступность (серия проб)
            if (Availability.Probes > 0)
            {
                // Пороговая логика
                // Error: ниже errorThreshold
                // Warning: ниже warnThreshold (по умолчанию < 100%)
                // Info: если 100% ok — не пишем
                // Примечание: thresholds задаются опциями сервиса, но они внутри.
                // Здесь используем простую интерпретацию: <100% — warning, <60% — error (как default в opt)
                if (Availability.OkPercent < 60)
                {
                    issues.Add(new LinkIssue(
                        "SITE_AVAILABILITY_LOW",
                        $"Низкая доступность сайта: {Availability.OkPercent}% успешных ответов ({Availability.OkCount}/{Availability.Probes}), avg {Availability.AvgSeconds:F2}s",
                        IssueSeverity.Error));
                }
                else if (Availability.OkPercent < 100)
                {
                    issues.Add(new LinkIssue(
                        "SITE_AVAILABILITY_UNSTABLE",
                        $"Нестабильная доступность сайта: {Availability.OkPercent}% успешных ответов ({Availability.OkCount}/{Availability.Probes}), avg {Availability.AvgSeconds:F2}s",
                        IssueSeverity.Warning));
                }
            }

            // Fake 404
            if (Fake404.FakeUrl.Length > 0 && !Fake404.IsOk)
            {
                if (Fake404.IsSoft404)
                {
                    issues.Add(new LinkIssue(
                        "SOFT_404",
                        $"Неверная обработка несуществующих URL: вернулся {Fake404.StatusCode}, финал: {Fake404.FinalUrl}",
                        IssueSeverity.Warning));
                }
                else
                {
                    issues.Add(new LinkIssue(
                        "FAKE_404_NOT_OK",
                        $"Несущеcтвующий URL не вернул 404/410: статус {Fake404.StatusCode}, финал: {Fake404.FinalUrl}",
                        IssueSeverity.Info));
                }
            }

            return issues;
        }
    }

    public sealed record SizeAudit(
        string Url,
        IReadOnlyList<long> Sizes,
        bool IsStable,
        string? Error
    )
    {
        public IEnumerable<LinkIssue> ToIssues()
        {
            if (!string.IsNullOrWhiteSpace(Error))
            {
                return new[]
                {
                    new LinkIssue("DOC_SIZE_CHECK_FAILED", $"Не удалось проверить стабильность размера документа: {Error}", IssueSeverity.Info)
                };
            }

            if (Sizes is null || Sizes.Count < 2) return Array.Empty<LinkIssue>();

            if (!IsStable)
            {
                var min = Sizes.Min();
                var max = Sizes.Max();
                return new[]
                {
                    new LinkIssue("DOC_SIZE_UNSTABLE", $"Размер документа нестабилен между запросами: min={min}, max={max}", IssueSeverity.Warning)
                };
            }

            return Array.Empty<LinkIssue>();
        }
    }

    #endregion

    #region Helpers (DistinctBy for net6)

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

    #endregion
}
