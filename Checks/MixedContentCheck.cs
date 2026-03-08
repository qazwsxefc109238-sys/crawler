using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class MixedContentCheck : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (ctx.Document is null ||
                string.IsNullOrWhiteSpace(ctx.ContentType) ||
                !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (!ctx.FinalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            // src/href
            var nodes = ctx.Document.DocumentNode.SelectNodes("//*[@src or @href or @style] | //style");
            if (nodes is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            bool mixed = nodes.Any(n =>
            {
                var src = n.GetAttributeValue("src", string.Empty);
                var href = n.GetAttributeValue("href", string.Empty);
                var style = n.GetAttributeValue("style", string.Empty);
                var inner = n.Name.Equals("style", StringComparison.OrdinalIgnoreCase) ? (n.InnerText ?? "") : "";

                return StartsWithHttp(src) || StartsWithHttp(href) ||
                       ContainsHttpUrl(style) || ContainsHttpUrl(inner);
            });

            if (!mixed) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
            {
                new LinkIssue("MIXED_CONTENT",
                    "Найден смешанный контент (http:// ресурсы на https странице)",
                    IssueSeverity.Warning)
            });

            static bool StartsWithHttp(string? s) =>
                !string.IsNullOrWhiteSpace(s) && s.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase);

            static bool ContainsHttpUrl(string? s) =>
                !string.IsNullOrWhiteSpace(s) && s.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
