using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace Crawler_project.Checks
{
    public sealed class IndexabilitySiteAndLandingOptions
    {
        /// <summary>User-agent для robots.txt логики</summary>
        public string UserAgent { get; set; } = "MyCrawler";

        /// <summary>Считать посадочными страницы с глубиной пути <= N ("/" = 0, "/catalog/" = 1)</summary>
        public int LandingMaxPathSegments { get; set; } = 1;

        /// <summary>Дополнительные шаблоны посадочных (частые index.*)</summary>
        public bool TreatIndexFilesAsLanding { get; set; } = true;

        /// <summary>Максимум HTML символов для проверки meta robots на главной</summary>
        public int MaxHomepageHtmlChars { get; set; } = 400_000;

        /// <summary>Таймаут на host-level запросы</summary>
        public int HostAuditTimeoutSeconds { get; set; } = 20;

        /// <summary>Если true — посадочные считаем только HTML-страницы (Content-Type содержит html)</summary>
        public bool LandingOnlyHtml { get; set; } = true;
    }

    /// <summary>
    /// Индексация:
    /// 1) Сайт разрешён для индексации (host-level, выдаётся один раз на хост):
    ///    - robots.txt (блокирует ли "/")
    ///    - noindex на главной: X-Robots-Tag / meta robots
    /// 2) Блокировки индексации посадочных страниц (page-level):
    ///    - посадочная по эвристике (глубина пути, index.*)
    ///    - robots / noindex (X-Robots-Tag, meta robots)
    /// </summary>
    public sealed class IndexabilitySiteAndLandingCheck : ILinkCheck
    {
        private readonly IndexabilitySiteAuditService _svc;
        private readonly IndexabilitySiteAndLandingOptions _opt;

        public IndexabilitySiteAndLandingCheck(IndexabilitySiteAuditService svc, IndexabilitySiteAndLandingOptions opt)
        {
            _svc = svc;
            _opt = opt;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            var host = ctx.FinalUri.Host;

            // 1) Host-level: "Сайт разрешён для индексации" — эмитим 1 раз на хост
            if (_svc.TryEmitSiteOnce(host))
            {
                var site = await _svc.GetSiteSnapshotAsync(ctx.FinalUri, ct);
                issues.AddRange(site.ToIssues());
            }

            // 2) Page-level: "Блокировки индексации посадочных страниц"
            if (IsLandingCandidate(ctx, _opt))
            {
                var site = await _svc.GetSiteSnapshotAsync(ctx.FinalUri, ct);

                // robots
                if (site.Robots is not null && !site.Robots.IsAllowed(ctx.FinalUri))
                {
                    issues.Add(new LinkIssue(
                        "LANDING_BLOCKED_BY_ROBOTS",
                        "Посадочная страница заблокирована robots.txt для выбранного User-agent",
                        IssueSeverity.Warning));
                }

                // noindex (headers / meta)
                if (HasNoindexHeader(ctx.Headers))
                {
                    issues.Add(new LinkIssue(
                        "LANDING_NOINDEX_HEADER",
                        "Посадочная страница закрыта от индексации через X-Robots-Tag: noindex",
                        IssueSeverity.Warning));
                }

                if (HasNoindexMeta(ctx.Document))
                {
                    issues.Add(new LinkIssue(
                        "LANDING_NOINDEX_META",
                        "Посадочная страница закрыта от индексации через meta robots: noindex",
                        IssueSeverity.Warning));
                }
            }

            return issues;
        }

        private static bool IsLandingCandidate(LinkCheckContext ctx, IndexabilitySiteAndLandingOptions opt)
        {
            // только успешные/валидные страницы
            if (ctx.FinalStatusCode <= 0) return false;
            if (ctx.FinalStatusCode >= 400) return false;

            if (opt.LandingOnlyHtml)
            {
                if (string.IsNullOrWhiteSpace(ctx.ContentType)) return false;
                if (!ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return false;
            }

            var path = ctx.FinalUri.AbsolutePath ?? "/";
            if (string.IsNullOrEmpty(path)) path = "/";
            if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;

            // "/" всегда посадочная
            if (path == "/") return true;

            // index.* часто считается посадочной
            if (opt.TreatIndexFilesAsLanding)
            {
                var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                if (last.StartsWith("index.", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // глубина пути
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            return segments <= Math.Max(0, opt.LandingMaxPathSegments);
        }

        private static bool HasNoindexHeader(IReadOnlyDictionary<string, string> headers)
        {
            if (headers is null) return false;

            // В LinkVerifier вы кладёте и resp.Headers и resp.Content.Headers в один словарь.
            if (headers.TryGetValue("X-Robots-Tag", out var xrt) && !string.IsNullOrWhiteSpace(xrt))
            {
                return xrt.Contains("noindex", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool HasNoindexMeta(HtmlDocument? doc)
        {
            if (doc?.DocumentNode is null) return false;

            var nodes = doc.DocumentNode.SelectNodes("//meta[@name and @content]");
            if (nodes is null) return false;

            foreach (var n in nodes)
            {
                var name = (n.GetAttributeValue("name", "") ?? "").Trim();
                if (name.Length == 0) continue;

                // robots / googlebot / yandex — минимальный набор
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

    // ----------------------- Service -----------------------

    public sealed class IndexabilitySiteAuditService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IndexabilitySiteAndLandingOptions _opt;

        // host -> snapshot (кэш)
        private readonly ConcurrentDictionary<string, Lazy<Task<SiteIndexabilitySnapshot>>> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        // host -> emitted?
        private readonly ConcurrentDictionary<string, byte> _siteEmitted =
            new(StringComparer.OrdinalIgnoreCase);

        public IndexabilitySiteAuditService(IHttpClientFactory httpFactory, IndexabilitySiteAndLandingOptions opt)
        {
            _httpFactory = httpFactory;
            _opt = opt;
        }

        public bool TryEmitSiteOnce(string host) => _siteEmitted.TryAdd(host, 0);

        public Task<SiteIndexabilitySnapshot> GetSiteSnapshotAsync(Uri anyUriOnHost, CancellationToken ct)
        {
            var host = anyUriOnHost.Host;

            var lazy = _cache.GetOrAdd(host, _ => new Lazy<Task<SiteIndexabilitySnapshot>>(() => BuildSnapshotAsync(host, ct)));
            return lazy.Value;
        }

        private async Task<SiteIndexabilitySnapshot> BuildSnapshotAsync(string host, CancellationToken ct)
        {
            var http = _httpFactory.CreateClient("crawler");
            http.Timeout = TimeSpan.FromSeconds(_opt.HostAuditTimeoutSeconds);

            // robots.txt
            RobotsRules? robots = null;
            bool robotsFetchOk = false;
            string? robotsError = null;

            try
            {
                robots = await RobotsRules.FetchAsync(new Uri($"https://{host}/"), _opt.UserAgent, http);
                robotsFetchOk = true;
            }
            catch (Exception ex)
            {
                robotsFetchOk = false;
                robotsError = ex.Message;
            }

            // проверяем “сайт разрешён для индексации” по корню "/"
            bool rootAllowedByRobots = true;
            if (robots is not null)
            {
                rootAllowedByRobots = robots.IsAllowed(new Uri($"https://{host}/"));
            }

            // homepage noindex: X-Robots-Tag / meta robots
            bool homepageNoindexHeader = false;
            bool homepageNoindexMeta = false;
            int homepageStatus = 0;

            try
            {
                var homepageUrl = new Uri($"https://{host}/");

                using var headReq = new HttpRequestMessage(HttpMethod.Head, homepageUrl);
                using var headResp = await http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);
                homepageStatus = (int)headResp.StatusCode;

                if (headResp.Headers.TryGetValues("X-Robots-Tag", out var xrt))
                {
                    var s = string.Join(", ", xrt);
                    if (s.Contains("noindex", StringComparison.OrdinalIgnoreCase))
                        homepageNoindexHeader = true;
                }

                // meta robots — только если HTML и GET успешен
                if (homepageStatus > 0 && homepageStatus < 400)
                {
                    using var getReq = new HttpRequestMessage(HttpMethod.Get, homepageUrl);
                    using var getResp = await http.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, ct);
                    homepageStatus = (int)getResp.StatusCode;

                    var ctype = getResp.Content.Headers.ContentType?.MediaType ?? "";
                    if (ctype.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        var html = await getResp.Content.ReadAsStringAsync(ct);
                        if (html.Length > _opt.MaxHomepageHtmlChars)
                            html = html.Substring(0, _opt.MaxHomepageHtmlChars);

                        var doc = new HtmlDocument
                        {
                            OptionCheckSyntax = true,
                            OptionFixNestedTags = true
                        };
                        doc.LoadHtml(html);

                        homepageNoindexMeta = HasNoindexMeta(doc);
                    }
                }
            }
            catch
            {
                // если главная недоступна — это отдельная проблема, но здесь мы фиксируем как “не смогли проверить meta”
            }

            return new SiteIndexabilitySnapshot(
                Host: host,
                Robots: robots,
                RobotsFetchOk: robotsFetchOk,
                RobotsError: robotsError,
                RootAllowedByRobots: rootAllowedByRobots,
                HomepageStatus: homepageStatus,
                HomepageNoindexHeader: homepageNoindexHeader,
                HomepageNoindexMeta: homepageNoindexMeta
            );
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

    public sealed record SiteIndexabilitySnapshot(
        string Host,
        RobotsRules? Robots,
        bool RobotsFetchOk,
        string? RobotsError,
        bool RootAllowedByRobots,
        int HomepageStatus,
        bool HomepageNoindexHeader,
        bool HomepageNoindexMeta
    )
    {
        public IEnumerable<LinkIssue> ToIssues()
        {
            var issues = new List<LinkIssue>();

            // 1) robots.txt не удалось получить — предупреждение (само по себе не “запрещено”, но контроля нет)
            if (!RobotsFetchOk)
            {
                issues.Add(new LinkIssue(
                    "SITE_INDEX_ROBOTS_FETCH_FAILED",
                    $"Не удалось получить robots.txt для проверки индексации (host={Host}). Ошибка: {RobotsError ?? "n/a"}",
                    IssueSeverity.Warning));
            }

            // 2) robots блокирует корень
            if (Robots is not null && !RootAllowedByRobots)
            {
                issues.Add(new LinkIssue(
                    "SITE_INDEX_BLOCKED_BY_ROBOTS",
                    "Сайт не разрешён для индексации: robots.txt блокирует корень '/' для выбранного User-agent",
                    IssueSeverity.Error));
            }

            // 3) noindex на главной (X-Robots-Tag / meta)
            if (HomepageNoindexHeader)
            {
                issues.Add(new LinkIssue(
                    "SITE_INDEX_HOMEPAGE_NOINDEX_HEADER",
                    "Сайт не разрешён для индексации: главная страница закрыта через X-Robots-Tag: noindex",
                    IssueSeverity.Error));
            }

            if (HomepageNoindexMeta)
            {
                issues.Add(new LinkIssue(
                    "SITE_INDEX_HOMEPAGE_NOINDEX_META",
                    "Сайт не разрешён для индексации: главная страница закрыта через meta robots: noindex",
                    IssueSeverity.Error));
            }

            // 4) если главная явно недоступна
            if (HomepageStatus >= 400)
            {
                issues.Add(new LinkIssue(
                    "SITE_INDEX_HOMEPAGE_HTTP_ERROR",
                    $"Главная страница недоступна (HTTP {HomepageStatus}) — индексация сайта будет проблемной",
                    IssueSeverity.Warning));
            }

            return issues;
        }
    }
}
