using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public sealed class ContentLengthUniformitySummaryCheck : ILinkCheck
    {
        private readonly ContentLengthUniformityOptions _opt;
        private readonly ContentLengthUniformityStore _store;

        private static readonly ConcurrentDictionary<string, byte> _reported =
            new(StringComparer.OrdinalIgnoreCase);

        public ContentLengthUniformitySummaryCheck(ContentLengthUniformityOptions opt, ContentLengthUniformityStore store)
        {
            _opt = opt;
            _store = store;
        }

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            var host = ctx.FinalUri.Host;

            if (_reported.ContainsKey(host))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var pages = _store.GetPagesCount(host);
            if (pages < _opt.MinPagesForUniformityCheck)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            // чтобы не строить отчёт на каждой странице
            if (pages != _opt.MinPagesForUniformityCheck && pages % 25 != 0)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var rep = _store.Build(host, _opt);
            if (!rep.SuspiciousUniformity || rep.TopLengths.Length == 0)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (!_reported.TryAdd(host, 0))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var top = rep.TopLengths[0];
            var sharePct = rep.TopLengthShare * 100.0;

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(new[]
            {
                new LinkIssue(
                    "CONTENT_LENGTH_UNIFORMITY",
                    $"Подозрительно одинаковый размер документа: {top.ContentLength} байт встречается в {sharePct:F1}% страниц (pages={rep.PagesWithKnownContentLength}). Возможна заглушка/антибот/ошибка отдачи.",
                    IssueSeverity.Warning)
            });
        }
    }
}