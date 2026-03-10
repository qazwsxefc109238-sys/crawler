using Crawler_project.Checks;
using Crawler_project.LinkChecks;
using Crawler_project.Models;
using Crawler_project.Models.LinkChecks;
using Crawler_project.Services;
using System.Net;

namespace Crawler_project
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();

            // Core
            builder.Services.AddSingleton<LinkCheckRunner>();
            builder.Services.AddSingleton<LinkVerifier>();

            builder.Services.AddSingleton<JobStore, InMemJobStore>();
            builder.Services.AddOpenApi();

            // Http clients

            builder.Services.AddHttpClient("crawler", c =>
            {
                c.Timeout = TimeSpan.FromSeconds(20);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Crawler/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            });

            builder.Services.AddHttpClient("crawler_noredirect", c =>
            {
                c.Timeout = TimeSpan.FromSeconds(20);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Crawler/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            });

            builder.Services.AddHttpClient("pagespeed", c =>
            {
                c.BaseAddress = new Uri("https://www.googleapis.com/pagespeedonline/v5/");
                c.Timeout = TimeSpan.FromSeconds(60);
            });
            builder.Services.AddHttpClient("domainwhois", c =>
            {
                c.Timeout = TimeSpan.FromSeconds(20);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Crawler/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            });

            builder.Services.AddHttpClient("rdap", c =>
            {
                c.Timeout = TimeSpan.FromSeconds(12);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Crawler/1.0");
            });



            // Options
            builder.Services.AddSingleton(new LinkVerifierOptions
            {
                MaxRedirects = 10,
                MaxHtmlChars = 2_000_000
            });

            builder.Services.AddSingleton(new Crawler_project.Models.DnsAuditOptions());

            builder.Services.AddSingleton(new RblOptions
            {
                QueryTimeoutMs = 2500,
                MaxConcurrency = 10,

                // IP-хостинга (A/AAAA) → reverse-ip + ".zen.spamhaus.org"
                IpZones = new[] { "zen.spamhaus.org" },

                // Домены из ссылок/ресурсов → domain + ".dbl.spamhaus.org"
                DomainZones = new[] { "dbl.spamhaus.org" },

                MaxExternalDomainsPerPage = 40,
                SampleHitsPerIssue = 5,
                StripWww = true,
                AlsoCheckBaseDomainHeuristic = true
            });

            builder.Services.AddSingleton(new ResourceIntegrityOptions
            {
                MaxConcurrentRequestsPerPage = 4,
                OnlyInternalLinks = true,   // внешние домены часто режут ботов (vk 418 и т.п.)
                MaxLinksPerPage = 15,
                MaxJsPerPage = 15,
                MaxCssPerPage = 15,
                MaxImagesPerPage = 15,
                SampleUrlsInIssue = 5,
                PreferHead = true,
                FallbackToGet = true
            });

            builder.Services.AddSingleton(new SitemapAuditOptions
            {
                UserAgent = "MyCrawler",
                MaxSitemapsToProcess = 200,
                MaxUrlsToCollect = 200_000,
                MaxIndexabilityChecksFromSitemap = 3000,
                MaxIndexabilityChecksFromCrawl = 3000,
                MaxConcurrentRequests = 10,
                IgnoreQueryAndFragment = true
            });

            builder.Services.AddSingleton(new HtmlMarkupErrorsOptions
            {
                MaxParseErrorsToReport = 10,
                MaxDuplicateIdsToReport = 20,
                MaxNestedSamplesToReport = 5,
                EmitHintWhenNoParseErrors = false,
                ParseErrorsErrorThreshold = 15
            });



            builder.Services.AddSingleton(new RobotsTxtOptions
            {
                UserAgent = "MyCrawler",
                MaxChars = 250_000,
                MissingGroupIsWarning = true
            });

            builder.Services.AddSingleton(new IndexabilitySiteAndLandingOptions
            {
                UserAgent = "MyCrawler",
                LandingMaxPathSegments = 1,
                TreatIndexFilesAsLanding = true,
                HostAuditTimeoutSeconds = 20,
                MaxHomepageHtmlChars = 400_000,
                LandingOnlyHtml = true
            });

            builder.Services.AddSingleton(new SeoHtmlCriticalOptions
            {
                TitleMinWords = 2,
                DescriptionMinWords = 5,
                DescriptionMaxChars = 320,
                MaxUrlsPerDuplicateGroup = 10,
                MaxGroupsInReport = 200,
                MaxTextPreviewChars = 200,
                MaxKeyChars = 1000
            });

            builder.Services.AddSingleton(new SiteImagesOptions
            {
                RareCopiesThreshold = 10,
                IgnoreQueryAndFragment = true,
                MaxImagesPerPage = 300
            });

            builder.Services.AddSingleton(new InternalLinkGraphOptions
            {
                IgnoreQueryAndFragment = true,
                LandingMaxPathSegments = 1,
                TreatIndexFilesAsLanding = true,
                InboundThreshold = 5,
                MaxLinksPerPage = 800
            });

            builder.Services.AddSingleton(new HtmlSeoStructureOptions());
            builder.Services.AddSingleton(new CanonicalOptions());
            builder.Services.AddSingleton(new ImagesMetaOptions());
            builder.Services.AddSingleton(new OutgoingLinksOptions());
            builder.Services.AddSingleton(new PerformanceOptions());
            builder.Services.AddSingleton(new ContentLengthUniformityOptions());

            builder.Services.AddSingleton(new ContentQualityOptions());

            builder.Services.AddSingleton(new WhoisOptions
            {
                TcpTimeoutSeconds = 12,
                ExpiringSoonDays = 30,
                MaxResponseChars = 300_000,
                UseRegistrableDomainHeuristic = true
            });
            builder.Services.AddHttpClient("domainwhois", c =>
            {
                c.Timeout = TimeSpan.FromSeconds(25);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Crawler/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            });


            builder.Services.AddSingleton(new DomainWhoisOptions
            {
                TotalTimeoutSeconds = 20,
                HttpTimeoutSeconds = 25,
                ExpiringSoonDays = 30
            });

            builder.Services.AddSingleton<ILinkCheck, DomainWhoisChecks>();







            var psiKey =
                builder.Configuration["Google:PageSpeedApiKey"]           // appsettings: Google:PageSpeedApiKey или env Google__PageSpeedApiKey
                ?? builder.Configuration["PAGESPEED_API_KEY"]             // env: PAGESPEED_API_KEY
                ?? builder.Configuration["GOOGLE_PAGESPEED_API_KEY"]
                ?? Environment.GetEnvironmentVariable("PAGESPEED_API_KEY")
                ?? Environment.GetEnvironmentVariable("GOOGLE_PAGESPEED_API_KEY");

            builder.Services.AddSingleton(new PageSpeedOptions
            {
                ApiKey = psiKey,
                HostRootOnly = true,
                MaxPagesPerHostPerStrategy = 1,   // чтобы не сжигать квоту (по 1 запросу на стратегию)
                MaxConcurrentRequests = 1,
                TimeoutSeconds = 90,
                MaxRetries = 3,
                RetryBaseDelayMs = 1500,
                ScoreWarnBelow = 50,
                ScoreInfoBelow = 90
            });


            // Stores (без дублей)
            builder.Services.AddSingleton<SeoDuplicatesStore>();
            builder.Services.AddSingleton<ContentQualityStore>();
            builder.Services.AddSingleton<SiteImagesStore>();
            builder.Services.AddSingleton<InternalLinkGraphStore>();

            builder.Services.AddSingleton<CanonicalSummaryStore>();
            builder.Services.AddSingleton<NoindexStore>();
            builder.Services.AddSingleton<ImagesMetaStore>();
            builder.Services.AddSingleton<OutgoingLinksStore>();
            builder.Services.AddSingleton<PerformanceStore>();
            builder.Services.AddSingleton<ContentLengthUniformityStore>();

            //builder.Services.AddSingleton<WhoisStore>();
            builder.Services.AddSingleton<PageSpeedStore>();

            // Services (без дублей)
            builder.Services.AddSingleton<ITlsAuditService, TlsAuditService>();

            builder.Services.AddSingleton<Crawler_project.Models.DnsAuditService>();
            builder.Services.AddSingleton<RblAuditService>();
            builder.Services.AddSingleton<SitemapAuditService>();
            builder.Services.AddSingleton<ResourceIntegrityAuditService>();

            builder.Services.AddSingleton<IndexabilitySiteAuditService>();
            builder.Services.AddSingleton<RobotsTxtAuditService>();

            builder.Services.AddSingleton<CanonicalAuditService>();
            builder.Services.AddSingleton<Crawler_project.Services.SitemapAuditRunner>();
            builder.Services.AddSingleton<PageSpeedClient>();


            // Checks
            // builder.Services.AddSingleton<ILinkCheck>(sp => new SslCertificateChecks(sp.GetRequiredService<ITlsAuditService>(), warnDays: 30));
            //builder.Services.AddSingleton<ILinkCheck, DomainWhoisChecks>();
            builder.Services.AddSingleton<ILinkCheck>(sp => new SslCertificateChecks(sp.GetRequiredService<ITlsAuditService>(), warnDays: 30));

            //builder.Services.AddSingleton<ILinkCheck, TitleCheck>();
            builder.Services.AddSingleton<ILinkCheck, H1Check>();
            builder.Services.AddSingleton<ILinkCheck, Explicit443PortCheck>();
            builder.Services.AddSingleton<ILinkCheck, DomainVariantsCheck>();
            builder.Services.AddSingleton<ILinkCheck, HttpStatusCheck>();
            builder.Services.AddSingleton<ILinkCheck, HttpsAndRedirectChecks>();
            builder.Services.AddSingleton<ILinkCheck, MixedContentCheck>();
            builder.Services.AddSingleton<ILinkCheck, IFrameCheck>();
            builder.Services.AddSingleton<ILinkCheck, ServerErrorLeakCheck>();
            builder.Services.AddSingleton<ILinkCheck, TitleDescriptionChecks>();
           // builder.Services.AddSingleton<ILinkCheck, H1MissingCheck>();
            builder.Services.AddSingleton<ILinkCheck, DoctypeAndSizeChecks>();
            builder.Services.AddSingleton<ILinkCheck, ImagesAltTitleChecks>();
            builder.Services.AddSingleton<ILinkCheck, LinksTextAndCountsCheck>();
            builder.Services.AddSingleton<ILinkCheck, CanonicalChecks>();
            builder.Services.AddSingleton<ILinkCheck, NoindexChecks>();

            builder.Services.AddSingleton<ILinkCheck, HtmlMarkupErrorsCheck>();
            builder.Services.AddSingleton<ILinkCheck, ResourcesIntegrityChecks>();

            builder.Services.AddSingleton<ILinkCheck, HostingDnsChecks>();
            builder.Services.AddSingleton<RdapGeoService>();
            builder.Services.AddSingleton<ILinkCheck, HostingGeoChecks>();
            // LoadTimeCheck с параметром
            builder.Services.AddSingleton<ILinkCheck>(_ => new LoadTimeCheck(3.0));

            builder.Services.AddSingleton<ILinkCheck, ContentQualityCheck>();
            builder.Services.AddSingleton<ILinkCheck, IndexabilitySiteAndLandingCheck>();
            builder.Services.AddSingleton<ILinkCheck, RobotsTxtChecks>();

            builder.Services.AddSingleton<ILinkCheck, ImagesCollectorCheck>();
            builder.Services.AddSingleton<ILinkCheck, InternalLinkGraphCollectorCheck>();

            builder.Services.AddSingleton<ILinkCheck, SeoHtmlCriticalCheck>();
            builder.Services.AddSingleton<WhoisStore>();

            builder.Services.AddSingleton<ILinkCheck, HtmlSeoStructureCheck>();
            builder.Services.AddSingleton<ILinkCheck, CanonicalCheck>();
            builder.Services.AddSingleton<ILinkCheck, NoindexAllPagesCheck>();
            builder.Services.AddSingleton<ILinkCheck, ImagesMetaCheck>();
            builder.Services.AddSingleton<ILinkCheck, OutgoingLinksCheck>();
            builder.Services.AddSingleton<ILinkCheck, RedirectToSubdomainCheck>();
            builder.Services.AddSingleton<ILinkCheck, PerformanceCheck>();
            builder.Services.AddSingleton<ILinkCheck, ContentLengthUniformityCollectorCheck>();
            builder.Services.AddSingleton<ILinkCheck, ContentLengthUniformitySummaryCheck>();
            builder.Services.AddSingleton<ILinkCheck, HostingRblChecks>();
            builder.Services.AddSingleton<ILinkCheck, OutgoingDomainBlacklistChecks>();
            //builder.Services.AddSingleton<ILinkCheck, WhoisCollectorCheck>();
            builder.WebHost.UseUrls("http://0.0.0.0:7168");
            builder.Services.AddSingleton<ILinkCheck, PageSpeedMobileCheck>();
            builder.Services.AddSingleton<ILinkCheck, PageSpeedDesktopCheck>();

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }
            //app.Run("http://0.0.0.0:7168");
            //app.UseHttpsRedirection();
    
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.UseAuthorization();
            app.MapControllers();

            app.Run();
        }
    }
}
