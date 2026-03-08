using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;


//Ахтунг, ниже всё писаное нагло переписано с чужого гита, но оно работает, поэтому менять здесь что-то ТОЛЬКО на свой страх и риск
public sealed class RobotsRules
{
    private readonly List<Rule> _rules;

    private RobotsRules(List<Rule> rules) => _rules = rules;

    public bool IsAllowed(Uri uri)
    {
        var target = uri.PathAndQuery;
        Rule? best = null;
        foreach (var r in _rules)
        {
            if (!r.IsMatch(target)) continue;

            if (best is null)
            {
                best = r;
                continue;
            }
            if (r.Specificity > best.Specificity) best = r;
            else if (r.Specificity == best.Specificity && r.Allow && !best.Allow) best = r;
        }
        return best?.Allow ?? true;
    }

    public static async Task<RobotsRules> FetchAsync(Uri anyPageOnHost, string userAgent, HttpClient http)
    {
        var robotsUri = new Uri($"{anyPageOnHost.Scheme}://{anyPageOnHost.Host}/robots.txt");

        string text;
        try
        {
            using var resp = await http.GetAsync(robotsUri);
            if ((int)resp.StatusCode == 404)
                return new RobotsRules(new List<Rule>());

            resp.EnsureSuccessStatusCode();
            text = await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            return new RobotsRules(new List<Rule>());
        }

        return Parse(text, userAgent);
    }

    public static RobotsRules Parse(string robotsTxt, string userAgent)
    {
        var groups = new Dictionary<string, List<Rule>>(StringComparer.OrdinalIgnoreCase);

        var currentAgents = new List<string>();
        bool groupHasRules = false;

        foreach (var raw in robotsTxt.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim(); 
            if (string.IsNullOrWhiteSpace(line)) continue;

            var idx = line.IndexOf(':');
            if (idx <= 0) continue;

            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();

            if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (groupHasRules)
                {
                    currentAgents.Clear();
                    groupHasRules = false;
                }

                currentAgents.Add(val);

                foreach (var ua in currentAgents)
                    if (!groups.ContainsKey(ua))
                        groups[ua] = new List<Rule>();

                continue;
            }

            if (currentAgents.Count == 0) continue;

            if (key.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
            {
                groupHasRules = true;

                
                if (string.IsNullOrEmpty(val)) continue;

                foreach (var ua in currentAgents)
                    groups[ua].Add(Rule.CreateDisallowRule(val));
            }
            else if (key.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                groupHasRules = true;

                if (string.IsNullOrEmpty(val)) continue;

                foreach (var ua in currentAgents)
                    groups[ua].Add(Rule.CreateAllowRule(val));
            }

        }

        if (!groups.TryGetValue(userAgent, out var rules) &&
            !groups.TryGetValue("*", out rules))
        {
            rules = new List<Rule>();
        }

        return new RobotsRules(rules);
    }

    private sealed class Rule
    {
       
        public bool Allow { get; }
        public int Specificity { get; }

        private readonly Regex _rx;

        private Rule(bool allow, string pattern)
        {
            Allow = allow;
            Specificity = pattern.Length;

            var anchored = pattern.EndsWith("$", StringComparison.Ordinal);
            if (anchored) pattern = pattern[..^1];

            var re = Regex.Escape(pattern).Replace("\\*", ".*");
            re = "^" + re + (anchored ? "$" : "");

            _rx = new Regex(re, RegexOptions.Compiled);
        }

        public bool IsMatch(string pathAndQuery) => _rx.IsMatch(pathAndQuery);

        public static Rule CreateAllowRule(string pattern) => new Rule(true, pattern);
        public static Rule CreateDisallowRule(string pattern) => new Rule(false, pattern);
    }
}
