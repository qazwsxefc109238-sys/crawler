using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class DoctypeAndSizeChecks : ILinkCheck
    {
        private const long BigPageBytes = 2_000_000; // 2MB

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            // Проверяем только успешные HTML-страницы
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);

            if (string.IsNullOrWhiteSpace(ctx.ContentType) ||
                !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);

            // HtmlAgilityPack не всегда даёт DOCTYPE как узел — проверяем по сырому HTML
            if (!string.IsNullOrWhiteSpace(ctx.Html) &&
                ctx.Html.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase) < 0)
            {
                issues.Add(new LinkIssue("NO_DOCTYPE", "Не найден HTML DOCTYPE", IssueSeverity.Info));
            }

            if (ctx.ContentLength.HasValue && ctx.ContentLength.Value >= BigPageBytes)
                issues.Add(new LinkIssue("BIG_PAGE", $"Очень большая страница: {ctx.ContentLength.Value} bytes", IssueSeverity.Info));

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }

    }
}
