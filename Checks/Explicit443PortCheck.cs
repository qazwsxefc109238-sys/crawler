using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class Explicit443PortCheck : ILinkCheck
    {
        private static readonly Regex Re = new(@"^https?://[^/]+:443(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (!string.Equals(ctx.OriginalUri.AbsolutePath, "/", StringComparison.Ordinal))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var has = Re.IsMatch(ctx.OriginalUrl) || Re.IsMatch(ctx.FinalUrl);
            if (!has)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
            {
                new LinkIssue("URL_EXPLICIT_443", $"В URL явно указан порт :443 ({ctx.FinalUrl})", IssueSeverity.Info)
            });
        }
    }
}