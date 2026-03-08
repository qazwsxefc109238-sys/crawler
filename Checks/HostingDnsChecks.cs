//using Crawler_project.Checks;
//using System;
//using System.Buffers.Binary;
//using System.Collections.Concurrent;
//using System.Collections.Generic;
//using System.Linq;
//using System.Net;
//using System.Net.NetworkInformation;
//using System.Net.Sockets;
//using System.Security.Cryptography;
//using System.Text;
//using System.Threading;
//using System.Threading.Tasks;

//namespace Crawler_project.Models
//{
//    /// <summary>
//    /// Host-level DNS аудит:
//    /// - NS записи (через прямой DNS запрос UDP)
//    /// - IP адреса (A/AAAA) через системный резолвер
//    /// Кэшируется по host.
//    /// </summary>
//    public sealed class HostingDnsChecks : ILinkCheck
//    {
//        private readonly DnsAuditService _dns;

//        public HostingDnsChecks(DnsAuditService dns) => _dns = dns;
//        public async ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
//        {
//            var host = ctx.FinalUri.Host;

//            if (!string.Equals(ctx.FinalUri.AbsolutePath, "/", StringComparison.Ordinal))
//                return Array.Empty<LinkIssue>();

//            var report = await _dns.GetAsync(host, ct);

//            var issues = new List<LinkIssue>();

//            if (report.IpAddresses.Count == 0)
//                issues.Add(new LinkIssue("DNS_NO_A_AAAA", $"Не найдены A/AAAA записи для {host}", IssueSeverity.Error));

//            if (!report.NsLookupOk)
//                issues.Add(new LinkIssue("DNS_NS_LOOKUP_FAILED", $"NS lookup failed (zone: {report.ZoneTried}): {report.NsError}", IssueSeverity.Warning));
//            else if (report.NameServers.Count == 0)
//                issues.Add(new LinkIssue("DNS_NO_NS", $"NS записи не найдены (зона: {report.ZoneTried})", IssueSeverity.Warning));

            
//            // INFO-вывод для UI (чтобы "NS" и "IP-адрес сервера" отображались даже при отсутствии ошибок)
//            if (report.NameServers.Count > 0)
//            {
//                var shown = report.NameServers.Take(10).ToArray();
//                var tail = report.NameServers.Count > shown.Length ? $" …(+{report.NameServers.Count - shown.Length})" : "";
//                issues.Add(new LinkIssue("HOST_NS", $"NS: {string.Join(", ", shown)}{tail}", IssueSeverity.Info));
//            }
//            else
//            {
//                issues.Add(new LinkIssue("HOST_NS", "NS: —", IssueSeverity.Warning));
//            }

//            if (report.IpAddresses.Count > 0)
//            {
//                var shown = report.IpAddresses.Take(10).ToArray();
//                var tail = report.IpAddresses.Count > shown.Length ? $" …(+{report.IpAddresses.Count - shown.Length})" : "";
//                issues.Add(new LinkIssue("HOST_IPS", $"IP-адрес сервера: {string.Join(", ", shown)}{tail}", IssueSeverity.Info));
//            }
//            else
//            {
//                issues.Add(new LinkIssue("HOST_IPS", "IP-адрес сервера: —", IssueSeverity.Error));
//            }

//            return issues;
//        }

//    }
//    #region Service + Models

//    public sealed record DnsAuditReport(
//        string Host,
//        bool NsLookupOk,
//        string ZoneTried,
//        IReadOnlyList<string> NameServers,
//        string? NsError,
//        IReadOnlyList<string> IpAddresses
//    );

//    public sealed class DnsAuditOptions
//    {
//        public int DnsTimeoutMs { get; set; } = 2500;
//        public int MaxZoneWalkSteps { get; set; } = 6;  // сколько раз "подниматься вверх" по домену, чтобы найти NS
//    }

//    public sealed class DnsAuditService
//    {
//        private readonly DnsAuditOptions _opt;
//        private readonly ConcurrentDictionary<string, Lazy<Task<DnsAuditReport>>> _cache = new(StringComparer.OrdinalIgnoreCase);

//        public Task<DnsAuditReport> GetAsync(string host, CancellationToken ct)
//        {
//            host = host.Trim().TrimEnd('.');

