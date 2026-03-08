using Crawler_project.Checks;
using HtmlAgilityPack;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Models
{
    /// <summary>
    /// Опции DNSBL/RBL проверок.
    /// ВАЖНО: многие списки имеют ограничения/лицензию/лимиты.
    /// Держите списки пустыми по умолчанию и включайте осознанно (или через конфиг).
    /// </summary>
    public sealed class RblOptions
    {
        /// <summary>Таймаут одной DNSBL-запроса (мс).</summary>
        public int QueryTimeoutMs { get; set; } = 2500;

        /// <summary>Глобальный лимит параллельных DNSBL-запросов.</summary>
        public int MaxConcurrency { get; set; } = 15;

        /// <summary>Какие IP-RBL зоны проверять для IP хоста (IPv4/IPv6). Пример: "zen.spamhaus.org".</summary>
        public string[] IpZones { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Какие domain-DNSBL зоны проверять для внешних доменов.
        /// Примеры: "dbl.spamhaus.org", "multi.surbl.org", "multi.uribl.com".
        /// </summary>
        public string[] DomainZones { get; set; } = Array.Empty<string>();

        /// <summary>Сколько доменов максимум проверять на страницу (защита от explosion).</summary>
        public int MaxExternalDomainsPerPage { get; set; } = 40;

        /// <summary>Сколько примеров/совпадений выводить в тексте issue.</summary>
        public int SampleHitsPerIssue { get; set; } = 5;

        /// <summary>Считать www.example.com и example.com одним доменом (для проверки доменных DNSBL).</summary>
        public bool StripWww { get; set; } = true;

        /// <summary>
        /// Дополнительно проверять «базовый домен» (грубая эвристика last-2 labels) помимо точного host.
        /// Для идеала нужен Public Suffix List.
        /// </summary>
        public bool AlsoCheckBaseDomainHeuristic { get; set; } = true;
    }

    public sealed record RblHit(string Target, string Zone, IReadOnlyList<string> Codes);
    public sealed record RblLookupResult(bool Listed, IReadOnlyList<string> Codes, string? Error);

    /// <summary>
    /// DNSBL/RBL клиент с кэшем.
    /// Реализация через системный резолвер (Dns.GetHostAddressesAsync) + принудительный timeout.
    /// </summary>
    public sealed class RblAuditService
    {
        private readonly RblOptions _opt;
        private readonly SemaphoreSlim _gate;
        private readonly ConcurrentDictionary<string, Lazy<Task<RblLookupResult>>> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly IdnMapping _idn = new();

        public RblAuditService(RblOptions opt)
        {
            _opt = opt;
            _gate = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrency));
        }

        public Task<RblLookupResult> QueryAAsync(string qname, CancellationToken ct)
        {
            qname = qname.Trim().TrimEnd('.');

            var lazy = _cache.GetOrAdd(qname, _ => new Lazy<Task<RblLookupResult>>(
                () => QueryAInternalAsync(qname, CancellationToken.None)));

            return lazy.Value.WaitAsync(ct);
        }

        public async Task<RblLookupResult> CheckIpAsync(IPAddress ip, string zone, CancellationToken ct)
        {
            zone = NormalizeZone(zone);
            if (zone.Length == 0) return new RblLookupResult(false, Array.Empty<string>(), "EMPTY_ZONE");

            var q = BuildIpQueryName(ip, zone);
            return await QueryAAsync(q, ct);
        }

        public async Task<RblLookupResult> CheckDomainAsync(string domainOrHost, string zone, CancellationToken ct)
        {
            zone = NormalizeZone(zone);
            if (zone.Length == 0) return new RblLookupResult(false, Array.Empty<string>(), "EMPTY_ZONE");

            var d = NormalizeDomain(domainOrHost);
            if (d.Length == 0) return new RblLookupResult(false, Array.Empty<string>(), "EMPTY_DOMAIN");

            var q = $"{d}.{zone}";
            return await QueryAAsync(q, ct);
        }

        private async Task<RblLookupResult> QueryAInternalAsync(string qname, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                try
                {
                    var addrs = await WithTimeout(System.Net.Dns.GetHostAddressesAsync(qname), _opt.QueryTimeoutMs, ct);

                    // DNSBL: «listed» если вернулся A (обычно 127.0.0.x)
                    if (addrs is null || addrs.Length == 0)
                        return new RblLookupResult(false, Array.Empty<string>(), null);

                    var codes = addrs
                        .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    return new RblLookupResult(true, codes, null);
                }
                catch (TimeoutException)
                {
                    return new RblLookupResult(false, Array.Empty<string>(), "TIMEOUT");
                }
                catch (Exception ex)
                {
                    // HostNotFound/SocketException => чаще всего «не listed», это норм.
                    // Для диагностики вернём тип ошибки (не роняем чек).
                    return new RblLookupResult(false, Array.Empty<string>(), ex.GetType().Name);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private string NormalizeDomain(string host)
        {
            host = host.Trim().TrimEnd('.');
            if (host.Length == 0) return "";

            try { host = _idn.GetAscii(host); }
            catch { return ""; }

            host = host.ToLowerInvariant();

            if (_opt.StripWww && host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                host = host.Substring(4);

            return host;
        }

        private static string NormalizeZone(string zone)
            => (zone ?? "").Trim().TrimEnd('.').ToLowerInvariant();

        private static string BuildIpQueryName(IPAddress ip, string zone)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes(); // reverse octets
                return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.{zone}";
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                // reverse nibbles: 32 hex chars -> reverse with dots
                var hex = Convert.ToHexString(ip.GetAddressBytes()).ToLowerInvariant();
                var rev = string.Join('.', hex.Reverse());
                return $"{rev}.{zone}";
            }

            return $"{ip}.{zone}";
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, int timeoutMs, CancellationToken ct)
        {
            if (timeoutMs <= 0) return await task;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(timeoutMs, cts.Token);

            var completed = await Task.WhenAny(task, delay);
            if (completed == task)
            {
                cts.Cancel();
                return await task;
            }

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }
    }

    /// <summary>
    /// 1) Проверка «хостинг IP в RBL/DNSBL».
    /// Выполняется ОДИН раз на хост (только для главной страницы "/").
    /// </summary>
    public sealed class HostingRblChecks : ILinkCheck
    {
        private readonly DnsAuditService _dns;
        private readonly RblAuditService _rbl;
        private readonly RblOptions _opt;

        public HostingRblChecks(DnsAuditService dns, RblAuditService rbl, RblOptions opt)
        {
            _dns = dns;
            _rbl = rbl;
            _opt = opt;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (_opt.IpZones.Length == 0)
            {
                // чтобы в сводке было видно, что проверка отключена конфигом
                return new[]
                {
        new LinkIssue("HOSTING_RBL_DISABLED",
            "RBL/DNSBL: проверка отключена (RblOptions.IpZones пустой).",
            IssueSeverity.Info)
    };
            }

            if (!string.Equals(ctx.FinalUri.AbsolutePath, "/", StringComparison.Ordinal))
                return Array.Empty<LinkIssue>();

            var host = ctx.FinalUri.Host;
            var report = await _dns.GetAsync(host, ct);

            var ips = report.IpAddresses
                .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
                .Where(ip => ip is not null)
                .Cast<IPAddress>()
                .ToList();

            if (ips.Count == 0)
            {
                return new[]
                {
                    new LinkIssue("RBL_NO_IP", $"RBL/DNSBL проверка пропущена: нет A/AAAA для {host}", IssueSeverity.Warning)
                };
            }

            var hits = new List<RblHit>();

            foreach (var ip in ips)
            {
                foreach (var zone in _opt.IpZones)
                {
                    ct.ThrowIfCancellationRequested();
                    var res = await _rbl.CheckIpAsync(ip, zone, ct);
                    if (res.Listed)
                        hits.Add(new RblHit(ip.ToString(), zone, res.Codes));
                }
            }

            if (hits.Count == 0)
            {
                return new[]
                {
                 new LinkIssue("HOSTING_RBL_CLEAN",
                 $"RBL/DNSBL: совпадений нет. IP: {string.Join(", ", ips.Select(x => x.ToString()))}. Zones: {string.Join(", ", _opt.IpZones)}",
                 IssueSeverity.Info)
                };
            }

            var sample = hits
                .Take(Math.Max(1, _opt.SampleHitsPerIssue))
                .Select(h => $"{h.Target} в {h.Zone}{(h.Codes.Count > 0 ? " (" + string.Join(",", h.Codes) + ")" : "")}")
                .ToArray();

            return new[]
            {
                new LinkIssue("HOSTING_RBL_LISTED",
                    $"IP адрес(а) хоста {host} найдены в RBL/DNSBL: {string.Join("; ", sample)}",
                    IssueSeverity.Warning)
            };
        }
    }

    /// <summary>
    /// 2) Проверка «на странице есть внешние ссылки/ресурсы на домены из domain-DNSBL (DBL/SURBL/URIBL…)».
    /// </summary>
    public sealed class OutgoingDomainBlacklistChecks : ILinkCheck
    {
        private readonly RblAuditService _rbl;
        private readonly RblOptions _opt;

        public OutgoingDomainBlacklistChecks(RblAuditService rbl, RblOptions opt)
        {
            _rbl = rbl;
            _opt = opt;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (_opt.DomainZones.Length == 0)
                return Array.Empty<LinkIssue>();

            if (ctx.Document is null)
                return Array.Empty<LinkIssue>();

            var baseHost = ctx.FinalUri.Host;
            var externals = ExtractExternalHosts(ctx.Document, ctx.FinalUri, baseHost, _opt.MaxExternalDomainsPerPage);

            if (externals.Count == 0)
                return Array.Empty<LinkIssue>();

            var hits = new List<RblHit>();

            foreach (var host in externals)
            {
                foreach (var candidate in DomainCandidates(host, _opt.StripWww, _opt.AlsoCheckBaseDomainHeuristic))
                {
                    foreach (var zone in _opt.DomainZones)
                    {
                        ct.ThrowIfCancellationRequested();
                        var res = await _rbl.CheckDomainAsync(candidate, zone, ct);
                        if (res.Listed)
                            hits.Add(new RblHit(candidate, zone, res.Codes));
                    }
                }
            }

            if (hits.Count == 0)
                return Array.Empty<LinkIssue>();

            var grouped = hits
                .GroupBy(h => h.Target, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Domain = g.Key,
                    Zones = g.GroupBy(x => x.Zone, StringComparer.OrdinalIgnoreCase)
                            .Select(zg => $"{zg.Key}{(zg.SelectMany(x => x.Codes).Any() ? " (" + string.Join(",", zg.SelectMany(x => x.Codes).Distinct(StringComparer.OrdinalIgnoreCase)) + ")" : "")}")
                            .ToArray()
                })
                .OrderByDescending(x => x.Zones.Length)
                .Take(Math.Max(1, _opt.SampleHitsPerIssue))
                .ToArray();

            var sample = grouped
                .Select(x => $"{x.Domain}: {string.Join(", ", x.Zones)}")
                .ToArray();

            return new[]
            {
                new LinkIssue("OUTGOING_DOMAIN_DNSBL",
                    $"На странице есть ссылки/ресурсы на домены из DNSBL: {string.Join("; ", sample)}",
                    IssueSeverity.Warning)
            };
        }

        private static HashSet<string> ExtractExternalHosts(HtmlDocument doc, Uri baseUri, string baseHost, int max)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var xpaths = new (string xpath, string attr)[]
            {
                ("//a[@href]", "href"),
                ("//img[@src]", "src"),
                ("//script[@src]", "src"),
                ("//link[@href]", "href"),
                ("//iframe[@src]", "src"),
            };

            foreach (var (xpath, attr) in xpaths)
            {
                var nodes = doc.DocumentNode.SelectNodes(xpath);
                if (nodes is null) continue;

                foreach (var n in nodes)
                {
                    if (set.Count >= max) return set;

                    var raw = n.GetAttributeValue(attr, null);
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    raw = raw.Trim();
                    if (raw.StartsWith("#")) continue;
                    if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                    if (!Uri.TryCreate(baseUri, raw, out var u)) continue;
                    if (!(u.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || u.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var h = u.Host;
                    if (string.IsNullOrWhiteSpace(h)) continue;

                    if (h.Equals(baseHost, StringComparison.OrdinalIgnoreCase))
                        continue;

                    set.Add(h.Trim().TrimEnd('.'));
                }
            }

            return set;
        }

        private static IEnumerable<string> DomainCandidates(string host, bool stripWww, bool alsoBase)
        {
            host = host.Trim().TrimEnd('.');
            if (host.Length == 0) yield break;

            host = host.ToLowerInvariant();

            string h = host;
            if (stripWww && h.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                h = h.Substring(4);

            yield return h;

            if (!alsoBase) yield break;

            // грубая эвристика: last-2 labels (лучше заменить на PSL, если будет нужно)
            var labels = h.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (labels.Length >= 3)
                yield return labels[^2] + "." + labels[^1];
        }
    }
}