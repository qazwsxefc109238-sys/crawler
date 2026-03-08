using HtmlAgilityPack;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Checks
{
    public enum IssueSeverity { Info, Warning, Error }

    public sealed record LinkIssue(string Code, string Message, IssueSeverity Severity = IssueSeverity.Warning);

    public sealed record RedirectHop(string FromUrl, int StatusCode, string? Location);

    public sealed record LinkCheckContext(
        string OriginalUrl,
        string FinalUrl,
        int FinalStatusCode,
        string? ContentType,
        long? ContentLength,
        TimeSpan TotalTime,
        IReadOnlyDictionary<string, string> Headers,
        IReadOnlyList<RedirectHop> Redirects,
        string? Html,
        HtmlDocument? Document,
        Uri OriginalUri,
        Uri FinalUri
    );

    public sealed record LinkCheckResult(
        string Url,
        string FinalUrl,
        int StatusCode,
        string? ContentType,
        long? ContentLength,
        double LoadSeconds,
        IReadOnlyList<RedirectHop> Redirects,
        IReadOnlyList<LinkIssue> Issues,
        string? Error = null
    );
}
