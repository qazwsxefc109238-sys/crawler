using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class NoindexChecks : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            // X-Robots-Tag header
            if (ctx.Headers.TryGetValue("X-Robots-Tag", out var xrt) &&
                xrt.IndexOf("noindex", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                issues.Add(new LinkIssue("NOINDEX_HEADER", $"X-Robots-Tag содержит noindex: {xrt}", IssueSeverity.Warning));
            }

            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);

            var meta = ctx.Document.DocumentNode.SelectSingleNode(
                "//meta[translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='robots']");

            if (meta is not null)
            {
                var content = meta.GetAttributeValue("content", "");
                if (content.IndexOf("noindex", StringComparison.OrdinalIgnoreCase) >= 0)
                    issues.Add(new LinkIssue("NOINDEX_META", $"meta robots содержит noindex: {content}", IssueSeverity.Warning));
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }
    }
}
