using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class H1Check : ILinkCheck
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

            var h1 = ctx.Document.DocumentNode.SelectNodes("//h1")?.ToList() ?? new List<HtmlAgilityPack.HtmlNode>();

            if (h1.Count == 0)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[] { new LinkIssue("NO_H1", "Отсутствует <h1>", IssueSeverity.Warning) });

            if (h1.Count > 1)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[] { new LinkIssue("H1_MULTIPLE", $"Несколько <h1>: {h1.Count}", IssueSeverity.Info) });

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }

    }
}
