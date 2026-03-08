using Crawler_project.Checks;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Crawler_project.Models
{
    /// <summary>
    /// Возраст домена + paid-till/expiry без TCP/43 WHOIS:
    /// 1) RDAP: https://rdap.org/domain/<domain>
    /// 2) Fallback RU/RF/SU/ДЕТИ/TATAR: https://tcinet.ru/whois/?action=yes&domain=...&domen=...
    /// </summary>
    public sealed class DomainWhoisChecks : ILinkCheck
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly DomainWhoisOptions _opt;

        // чтобы не дергать сервисы по 1000 раз на один и тот же хост во время одного прогона
        private static readonly ConcurrentDictionary<string, byte> _doneHosts = new(StringComparer.OrdinalIgnoreCase);

        public DomainWhoisChecks(IHttpClientFactory httpFactory, DomainWhoisOptions opt)
        {
            _httpFactory = httpFactory;
            _opt = opt;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            // ВАЖНО: проверяем "главную" по OriginalUri, иначе редирект на /ru/ убивает чек полностью
            if (!string.Equals(ctx.OriginalUri.AbsolutePath, "/", StringComparison.Ordinal))
                return Array.Empty<LinkIssue>();

            var host = ctx.FinalUri.Host.Trim().TrimEnd('.');

            // один раз на хост за прогон
            if (!_doneHosts.TryAdd(host, 0))
                return Array.Empty<LinkIssue>();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _opt.TotalTimeoutSeconds)));

            DomainDates? dates;
            string source;

            try
            {
                using var http = CreateClient();
                (dates, source) = await GetDatesAsync(http, host, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                return new[]
                {
                    new LinkIssue(
                        "DOMAIN_WHOIS_TIMEOUT",
                        $"Таймаут получения даты регистрации/оплаты домена. Host={host}, Timeout={_opt.TotalTimeoutSeconds}s",
                        IssueSeverity.Warning)
                };
            }
            catch (Exception ex)
            {
                return new[]
                {
                    new LinkIssue(
                        "DOMAIN_WHOIS_FAILED",
                        $"Ошибка получения данных домена. Host={host}, Error={ex.GetType().Name}: {ex.Message}",
                        IssueSeverity.Warning)
                };
            }

            if (dates is null || (dates.CreatedUtc is null && dates.ExpiresUtc is null))
            {
                return new[]
                {
                    new LinkIssue(
                        "DOMAIN_WHOIS_FAILED",
                        $"Не удалось получить даты домена (RDAP/TCI). Host={host}",
                        IssueSeverity.Warning)
                };
            }

            var issues = new List<LinkIssue>();

            if (dates.CreatedUtc is not null)
            {
                var ageDays = (int)Math.Floor((DateTimeOffset.UtcNow - dates.CreatedUtc.Value).TotalDays);
                issues.Add(new LinkIssue(
                    "DOMAIN_CREATED_AT",
                    $"Домен зарегистрирован: {dates.CreatedUtc:yyyy-MM-dd} (≈ {ageDays} дней). Source: {source}",
                    IssueSeverity.Info));
            }
            else
            {
                issues.Add(new LinkIssue(
                    "DOMAIN_CREATED_UNKNOWN",
                    $"Не удалось определить дату регистрации домена. Source: {source}",
                    IssueSeverity.Info));
            }

            if (dates.ExpiresUtc is not null)
            {
                var daysLeft = (int)Math.Ceiling((dates.ExpiresUtc.Value - DateTimeOffset.UtcNow).TotalDays);

                if (daysLeft < 0)
                {
                    issues.Add(new LinkIssue(
                        "DOMAIN_EXPIRED",
                        $"Домен просрочен: {dates.ExpiresUtc:yyyy-MM-dd}. Source: {source}",
                        IssueSeverity.Error));
                }
                else if (daysLeft <= _opt.ExpiringSoonDays)
                {
                    issues.Add(new LinkIssue(
                        "DOMAIN_EXPIRES_SOON",
                        $"Домен оплачен до: {dates.ExpiresUtc:yyyy-MM-dd} (осталось ~{daysLeft} дн.). Source: {source}",
                        IssueSeverity.Warning));
                }
                else
                {
                    issues.Add(new LinkIssue(
                        "DOMAIN_EXPIRES_AT",
                        $"Домен оплачен до: {dates.ExpiresUtc:yyyy-MM-dd} (осталось ~{daysLeft} дн.). Source: {source}",
                        IssueSeverity.Info));
                }
            }
            else
            {
                issues.Add(new LinkIssue(
                    "DOMAIN_EXPIRES_UNKNOWN",
                    $"Не удалось определить paid-till/expiry домена. Source: {source}",
                    IssueSeverity.Warning));
            }

            return issues;
        }

        // -------------------- core --------------------

        private sealed record DomainDates(DateTimeOffset? CreatedUtc, DateTimeOffset? ExpiresUtc);

        private async Task<(DomainDates? dates, string source)> GetDatesAsync(HttpClient http, string host, CancellationToken ct)
        {
            var asciiHost = ToAsciiDomain(host);
            var candidates = BuildCandidates(asciiHost);

            // 1) RDAP
            foreach (var d in candidates)
            {
                var rdap = await TryGetFromRdapAsync(http, d, ct);
                if (rdap is not null && (rdap.CreatedUtc is not null || rdap.ExpiresUtc is not null))
                    return (rdap, $"RDAP (rdap.org) for {d}");
            }

            // 2) TCI fallback для поддерживаемых зон
            var tld = GetTld(asciiHost);
            if (IsTcinetSupportedTld(tld))
            {
                foreach (var d in candidates)
                {
                    var tci = await TryGetFromTcinetAsync(http, d, tld, ct);
                    if (tci is not null && (tci.CreatedUtc is not null || tci.ExpiresUtc is not null))
                        return (tci, $"TCI (tcinet.ru) for {d}");
                }
            }

            return (null, "RDAP/TCI");
        }

        // -------------------- RDAP --------------------

        private static async Task<DomainDates?> TryGetFromRdapAsync(HttpClient http, string domain, CancellationToken ct)
        {
            var url = $"https://rdap.org/domain/{Uri.EscapeDataString(domain)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rdap+json"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode) return null;

            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
                return null;

            DateTimeOffset? created = null;
            DateTimeOffset? expires = null;

            foreach (var ev in events.EnumerateArray())
            {
                if (!ev.TryGetProperty("eventAction", out var actEl)) continue;
                if (!ev.TryGetProperty("eventDate", out var dateEl)) continue;

                var action = (actEl.GetString() ?? "").Trim().ToLowerInvariant();
                var dateStr = (dateEl.GetString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(dateStr)) continue;

                if (!DateTimeOffset.TryParse(
                        dateStr,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var dto))
                    continue;

                if (created is null && (action.Contains("registration") || action.Contains("registered") || action.Contains("created")))
                    created = dto;

                if (expires is null && (action.Contains("expiration") || action.Contains("expires") || action.Contains("expiry")))
                    expires = dto;
            }

            return new DomainDates(created, expires);
        }

        // -------------------- TCI --------------------

        private static async Task<DomainDates?> TryGetFromTcinetAsync(HttpClient http, string domain, string tld, CancellationToken ct)
        {
            var domenPrimary = MapTcinetDomenParam(tld);

            foreach (var domen in new[] { domenPrimary, "all" })
            {
                var url = $"https://tcinet.ru/whois/?action=yes&domain={Uri.EscapeDataString(domain)}&domen={Uri.EscapeDataString(domen)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var html = await resp.Content.ReadAsStringAsync(ct);
                var text = HtmlToText(html);

                // tcinet иногда отдает created в формате yyyy.MM.dd, поэтому берем "всю дату" и парсим несколькими форматами
                var createdRaw = Match1(text, @"(?im)^\s*created:\s*(.+?)\s*$");
                var paidTillRaw = Match1(text, @"(?im)^\s*paid-till:\s*(.+?)\s*$");

                var created = ParseAnyDate(createdRaw);
                var paidTill = ParseAnyDate(paidTillRaw);

                if (created is not null || paidTill is not null)
                    return new DomainDates(created, paidTill);
            }

            return null;
        }

        private static string? Match1(string text, string pattern)
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static DateTimeOffset? ParseAnyDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;

            s = s.Trim();

            // 1) общий TryParse (ISO, с таймзоной и т.д.)
            if (DateTimeOffset.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dto))
                return dto;

            // 2) частые форматы whois
            var fmts = new[]
            {
                "yyyy-MM-dd",
                "yyyy.MM.dd",
                "dd.MM.yyyy",
                "yyyy-MM-ddTHH:mm:ss'Z'",
                "yyyy-MM-ddTHH:mm:ss.fff'Z'",
            };

            if (DateTime.TryParseExact(
                    s,
                    fmts,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dt))
                return new DateTimeOffset(dt, TimeSpan.Zero);

            return null;
        }

        // -------------------- http client --------------------

        private HttpClient CreateClient()
        {
            // если named client не заведен — CreateClient просто вернет дефолтный
            var http = _httpFactory.CreateClient("domainwhois");
            http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _opt.HttpTimeoutSeconds));
            return http;
        }

        // -------------------- domain helpers --------------------

        private static IReadOnlyList<string> BuildCandidates(string asciiHost)
        {
            var list = new List<string>();

            static void Add(List<string> l, string v)
            {
                if (string.IsNullOrWhiteSpace(v)) return;
                if (!l.Contains(v, StringComparer.OrdinalIgnoreCase)) l.Add(v);
            }

            Add(list, asciiHost);

            if (asciiHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                Add(list, asciiHost[4..]);

            Add(list, GetRegistrableDomain(asciiHost));

            return list;
        }

        private static string ToAsciiDomain(string host)
        {
            var idn = new IdnMapping();
            var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join('.', labels.Select(label => idn.GetAscii(label))).ToLowerInvariant();

        }

        private static string GetTld(string asciiDomain)
        {
            var parts = asciiDomain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? "" : parts[^1].ToLowerInvariant();
        }

        // эвристика eTLD+1 (как у тебя было, но короче)
        private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "co.uk","org.uk","gov.uk","ac.uk",
            "com.au","net.au","org.au",
            "co.jp","ne.jp",
            "com.br","com.tr",
            "com.cn","net.cn","org.cn",
        };

        private static string GetRegistrableDomain(string asciiHost)
        {
            asciiHost = asciiHost.Trim().TrimEnd('.').ToLowerInvariant();
            if (asciiHost.StartsWith("www.")) asciiHost = asciiHost[4..];

            var parts = asciiHost.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 2) return asciiHost;

            var last2 = $"{parts[^2]}.{parts[^1]}";
            if (TwoLevelSuffixes.Contains(last2) && parts.Length >= 3)
                return $"{parts[^3]}.{last2}";

            return last2;
        }

        private static bool IsTcinetSupportedTld(string tld) =>
            tld is "ru" or "su" or "xn--p1ai" or "xn--d1acj3b" or "tatar";

        private static string MapTcinetDomenParam(string tld) =>
            tld switch
            {
                "xn--p1ai" => "p1ai",
                _ => tld
            };

        private static string HtmlToText(string html)
        {
            var s = WebUtility.HtmlDecode(html);
            s = Regex.Replace(s, @"(?i)<\s*br\s*/?\s*>", "\n");
            s = Regex.Replace(s, @"(?i)</\s*p\s*>", "\n");
            s = Regex.Replace(s, @"(?i)</\s*div\s*>", "\n");
            s = Regex.Replace(s, @"<[^>]+>", " ");
            s = s.Replace("\r", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            s = Regex.Replace(s, @"[ \t]{2,}", " ");
            return s.Trim();
        }
    }

    public sealed class DomainWhoisOptions
    {
        public int TotalTimeoutSeconds { get; set; } = 20;
        public int HttpTimeoutSeconds { get; set; } = 25;
        public int ExpiringSoonDays { get; set; } = 30;
    }
}
