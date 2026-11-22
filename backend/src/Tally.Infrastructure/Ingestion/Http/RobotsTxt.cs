using System.Text;
using System.Text.RegularExpressions;

namespace Tally.Infrastructure.Ingestion.Http;

/// <summary>
/// Minimal RFC 9309 robots.txt evaluation: group selection by product token (falling back
/// to <c>*</c>), Allow/Disallow with <c>*</c> and <c>$</c>, longest match wins, Allow wins ties.
/// </summary>
internal sealed class RobotsTxt
{
    private readonly IReadOnlyList<Rule> _rules;

    private RobotsTxt(IReadOnlyList<Rule> rules) => _rules = rules;

    public static RobotsTxt AllowAll { get; } = new([]);

    public static RobotsTxt DisallowAll { get; } = new([new Rule(false, "/", new Regex("^/"))]);

    public bool IsAllowed(string pathAndQuery)
    {
        if (string.IsNullOrEmpty(pathAndQuery))
        {
            pathAndQuery = "/";
        }

        Rule? best = null;
        foreach (var rule in _rules)
        {
            if (!rule.Matcher.IsMatch(pathAndQuery))
            {
                continue;
            }

            if (best is null
                || rule.Pattern.Length > best.Pattern.Length
                || (rule.Pattern.Length == best.Pattern.Length && rule.Allow && !best.Allow))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }

    public static RobotsTxt Parse(string content, string productToken)
    {
        var specific = new List<Rule>();
        var wildcard = new List<Rule>();
        var currentAgents = new List<string>();
        var lastWasAgent = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (key == "user-agent")
            {
                if (!lastWasAgent)
                {
                    currentAgents.Clear();
                }
                currentAgents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }

            lastWasAgent = false;
            if (key is not ("allow" or "disallow"))
            {
                continue;
            }

            // Empty Disallow means "nothing disallowed": no rule.
            if (value.Length == 0)
            {
                continue;
            }

            var rule = new Rule(key == "allow", value, ToRegex(value));
            if (currentAgents.Contains(productToken.ToLowerInvariant()))
            {
                specific.Add(rule);
            }
            else if (currentAgents.Contains("*"))
            {
                wildcard.Add(rule);
            }
        }

        return new RobotsTxt(specific.Count > 0 ? specific : wildcard);
    }

    private static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        var anchored = pattern.EndsWith('$');
        var body = anchored ? pattern[..^1] : pattern;
        foreach (var ch in body)
        {
            sb.Append(ch == '*' ? ".*" : Regex.Escape(ch.ToString()));
        }
        if (anchored)
        {
            sb.Append('$');
        }
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }

    private sealed record Rule(bool Allow, string Pattern, Regex Matcher);
}