//            // ВАЖНО: в кэш кладём task без внешнего ct
//            var lazy = _cache.GetOrAdd(host, _ => new Lazy<Task<DnsAuditReport>>(
//                () => AuditAsync(host, CancellationToken.None)));

//            // а ожидание — с ct вызывающего
//            return lazy.Value.WaitAsync(ct);
//        }

//        private readonly IPEndPoint _dnsServer;

//        public DnsAuditService(DnsAuditOptions opt)
//        {
//            _opt = opt;
//            _dnsServer = new IPEndPoint(PickDnsServer(), 53);
//        }


//        private async Task<DnsAuditReport> AuditAsync(string host, CancellationToken ct)
//        {
//            // 1) IP (A/AAAA) — системный резолвер
//            var ips = new List<string>();
//            try
//            {
//                var addrs = await System.Net.Dns.GetHostAddressesAsync(host);
//                ips.AddRange(addrs.Select(a => a.ToString()).Distinct(StringComparer.OrdinalIgnoreCase));
//            }
//            catch
//            {
//                // IP пустой — отразим в issues
//            }

//            // 2) NS — прямой DNS запрос; ищем NS для зоны (walk-up по домену)
//            var (nsOk, zoneTried, nameServers, err) = await TryResolveNsWithWalkUpAsync(host, ct);

//            return new DnsAuditReport(
//                Host: host,
//                NsLookupOk: nsOk,
//                ZoneTried: zoneTried,
//                NameServers: nameServers,
//                NsError: err,
//                IpAddresses: ips
//            );
//        }

//        private async Task<(bool ok, string zone, List<string> ns, string? error)> TryResolveNsWithWalkUpAsync(string host, CancellationToken ct)
//        {
//            var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
//            if (labels.Count < 2)
//                return (false, host, new List<string>(), "HOST_TOO_SHORT_FOR_NS");

//            int steps = 0;
//            string current = host;

//            bool lastOk = false;
//            List<string> lastNs = new();
//            string? lastErr = null;

//            while (labels.Count >= 2 && steps < _opt.MaxZoneWalkSteps)
//            {
//                ct.ThrowIfCancellationRequested();

//                var (ok, ns, err) = await ResolveNsAsync(current, ct);

//                lastOk = ok;
//                lastNs = ns;
//                lastErr = err;

//                if (ok && ns.Count > 0)
//                    return (true, current, ns, null);

//                steps++;
//                labels.RemoveAt(0);
//                current = string.Join('.', labels);
//            }

//            var (okLast, nsLast, errLast) = await ResolveNsAsync(current, ct);

//            // ✅ ok = “запрос успешен”, даже если NS не найден
//            if (okLast)
//            {
//                if (nsLast.Count > 0)
//                    return (true, current, nsLast, null);

//                return (true, current, nsLast, "NS_NOT_FOUND");
//            }

//            return (false, current, nsLast, errLast ?? "NS_LOOKUP_FAILED");
//        }



//        private async Task<(bool ok, List<string> ns, string? error)> ResolveNsAsync(string zoneName, CancellationToken ct)
//        {
//            try
//            {
//                var query = DnsWire.BuildQuery(zoneName, qtype: 2 /* NS */);
//                var resp = await DnsWire.SendUdpAsync(_dnsServer, query, _opt.DnsTimeoutMs, ct);

//                var parsed = DnsWire.ParseResponse(resp);

//                // RCODE != 0 => ошибка (NXDOMAIN, SERVFAIL, etc)
//                if (parsed.RCode != 0)
//                    return (false, new List<string>(), $"RCODE={parsed.RCode}");

//                // ✅ ВАЖНО: NS часто приходит в Authorities, не только в Answers
//                var ns = parsed.Answers
//                    .Concat(parsed.Authorities)
//                    .Where(a => a.Type == 2)
//                    .Select(a => a.Data)
//                    .Where(s => !string.IsNullOrWhiteSpace(s))
//                    .Distinct(StringComparer.OrdinalIgnoreCase)
//                    .ToList();

//                return (true, ns, null);
//            }
//            catch (Exception ex)
//            {
//                return (false, new List<string>(), ex.Message);
//            }
//        }



