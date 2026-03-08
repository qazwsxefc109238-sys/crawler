namespace Crawler_project.Models
{

    public class DTO
    {
        public sealed record CrawlStartRequest(
        string StartUrl,
        int MaxPages = 1000,
        int Workers = 8,
        bool RespectRobots = true);

        public sealed record CrawlStartResponse(
        Guid JobId,
        string StatusUrl,
        string UrlsUrl);

        public sealed record CrawlStatusResponse(
        Guid JobId,
        string State,
        string StartUrl,
        int MaxPages,
        int Workers,
        int Visited,
        int Discovered,
        DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt,
        string? Error);

        public sealed record PagedUrlsResponse(
        int Total,
        int Offset,
        int Limit,
        string[] Urls);

    }
}
