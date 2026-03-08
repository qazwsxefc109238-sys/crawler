using Crawler_project.Checks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Models
{
    /// <summary>
    /// Определение "страны хостинга" по публичному IP через RDAP (RIR).
    /// Возвращает country code (ISO-2) и, если возможно, человекочитаемое имя.
    /// </summary>
    public sealed class HostingGeoChecks : ILinkCheck
    {
        private readonly DnsAuditService _dns;
        private readonly RdapGeoService _geo;

        public HostingGeoChecks(DnsAuditService dns, RdapGeoService geo)
        {
            _dns = dns;
            _geo = geo;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            // Хостовый чек — только на главной
            if (!string.Equals(ctx.FinalUri.AbsolutePath, "/", StringComparison.Ordinal))
                return Array.Empty<LinkIssue>();

            var host = ctx.FinalUri.Host;
            var report = await _dns.GetAsync(host, ct);

            // Берём публичные IP, предпочитаем IPv4
            var publicIps = report.IpAddresses
                .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
                .Where(ip => ip is not null && IsPublicIp(ip))
                .Cast<IPAddress>()
                .OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Select(ip => ip.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3) // чтобы не делать много запросов, если у хоста пачка IP
                .ToList();

            if (publicIps.Count == 0)
            {
                return new[]
                {
                    new LinkIssue("HOST_GEO_NO_PUBLIC_IP", $"Не удалось получить публичный IP для определения страны хостинга: {host}", IssueSeverity.Warning)
                };
            }

            var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ip in publicIps)
            {
                var cc = await _geo.GetCountryCodeAsync(ip, ct);
                if (!string.IsNullOrWhiteSpace(cc))
                    countries.Add(cc.Trim().ToUpperInvariant());
            }

            if (countries.Count == 0)
            {
                return new[]
                {
                    new LinkIssue("HOST_GEO_UNKNOWN", $"Не удалось определить страну хостинга по RDAP (IP: {string.Join(", ", publicIps)})", IssueSeverity.Warning)
                };
            }

            var formatted = countries
                .Select(cc =>
                {
                    if (TryGetCountryName(cc, out var name))
                        return $"{cc} ({name})";
                    return cc;
                })
                .OrderBy(s => s)
                .ToList();

            return new[]
            {
                new LinkIssue(
                    "HOST_COUNTRY",
                    $"Страна хостинга (по RDAP): {string.Join(", ", formatted)}",
                    IssueSeverity.Info)
            };
        }

        private static bool TryGetCountryName(string cc, out string name)
        {
            name = "";
            try
            {
                // имя будет в культуре окружения (часто EN), но это лучше чем ничего
                name = new RegionInfo(cc).DisplayName;
                return !string.IsNullOrWhiteSpace(name);
            }
            catch { return false; }
        }

        private static bool IsPublicIp(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return false;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10) return false;                         // 10.0.0.0/8
                if (b[0] == 127) return false;                        // 127.0.0.0/8
                if (b[0] == 169 && b[1] == 254) return false;         // link-local
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false; // 172.16/12
                if (b[0] == 192 && b[1] == 168) return false;         // 192.168/16
                if (b[0] >= 224) return false;                        // multicast/reserved
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.Equals(IPAddress.IPv6Loopback)) return false;
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6Teredo) return false;

                var b = ip.GetAddressBytes();
                // fc00::/7 unique local
                if ((b[0] & 0xFE) == 0xFC) return false;
                return true;
            }

            return false;
        }
    }

    public sealed class RdapGeoService
    {
        private static readonly string[] RdapIpBases =
        {
            "https://rdap.arin.net/registry/ip/",
            "https://rdap.db.ripe.net/ip/",
            "https://rdap.apnic.net/ip/",
            "https://rdap.lacnic.net/rdap/ip/",
            "https://rdap.afrinic.net/rdap/ip/",
        };

        private readonly IHttpClientFactory _httpFactory;
        private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cache = new(StringComparer.OrdinalIgnoreCase);

        public RdapGeoService(IHttpClientFactory httpFactory)
        {
            _httpFactory = httpFactory;
        }

        public Task<string?> GetCountryCodeAsync(string ip, CancellationToken ct)
        {
            var lazy = _cache.GetOrAdd(ip, _ => new Lazy<Task<string?>>(
                () => QueryRdapCountryAsync(ip, CancellationToken.None)));

            return lazy.Value.WaitAsync(ct);
        }

        private async Task<string?> QueryRdapCountryAsync(string ip, CancellationToken ct)
        {
            var http = _httpFactory.CreateClient("rdap");

            foreach (var baseUrl in RdapIpBases)
            {
                try
                {
                    var url = baseUrl + Uri.EscapeDataString(ip);
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Accept.ParseAdd("application/rdap+json, application/json");

                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                    // если это не "наш" реестр, часто 404/400
                    if (!resp.IsSuccessStatusCode)
                        continue;

                    await using var s = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

                    if (TryExtractCountryCode(doc.RootElement, out var cc))
                        return cc;
                }
                catch
                {
                    // пробуем следующий реестр
                }
            }

            return null;
        }

        private static bool TryExtractCountryCode(JsonElement root, out string cc)
        {
            cc = "";

            // Наиболее частое: country
            if (root.TryGetProperty("country", out var c) && c.ValueKind == JsonValueKind.String)
            {
                cc = (c.GetString() ?? "").Trim();
                if (cc.Length == 2) return true;
            }

            // Фоллбеки на случай вариаций
            if (root.TryGetProperty("countryCode", out var c2) && c2.ValueKind == JsonValueKind.String)
            {
                cc = (c2.GetString() ?? "").Trim();
                if (cc.Length == 2) return true;
            }

            if (root.TryGetProperty("cc", out var c3) && c3.ValueKind == JsonValueKind.String)
            {
                cc = (c3.GetString() ?? "").Trim();
                if (cc.Length == 2) return true;
            }

            // Иногда country лежит в entities
            if (root.TryGetProperty("entities", out var ents) && ents.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in ents.EnumerateArray())
                {
                    if (e.TryGetProperty("country", out var ec) && ec.ValueKind == JsonValueKind.String)
                    {
                        cc = (ec.GetString() ?? "").Trim();
                        if (cc.Length == 2) return true;
                    }
                }
            }

            return false;
        }
    }
}