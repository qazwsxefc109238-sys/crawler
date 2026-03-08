using Crawler_project.Checks;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Crawler_project.Models.LinkChecks
{
    public sealed class HtmlMarkupErrorsOptions
    {
        public int MaxParseErrorsToReport { get; set; } = 10;
        public int MaxDuplicateIdsToReport { get; set; } = 20;
        public int MaxNestedSamplesToReport { get; set; } = 5;

        /// <summary>
        /// Если ParseErrors пустые, можно показать подсказку про OptionCheckSyntax.
        /// </summary>
        public bool EmitHintWhenNoParseErrors { get; set; } = true;

        /// <summary>
        /// Порог, после которого ParseErrors считаем Error (иначе Warning).
        /// </summary>
        public int ParseErrorsErrorThreshold { get; set; } = 15;
    }

    /// <summary>
    /// Ошибки в разметке HTML (эвристически):
    /// - HtmlAgilityPack.ParseErrors (если включен OptionCheckSyntax на этапе парсинга HTML)
    /// - Duplicate id
    /// - Nested anchors
    /// - Nested forms
    /// </summary>
    public sealed class HtmlMarkupErrorsCheck : ILinkCheck
    {
        private readonly HtmlMarkupErrorsOptions _opt;

        public HtmlMarkupErrorsCheck(HtmlMarkupErrorsOptions opt) => _opt = opt;

        public ValueTask<IEnumerable<LinkIssue>> CheckAsync(LinkCheckContext ctx, CancellationToken ct)
        {
            if (ctx.Document is null)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (ctx.FinalStatusCode < 200 || ctx.FinalStatusCode >= 300)
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());

            if (string.IsNullOrWhiteSpace(ctx.ContentType) ||
                !ctx.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<IEnumerable<LinkIssue>>(Array.Empty<LinkIssue>());


            var doc = ctx.Document;
            var issues = new List<LinkIssue>();

            // 1) ParseErrors (HtmlAgilityPack)
            var parseErrors = SafeParseErrors(doc);

            if (parseErrors.Count > 0)
            {
                var sev = parseErrors.Count >= _opt.ParseErrorsErrorThreshold ? IssueSeverity.Error : IssueSeverity.Warning;

                var sb = new StringBuilder();
                sb.Append($"Найдены ошибки парсинга HTML (HtmlAgilityPack): {parseErrors.Count}. Примеры: ");

                foreach (var e in parseErrors.Take(Math.Clamp(_opt.MaxParseErrorsToReport, 1, 50)))
                {
                    // HtmlParseError has Line/LinePosition/Reason/Code depending on version
                    var line = TryGetInt(e, "Line");
                    var pos = TryGetInt(e, "LinePosition");
                    var reason = TryGetString(e, "Reason") ?? TryGetString(e, "Code") ?? "unknown";

                    sb.Append($"[L{line}:C{pos}] {TrimTo(reason, 140)}; ");
                }

                issues.Add(new LinkIssue("HTML_MARKUP_PARSE_ERRORS", sb.ToString().Trim(), sev));
            }
            else if (_opt.EmitHintWhenNoParseErrors)
            {
                // Это не ошибка страницы, а подсказка для корректной настройки сборки ParseErrors
                issues.Add(new LinkIssue(
                    "HTML_MARKUP_PARSE_ERRORS_HINT",
                    "ParseErrors отсутствуют. Чтобы HtmlAgilityPack собирал ошибки разметки, создавайте HtmlDocument с OptionCheckSyntax=true до LoadHtml().",
                    IssueSeverity.Info));
            }

            // 2) Duplicate id
            var dupIds = FindDuplicateIds(doc);
            if (dupIds.Count > 0)
            {
                var sample = string.Join(", ",
                    dupIds.Take(Math.Clamp(_opt.MaxDuplicateIdsToReport, 1, 200))
                          .Select(x => $"{x.Id}({x.Count})"));

                issues.Add(new LinkIssue(
                    "HTML_DUPLICATE_IDS",
                    $"Найдены дублирующиеся id (id должен быть уникальным): {dupIds.Count}. Примеры: {sample}",
                    IssueSeverity.Warning));
            }

            // 3) Nested <a> inside <a>
            var nestedAnchors = doc.DocumentNode.SelectNodes("//a//a");
            if (nestedAnchors is not null && nestedAnchors.Count > 0)
            {
                var sample = string.Join(" | ",
                    nestedAnchors.Take(Math.Clamp(_opt.MaxNestedSamplesToReport, 1, 20))
                        .Select(n =>
                        {
                            var href = n.GetAttributeValue("href", "");
                            return string.IsNullOrWhiteSpace(href) ? "<a>(no href)</a>" : $"<a href=\"{TrimTo(href, 80)}\">";
                        }));

                issues.Add(new LinkIssue(
                    "HTML_NESTED_ANCHORS",
                    $"Найдены вложенные теги <a> внутри <a> (невалидная разметка): {nestedAnchors.Count}. Примеры: {sample}",
                    IssueSeverity.Warning));
            }

            // 4) Nested <form> inside <form>
            var nestedForms = doc.DocumentNode.SelectNodes("//form//form");
            if (nestedForms is not null && nestedForms.Count > 0)
            {
                var sample = string.Join(" | ",
                    nestedForms.Take(Math.Clamp(_opt.MaxNestedSamplesToReport, 1, 20))
                        .Select(n =>
                        {
                            var action = n.GetAttributeValue("action", "");
                            return string.IsNullOrWhiteSpace(action) ? "<form>(no action)</form>" : $"<form action=\"{TrimTo(action, 80)}\">";
                        }));

                issues.Add(new LinkIssue(
                    "HTML_NESTED_FORMS",
                    $"Найдены вложенные теги <form> внутри <form> (невалидная разметка): {nestedForms.Count}. Примеры: {sample}",
                    IssueSeverity.Warning));
            }

            return ValueTask.FromResult<IEnumerable<LinkIssue>>(issues);
        }

        private static List<object> SafeParseErrors(HtmlDocument doc)
        {
            try
            {
                // HtmlAgilityPack.ParseErrors: IEnumerable<HtmlParseError>
                // чтобы не завязываться на конкретную версию пакета, читаем как object
                var pe = doc.ParseErrors;
                if (pe is null) return new List<object>();
                return pe.Cast<object>().ToList();
            }
            catch
            {
                return new List<object>();
            }
        }

        private static List<(string Id, int Count)> FindDuplicateIds(HtmlDocument doc)
        {
            var nodes = doc.DocumentNode.SelectNodes("//*[@id]");
            if (nodes is null) return new List<(string, int)>();

            var groups = nodes
                .Select(n => (n.GetAttributeValue("id", "") ?? "").Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .GroupBy(id => id, StringComparer.Ordinal)
                .Select(g => (Id: g.Key, Count: g.Count()))
                .Where(x => x.Count > 1)
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .ToList();

            return groups;
        }

        private static int TryGetInt(object obj, string prop)
        {
            var pi = obj.GetType().GetProperty(prop);
            if (pi is null) return 0;
            var v = pi.GetValue(obj);
            return v is int i ? i : 0;
        }

        private static string? TryGetString(object obj, string prop)
        {
            var pi = obj.GetType().GetProperty(prop);
            if (pi is null) return null;
            var v = pi.GetValue(obj);
            return v?.ToString();
        }

        private static string TrimTo(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
