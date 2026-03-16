using Crawler_project.Checks;
using Crawler_project.Models;
using Crawler_project.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Crawler_project.Models.DTO;
namespace Crawler_project.Controllers
{
    
    /// <summary>
    /// Ну это база, я б сказал основа основ.
    /// Короче немного переделанный базовый шаблон контроллера, который идёт базово при создании проекта с ASP.NET
    /// </summary>
    [ApiController]
    [Route("api/crawl")]
    [Authorize(AuthenticationSchemes = SessionAuthenticationHandler.SchemeName)]
    public sealed class CrawlController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly SeoDuplicatesStore _dup;
        private readonly ContentQualityStore _content;
        private readonly SiteImagesStore _images;
        private readonly InternalLinkGraphStore _graph;
        private readonly CanonicalSummaryStore _canonical;
        private readonly NoindexStore _noindex;
        private readonly ImagesMetaStore _imagesMeta;
        private readonly OutgoingLinksStore _outgoing;
        private readonly PerformanceStore _perf;
        private readonly ContentLengthUniformityStore _len;
       // private readonly WhoisStore _whois;
        private readonly PageSpeedStore _psi;



        public CrawlController(
    JobStore store,
    SeoDuplicatesStore dup,
    ContentQualityStore content,
    SiteImagesStore images,
    InternalLinkGraphStore graph,
    CanonicalSummaryStore canonical,
    NoindexStore noindex,
    ImagesMetaStore imagesMeta,
    OutgoingLinksStore outgoing,
    PerformanceStore perf,
    ContentLengthUniformityStore len,
    //WhoisStore whois,
    PageSpeedStore psi)
{
    _store = store;
    _dup = dup;
    _content = content;
    _images = images;
    _graph = graph;

    _canonical = canonical;
    _noindex = noindex;
    _imagesMeta = imagesMeta;
    _outgoing = outgoing;
    _perf = perf;
    _len = len;

    //_whois = whois;
    _psi = psi;
}



        [HttpPost]
        public ActionResult<CrawlStartResponse> Start([FromBody] CrawlStartRequest req)
        {
            var job = _store.CreateAndStart(req);
            var host = new Uri(job.StartUrl).Host;
            _dup.ResetHost(host);
            _content.ResetHost(host);

            // СБРОС агрегаторов дублей TITLE/DESCRIPTION для текущего сайта (host),
            // чтобы дубли считались только в рамках этого job, а не копились между запусками.


            var resp = new CrawlStartResponse(
                job.JobId,
                StatusUrl: $"/api/crawl/{job.JobId}",
                UrlsUrl: $"/api/crawl/{job.JobId}/urls"
            );

            return Accepted(resp);
        }


        [HttpGet("info")]
        [AllowAnonymous]
        public string Get()
        {
            return Logo.Show();
        }

        [HttpGet("{jobId:guid}")]
        public ActionResult<CrawlStatusResponse> Status(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            return new CrawlStatusResponse(
            job.JobId,
            job.State.ToString(),
            job.StartUrl,
            job.MaxPages,
            job.Workers,
            Visited: job.VisitedCount,
            Discovered: job.Discovered.Count,
            job.StartedAt,
            job.FinishedAt,
            job.Error,
            BootstrapFromSitemap: job.BootstrapFromSitemap,
            AllowQueryUrls: job.AllowQueryUrls,
            SeededFromSitemap: job.SeededFromSitemap,
            SkippedByRobots: job.SkippedByRobots,
            SkippedExternalHost: job.SkippedExternalHost,
            SkippedQueryUrls: job.SkippedQueryUrls,
            SkippedNonHtml: job.SkippedNonHtml,
            SkippedHttpErrors: job.SkippedHttpErrors,
            TruncatedHtmlPages: job.TruncatedHtmlPages
);
        }

        [HttpGet("{jobId:guid}/urls")]
        public ActionResult<PagedUrlsResponse> Urls(Guid jobId, [FromQuery] int offset = 0, [FromQuery] int limit = 100)
        {
            var urls = _store.GetUrls(jobId, offset, limit, out var total);
            if (total == 0 && _store.Get(jobId) is null) return NotFound();

            return new PagedUrlsResponse(total, offset, limit, urls);
        }

        [HttpPost("{jobId:guid}/cancel")]
        public IActionResult Cancel(Guid jobId)
        {
            if (_store.Get(jobId) is null) return NotFound();
            _store.Cancel(jobId);
            return Accepted();
        }

        [HttpPost("{jobId:guid}/checks/start")]
        public IActionResult StartChecks(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            if (job.ChecksTask is not null)
            {
                return Accepted(new
                {
                    StatusUrl = $"/api/crawl/{jobId}/checks/status",
                    ResultsUrl = $"/api/crawl/{jobId}/checks/results"
                });
            }

            var host = new Uri(job.StartUrl).Host;
            _psi.ResetHost(host);

            _images.ResetHost(host);
            _graph.ResetHost(host);
            _canonical.ResetHost(host);
            _noindex.ResetHost(host);
            _imagesMeta.ResetHost(host);
            _outgoing.ResetHost(host);
            _perf.ResetHost(host);
            _len.ResetHost(host);

            try
            {
                _store.StartChecks(jobId);
                return Accepted(new
                {
                    StatusUrl = $"/api/crawl/{jobId}/checks/status",
                    ResultsUrl = $"/api/crawl/{jobId}/checks/results"
                });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { error = ex.Message });
            }
        }


        [HttpGet("{jobId:guid}/checks/status")]
        public IActionResult ChecksStatus(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            return Ok(new
            {
                jobId = job.JobId,
                state = job.ChecksState.ToString(),
                startedAt = job.ChecksStartedAt,
                finishedAt = job.ChecksFinishedAt,
                error = job.ChecksError,
                total = job.Discovered.Count,
                checkedCount = job.CheckResults.Count
            });
        }

        [HttpGet("{jobId:guid}/checks/results")]
        public IActionResult CheckResults(Guid jobId, [FromQuery] int offset = 0, [FromQuery] int limit = 100)
        {
            var items = _store.GetCheckResults(jobId, offset, limit, out var total);
            if (total == 0 && _store.Get(jobId) is null) return NotFound();

            return Ok(new { total, offset, limit, items });
        }

    }
}
