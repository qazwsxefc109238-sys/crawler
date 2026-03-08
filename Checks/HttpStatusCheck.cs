using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class HttpStatusCheck : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.FinalStatusCode == 0)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                {
            new LinkIssue("UNAVAILABLE", "—траница недоступна", IssueSeverity.Error)
        });

            if (ctx.FinalStatusCode >= 500)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                {
            new LinkIssue("HTTP_5XX", $"HTTP {ctx.FinalStatusCode}", IssueSeverity.Error)
        });

            if (ctx.FinalStatusCode >= 400)
            {
                var hint = ctx.FinalStatusCode == 403
                    ? " (возможна блокировка проверьте частоту запросов/cookies)"
                    : "";

                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                {
            new LinkIssue("HTTP_4XX", $"HTTP {ctx.FinalStatusCode}{hint}", IssueSeverity.Error)
        });
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }

    }
}