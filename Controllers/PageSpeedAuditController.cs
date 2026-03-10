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

            var report = _psi.Build(host, s);
            if (report.Tested == 0)
            {
                var altHost = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? host.Substring(4)
                    : "www." + host;

                var altReport = _psi.Build(altHost, s);
                if (altReport.Tested > 0)
                    return Ok(altReport);
            }

            return Ok(report);
        }
    }
}
