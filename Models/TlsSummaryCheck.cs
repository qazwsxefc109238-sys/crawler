using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crawler_project.Services;

namespace Crawler_project.Checks
{
    public sealed class TlsSummaryCheck : ILinkCheck
    {
        private readonly ITlsAuditService _tls;
        private readonly int _warnDays;

        private static readonly ConcurrentDictionary<string, byte> _done =
            new(StringComparer.OrdinalIgnoreCase);

        public TlsSummaryCheck(ITlsAuditService tls, int warnDays = 30)
        {
            _tls = tls;
            _warnDays = warnDays;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            // один раз на хост: на главной, и по OriginalUri (чтобы редирект на /ru/ не убил чек)
            var host = ctx.FinalUri.Host;

            if (!_done.TryAdd(host, 0))
                return Array.Empty<LinkIssue>();

            // всегда проверяем именно HTTPS:443
            var httpsUri = new UriBuilder(ctx.FinalUri)
            {
                Scheme = "https",
                Port = 443,
                Path = "/",
                Query = "",
                Fragment = ""
            }.Uri;

            var rep = await _tls.InspectAsync(httpsUri, ct);

            var issues = new List<LinkIssue>();

            // Наличие HTTPS
            if (!rep.HandshakeOk)
            {
                issues.Add(new LinkIssue(
                    "HTTPS_MISSING",
                    $"HTTPS недоступен (handshake failed). Host={rep.Host}:{rep.Port}. Error={rep.Error}",
                    IssueSeverity.Error));
                return issues;
            }

            issues.Add(new LinkIssue(
                "HTTPS_PRESENT",
                $"HTTPS доступен: {rep.Host}:{rep.Port}",
                IssueSeverity.Info));

            if (!ctx.FinalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new LinkIssue(
                    "HTTPS_NOT_USED",
                    $"Итоговый URL не HTTPS: {ctx.FinalUrl}",
                    IssueSeverity.Warning));
            }

            // Дата завершения SSL
            if (rep.NotAfter is not null)
            {
                var daysLeft = (int)Math.Ceiling((rep.NotAfter.Value - DateTimeOffset.UtcNow).TotalDays);
                var sev = daysLeft < 0 ? IssueSeverity.Error :
                          (daysLeft <= _warnDays ? IssueSeverity.Warning : IssueSeverity.Info);

                issues.Add(new LinkIssue(
                    "SSL_EXPIRES_AT",
                    $"SSL истекает: {rep.NotAfter:yyyy-MM-dd} (осталось ~{daysLeft} дн.)",
                    sev));
            }
            else
            {
                issues.Add(new LinkIssue("SSL_EXPIRES_UNKNOWN", "Не удалось определить дату окончания SSL.", IssueSeverity.Warning));
            }

            // Самоподписанный
            issues.Add(new LinkIssue(
                "SSL_SELF_SIGNED",
                rep.IsSelfSigned ? "Сертификат самоподписанный." : "Сертификат не самоподписанный.",
                rep.IsSelfSigned ? IssueSeverity.Warning : IssueSeverity.Info));

            // Сертификат для этого домена
            issues.Add(new LinkIssue(
                "SSL_DOMAIN_MATCH",
                rep.IsDomainMatch ? "Сертификат подходит для домена (SAN/CN match)." : $"Сертификат НЕ подходит для домена {host} (SAN/CN mismatch).",
                rep.IsDomainMatch ? IssueSeverity.Info : IssueSeverity.Warning));

            // CA chain
            if (rep.IsCaChainValid)
            {
                issues.Add(new LinkIssue("SSL_CA_CHAIN_VALID", "Цепочка CA валидна.", IssueSeverity.Info));
            }
            else
            {
                var sample = rep.ChainErrors is null ? "" : string.Join("; ", rep.ChainErrors.Take(5));
                if (!string.IsNullOrWhiteSpace(sample)) sample = " Ошибки: " + sample;

                issues.Add(new LinkIssue("SSL_CA_CHAIN_INVALID", $"Цепочка CA невалидна.{sample}", IssueSeverity.Warning));
            }

            return issues;
        }
    }
}