using System;
using Microsoft.AspNetCore.Mvc;
using Crawler_project.Models;
using Crawler_project.Checks;

namespace Crawler_project.Controllers
{
    [ApiController]
    [Route("api/crawl")]
    public sealed class PageSpeedAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly PageSpeedStore _psi;

        public PageSpeedAuditController(JobStore store, PageSpeedStore psi)
        {
            _store = store;
            _psi = psi;
        }

        [HttpGet("{jobId:guid}/pagespeed-audit")]
        public IActionResult PageSpeedAudit(Guid jobId, [FromQuery] string strategy = "mobile")
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;

            var s = strategy.Equals("desktop", StringComparison.OrdinalIgnoreCase)
                ? PageSpeedStrategy.Desktop
                : PageSpeedStrategy.Mobile;

            return Ok(_psi.Build(host, s));
        }
    }
}
