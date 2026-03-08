using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class TitleDescriptionChecks : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            // ✅ Только успешные HTML
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (ctx.Document is null)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (string.IsNullOrWhiteSpace(ctx.ContentType) ||
                !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());


            var issues = new List<LinkIssue>();

            var titles = ctx.Document.DocumentNode.SelectNodes("//title");
            var titleCount = titles?.Count ?? 0;

            if (titleCount == 0)
                issues.Add(new LinkIssue("NO_TITLE", "Отсутствует <title>", IssueSeverity.Warning));
            else if (titleCount > 1)
                issues.Add(new LinkIssue("MULTI_TITLE", $"Несколько <title>: {titleCount}", IssueSeverity.Info));

            var descNodes = ctx.Document.DocumentNode.SelectNodes(
                "//meta[translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='description']");
            var descCount = descNodes?.Count ?? 0;

            if (descCount == 0)
                issues.Add(new LinkIssue("NO_DESCRIPTION", "Отсутствует meta description", IssueSeverity.Warning));
            else if (descCount > 1)
                issues.Add(new LinkIssue("MULTI_DESCRIPTION", $"Несколько meta description: {descCount}", IssueSeverity.Info));

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }

    }
}