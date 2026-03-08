using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class DomainVariantsCheck : ILinkCheck
    {
        private readonly IHttpClientFactory _httpFactory;

        private static readonly ConcurrentDictionary<string, byte> _done =
            new(StringComparer.OrdinalIgnoreCase);

        public DomainVariantsCheck(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (!string.Equals(ctx.OriginalUri.AbsolutePath, "/", StringComparison.Ordinal))
                return Array.Empty<LinkIssue>();

            var baseHost = ctx.FinalUri.Host;
            if (!_done.TryAdd(baseHost, 0))
                return Array.Empty<LinkIssue>();

            var naked = baseHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? baseHost[4..] : baseHost;
            var www = baseHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? baseHost : "www." + baseHost;

            var variants = new (string Name, Uri Uri)[]
            {
                ("https", new Uri($"https://{naked}/")),
                ("http",  new Uri($"http://{naked}/")),
                ("https+www", new Uri($"https://{www}/")),
                ("http+www",  new Uri($"http://{www}/")),
            }
            .GroupBy(x => x.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

            var expectedHost = ctx.FinalUri.Host;
            var expectedScheme = ctx.FinalUri.Scheme;

            var results = new List<string>();
            var mismatches = new List<string>();
            var fails = new List<string>();

            foreach (var v in variants)
            {
                var r = await ResolveFinalAsync(v.Uri, maxRedirects: 10, ct);

                if (!r.Ok)
                {
                    fails.Add($"{v.Name}: FAIL ({r.Error})");
                    continue;
                }

                results.Add($"{v.Name}: {v.Uri} -> {r.Final} [{r.Status}]");

                if (!string.Equals(r.Final.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(r.Final.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"{v.Name}: {v.Uri} -> {r.Final}");
                }
            }

            if (fails.Count == 0 && mismatches.Count == 0)
            {
                return new[]
                {
                    new LinkIssue(
                        "DOMAIN_VARIANTS_OK",
                        $"Варианты домена корректны: все приводят к {expectedScheme}://{expectedHost}/",
                        IssueSeverity.Info)
                };
            }

            var msgParts = new List<string>();
            if (mismatches.Count > 0) msgParts.Add("Несовпадения: " + string.Join("; ", mismatches.Take(5)));
            if (fails.Count > 0) msgParts.Add("Ошибки: " + string.Join("; ", fails.Take(5)));

            return new[]
            {
                new LinkIssue(
                    "DOMAIN_VARIANTS_PROBLEM",
                    $"Проблема с вариантами домена. {string.Join(" | ", msgParts)}",
                    IssueSeverity.Warning)
            };
        }

        private async Task<(bool Ok, Uri Final, int Status, string? Error)> ResolveFinalAsync(Uri start, int maxRedirects, CancellationToken ct)
        {
            var http = _httpFactory.CreateClient("crawler_noredirect");

            Uri current = start;

            try
            {
                for (int i = 0; i <= maxRedirects; i++)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, current);
                    var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                    var status = (int)resp.StatusCode;

                    if (status >= 300 && status < 400 && resp.Headers.Location is not null)
                    {
                        var loc = resp.Headers.Location;
                        current = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                        continue;
                    }

                    return (true, current, status, null);
                }

                return (false, start, 0, "TOO_MANY_REDIRECTS");
            }
            catch (Exception ex)
            {
                return (false, start, 0, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}