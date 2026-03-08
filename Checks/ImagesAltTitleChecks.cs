using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class ImagesAltTitleChecks : ILinkCheck
    {
        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var imgs = ctx.Document.DocumentNode.SelectNodes("//img");
            if (imgs is null || imgs.Count == 0)
            {
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
                {
                    new LinkIssue("NO_IMAGES", "На странице нет изображений", IssueSeverity.Info)
                });
            }

            int emptyAlt = imgs.Count(n => string.IsNullOrWhiteSpace(n.GetAttributeValue("alt", "")));
            int emptyTitle = imgs.Count(n => string.IsNullOrWhiteSpace(n.GetAttributeValue("title", "")));

            var issues = new List<LinkIssue>
            {
                new LinkIssue("IMAGES_COUNT", $"Число изображений: {imgs.Count}", IssueSeverity.Info)
            };

            if (emptyAlt > 0)
                issues.Add(new LinkIssue("IMG_EMPTY_ALT", $"Изображений с пустым alt: {emptyAlt}", IssueSeverity.Info));

            if (emptyTitle > 0)
                issues.Add(new LinkIssue("IMG_EMPTY_TITLE", $"Изображений с пустым title: {emptyTitle}", IssueSeverity.Info));

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }
    }
}