//        private static IPAddress PickDnsServer()
//        {
//            try
//            {
//                IPAddress? firstV6 = null;

//                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
//                {
//                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

//                    var props = ni.GetIPProperties();
//                    foreach (var dns in props.DnsAddresses)
//                    {
//                        if (dns is null) continue;
//                        if (dns.Equals(IPAddress.Any) || dns.Equals(IPAddress.IPv6Any)) continue;

//                        // 1) предпочитаем IPv4
//                        if (dns.AddressFamily == AddressFamily.InterNetwork)
//                            return dns;

//                        // 2) IPv6 берём только если не link-local/site-local/teredo
//                        if (dns.AddressFamily == AddressFamily.InterNetworkV6)
//                        {
//                            if (dns.IsIPv6LinkLocal || dns.IsIPv6SiteLocal || dns.IsIPv6Teredo || dns.IsIPv6Multicast)
//                                continue;

//                            firstV6 ??= dns;
//                        }
//                    }
//                }

//                if (firstV6 != null)
//                    return firstV6;
//            }
//            catch
//            {
//                // ignore
//            }

//            return IPAddress.Parse("1.1.1.1");
//        }


//    }

//    #endregion

//    #region Minimal DNS wire (UDP) + parser (NS)

//    internal static class DnsWire
//    {
//        internal sealed record Answer(string Name, ushort Type, ushort Class, uint Ttl, string Data);

//        internal sealed record ParsedResponse(ushort Id, int RCode, List<Answer> Answers, List<Answer> Authorities);

//        public static byte[] BuildQuery(string qname, ushort qtype)
//        {
//            // DNS header (12 bytes)
//            // ID (2), FLAGS (2), QDCOUNT (2), ANCOUNT (2), NSCOUNT (2), ARCOUNT (2)
//            var id = RandomNumberGenerator.GetInt32(0, 65536);
//            var buf = new List<byte>(512);

//            WriteU16(buf, (ushort)id);
//            WriteU16(buf, 0x0100); // RD=1
//            WriteU16(buf, 0x0001); // QDCOUNT=1
//            WriteU16(buf, 0x0000); // ANCOUNT
//            WriteU16(buf, 0x0000); // NSCOUNT
//            WriteU16(buf, 0x0000); // ARCOUNT

//            WriteQName(buf, qname);
//            WriteU16(buf, qtype);   // QTYPE
//            WriteU16(buf, 0x0001);  // QCLASS=IN

//            return buf.ToArray();
//        }

//        public static async Task<byte[]> SendUdpAsync(IPEndPoint dnsServer, byte[] query, int timeoutMs, CancellationToken ct)
//        {
//            using var udp = new UdpClient(dnsServer.AddressFamily);

//            if (dnsServer.AddressFamily == AddressFamily.InterNetworkV6)
//                udp.Client.DualMode = true; // безопасно: позволяет работать и с v6, и с v4-mapped

//            udp.Connect(dnsServer);

//            await udp.SendAsync(query, query.Length);

//            var receiveTask = udp.ReceiveAsync();
//            var timeoutTask = Task.Delay(timeoutMs, ct); // если ct отменён — выйдем корректно

//            var completed = await Task.WhenAny(receiveTask, timeoutTask);
//            if (completed != receiveTask)
//            {
//                ct.ThrowIfCancellationRequested();
//                throw new TimeoutException($"DNS timeout ({timeoutMs}ms) for {dnsServer}");
//            }

//            return receiveTask.Result.Buffer;
//        }

//        public static ParsedResponse ParseResponse(byte[] msg)
//        {
//            if (msg.Length < 12) throw new FormatException("DNS response too short");

//            int offset = 0;
//            ushort id = ReadU16(msg, ref offset);
//            ushort flags = ReadU16(msg, ref offset);
//            ushort qdCount = ReadU16(msg, ref offset);
//            ushort anCount = ReadU16(msg, ref offset);
//            ushort nsCount = ReadU16(msg, ref offset);
//            ushort arCount = ReadU16(msg, ref offset);

//            int rcode = flags & 0x000F;

