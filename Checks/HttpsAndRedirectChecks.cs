namespace Crawler_project.Checks
{
    public sealed class HttpsAndRedirectChecks : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var issues = new List<LinkIssue>();

            // Наличие HTTPS (по финальному URL)
            if (!ctx.FinalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                issues.Add(new LinkIssue("NO_HTTPS", $"Финальный URL не HTTPS: {ctx.FinalUrl}", IssueSeverity.Error));

            // Порт :443 в URL
            if (ctx.OriginalUri.IsDefaultPort == false && ctx.OriginalUri.Port == 443)
                issues.Add(new LinkIssue("PORT_443_IN_URL", $"В исходном URL указан порт :443 ({ctx.OriginalUrl})", IssueSeverity.Info));
            if (ctx.FinalUri.IsDefaultPort == false && ctx.FinalUri.Port == 443)
                issues.Add(new LinkIssue("PORT_443_IN_FINAL_URL", $"В финальном URL указан порт :443 ({ctx.FinalUrl})", IssueSeverity.Info));

            // Информация по редиректам / множественные редиректы
            if (ctx.Redirects.Count > 1)
                issues.Add(new LinkIssue("MULTI_REDIRECTS", $"Множественные редиректы: {ctx.Redirects.Count}", IssueSeverity.Info));

            // Есть редирект с HTTP на HTTPS (логика: исходный http, финальный https)
            if (ctx.OriginalUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                ctx.FinalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new LinkIssue("HTTP_TO_HTTPS_REDIRECT", "Есть редирект HTTP → HTTPS", IssueSeverity.Info));
            }
            else if (ctx.OriginalUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                     !ctx.FinalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new LinkIssue("NO_HTTP_TO_HTTPS_REDIRECT", "Нет редиректа HTTP → HTTPS", IssueSeverity.Warning));
            }

            // Переадресация на другие домены
            if (!ctx.OriginalUri.Host.Equals(ctx.FinalUri.Host, StringComparison.OrdinalIgnoreCase))
                issues.Add(new LinkIssue("REDIRECT_OTHER_DOMAIN",
                    $"Редирект на другой домен: {ctx.OriginalUri.Host} → {ctx.FinalUri.Host}", IssueSeverity.Warning));

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }
    }
}