using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Services
{
    public enum SitemapAuditRunStatus { NotStarted, Running, Completed, Failed, Canceled }

    public sealed record SitemapAuditRunInfo(
        Guid JobId,
        SitemapAuditRunStatus Status,
        string Stage,
        int Total,
        int Processed,
        int Remaining,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? FinishedAtUtc,
        string? Error
    );

    public sealed class SitemapAuditRunner
    {
        private sealed class RunState
        {
            public Guid JobId;
            public SitemapAuditRunStatus Status;
            public string Stage = "init";
            public int Total;
            public int Processed;
            public DateTimeOffset StartedAtUtc;
            public DateTimeOffset? FinishedAtUtc;
            public string? Error;

            public CancellationTokenSource? Cts;
            public Task? Task;
            public SitemapAuditReport? Report;
        }

        private readonly ConcurrentDictionary<Guid, RunState> _runs = new();
        private readonly SitemapAuditService _svc;

        public SitemapAuditRunner(SitemapAuditService svc) => _svc = svc;

        public SitemapAuditRunInfo Start(Guid jobId, Uri startUri, string[] crawledUrls)
        {
            var st = _runs.GetOrAdd(jobId, _ => new RunState { JobId = jobId });

            lock (st)
            {
                if (st.Status == SitemapAuditRunStatus.Running)
                    return ToInfo(st);

                if (st.Status == SitemapAuditRunStatus.Completed)
                    return ToInfo(st);

                st.Status = SitemapAuditRunStatus.Running;
                st.Stage = "starting";
                st.Total = 0;
                st.Processed = 0;
                st.Error = null;
                st.Report = null;
                st.StartedAtUtc = DateTimeOffset.UtcNow;
                st.FinishedAtUtc = null;

                st.Cts?.Dispose();
                st.Cts = new CancellationTokenSource();

                st.Task = Task.Run(async () =>
                {
                    try
                    {
                        async Task OnProgress(SitemapAuditProgress p)
                        {
                            st.Stage = p.Stage;
                            st.Total = p.Total;
                            st.Processed = p.Processed;
                            await Task.CompletedTask;
                        }

                        var report = await _svc.AuditAsync(startUri, crawledUrls, st.Cts.Token, OnProgress);

                        lock (st)
                        {
                            st.Report = report;
                            st.Status = SitemapAuditRunStatus.Completed;
                            st.Stage = "done";
                            st.FinishedAtUtc = DateTimeOffset.UtcNow;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        lock (st)
                        {
                            st.Status = SitemapAuditRunStatus.Canceled;
                            st.Stage = "canceled";
                            st.FinishedAtUtc = DateTimeOffset.UtcNow;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (st)
                        {
                            st.Status = SitemapAuditRunStatus.Failed;
                            st.Stage = "failed";
                            st.Error = ex.GetType().Name + ": " + ex.Message;
                            st.FinishedAtUtc = DateTimeOffset.UtcNow;
                        }
                    }
                });

                return ToInfo(st);
            }
        }

        public SitemapAuditRunInfo GetStatus(Guid jobId)
        {
            if (!_runs.TryGetValue(jobId, out var st))
                return new SitemapAuditRunInfo(jobId, SitemapAuditRunStatus.NotStarted, "not-started", 0, 0, 0, DateTimeOffset.MinValue, null, null);

            lock (st) return ToInfo(st);
        }

        public SitemapAuditReport? GetResult(Guid jobId)
        {
            if (!_runs.TryGetValue(jobId, out var st)) return null;
            lock (st) return st.Report;
        }

        public bool Cancel(Guid jobId)
        {
            if (!_runs.TryGetValue(jobId, out var st)) return false;
            lock (st)
            {
                if (st.Status != SitemapAuditRunStatus.Running) return false;
                st.Cts?.Cancel();
                return true;
            }
        }

        private static SitemapAuditRunInfo ToInfo(RunState st)
        {
            var rem = Math.Max(0, st.Total - st.Processed);
            return new SitemapAuditRunInfo(
                st.JobId,
                st.Status,
                st.Stage,
                st.Total,
                st.Processed,
                rem,
                st.StartedAtUtc,
                st.FinishedAtUtc,
                st.Error
            );
        }
    }
}