//            // skip questions
//            for (int i = 0; i < qdCount; i++)
//            {
//                ReadName(msg, ref offset);
//                offset += 4; // QTYPE+QCLASS
//                if (offset > msg.Length) throw new FormatException("DNS question overflow");
//            }

//            var answers = new List<Answer>(anCount);
//            var authorities = new List<Answer>(nsCount);

//            // answers
//            for (int i = 0; i < anCount; i++)
//                answers.Add(ReadResourceRecord(msg, ref offset));

//            // authority (NSCOUNT)
//            for (int i = 0; i < nsCount; i++)
//                authorities.Add(ReadResourceRecord(msg, ref offset));

//            // additional (ARCOUNT) — просто корректно проматываем
//            for (int i = 0; i < arCount; i++)
//                _ = ReadResourceRecord(msg, ref offset);

//            return new ParsedResponse(id, rcode, answers, authorities);
//        }
//        private static Answer ReadResourceRecord(byte[] msg, ref int offset)
//        {
//            var name = ReadName(msg, ref offset);
//            var type = ReadU16(msg, ref offset);
//            var klass = ReadU16(msg, ref offset);
//            var ttl = ReadU32(msg, ref offset);
//            var rdlen = ReadU16(msg, ref offset);

//            if (offset + rdlen > msg.Length) throw new FormatException("DNS rdata overflow");

//            string data;
//            if (type == 2) // NS
//            {
//                int rdataOffset = offset;
//                data = ReadName(msg, ref rdataOffset).TrimEnd('.');
//            }
//            else
//            {
//                data = "";
//            }

//            offset += rdlen;
//            return new Answer(name, type, klass, ttl, data);
//        }

//        private static void WriteQName(List<byte> buf, string name)
//        {
//            // example.com -> [7]example[3]com[0]
//            var labels = name.Trim().TrimEnd('.')
//                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

//            foreach (var lab in labels)
//            {
//                var bytes = Encoding.ASCII.GetBytes(lab);
//                if (bytes.Length > 63) throw new ArgumentException("Label too long");
//                buf.Add((byte)bytes.Length);
//                buf.AddRange(bytes);
//            }
//            buf.Add(0);
//        }

//        private static void WriteU16(List<byte> buf, ushort v)
//        {
//            buf.Add((byte)((v >> 8) & 0xFF));
//            buf.Add((byte)(v & 0xFF));
//        }

//        private static ushort ReadU16(byte[] msg, ref int offset)
//        {
//            if (offset + 2 > msg.Length) throw new FormatException("ReadU16 overflow");
//            ushort v = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(offset, 2));
//            offset += 2;
//            return v;
//        }

//        private static uint ReadU32(byte[] msg, ref int offset)
//        {
//            if (offset + 4 > msg.Length) throw new FormatException("ReadU32 overflow");
//            uint v = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(offset, 4));
//            offset += 4;
//            return v;
//        }

//        private static string ReadName(byte[] msg, ref int offset)
//        {
//            // DNS name with compression pointers
//            var sb = new StringBuilder();
//            int originalOffset = offset;
//            bool jumped = false;
//            int jumps = 0;

//            while (true)
//            {
//                if (offset >= msg.Length) throw new FormatException("Name overflow");

//                byte len = msg[offset++];

//                // pointer: 11xxxxxx xxxxxxxx
//                if ((len & 0xC0) == 0xC0)
//                {
//                    if (offset >= msg.Length) throw new FormatException("Pointer overflow");
//                    ushort ptr = (ushort)(((len & 0x3F) << 8) | msg[offset++]);

//                    if (!jumped)
//                    {
//                        originalOffset = offset;
//                        jumped = true;
//                    }

//                    offset = ptr;

//                    if (++jumps > 10) throw new FormatException("Too many DNS pointer jumps");
//                    continue;
//                }

//                if (len == 0)
//                    break;

//                if (offset + len > msg.Length) throw new FormatException("Label overflow");

//                if (sb.Length > 0) sb.Append('.');
//                sb.Append(Encoding.ASCII.GetString(msg, offset, len));
//                offset += len;
//            }

//            if (jumped)
//                offset = originalOffset;

//            return sb.ToString();
//        }
//    }

//    #endregion
//}
