using Crawler_project.Checks;
using Crawler_project.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.LinkChecks
{
    public sealed class SslCertificateChecks : ILinkCheck
    {
        private readonly ITlsAuditService _tls;
        private readonly int _warnDays;

        public SslCertificateChecks(ITlsAuditService tls, int warnDays = 30)
        {
            _tls = tls;
            _warnDays = warnDays;
        }

        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            // Сертификаты проверяем только если финальный URL https
            if (!ctx.FinalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<LinkIssue>();

            var rep = await _tls.InspectAsync(ctx.FinalUri, ct);

            if (!rep.HandshakeOk)
            {
                return new[]
                {
                    new LinkIssue("TLS_HANDSHAKE_FAILED", $"TLS handshake не выполнен: {rep.Error}", IssueSeverity.Error)
                };
            }

            var issues = new List<LinkIssue>();

            // 1) Дата завершения SSL
            if (rep.NotAfter is null)
            {
                issues.Add(new LinkIssue("SSL_NO_EXPIRY", "Не удалось определить дату истечения сертификата", IssueSeverity.Warning));
            }
            else
            {
                var daysLeft = (rep.NotAfter.Value - DateTimeOffset.Now).TotalDays;
                if (daysLeft < 0)
                    issues.Add(new LinkIssue("SSL_EXPIRED", $"Сертификат просрочен ({rep.NotAfter:O})", IssueSeverity.Error));
                else if (daysLeft <= _warnDays)
                    issues.Add(new LinkIssue("SSL_EXPIRING_SOON", $"Сертификат скоро истекает: {daysLeft:F0} дн. (до {rep.NotAfter:yyyy-MM-dd})", IssueSeverity.Warning));
            }

            // 2) Самоподписанный
            if (rep.IsSelfSigned)
                issues.Add(new LinkIssue("SSL_SELF_SIGNED", "Самоподписанный сертификат", IssueSeverity.Error));

            // 3) Сертификат для этого домена
            if (!rep.IsDomainMatch)
                issues.Add(new LinkIssue("SSL_DOMAIN_MISMATCH", $"Сертификат не соответствует домену: {rep.Host}", IssueSeverity.Error));

            // 4) Подтверждён CA chain
            if (!rep.IsCaChainValid)
            {
                var details = rep.ChainErrors.Count > 0 ? string.Join("; ", rep.ChainErrors) : rep.PolicyErrors.ToString();
                issues.Add(new LinkIssue("SSL_CA_CHAIN_INVALID", $"Цепочка CA не подтверждена: {details}", IssueSeverity.Error));
            }

            return issues;
        }
    }
}
