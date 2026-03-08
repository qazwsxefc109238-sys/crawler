using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class LinksTextAndCountsCheck : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.Document is null)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (string.IsNullOrWhiteSpace(ctx.ContentType) ||
                !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var nodes = ctx.Document.DocumentNode.SelectNodes("//a[@href]");
            if (nodes is null || nodes.Count == 0)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            int emptyAnchor = 0;
            var internalSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var externalSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in nodes)
            {
                var href = (a.GetAttributeValue("href", "") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(href)) continue;

                // не считаем “ссылками” якоря/служебные схемы
                if (href.StartsWith("#")) continue;
                if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;

                var text = a.InnerText?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(text)) emptyAnchor++;

                if (!TryResolve(ctx.FinalUri, href, out var u)) continue;

                var normalized = RemoveFragment(u);

                if (u.Host.Equals(ctx.FinalUri.Host, StringComparison.OrdinalIgnoreCase))
                    internalSet.Add(normalized);
                else
                    externalSet.Add(normalized);
            }

            var issues = new List<LinkIssue>();

            if (emptyAnchor > 0)
                issues.Add(new LinkIssue("EMPTY_ANCHOR", $"Ссылок без анкоров (пустой текст): {emptyAnchor}", IssueSeverity.Info));

            issues.Add(new LinkIssue("OUT_INTERNAL_COUNT", $"Исходящих внутренних ссылок (unique): {internalSet.Count}", IssueSeverity.Info));
            issues.Add(new LinkIssue("OUT_EXTERNAL_COUNT", $"Исходящих внешних ссылок (unique): {externalSet.Count}", IssueSeverity.Info));

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);

            static bool TryResolve(Uri baseUri, string raw, out Uri uri)
            {
                uri = default!;
                if (raw.StartsWith("//", StringComparison.Ordinal)) raw = baseUri.Scheme + ":" + raw;
                if (Uri.TryCreate(raw, UriKind.Absolute, out var abs)) { uri = abs; return true; }
                try { uri = new Uri(baseUri, raw); return true; } catch { return false; }
            }

            static string RemoveFragment(Uri u)
            {
                if (string.IsNullOrEmpty(u.Fragment)) return u.AbsoluteUri;
                var b = new UriBuilder(u) { Fragment = "" };
                return b.Uri.AbsoluteUri;
            }
        }

    }
}