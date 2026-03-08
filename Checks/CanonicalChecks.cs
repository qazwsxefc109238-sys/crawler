using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class CanonicalChecks : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var issues = new List<LinkIssue>();

            var canonNodes = ctx.Document.DocumentNode.SelectNodes("//link[translate(@rel,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='canonical']");
            if (canonNodes is null || canonNodes.Count == 0)
            {
                issues.Add(new LinkIssue("NO_CANONICAL", "rel=canonical не найден", IssueSeverity.Info));
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
            }

            if (canonNodes.Count > 1)
                issues.Add(new LinkIssue("MULTI_CANONICAL", $"Несколько rel=canonical: {canonNodes.Count}", IssueSeverity.Warning));

            // canonical в body
            var canonInBody = ctx.Document.DocumentNode.SelectNodes("//body//link[translate(@rel,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='canonical']");
            if (canonInBody is not null && canonInBody.Count > 0)
                issues.Add(new LinkIssue("CANONICAL_IN_BODY", "rel=canonical найден в <body>", IssueSeverity.Warning));

            // кросс-доменный canonical
            foreach (var n in canonNodes)
            {
                var href = n.GetAttributeValue("href", "").Trim();
                if (string.IsNullOrWhiteSpace(href)) continue;

                Uri canonUri;
                if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) canonUri = abs;
                else canonUri = new Uri(ctx.FinalUri, href);

                if (!canonUri.Host.Equals(ctx.FinalUri.Host, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new LinkIssue("CROSS_DOMAIN_CANONICAL", $"Кросс-доменный canonical: {canonUri}", IssueSeverity.Warning));
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }
    }
}
