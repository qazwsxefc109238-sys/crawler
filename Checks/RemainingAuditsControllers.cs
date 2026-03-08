using System;
using Microsoft.AspNetCore.Mvc;
using Crawler_project.Models;
using Crawler_project.Checks;

namespace Crawler_project.Controllers
{
    [ApiController]
    [Route("api/crawl")]
    public sealed class CanonicalAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly CanonicalSummaryStore _canonical;

        public CanonicalAuditController(JobStore store, CanonicalSummaryStore canonical)
        {
            _store = store;
            _canonical = canonical;
        }

        [HttpGet("{jobId:guid}/canonical-audit")]
        public ActionResult<CanonicalAuditReport> CanonicalAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;
            return Ok(_canonical.Build(host));
        }
    }

    [ApiController]
    [Route("api/crawl")]
    public sealed class NoindexAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly NoindexStore _noindex;

        public NoindexAuditController(JobStore store, NoindexStore noindex)
        {
            _store = store;
            _noindex = noindex;
        }

        [HttpGet("{jobId:guid}/noindex-audit")]
        public ActionResult<NoindexAuditReport> NoindexAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;
            return Ok(_noindex.Build(host));
        }
    }

    [ApiController]
    [Route("api/crawl")]
    public sealed class ImagesMetaAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly ImagesMetaStore _imgs;

        public ImagesMetaAuditController(JobStore store, ImagesMetaStore imgs)
        {
            _store = store;
            _imgs = imgs;
        }

        [HttpGet("{jobId:guid}/images-meta-audit")]
        public ActionResult<ImagesMetaAuditReport> ImagesMetaAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;
            return Ok(_imgs.Build(host));
        }
    }

    [ApiController]
    [Route("api/crawl")]
    public sealed class OutgoingLinksAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly OutgoingLinksStore _out;
        private readonly OutgoingLinksOptions _opt;

        public OutgoingLinksAuditController(JobStore store, OutgoingLinksStore @out, OutgoingLinksOptions opt)
        {
            _store = store;
            _out = @out;
            _opt = opt;
        }

        [HttpGet("{jobId:guid}/outgoing-links-audit")]
        public ActionResult<OutgoingLinksAuditReport> OutgoingLinksAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;
            return Ok(_out.Build(host, _opt.InternalTooManyThreshold));
        }
    }

    [ApiController]
    [Route("api/crawl")]
    public sealed class PerformanceAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly PerformanceStore _perf;

        public PerformanceAuditController(JobStore store, PerformanceStore perf)
        {
            _store = store;
            _perf = perf;
        }

        [HttpGet("{jobId:guid}/performance-audit")]
        public ActionResult<PerformanceAuditReport> PerformanceAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;
            return Ok(_perf.Build(host));
        }
    }

    [ApiController]
    [Route("api/crawl")]
    public sealed class ContentLengthAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly ContentLengthUniformityStore _len;
        private readonly ContentLengthUniformityOptions _opt;

        public ContentLengthAuditController(JobStore store, ContentLengthUniformityStore len, ContentLengthUniformityOptions opt)
        {
            _store = store;
            _len = len;
            _opt = opt;
        }

        [HttpGet("{jobId:guid}/content-length-audit")]
        public ActionResult<ContentLengthAuditReport> ContentLengthAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;
            return Ok(_len.Build(host, _opt));
        }
    }
}
