using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Crawler_project.Checks;
using System.Diagnostics;


namespace Crawler_project.Services
{
    public sealed class LinkVerifierOptions
    {
        public int MaxRedirects { get; set; } = 10;
        public int MaxHtmlChars { get; set; } = 2_000_000; // защита от гигантских страниц
        public int PerHostMaxConcurrency { get; set; } = 2;  // лимит на хост
        public int RetryCount { get; set; } = 2;             // ретраи на transient
        public int RetryBaseDelayMs { get; set; } = 300;     // база backoff
        public int RetryMaxDelayMs { get; set; } = 4000;     // потолок backoff
    }

    public sealed class LinkVerifier
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly LinkCheckRunner _runner;
        private readonly LinkVerifierOptions _opt;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);

        private SemaphoreSlim Gate(string host) => _hostGates.GetOrAdd(host, _ => new SemaphoreSlim(Math.Max(1, _opt.PerHostMaxConcurrency)));


        public LinkVerifier(IHttpClientFactory httpFactory, LinkCheckRunner runner, LinkVerifierOptions opt)
        {
            _httpFactory = httpFactory;
            _runner = runner;
            _opt = opt;
        }

        public async Task VerifyAllAsync(
            string[] urls,
            ConcurrentDictionary<string, LinkCheckResult> results,
            int degreeOfParallelism,
            CancellationToken ct)
        {
            await Parallel.ForEachAsync(
                urls,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degreeOfParallelism), CancellationToken = ct },
                async (url, token) =>
                {
                    var res = await VerifyOneAsync(url, token);
                    results[url] = res;
                });
        }

        private async Task<LinkCheckResult> VerifyOneAsync(string url, CancellationToken ct)
        {
            try
            {
                var (ctx, error) = await FetchWithRedirectsAsync(url, ct);

                if (ctx is null)
                {
                    return new LinkCheckResult(
                        Url: url,
                        FinalUrl: url,
                        StatusCode: 0,
                        ContentType: null,
                        ContentLength: null,
                        LoadSeconds: 0,
                        Redirects: Array.Empty<RedirectHop>(),
                        Issues: Array.Empty<LinkIssue>(),
                        Error: error ?? "FETCH_FAILED"
                    );
                }

                var issues = await _runner.RunAsync(ctx, ct);

                return new LinkCheckResult(
                    Url: ctx.OriginalUrl,
                    FinalUrl: ctx.FinalUrl,
                    StatusCode: ctx.FinalStatusCode,
                    ContentType: ctx.ContentType,
                    ContentLength: ctx.ContentLength,
                    LoadSeconds: ctx.TotalTime.TotalSeconds,
                    Redirects: ctx.Redirects,
                    Issues: issues,
                    Error: null
                );
            }
            catch (Exception ex)
            {
                return new LinkCheckResult(
                    Url: url,
                    FinalUrl: url,
                    StatusCode: 0,
                    ContentType: null,
                    ContentLength: null,
                    LoadSeconds: 0,
                    Redirects: Array.Empty<RedirectHop>(),
                    Issues: Array.Empty<LinkIssue>(),
                    Error: ex.Message
                );
            }
        }
        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient http, Uri uri, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= Math.Max(1, _opt.RetryCount + 1); attempt++)
            {
                // per-host throttle
                var gate = Gate(uri.Host);
                await gate.WaitAsync(ct);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                    req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                    var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                    var code = (int)resp.StatusCode;

                    if (!IsTransient(code) || attempt == _opt.RetryCount + 1)
                        return resp;

                    resp.Dispose();
                }
                catch (HttpRequestException) when (attempt < _opt.RetryCount + 1)
                {
                    // retry
                }
                catch (TaskCanceledException) when (attempt < _opt.RetryCount + 1)
                {
                    // timeout retry
                }
                finally
                {
                    gate.Release();
                }

                var delayMs = Math.Min(_opt.RetryMaxDelayMs, _opt.RetryBaseDelayMs * (1 << (attempt - 1)));
                delayMs += Random.Shared.Next(0, 200); // jitter
                await Task.Delay(delayMs, ct);
            }

            // теоретически сюда не попадём
            throw new InvalidOperationException("SendWithRetryAsync failed unexpectedly.");

            static bool IsTransient(int status) => status == 429 || status == 502 || status == 503 || status == 504 || status == 0;
        }

        private async Task<(LinkCheckContext? ctx, string? error)> FetchWithRedirectsAsync(string url, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var originalUri))
                return (null, "INVALID_URL");

            var http = _httpFactory.CreateClient("crawler_noredirect");

            var sw = Stopwatch.StartNew();
            var redirects = new List<RedirectHop>();

            Uri current = originalUri;
            HttpResponseMessage? finalResp = null;

            for (int i = 0; i <= _opt.MaxRedirects; i++)
            {
                var resp = await SendWithRetryAsync(http, current, ct);

                var status = (int)resp.StatusCode;

                if (status >= 300 && status < 400 && resp.Headers.Location is not null)
                {
                    var loc = resp.Headers.Location;
                    var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);

                    redirects.Add(new RedirectHop(current.AbsoluteUri, status, next.AbsoluteUri));
                    current = next;

                    resp.Dispose();
                    continue;
                }

                finalResp = resp;
                break;
            }

            if (finalResp is null)
                return (null, "TOO_MANY_REDIRECTS_OR_NO_RESPONSE");

            using (finalResp)
            {
                var finalUrl = current.AbsoluteUri;
                var contentType = finalResp.Content.Headers.ContentType?.MediaType;
                var contentLength = finalResp.Content.Headers.ContentLength;

                string? html = null;
                HtmlDocument? doc = null;

                if (!string.IsNullOrWhiteSpace(contentType) &&
                    contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    // Если у вас .NET < 8 и ругается на перегрузку с ct — замените на ReadAsStringAsync()
                    html = await finalResp.Content.ReadAsStringAsync(ct);

                    if (html.Length > _opt.MaxHtmlChars)
                        html = html.Substring(0, _opt.MaxHtmlChars);

                    doc = new HtmlDocument
                    {
                        OptionCheckSyntax = true,
                        OptionFixNestedTags = true
                    };
                    doc.LoadHtml(html);

                }

                sw.Stop();

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in finalResp.Headers)
                    headers[h.Key] = string.Join(", ", h.Value);
                foreach (var h in finalResp.Content.Headers)
                    headers[h.Key] = string.Join(", ", h.Value);

                var ctx = new LinkCheckContext(
                    OriginalUrl: originalUri.AbsoluteUri,
                    FinalUrl: finalUrl,
                    FinalStatusCode: (int)finalResp.StatusCode,
                    ContentType: contentType,
                    ContentLength: contentLength ?? (html is null ? null : html.Length),
                    TotalTime: sw.Elapsed,
                    Headers: headers,
                    Redirects: redirects,
                    Html: html,
                    Document: doc,
                    OriginalUri: originalUri,
                    FinalUri: new Uri(finalUrl)
                );

                return (ctx, null);
            }
        }
    }
}
