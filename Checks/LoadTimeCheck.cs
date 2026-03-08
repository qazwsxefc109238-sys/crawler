using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class LoadTimeCheck : ILinkCheck
    {
        private readonly double _warnSeconds;

        public LoadTimeCheck(double warnSeconds = 3.0) => _warnSeconds = warnSeconds;

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.TotalTime.TotalSeconds >= _warnSeconds)
            {
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                {
                    new LinkIssue("SLOW_PAGE", $"Долгая загрузка: {ctx.TotalTime.TotalSeconds:F2} сек", IssueSeverity.Info)
                });
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }
    }
}
