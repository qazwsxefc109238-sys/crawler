using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Crawler_project.Models;

namespace Crawler_project.Checks
{
    public sealed class ContentQualityOptions
    {
        // Thin pages
        public int ThinMinVisibleChars { get; set; } = 600;
        public int ThinMinWords { get; set; } = 120;
        public double ThinMinTextToHtmlRatio { get; set; } = 0.05; // visibleChars / htmlChars
        public int ThinIssueSamplePreviewChars { get; set; } = 160;

        // Exact duplicates
        public int MaxUrlsPerExactDupGroup { get; set; } = 10;

        // Near duplicates (SimHash)
        public int SimHashMaxHammingDistance { get; set; } = 5; // 3-5 обычно норм
        public int LshBands { get; set; } = 4;                 // 4*16бит = 64
        public int MaxCandidatesPerBucket { get; set; } = 200;
        public int MaxNearDupPairsToKeep { get; set; } = 5000;

        // Spam (keyword stuffing)
        public int SpamMinTotalTokens { get; set; } = 120;      // ниже — не оцениваем “переспам”
        public double SpamTopTokenRatioThreshold { get; set; } = 0.09; // топ-слово >9%
        public double SpamUniqueRatioThreshold { get; set; } = 0.35;   // unique/total < 0.35
        public int SpamMaxSingleTokenCount { get; set; } = 80;   // один токен встречается >80 раз
        public int SpamMinTokenLen { get; set; } = 3;            // игнорировать короткие токены для топ-слова

        // Общие лимиты/защита
        public int MaxTextCharsToProcess { get; set; } = 1_500_000; // не перемалывать мегабайты
    }

    /// <summary>
    /// Проблемы в содержании:
    /// - тощие страницы
    /// - точные дубли (100%)
    /// - очень похожие тексты (SimHash)
    /// - переспамленность (эвристики)
    /// </summary>
    public sealed class ContentQualityCheck : ILinkCheck
    {
        private readonly ContentQualityOptions _opt;
        private readonly ContentQualityStore _store;

