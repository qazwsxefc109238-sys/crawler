using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class ServerErrorLeakCheck : ILinkCheck
    {
        private static readonly string[] Patterns =
        {
            "Warning:", "Fatal error", "Parse error",
            "Undefined index", "Undefined variable",
            "mysql_", "mysqli_", "PDOException",
            "SQLSTATE[", "Call to a member function"
        };

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ctx.Html))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            foreach (var p in Patterns)
            {
                if (ctx.Html!.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                    {
                        new LinkIssue("SERVER_ERROR_LEAK", $"В HTML обнаружена сигнатура ошибки: {p}", IssueSeverity.Error)
                    });
                }
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }
    }
}