using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace Crawler_project.Checks
{
    public interface ILinkCheck
    {
        ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct);
    }
}
