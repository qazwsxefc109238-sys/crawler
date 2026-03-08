using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class TitleCheck : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.Document is null)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var title = ctx.Document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(title))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                {
                    new LinkIssue("TITLE_EMPTY", "Отсутствует <title>", IssueSeverity.Warning)
                });

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }
    }
}
