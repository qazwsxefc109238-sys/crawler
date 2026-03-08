namespace Crawler_project.Checks
{
    public sealed class IFrameCheck : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var frames = ctx.Document.DocumentNode.SelectNodes("//frame|//iframe");
            if (frames is null || frames.Count == 0)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
            {
                new LinkIssue("FRAME_IFRAME", $"Найдены frame/iframe: {frames.Count}", IssueSeverity.Info)
            });
        }
    }
}