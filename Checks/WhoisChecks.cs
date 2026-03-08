using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Crawler_project.Models;

namespace Crawler_project.Checks
{
    // =========================
    // Options / Report
    // =========================

    public sealed class WhoisOptions
    {
        public int TcpTimeoutSeconds { get; set; } = 12;
        public int ExpiringSoonDays { get; set; } = 30;

        /// <summary>Ограничение на размер ответа WHOIS (защита)</summary>
        public int MaxResponseChars { get; set; } = 300_000;

        /// <summary>Если true — будем пытаться извлекать registrable domain из host (site.com из www.site.com)</summary>
        public bool UseRegistrableDomainHeuristic { get; set; } = true;
    }

    public sealed record WhoisAuditReport(
        string Host,
        string WhoisQueryDomain,
        bool Success,
        string? WhoisServer,
        DateTimeOffset? CreatedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        int? DomainAgeDays,
        int? DaysToExpire,
        string? ParseNote,
        string? Error
    );

    // =========================
    // Store (1 запрос WHOIS на host)
    // =========================

    public sealed class WhoisStore
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<WhoisAuditReport>>> _byHost =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host) => _byHost.TryRemove(host, out _);

        public Task<WhoisAuditReport> GetOrFetchAsync(
            string host,
            string whoisDomain,
            Func<CancellationToken, Task<WhoisAuditReport>> fetch,
            CancellationToken ct)
        {
            var lazy = _byHost.GetOrAdd(
                host,
                _ => new Lazy<Task<WhoisAuditReport>>(
                    () => fetch(CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }

        public bool TryGet(string host, out WhoisAuditReport? report)
        {
            report = null;
            if (_byHost.TryGetValue(host, out var lazy))
            {
                if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                {
                    report = lazy.Value.Result;
                    return true;
                }
            }
            return false;
        }
    }

    // =========================
    // WHOIS client (TCP 43) + IANA server discovery
    // =========================

    public sealed class WhoisClient
    {
        private readonly WhoisOptions _opt;

        // cache: tld -> whois server
        private readonly ConcurrentDictionary<string, string> _tldServerCache =
            new(StringComparer.OrdinalIgnoreCase);

        public WhoisClient(WhoisOptions opt) => _opt = opt;

        public async Task<(string whoisServer, string raw)> QueryAsync(string domain, CancellationToken ct)
        {
            domain = DomainUtil.ToAsciiDomain(domain);

            var tld = DomainUtil.GetTld(domain);
            if (string.IsNullOrWhiteSpace(tld))
                throw new InvalidOperationException("Не удалось определить TLD для WHOIS.");

            var server = await GetWhoisServerForTldAsync(tld, ct);

            var raw = await QueryServerAsync(server, domain, ct);
            return (server, raw);
        }

        private async Task<string> GetWhoisServerForTldAsync(string tld, CancellationToken ct)
        {
            if (_tldServerCache.TryGetValue(tld, out var cached))
                return cached;

            // 1) спросим whois.iana.org (тоже WHOIS)
            var ianaRaw = await QueryServerAsync("whois.iana.org", tld, ct);

            // в ответе IANA обычно есть строка: "whois: whois.verisign-grs.com"
            var m = Regex.Match(ianaRaw, @"(?im)^\s*whois:\s*(\S+)\s*$");
            if (!m.Success)
                throw new InvalidOperationException($"IANA не вернул whois-сервер для TLD: {tld}");

            var server = m.Groups[1].Value.Trim();
            _tldServerCache[tld] = server;
            return server;
        }

        private async Task<string> QueryServerAsync(string server, string query, CancellationToken ct)
        {
            using var client = new TcpClient();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_opt.TcpTimeoutSeconds));

            await client.ConnectAsync(server, 43, cts.Token);

            using var stream = client.GetStream();
            stream.ReadTimeout = _opt.TcpTimeoutSeconds * 1000;
            stream.WriteTimeout = _opt.TcpTimeoutSeconds * 1000;

            var bytes = Encoding.ASCII.GetBytes(query + "\r\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, cts.Token);
            await stream.FlushAsync(cts.Token);

            using var ms = new MemoryStream();
            var buf = new byte[16_384];

            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                }
                catch (IOException)
                {
                    break;
                }

                if (read <= 0) break;
                ms.Write(buf, 0, read);

                if (ms.Length > _opt.MaxResponseChars * 2L) // грубо, т.к. пока байты
                    break;
            }

            // WHOIS чаще ASCII, иногда UTF-8 — попробуем UTF-8, fallback ASCII
            var raw = TryDecodeUtf8(ms.ToArray());
            if (raw.Length > _opt.MaxResponseChars)
                raw = raw.Substring(0, _opt.MaxResponseChars);

            return raw;
        }

        private static string TryDecodeUtf8(byte[] bytes)
        {
            try { return Encoding.UTF8.GetString(bytes); }
            catch { return Encoding.ASCII.GetString(bytes); }
        }
    }

    // =========================
    // Parser (Creation/Expiry/Paid-till)
    // =========================

    internal static class WhoisParser
    {
        // ключи, которые чаще всего встречаются
        private static readonly string[] CreationKeys =
        {
            "Creation Date", "Created", "Created On", "Registered On", "Registration Time",
            "Domain Registration Date", "created:", "created on", "created-date",
        };

        private static readonly string[] ExpiryKeys =
        {
            "Registry Expiry Date", "Registrar Registration Expiration Date", "Expiration Date",
            "Expires On", "Expiry Date", "paid-till", "paid till", "expire:", "expires:",
        };

        public static (DateTimeOffset? createdUtc, DateTimeOffset? expiresUtc, string? note) TryParseDates(string raw)
        {
            DateTimeOffset? created = null;
            DateTimeOffset? expires = null;

            // нормализуем строки
            var lines = raw.Split('\n')
                .Select(l => l.Trim().Trim('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();

            foreach (var line in lines)
            {
                if (created is null && StartsWithAnyKey(line, CreationKeys, out var valC))
                {
                    if (TryParseDate(valC, out var dt))
                        created = dt.ToUniversalTime();
                }

                if (expires is null && StartsWithAnyKey(line, ExpiryKeys, out var valE))
                {
                    if (TryParseDate(valE, out var dt))
                        expires = dt.ToUniversalTime();
                }

                if (created is not null && expires is not null) break;
            }

            string? note = null;
            if (created is null && expires is null)
                note = "WHOIS ответ получен, но даты Created/Expires не распознаны (формат/ключи не совпали).";

            return (created, expires, note);
        }

        private static bool StartsWithAnyKey(string line, string[] keys, out string valuePart)
        {
            foreach (var k in keys)
            {
                // поддержка "key: value" и "key value"
                if (line.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0 && idx < line.Length - 1)
                        valuePart = line[(idx + 1)..].Trim();
                    else
                        valuePart = line[k.Length..].Trim();

                    return true;
                }
            }

            valuePart = "";
            return false;
        }

        private static bool TryParseDate(string s, out DateTimeOffset dto)
        {
            dto = default;

            s = s.Trim();

            // часто встречается: 2025-01-27T00:00:00Z / 2025-01-27 00:00:00 / 27-Jan-2025
            var formats = new[]
            {
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                "yyyy-MM-dd'T'HH:mm:ssZ",
                "yyyy-MM-dd'T'HH:mm:ss",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd",
                "dd-MMM-yyyy",
                "dd-MMM-yyyy HH:mm:ss",
                "dd.MM.yyyy",
                "dd.MM.yyyy HH:mm:ss",
                "yyyy.MM.dd",
                "yyyy.MM.dd HH:mm:ss",
            };

            // 1) Exact
            foreach (var f in formats)
            {
                if (DateTimeOffset.TryParseExact(s, f, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dto))
                    return true;
            }

            // 2) General
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dto))
                return true;

            if (DateTimeOffset.TryParse(s, CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dto))
                return true;

            return false;
        }
    }

    // =========================
    // Domain helpers
    // =========================

    internal static class DomainUtil
    {
        private static readonly IdnMapping _idn = new();

        // небольшой эвристический список “двухуровневых зон”
        private static readonly HashSet<string> KnownSecondLevelPublicSuffixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "co.uk","org.uk","gov.uk","ac.uk",
            "com.au","net.au","org.au",
            "com.br","com.tr"
        };

        public static string ToAsciiDomain(string domain)
        {
            domain = domain.Trim().TrimEnd('.');

            // если уже punycode, оставим как есть
            if (domain.Contains("xn--", StringComparison.OrdinalIgnoreCase))
                return domain.ToLowerInvariant();

            // IDN -> punycode
            try
            {
                return _idn.GetAscii(domain).ToLowerInvariant();
            }
            catch
            {
                return domain.ToLowerInvariant();
            }
        }

        public static string GetTld(string domain)
        {
            var parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return "";
            return parts[^1];
        }

        public static string GetRegistrableDomainHeuristic(string host)
        {
            host = host.Trim().TrimEnd('.');

            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return host;

            // если домен типа a.b.co.uk -> registrable = b.co.uk
            if (parts.Length >= 3)
            {
                var last2 = parts[^2] + "." + parts[^1];
                var last3 = parts[^3] + "." + last2;
                if (KnownSecondLevelPublicSuffixes.Contains(last2) || KnownSecondLevelPublicSuffixes.Contains(last3))
                {
                    // если last2 — известный public suffix (co.uk) -> берем 3 хвост
                    if (KnownSecondLevelPublicSuffixes.Contains(last2))
                        return parts[^3] + "." + last2;

                    // если last3 в списке — берем 4 хвост, но это редко; оставим last3
                    return last3;
                }
            }

            // по умолчанию eTLD+1 “последние 2”
            return parts[^2] + "." + parts[^1];
        }
    }

    // =========================
    // ILinkCheck: собираем WHOIS 1 раз на host
    // =========================

    public sealed class WhoisCollectorCheck : ILinkCheck
    {
        private readonly WhoisClient _client;
        private readonly WhoisStore _store;
        private readonly WhoisOptions _opt;

        public WhoisCollectorCheck(WhoisClient client, WhoisStore store, WhoisOptions opt)
        {
            _client = client;
            _store = store;
            _opt = opt;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            // В WHOIS смысла нет для неуспешных страниц; но host один и тот же,
            // поэтому можно триггерить на первой попавшейся.
            var host = ctx.FinalUri.Host;

            var whoisDomain = _opt.UseRegistrableDomainHeuristic
                ? DomainUtil.GetRegistrableDomainHeuristic(host)
                : host;

            WhoisAuditReport report;
            try
            {
                report = await _store.GetOrFetchAsync(
                    host: host,
                    whoisDomain: whoisDomain,
                    fetch: async (token) =>
                    {
                        try
                        {
                            var (server, raw) = await _client.QueryAsync(whoisDomain, token);

                            var (created, expires, note) = WhoisParser.TryParseDates(raw);

                            int? ageDays = null;
                            int? daysToExpire = null;

                            if (created is not null)
                                ageDays = (int)Math.Floor((DateTimeOffset.UtcNow - created.Value).TotalDays);

                            if (expires is not null)
                                daysToExpire = (int)Math.Ceiling((expires.Value - DateTimeOffset.UtcNow).TotalDays);

                            return new WhoisAuditReport(
                                Host: host,
                                WhoisQueryDomain: whoisDomain,
                                Success: true,
                                WhoisServer: server,
                                CreatedAtUtc: created,
                                ExpiresAtUtc: expires,
                                DomainAgeDays: ageDays,
                                DaysToExpire: daysToExpire,
                                ParseNote: note,
                                Error: null
                            );
                        }
                        catch (Exception ex)
                        {
                            return new WhoisAuditReport(
                                Host: host,
                                WhoisQueryDomain: whoisDomain,
                                Success: false,
                                WhoisServer: null,
                                CreatedAtUtc: null,
                                ExpiresAtUtc: null,
                                DomainAgeDays: null,
                                DaysToExpire: null,
                                ParseNote: null,
                                Error: ex.Message
                            );
                        }
                    },
                    ct: ct);

            }
            catch (Exception ex)
            {
                report = new WhoisAuditReport(
                    Host: host,
                    WhoisQueryDomain: whoisDomain,
                    Success: false,
                    WhoisServer: null,
                    CreatedAtUtc: null,
                    ExpiresAtUtc: null,
                    DomainAgeDays: null,
                    DaysToExpire: null,
                    ParseNote: null,
                    Error: ex.Message
                );
            }

            // Page-level issues (минимально)
            var issues = new List<LinkIssue>();

            if (!report.Success)
            {
                issues.Add(new LinkIssue(
                    "WHOIS_UNAVAILABLE",
                    $"WHOIS недоступен/не распознан: {report.Error}",
                    IssueSeverity.Warning));
                return issues;
            }

            if (report.CreatedAtUtc is null)
            {
                issues.Add(new LinkIssue(
                    "WHOIS_CREATED_UNKNOWN",
                    "WHOIS: не удалось определить дату регистрации (Created).",
                    IssueSeverity.Info));
            }

            if (report.ExpiresAtUtc is null)
            {
                issues.Add(new LinkIssue(
                    "WHOIS_EXPIRES_UNKNOWN",
                    "WHOIS: не удалось определить дату окончания регистрации (Expires/Paid-till).",
                    IssueSeverity.Info));
            }
            else
            {
                if (report.DaysToExpire is int dte)
                {
                    if (dte < 0)
                    {
                        issues.Add(new LinkIssue(
                            "DOMAIN_EXPIRED",
                            $"Домен просрочен (Expires: {report.ExpiresAtUtc:yyyy-MM-dd}).",
                            IssueSeverity.Warning));
                    }
                    else if (dte <= _opt.ExpiringSoonDays)
                    {
                        issues.Add(new LinkIssue(
                            "DOMAIN_EXPIRING_SOON",
                            $"Домен скоро истекает: осталось {dte} дн. (Expires: {report.ExpiresAtUtc:yyyy-MM-dd}).",
                            IssueSeverity.Warning));
                    }
                }
            }

            return issues;
        }
    }

    // =========================
    // Controller: report
    // =========================

    [ApiController]
    [Route("api/crawl")]
    public sealed class WhoisAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly WhoisStore _whois;

        public WhoisAuditController(JobStore store, WhoisStore whois)
        {
            _store = store;
            _whois = whois;
        }

        [HttpGet("{jobId:guid}/whois-audit")]
        public ActionResult<WhoisAuditReport> WhoisAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;

            if (_whois.TryGet(host, out var rep) && rep is not null)
                return Ok(rep);

            // если checks ещё не запускали или whois не успел
            return Ok(new WhoisAuditReport(
                Host: host,
                WhoisQueryDomain: DomainUtil.GetRegistrableDomainHeuristic(host),
                Success: false,
                WhoisServer: null,
                CreatedAtUtc: null,
                ExpiresAtUtc: null,
                DomainAgeDays: null,
                DaysToExpire: null,
                ParseNote: "WHOIS ещё не был собран. Запустите /checks/start и дождитесь окончания.",
                Error: null
            ));
        }
    }
}
