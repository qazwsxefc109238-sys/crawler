using Crawler_project.Checks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Models.LinkChecks
{
    /// <summary>
    /// robots.txt:
    /// - наличие (https + fallback http)
    /// - ошибки доступа (4xx/5xx/таймаут)
    /// - синтаксические ошибки (невалидные строки, неизвестные директивы, директивы без User-agent и т.д.)
    /// - логические проблемы (блокирует всё, нет группы для * или для нашего UA)
    /// 
    /// Важно: robots.txt — host-level. Этот чек выдаёт результаты один раз на хост.
    /// </summary>
    public sealed class RobotsTxtChecks : ILinkCheck
    {
        private readonly RobotsTxtAuditService _svc;

        public RobotsTxtChecks(RobotsTxtAuditService svc) => _svc = svc;

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var host = ctx.FinalUri.Host;

            // чтобы не плодить одинаковые issues на каждой странице
            if (!_svc.TryEmitOnce(host))
                return Array.Empty<LinkIssue>();

            var report = await _svc.AuditAsync(ctx.FinalUri, ct);
            return report.ToIssues();
        }
    }

    // -------------------- Options --------------------

    public sealed class RobotsTxtOptions
    {
        /// <summary>Какой User-agent использовать для логических проверок (наличие группы, блокировка и т.п.)</summary>
        public string UserAgent { get; set; } = "MyCrawler";

        /// <summary>Максимальный размер robots.txt (символов), чтобы не тащить мегабайты</summary>
        public int MaxChars { get; set; } = 250_000;

        /// <summary>Считать отсутствие группы для UA/* ошибкой или предупреждением</summary>
        public bool MissingGroupIsWarning { get; set; } = true;
    }

    // -------------------- Service + Report --------------------

    public sealed class RobotsTxtAuditService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly RobotsTxtOptions _opt;

        // кэш по host: один раз скачали/проверили — используем дальше
        private readonly ConcurrentDictionary<string, Lazy<Task<RobotsTxtReport>>> _cache = new(StringComparer.OrdinalIgnoreCase);

        // чтобы “эмитить” issues по хосту один раз
        private readonly ConcurrentDictionary<string, byte> _emitted = new(StringComparer.OrdinalIgnoreCase);

        public RobotsTxtAuditService(IHttpClientFactory httpFactory, RobotsTxtOptions opt)
        {
            _httpFactory = httpFactory;
            _opt = opt;
        }

        public bool TryEmitOnce(string host) => _emitted.TryAdd(host, 0);

        public Task<RobotsTxtReport> AuditAsync(Uri anyUrlOnHost, CancellationToken ct)
        {
            var host = anyUrlOnHost.Host;
            var lazy = _cache.GetOrAdd(host, _ => new Lazy<Task<RobotsTxtReport>>(() => AuditCoreAsync(host, ct)));
            return lazy.Value;
        }

        private async Task<RobotsTxtReport> AuditCoreAsync(string host, CancellationToken ct)
        {
            // Пробуем сначала HTTPS robots.txt, затем HTTP
            var httpsUri = new Uri($"https://{host}/robots.txt");
            var httpUri = new Uri($"http://{host}/robots.txt");

            var https = await FetchAsync(httpsUri, ct);
            RobotsFetch? selected = null;

            if (https.Exists)
            {
                selected = https;
            }
            else
            {
                var http = await FetchAsync(httpUri, ct);
                selected = http;

                // Если на http есть, а на https нет — это важно отметить
                if (http.Exists && !https.Exists)
                {
                    http.Problems.Insert(0, new RobotsProblem(
                        Code: "ROBOTS_ONLY_HTTP",
                        Message: "robots.txt доступен по HTTP, но отсутствует/недоступен по HTTPS",
                        Severity: IssueSeverity.Warning));
                }

                // Если и https, и http не существуют — отдаём https-результат как основной (чтобы показать первопричину)
                if (!http.Exists && !https.Exists)
                    selected = https; // там будет более “правильный” контекст для HTTPS
            }

            // Если robots.txt не получен — формируем отчёт только по доступности
            if (!selected.Exists)
            {
                return new RobotsTxtReport(
                    Host: host,
                    Exists: false,
                    SourceUrl: selected.SourceUrl,
                    StatusCode: selected.StatusCode,
                    FetchError: selected.Error,
                    Problems: selected.Problems,
                    BlocksAll: false,
                    HasGroupForUaOrStar: false
                );
            }

            // Анализ содержимого
            var text = selected.Content ?? "";
            if (text.Length > _opt.MaxChars) text = text.Substring(0, _opt.MaxChars);

            var parse = RobotsTxtAnalyzer.Analyze(text, _opt.UserAgent);

            // Логические проверки через ваш существующий RobotsRules
            // (он не выдаёт ошибки, но помогает понять allow/disallow).
            bool blocksAll = false;
            bool hasGroup = parse.HasGroupForUaOrStar;

            try
            {
                var rules = RobotsRules.Parse(text, _opt.UserAgent);
                // "блокирует всё" для UA — если "/" запрещён
                blocksAll = !rules.IsAllowed(new Uri($"https://{host}/"));
            }
            catch
            {
                // если ваш парсер вдруг падает (не должен) — просто не считаем blocksAll
            }

            var problems = new List<RobotsProblem>();
            problems.AddRange(selected.Problems);
            problems.AddRange(parse.Problems);

            // Если нет группы для UA или * — это логическая проблема
            if (!hasGroup)
            {
                problems.Add(new RobotsProblem(
                    Code: "ROBOTS_NO_GROUP_FOR_UA",
                    Message: $"Не найдена группа User-agent для \"{_opt.UserAgent}\" или \"*\"",
                    Severity: _opt.MissingGroupIsWarning ? IssueSeverity.Warning : IssueSeverity.Error));
            }

            // Если блокирует весь сайт — отдельная проблема
            if (blocksAll)
            {
                problems.Add(new RobotsProblem(
                    Code: "ROBOTS_BLOCKS_ALL",
                    Message: "robots.txt блокирует индексацию всего сайта (Disallow: / для UA или по приоритету правил)",
                    Severity: IssueSeverity.Warning));
            }

            return new RobotsTxtReport(
                Host: host,
                Exists: true,
                SourceUrl: selected.SourceUrl,
                StatusCode: selected.StatusCode,
                FetchError: selected.Error,
                Problems: problems,
                BlocksAll: blocksAll,
                HasGroupForUaOrStar: hasGroup
            );
        }

        private async Task<RobotsFetch> FetchAsync(Uri robotsUri, CancellationToken ct)
        {
            // по возможности используем клиент без автиредиректов, чтобы видеть реальную картину,
            // но если его нет — используем обычный.
            HttpClient http;
            try
            {
                http = _httpFactory.CreateClient("crawler_noredirect");
            }
            catch
            {
                http = _httpFactory.CreateClient("crawler");
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, robotsUri);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                var status = (int)resp.StatusCode;

                if (status == 404)
                {
                    return new RobotsFetch(
                        SourceUrl: robotsUri.AbsoluteUri,
                        Exists: false,
                        StatusCode: status,
                        Error: null,
                        Content: null,
                        Problems: new List<RobotsProblem>
                        {
                            new RobotsProblem("ROBOTS_MISSING", $"robots.txt не найден: {robotsUri}", IssueSeverity.Warning)
                        });
                }

                if (status >= 400)
                {
                    return new RobotsFetch(
                        SourceUrl: robotsUri.AbsoluteUri,
                        Exists: false,
                        StatusCode: status,
                        Error: null,
                        Content: null,
                        Problems: new List<RobotsProblem>
                        {
                            new RobotsProblem("ROBOTS_HTTP_ERROR", $"robots.txt вернул статус {status}: {robotsUri}", IssueSeverity.Warning)
                        });
                }

                var text = await resp.Content.ReadAsStringAsync();
                return new RobotsFetch(
                    SourceUrl: robotsUri.AbsoluteUri,
                    Exists: true,
                    StatusCode: status,
                    Error: null,
                    Content: text,
                    Problems: new List<RobotsProblem>());
            }
            catch (Exception ex)
            {
                return new RobotsFetch(
                    SourceUrl: robotsUri.AbsoluteUri,
                    Exists: false,
                    StatusCode: null,
                    Error: ex.Message,
                    Content: null,
                    Problems: new List<RobotsProblem>
                    {
                        new RobotsProblem("ROBOTS_FETCH_FAILED", $"Не удалось получить robots.txt: {robotsUri}. Ошибка: {ex.Message}", IssueSeverity.Warning)
                    });
            }
        }

        private sealed record RobotsFetch(
            string SourceUrl,
            bool Exists,
            int? StatusCode,
            string? Error,
            string? Content,
            List<RobotsProblem> Problems);
    }

    public sealed record RobotsProblem(string Code, string Message, IssueSeverity Severity);

    public sealed record RobotsTxtReport(
        string Host,
        bool Exists,
        string SourceUrl,
        int? StatusCode,
        string? FetchError,
        IReadOnlyList<RobotsProblem> Problems,
        bool BlocksAll,
        bool HasGroupForUaOrStar
    )
    {
        public IEnumerable<LinkIssue> ToIssues()
        {
            var issues = new List<LinkIssue>();

            if (!Exists)
            {
                issues.Add(new LinkIssue(
                    "ROBOTS_MISSING_OR_UNAVAILABLE",
                    $"robots.txt отсутствует или недоступен. URL: {SourceUrl}. Status: {(StatusCode.HasValue ? StatusCode.Value.ToString() : "n/a")}. Ошибка: {FetchError ?? "n/a"}",
                    IssueSeverity.Warning));
            }

            foreach (var p in Problems)
            {
                // сгруппируем как отдельные issues — так проще фильтровать по коду
                issues.Add(new LinkIssue(p.Code, p.Message, p.Severity));
            }

            return issues;
        }
    }

    // -------------------- Analyzer (syntax/logic) --------------------

    internal static class RobotsTxtAnalyzer
    {
        private static readonly HashSet<string> KnownDirectives = new(StringComparer.OrdinalIgnoreCase)
        {
            "User-agent",
            "Disallow",
            "Allow",
            "Sitemap",
            "Crawl-delay",
            "Host" // часто в RU (Yandex)
        };

        internal sealed record AnalyzeResult(bool HasGroupForUaOrStar, List<RobotsProblem> Problems);

        public static AnalyzeResult Analyze(string robotsTxt, string userAgent)
        {
            var problems = new List<RobotsProblem>();

            var lines = robotsTxt.Split('\n');
            var currentAgents = new List<string>();
            var anyGroup = false;

            bool sawUaLine = false;
            bool groupHasRules = false;

            // фиксируем какие группы User-agent вообще встречались
            var seenAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];

                // убираем комментарии
                var line = raw.Split('#')[0].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var idx = line.IndexOf(':');
                if (idx <= 0)
                {
                    problems.Add(new RobotsProblem(
                        "ROBOTS_BAD_LINE",
                        $"Невалидная строка (нет ':') на строке {i + 1}: \"{raw.Trim()}\"",
                        IssueSeverity.Warning));
                    continue;
                }

                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim();

                if (!KnownDirectives.Contains(key))
                {
                    problems.Add(new RobotsProblem(
                        "ROBOTS_UNKNOWN_DIRECTIVE",
                        $"Неизвестная директива \"{key}\" на строке {i + 1}",
                        IssueSeverity.Info));
                    // продолжаем, но учитываем как “строку с директивой”
                }

                if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                {
                    sawUaLine = true;

                    if (string.IsNullOrWhiteSpace(val))
                    {
                        problems.Add(new RobotsProblem(
                            "ROBOTS_EMPTY_USER_AGENT",
                            $"Пустое значение User-agent на строке {i + 1}",
                            IssueSeverity.Warning));
                        currentAgents.Clear();
                        continue;
                    }

                    // новая группа начинается, когда ранее в группе уже были правила
                    if (groupHasRules)
                    {
                        currentAgents.Clear();
                        groupHasRules = false;
                    }

                    currentAgents.Add(val);
                    foreach (var ua in currentAgents)
                        seenAgents.Add(ua);

                    anyGroup = true;
                    continue;
                }

                // Любая директива правил без User-agent — синтаксическая ошибка
                if (currentAgents.Count == 0)
                {
                    // Разрешим Sitemap без UA (это допустимо и часто встречается)
                    if (key.Equals("Sitemap", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Uri.TryCreate(val, UriKind.Absolute, out _))
                        {
                            problems.Add(new RobotsProblem(
                                "ROBOTS_BAD_SITEMAP_URL",
                                $"Невалидный Sitemap URL на строке {i + 1}: \"{val}\"",
                                IssueSeverity.Warning));
                        }
                        continue;
                    }

                    problems.Add(new RobotsProblem(
                        "ROBOTS_RULE_BEFORE_UA",
                        $"Директива \"{key}\" встречена до объявления User-agent (строка {i + 1})",
                        IssueSeverity.Warning));
                    continue;
                }

                // Проверки по конкретным директивам
                if (key.Equals("Disallow", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Allow", StringComparison.OrdinalIgnoreCase))
                {
                    groupHasRules = true;

                    // пустой Disallow/Allow — формально допустимо, но часто ошибка/мусор
                    if (string.IsNullOrEmpty(val))
                    {
                        problems.Add(new RobotsProblem(
                            "ROBOTS_EMPTY_RULE",
                            $"Пустое значение {key} на строке {i + 1} (возможно, лишняя строка)",
                            IssueSeverity.Info));
                        continue;
                    }

                    // многие аудиторы считают, что путь должен начинаться с /
                    if (!val.StartsWith("/", StringComparison.Ordinal))
                    {
                        problems.Add(new RobotsProblem(
                            "ROBOTS_RULE_NOT_PATHLIKE",
                            $"Значение {key} не похоже на путь (не начинается с '/'): \"{val}\" (строка {i + 1})",
                            IssueSeverity.Info));
                    }
                }
                else if (key.Equals("Sitemap", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Uri.TryCreate(val, UriKind.Absolute, out _))
                    {
                        problems.Add(new RobotsProblem(
                            "ROBOTS_BAD_SITEMAP_URL",
                            $"Невалидный Sitemap URL на строке {i + 1}: \"{val}\"",
                            IssueSeverity.Warning));
                    }
                }
                else if (key.Equals("Crawl-delay", StringComparison.OrdinalIgnoreCase))
                {
                    if (!double.TryParse(val.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var _))
                    {
                        problems.Add(new RobotsProblem(
                            "ROBOTS_BAD_CRAWL_DELAY",
                            $"Невалидный Crawl-delay на строке {i + 1}: \"{val}\"",
                            IssueSeverity.Info));
                    }
                }
            }

            if (!anyGroup)
            {
                problems.Add(new RobotsProblem(
                    "ROBOTS_NO_USER_AGENT",
                    "В robots.txt не найдены директивы User-agent (нет групп правил)",
                    IssueSeverity.Warning));
            }

            // Проверка наличия группы для UA или *
            bool hasGroupForUaOrStar = seenAgents.Contains(userAgent) || seenAgents.Contains("*");

            // Если есть robots.txt, но нет UA групп — это почти всегда ошибка
            if (sawUaLine == false)
            {
                problems.Add(new RobotsProblem(
                    "ROBOTS_NO_UA_LINES",
                    "В robots.txt отсутствуют строки User-agent",
                    IssueSeverity.Warning));
            }

            return new AnalyzeResult(hasGroupForUaOrStar, problems);
        }
    }
}