        public ContentQualityCheck(ContentQualityOptions opt, ContentQualityStore store)
        {
            _opt = opt;
            _store = store;
        }

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            // Проверяем только HTML-страницы с успешным кодом
            if (ctx.Document is null) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());
            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 400) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var contentType = ctx.ContentType ?? "";
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            var html = ctx.Html ?? "";
            if (html.Length == 0) return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (html.Length > _opt.MaxTextCharsToProcess)
                html = html.Substring(0, _opt.MaxTextCharsToProcess);

            // 1) Извлекаем видимый текст
            var visibleText = ContentTextExtractor.ExtractVisibleText(ctx.Document);
            if (visibleText.Length > _opt.MaxTextCharsToProcess)
                visibleText = visibleText.Substring(0, _opt.MaxTextCharsToProcess);

            var visibleChars = visibleText.Length;
            var htmlChars = html.Length;

            var tokens = Tokenizer.Tokenize(visibleText);
            var words = tokens.Count;

            var textToHtmlRatio = htmlChars > 0 ? (double)visibleChars / htmlChars : 0.0;

            var issues = new List<LinkIssue>();

            // 2) Thin page
            var isThin = (visibleChars < _opt.ThinMinVisibleChars) ||
                         (words < _opt.ThinMinWords) ||
                         (textToHtmlRatio < _opt.ThinMinTextToHtmlRatio);

            if (isThin)
            {
                _store.MarkThin(ctx.FinalUri.Host, ctx.FinalUrl);

                var preview = visibleText.Length == 0
                    ? "(нет видимого текста)"
                    : TrimTo(visibleText, _opt.ThinIssueSamplePreviewChars);

                issues.Add(new LinkIssue(
                    "CONTENT_THIN_PAGE",
                    $"Тощая страница: visibleChars={visibleChars}, words={words}, text/html={textToHtmlRatio:F3}. Превью: {preview}",
                    IssueSeverity.Warning));
            }

            // 3) Exact duplicates (100%) по нормализованному видимому тексту
            // Нормализация: trim + collapse spaces + lower
            var normText = ContentTextExtractor.NormalizeForHash(visibleText);

            if (!string.IsNullOrWhiteSpace(normText))
            {
                var hash = ContentHash.Sha256Hex(normText);

                var exactCount = _store.AddExactDuplicate(ctx.FinalUri.Host, hash, ctx.FinalUrl, _opt.MaxUrlsPerExactDupGroup);
                if (exactCount >= 2)
                {
                    issues.Add(new LinkIssue(
                        "CONTENT_EXACT_DUPLICATE",
                        "Точный дубль страницы (100%) по тексту: совпадение хэша контента.",
                        IssueSeverity.Warning));
                }

                // 4) Near duplicates (SimHash)
                var simhash = SimHash.Compute64(tokens);

                var nearMatch = _store.TryFindNearDuplicateAndAdd(
                    host: ctx.FinalUri.Host,
                    url: ctx.FinalUrl,
                    simhash: simhash,
                    maxHamming: _opt.SimHashMaxHammingDistance,
                    bands: _opt.LshBands,
                    maxCandidatesPerBucket: _opt.MaxCandidatesPerBucket,
                    maxPairsToKeep: _opt.MaxNearDupPairsToKeep);

                if (nearMatch is not null)
                {
                    issues.Add(new LinkIssue(
                        "CONTENT_NEAR_DUPLICATE",
                        $"Очень похожий текст (SimHash, dist={nearMatch.HammingDistance}) — возможно, дубликат/перегенерированный контент. Похожа на: {nearMatch.OtherUrl}",
                        IssueSeverity.Warning));
                }
            }

            // 5) Spam / keyword stuffing
            var spam = SpamHeuristics.Check(tokens, _opt);
            if (spam.IsSpam)
            {
                _store.MarkSpam(ctx.FinalUri.Host, ctx.FinalUrl);

                issues.Add(new LinkIssue(
                    "CONTENT_KEYWORD_STUFFING",
                    $"Переспамленность (эвристика): topToken=\"{spam.TopToken}\" ratio={spam.TopTokenRatio:F3}, uniqueRatio={spam.UniqueRatio:F3}, topCount={spam.TopTokenCount}, total={spam.TotalTokens}.",
                    IssueSeverity.Warning));
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }

        private static string TrimTo(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }

    // -------------------- Store + report models --------------------

    public sealed class ContentQualityStore
    {
        // host -> thin urls
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _thin =
            new(StringComparer.OrdinalIgnoreCase);

        // host -> spam urls
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _spam =
            new(StringComparer.OrdinalIgnoreCase);

        // host -> exactHash -> counter+urls
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CounterWithSamples>> _exact =
            new(StringComparer.OrdinalIgnoreCase);

        // host -> LSH buckets
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<uint, ConcurrentBag<SimHashEntry>>> _lshBuckets =
            new(StringComparer.OrdinalIgnoreCase);

        // host -> known pairs (to avoid duplicates)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, NearDupPair>> _nearPairs =
            new(StringComparer.OrdinalIgnoreCase);

        public void ResetHost(string host)
        {
            _thin.TryRemove(host, out _);
            _spam.TryRemove(host, out _);
            _exact.TryRemove(host, out _);
            _lshBuckets.TryRemove(host, out _);
            _nearPairs.TryRemove(host, out _);
        }

        public void MarkThin(string host, string url)
        {
            var set = _thin.GetOrAdd(host, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            set.TryAdd(url, 0);
        }

        public void MarkSpam(string host, string url)
        {
            var set = _spam.GetOrAdd(host, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            set.TryAdd(url, 0);
        }

        public int AddExactDuplicate(string host, string hashHex, string url, int maxSamples)
        {
            var map = _exact.GetOrAdd(host, _ => new ConcurrentDictionary<string, CounterWithSamples>(StringComparer.OrdinalIgnoreCase));
            var ctr = map.GetOrAdd(hashHex, _ => new CounterWithSamples());
            return ctr.Increment(url, maxSamples);
        }

        public NearDupMatch? TryFindNearDuplicateAndAdd(
            string host,
            string url,
            ulong simhash,
            int maxHamming,
            int bands,
            int maxCandidatesPerBucket,
            int maxPairsToKeep)
        {
            var buckets = _lshBuckets.GetOrAdd(host, _ => new ConcurrentDictionary<uint, ConcurrentBag<SimHashEntry>>());
            var pairs = _nearPairs.GetOrAdd(host, _ => new ConcurrentDictionary<string, NearDupPair>(StringComparer.OrdinalIgnoreCase));

            // 1) Собираем кандидатов по LSH
            var candidates = new List<SimHashEntry>(256);

            foreach (var key in SimHash.LshKeys(simhash, bands))
            {
                if (buckets.TryGetValue(key, out var bag))
                {
                    // ограничим просмотр
                    int taken = 0;
                    foreach (var e in bag)
                    {
                        candidates.Add(e);
                        if (++taken >= maxCandidatesPerBucket) break;
                    }
                }
            }

            // 2) Ищем ближайшего
            NearDupMatch? best = null;

            foreach (var c in candidates)
            {
                if (string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dist = SimHash.HammingDistance(simhash, c.Hash);
                if (dist <= maxHamming)
                {
                    if (best is null || dist < best.HammingDistance)
                        best = new NearDupMatch(c.Url, dist);
                }
            }

            // 3) Добавляем текущую страницу в бакеты
            var entry = new SimHashEntry(url, simhash);
            foreach (var key in SimHash.LshKeys(simhash, bands))
            {
                var bag = buckets.GetOrAdd(key, _ => new ConcurrentBag<SimHashEntry>());
                bag.Add(entry);
            }

            // 4) Если нашли — фиксируем пару в store (для отчёта)
            if (best is not null)
            {
                var a = url;
                var b = best.OtherUrl;
                var pairKey = MakePairKey(a, b);

                // ограничим количество пар
                if (pairs.Count < maxPairsToKeep)
                {
                    pairs.TryAdd(pairKey, new NearDupPair(a, b, best.HammingDistance));
                }
            }

            return best;
        }

        public ContentAuditReport BuildReportForHost(string host, int maxExactGroups = 200, int maxGroupUrls = 10, int maxPairs = 500)
        {
            var thinUrls = _thin.TryGetValue(host, out var thinSet) ? thinSet.Keys.ToArray() : Array.Empty<string>();
            var spamUrls = _spam.TryGetValue(host, out var spamSet) ? spamSet.Keys.ToArray() : Array.Empty<string>();

            var exactGroups = new List<ExactDuplicateGroup>();
            if (_exact.TryGetValue(host, out var ex))
            {
                exactGroups = ex
                    .Where(kv => kv.Value.Count >= 2)
                    .OrderByDescending(kv => kv.Value.Count)
                    .Take(Math.Clamp(maxExactGroups, 1, 2000))
                    .Select(kv => new ExactDuplicateGroup(
                        Hash: kv.Key,
                        Count: kv.Value.Count,
                        UrlSamples: kv.Value.Samples.Take(maxGroupUrls).ToArray()
                    ))
                    .ToList();
            }

            var nearPairs = new List<NearDupPair>();
            if (_nearPairs.TryGetValue(host, out var np))
            {
                nearPairs = np.Values
                    .OrderBy(p => p.HammingDistance)
                    .Take(Math.Clamp(maxPairs, 1, 5000))
                    .ToList();
            }

            return new ContentAuditReport(
                Host: host,
                ThinPagesCount: thinUrls.Length,
                ThinPagesSample: thinUrls.Take(50).ToArray(),
                ExactDuplicateGroupsCount: exactGroups.Count,
                ExactDuplicateGroups: exactGroups,
                NearDuplicatePairsCount: nearPairs.Count,
                NearDuplicatePairs: nearPairs,
                SpamPagesCount: spamUrls.Length,
                SpamPagesSample: spamUrls.Take(50).ToArray()
            );
        }

        private sealed class CounterWithSamples
        {
            private int _count;
            private int _samplesCount;
            private readonly ConcurrentQueue<string> _urls = new();

            public int Increment(string url, int maxSamples)
            {
                var c = Interlocked.Increment(ref _count);

                var sc = Volatile.Read(ref _samplesCount);
                if (sc < maxSamples)
                {
                    _urls.Enqueue(url);
                    Interlocked.Increment(ref _samplesCount);
                }

                return c;
            }

            public int Count => Volatile.Read(ref _count);
            public string[] Samples => _urls.ToArray();
        }

        private static string MakePairKey(string a, string b)
        {
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
                ? a + "||" + b
                : b + "||" + a;
        }

        private sealed record SimHashEntry(string Url, ulong Hash);
    }

    public sealed record ExactDuplicateGroup(string Hash, int Count, string[] UrlSamples);
    public sealed record NearDupPair(string UrlA, string UrlB, int HammingDistance);
    public sealed record NearDupMatch(string OtherUrl, int HammingDistance);

    public sealed record ContentAuditReport(
        string Host,
        int ThinPagesCount,
        string[] ThinPagesSample,
        int ExactDuplicateGroupsCount,
        IReadOnlyList<ExactDuplicateGroup> ExactDuplicateGroups,
        int NearDuplicatePairsCount,
        IReadOnlyList<NearDupPair> NearDuplicatePairs,
        int SpamPagesCount,
        string[] SpamPagesSample
    );

    // -------------------- Controller for report --------------------

    // Отчёт логичнее смотреть по jobId (берём host из StartUrl)
    // и возвращаем агрегированную статистику по store.
    // Важно: store ключуется по host, поэтому перед запуском нового job для host нужно ResetHost(host).
}

namespace Crawler_project.Controllers
{
    using Crawler_project.Checks;

    [ApiController]
    [Route("api/crawl")]
    public sealed class ContentAuditController : ControllerBase
    {
        private readonly JobStore _store;
        private readonly ContentQualityStore _content;

        public ContentAuditController(JobStore store, ContentQualityStore content)
        {
            _store = store;
            _content = content;
        }

        [HttpGet("{jobId:guid}/content-audit")]
        public ActionResult<ContentAuditReport> ContentAudit(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            var host = new Uri(job.StartUrl).Host;
            var report = _content.BuildReportForHost(host);

            return Ok(report);
        }
    }
}

namespace Crawler_project.Checks
{
    // -------------------- Text extraction / normalization --------------------

    internal static class ContentTextExtractor
    {
        public static string ExtractVisibleText(HtmlDocument doc)
        {
            // ✅ Берём контентную зону, а не весь body
            var root =
                doc.DocumentNode.SelectSingleNode("//main") ??
                doc.DocumentNode.SelectSingleNode("//article") ??
                doc.DocumentNode.SelectSingleNode("//*[@role='main']") ??
                doc.DocumentNode.SelectSingleNode("//body") ??
                doc.DocumentNode;

            var clone = root.CloneNode(true);

            // тех. узлы
            RemoveNodes(clone, "//script|//style|//noscript|//template|//svg|//canvas|//iframe");
            RemoveNodes(clone, "//comment()");

            // ✅ выкидываем типовой boilerplate
            RemoveNodes(clone, "//header|//footer|//nav|//aside");
            RemoveNodes(clone, "//form|//button|//input|//select|//textarea");

            RemoveNodes(clone, "//*[@aria-hidden='true']");
            RemoveNodesContainsStyle(clone, "display:none");
            RemoveNodesContainsStyle(clone, "visibility:hidden");

            var text = WebUtility.HtmlDecode(clone.InnerText ?? "");
            text = NormalizeWhitespace(text);
            return text.Trim();
        }



        public static string NormalizeForHash(string s)
        {
            s = WebUtility.HtmlDecode(s ?? "");
            s = NormalizeWhitespace(s).Trim().ToLowerInvariant();
            return s;
        }

        private static void RemoveNodes(HtmlNode root, string xpath)
        {
            var nodes = root.SelectNodes(xpath);
            if (nodes is null) return;

            foreach (var n in nodes)
                n.Remove();
        }

        private static void RemoveNodesContainsStyle(HtmlNode root, string needleLower)
        {
            // XPath contains/translate для style; делаем вручную по атрибуту, чтобы не усложнять XPath.
            var nodes = root.SelectNodes("//*[@style]");
            if (nodes is null) return;

            foreach (var n in nodes)
            {
                var style = (n.GetAttributeValue("style", "") ?? "").ToLowerInvariant();
                if (style.Contains(needleLower, StringComparison.Ordinal))
                    n.Remove();
            }
        }

        private static string NormalizeWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace('\u00A0', ' ');

            var sb = new StringBuilder(s.Length);
            bool prevWs = false;

            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!prevWs) sb.Append(' ');
                    prevWs = true;
                }
                else
                {
                    sb.Append(ch);
                    prevWs = false;
                }
            }
            return sb.ToString();
        }
    }

    // -------------------- Tokenizer --------------------

    internal static class Tokenizer
    {
        public static List<string> Tokenize(string text)
        {
            var tokens = new List<string>(capacity: Math.Min(4096, text.Length / 4));

            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    Flush();
                }
            }
            Flush();

            return tokens;

            void Flush()
            {
                if (sb.Length == 0) return;
                var t = sb.ToString();
                sb.Clear();

                // отсекаем мусор
                if (t.Length < 2) return;
                tokens.Add(t);
            }
        }
    }

    // -------------------- Hash --------------------

    internal static class ContentHash
    {
        public static string Sha256Hex(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash); // .NET 5+
        }
    }

    // -------------------- SimHash (64-bit) + LSH keys --------------------

    internal static class SimHash
    {
        public static ulong Compute64(IReadOnlyList<string> tokens)
        {
            // Веса по частоте токенов (простая модель)
            var freq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in tokens)
            {
                if (freq.TryGetValue(t, out var c)) freq[t] = c + 1;
                else freq[t] = 1;
            }

            var v = new int[64];

            foreach (var kv in freq)
            {
                var h = Fnv1a64(kv.Key);
                var w = kv.Value;

                for (int i = 0; i < 64; i++)
                {
                    var bit = ((h >> i) & 1UL) != 0;
                    v[i] += bit ? w : -w;
                }
            }

            ulong outHash = 0;
            for (int i = 0; i < 64; i++)
            {
                if (v[i] > 0) outHash |= (1UL << i);
            }

            return outHash;
        }

        public static int HammingDistance(ulong a, ulong b)
        {
            ulong x = a ^ b;
            // popcount
            int cnt = 0;
            while (x != 0)
            {
                x &= (x - 1);
                cnt++;
            }
            return cnt;
        }

        public static IEnumerable<uint> LshKeys(ulong simhash, int bands)
        {
            // 64 bits split into bands of equal size
            bands = Math.Clamp(bands, 1, 8);
            int bandBits = 64 / bands; // например 16
            if (bandBits <= 0) bandBits = 16;

            for (int b = 0; b < bands; b++)
            {
                int shift = b * bandBits;
                ulong mask = bandBits == 64 ? ulong.MaxValue : ((1UL << bandBits) - 1UL);
                uint part = (uint)((simhash >> shift) & mask);

                // упакуем bandIndex + part в один uint ключ
                // (bandIndex в старшие 8-12 бит)
                uint key = ((uint)b << 24) ^ part;
                yield return key;
            }
        }

        private static ulong Fnv1a64(string s)
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            ulong h = fnvOffset;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= (byte)s[i];
                h *= fnvPrime;
            }
            return h;
        }
    }

    // -------------------- Spam heuristics --------------------

    internal static class SpamHeuristics
    {
        private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
        {
            // RU
            "и","в","во","не","что","он","на","я","с","со","как","а","то","все","она","так","его","но","да","ты","к","у","же","вы","за","бы","по","ее","мне","было","вот","от","меня","еще","нет","о","из","ему","теперь","когда","даже","ну","вдруг","ли","если","уже","или","ни","быть","был","него","до","вас","нибудь","опять","уж","вам","ведь","там","потом","себя","ничего","ей","может","они","тут","где","есть","надо","ней","для","мы","тебя","их","чем","была","сам","чтоб","без","будто","чего","раз","тоже","себе","под","будет","ж","тогда","кто","этот","того","потому","этого","какой","совсем","ним","здесь","этом","один","почти","мой","тем","чтобы","нее","кажется",
            // EN
            "the","and","for","with","that","this","you","your","from","are","was","were","have","has","had","not","but","they","their","them","his","her","she","him","our","ours","what","when","where","who","why","how","a","an","to","in","on","of","as","at","by","or","if","it","is","be"
        };

        internal sealed record SpamResult(bool IsSpam, string TopToken, double TopTokenRatio, double UniqueRatio, int TopTokenCount, int TotalTokens);

        public static SpamResult Check(IReadOnlyList<string> tokens, ContentQualityOptions opt)
        {
            if (tokens.Count < opt.SpamMinTotalTokens)
                return new SpamResult(false, "", 0, 1, 0, tokens.Count);

            var freq = new Dictionary<string, int>(StringComparer.Ordinal);
            int total = 0;

            foreach (var t in tokens)
            {
                total++;
                if (freq.TryGetValue(t, out var c)) freq[t] = c + 1;
                else freq[t] = 1;
            }

            int unique = freq.Count;
            double uniqueRatio = total > 0 ? (double)unique / total : 1.0;

            // Top token (игнорируем стоп-слова и короткие токены)
            string top = "";
            int topCount = 0;

            foreach (var kv in freq)
            {
                if (kv.Key.Length < opt.SpamMinTokenLen) continue;
                if (Stop.Contains(kv.Key)) continue;

                if (kv.Value > topCount)
                {
                    topCount = kv.Value;
                    top = kv.Key;
                }
            }

            // если не нашли ничего “смысленного”, возьмем абсолютный топ (чтобы всё равно посчитать)
            if (topCount == 0)
            {
                var absTop = freq.OrderByDescending(x => x.Value).FirstOrDefault();
                top = absTop.Key ?? "";
                topCount = absTop.Value;
            }

            double topRatio = total > 0 ? (double)topCount / total : 0;

            // эвристики
            bool spamByTopRatio = topRatio >= opt.SpamTopTokenRatioThreshold;
            bool spamByUniqueRatio = uniqueRatio <= opt.SpamUniqueRatioThreshold;
            bool spamByAbsoluteCount = topCount >= opt.SpamMaxSingleTokenCount;

            bool isSpam = (spamByTopRatio && spamByUniqueRatio) || spamByAbsoluteCount;

            return new SpamResult(isSpam, top, topRatio, uniqueRatio, topCount, total);
        }
    }
}
