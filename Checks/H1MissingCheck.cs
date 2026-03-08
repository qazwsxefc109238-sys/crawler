namespace Crawler_project.Checks
{
    public sealed class H1MissingCheck : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var h1 = ctx.Document.DocumentNode.SelectNodes("//h1");
            if (h1 is null || h1.Count == 0)
            {
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                {
                    new LinkIssue("NO_H1", "Отсутствует <h1>", IssueSeverity.Warning)
                });
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
        }
    }
}