using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class LinkCheckRunner
    {
        private readonly IEnumerable<ILinkCheck> _checks;

        public LinkCheckRunner(IEnumerable<ILinkCheck> checks) => _checks = checks;

        public async Task<IReadOnlyList<LinkIssue>> RunAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            foreach (var check in _checks)
            {
                try
                {
                    var res = await check.CheckAsync(ctx, ct);
                    if (res != null) issues.AddRange(res);
                }
                catch (Exception ex)
                {
                    issues.Add(new LinkIssue("CHECK_FAILED", $"{check.GetType().Name}: {ex.Message}", IssueSeverity.Error));
                }
            }

            return issues;
        }
    }
}
