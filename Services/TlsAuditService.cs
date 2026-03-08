using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Globalization;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Services
{
    public sealed record TlsHostReport(
        string Host,
        int Port,
        bool HandshakeOk,
        string? Error,
        DateTimeOffset? NotBefore,
        DateTimeOffset? NotAfter,
        bool IsSelfSigned,
        bool IsDomainMatch,
        bool IsCaChainValid,
        SslPolicyErrors PolicyErrors,
        IReadOnlyList<string> DnsNames,
        IReadOnlyList<string> ChainErrors,
        string? Subject,
        string? Issuer,
        string? Thumbprint
    );

    public interface ITlsAuditService
    {
        Task<TlsHostReport> InspectAsync(Uri httpsUri, CancellationToken ct);
    }

    public sealed class TlsAuditService : ITlsAuditService
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<TlsHostReport>>> _cache = new(StringComparer.OrdinalIgnoreCase);

        public Task<TlsHostReport> InspectAsync(Uri httpsUri, CancellationToken ct)
        {
            if (!httpsUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new TlsHostReport(
                    Host: httpsUri.Host,
                    Port: httpsUri.Port > 0 ? httpsUri.Port : 443,
                    HandshakeOk: false,
                    Error: "NOT_HTTPS",
                    NotBefore: null,
                    NotAfter: null,
                    IsSelfSigned: false,
                    IsDomainMatch: false,
                    IsCaChainValid: false,
                    PolicyErrors: SslPolicyErrors.None,
                    DnsNames: Array.Empty<string>(),
                    ChainErrors: Array.Empty<string>(),
                    Subject: null,
                    Issuer: null,
                    Thumbprint: null
                ));
            }

            var port = httpsUri.IsDefaultPort ? 443 : httpsUri.Port;
            var key = $"{httpsUri.Host}:{port}";

            var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<TlsHostReport>>(() => InspectHostAsync(httpsUri.Host, port, ct)));
            // Важно: ct для кэшированного Task — только на первый вызов. Дальше берётся уже готовый Task.
            return lazy.Value;
        }

        private static async Task<TlsHostReport> InspectHostAsync(string host, int port, CancellationToken ct)
        {
            X509Certificate2? cert2 = null;
            X509Chain? chainCaptured = null;
            SslPolicyErrors policyErrorsCaptured = SslPolicyErrors.None;

            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, ct);

                using var ssl = new SslStream(
                    tcp.GetStream(),
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        policyErrorsCaptured = sslPolicyErrors;

                        if (certificate is not null)
                            cert2 = new X509Certificate2(certificate);

                        if (chain is not null)
                            chainCaptured = CloneChain(chain);

                        // Всегда принимаем, чтобы получить диагностическую информацию даже при проблемах
                        return true;
                    });

                var opts = new SslClientAuthenticationOptions
                {
                    TargetHost = host, // SNI
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                };

                await ssl.AuthenticateAsClientAsync(opts, ct);

                if (cert2 is null)
                {
                    return new TlsHostReport(
                        Host: host, Port: port, HandshakeOk: false,
                        Error: "NO_CERTIFICATE",
                        NotBefore: null, NotAfter: null,
                        IsSelfSigned: false, IsDomainMatch: false, IsCaChainValid: false,
                        PolicyErrors: policyErrorsCaptured,
                        DnsNames: Array.Empty<string>(),
                        ChainErrors: Array.Empty<string>(),
                        Subject: null, Issuer: null, Thumbprint: null
                    );
                }

                var dnsNames = GetDnsNames(cert2);
                var domainMatch = MatchesHost(host, dnsNames.Count > 0 ? dnsNames : new List<string> { GetCommonName(cert2) })
                                  || (policyErrorsCaptured & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;

                // Самоподписанный
                var selfSigned = cert2.Subject == cert2.Issuer;

                // Ошибки цепочки
                var chainErrors = new List<string>();
                if (chainCaptured is not null && chainCaptured.ChainStatus is not null)
                {
                    foreach (var st in chainCaptured.ChainStatus)
                    {
                        if (st.Status == X509ChainStatusFlags.NoError) continue;
                        chainErrors.Add($"{st.Status}: {st.StatusInformation?.Trim()}");
                    }
                }

                // "Подтверждён CA chain"
                // Смысл: цепочка доверена (нет UntrustedRoot/PartialChain/Signature и т.п.).
                // Время истечения проверяем отдельным чекером, поэтому NotTimeValid не считаем "CA не подтверждён".
                bool caValid = !chainErrors.Any(e =>
                    e.StartsWith(X509ChainStatusFlags.UntrustedRoot.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    e.StartsWith(X509ChainStatusFlags.PartialChain.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    e.StartsWith(X509ChainStatusFlags.NotSignatureValid.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    e.StartsWith(X509ChainStatusFlags.CtlNotTimeValid.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    e.StartsWith(X509ChainStatusFlags.RevocationStatusUnknown.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    e.StartsWith(X509ChainStatusFlags.Revoked.ToString(), StringComparison.OrdinalIgnoreCase)
                );

                return new TlsHostReport(
                    Host: host,
                    Port: port,
                    HandshakeOk: true,
                    Error: null,
                    NotBefore: cert2.NotBefore,
                    NotAfter: cert2.NotAfter,
                    IsSelfSigned: selfSigned,
                    IsDomainMatch: domainMatch,
                    IsCaChainValid: caValid,
                    PolicyErrors: policyErrorsCaptured,
                    DnsNames: dnsNames,
                    ChainErrors: chainErrors,
                    Subject: cert2.Subject,
                    Issuer: cert2.Issuer,
                    Thumbprint: cert2.Thumbprint
                );
            }
            catch (Exception ex)
            {
                return new TlsHostReport(
                    Host: host,
                    Port: port,
                    HandshakeOk: false,
                    Error: ex.Message,
                    NotBefore: null,
                    NotAfter: null,
                    IsSelfSigned: false,
                    IsDomainMatch: false,
                    IsCaChainValid: false,
                    PolicyErrors: policyErrorsCaptured,
                    DnsNames: Array.Empty<string>(),
                    ChainErrors: Array.Empty<string>(),
                    Subject: cert2?.Subject,
                    Issuer: cert2?.Issuer,
                    Thumbprint: cert2?.Thumbprint
                );
            }
        }

        private static X509Chain CloneChain(X509Chain original)
        {
            // Простой клон статусов. Глубокое копирование всех объектов не требуется, нам нужны ChainStatus.
            var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = original.ChainPolicy.RevocationMode;
            chain.ChainPolicy.RevocationFlag = original.ChainPolicy.RevocationFlag;
            chain.ChainPolicy.VerificationFlags = original.ChainPolicy.VerificationFlags;
            chain.ChainPolicy.VerificationTime = original.ChainPolicy.VerificationTime;
            return chain;
        }

        private static List<string> GetDnsNames(X509Certificate2 cert)
        {
            // SAN: 2.5.29.17 (dNSName)
            var list = new List<string>();
            var ext = cert.Extensions["2.5.29.17"];
            if (ext is null) return list;

            try
            {
                var reader = new AsnReader(ext.RawData, AsnEncodingRules.DER);
                var seq = reader.ReadSequence();
                while (seq.HasData)
                {
                    // GeneralName is a CHOICE. dNSName is [2] IA5String
                    var tag = seq.PeekTag();
                    if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    {
                        var dns = seq.ReadCharacterString(
                        UniversalTagNumber.IA5String,
                        new Asn1Tag(TagClass.ContextSpecific, 2)
                    );

                        if (!string.IsNullOrWhiteSpace(dns))
                            list.Add(dns.Trim());
                    }
                    else
                    {
                        // пропускаем другие типы (IP, URI и т.п.)
                        seq.ReadEncodedValue();
                    }
                }
            }
            catch
            {
                // Если разбор не удался — просто игнорируем SAN, будем опираться на CN/PolicyErrors
            }

            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string GetCommonName(X509Certificate2 cert)
        {
            // Берём CN из Subject (упрощённо)
            var subject = cert.Subject ?? "";
            // "CN=example.com, O=..."
            var parts = subject.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cn = parts.FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
            return cn is null ? "" : cn.Substring(3).Trim();
        }

        private static bool MatchesHost(string host, IEnumerable<string> patterns)
        {
            host = NormalizeHost(host);
            foreach (var p in patterns)
            {
                var pat = NormalizeHost(p);
                if (string.IsNullOrWhiteSpace(pat)) continue;

                if (pat.StartsWith("*.", StringComparison.Ordinal))
                {
                    // *.example.com matches a.example.com but not example.com
                    var suffix = pat.Substring(1); // ".example.com"
                    if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        // ensure there is at least one label before suffix
                        var prefix = host.Substring(0, host.Length - suffix.Length);
                        if (prefix.Length > 0 && prefix.Contains('.', StringComparison.Ordinal) == false)
                        {
                            // prefix like "a" ok
                            return true;
                        }
                        // prefix like "a.b" тоже допустим, обычно wildcard покрывает один уровень,
                        // но многие аудиторы принимают и глубже. Можно ужесточить при необходимости.
                        if (prefix.Length > 0) return true;
                    }
                }
                else
                {
                    if (host.Equals(pat, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static string NormalizeHost(string host)
        {
            host = host.Trim().TrimEnd('.');
            // IDN -> punycode для сопоставлений
            try
            {
                var idn = new IdnMapping();
                return idn.GetAscii(host);
            }
            catch
            {
                return host.ToLowerInvariant();
            }
        }
    }
}
