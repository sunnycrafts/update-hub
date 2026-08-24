using System.Text.RegularExpressions;
using UpdateHub.Models;

namespace UpdateHub.Core;

/// <summary>
/// Parses the search box into filters, so the inventory can be queried rather than only
/// name-matched:
/// <code>
///   chrome                    plain text, matched against name/publisher/version
///   behind&gt;365                more than 365 days behind
///   publisher:Microsoft       publisher contains
///   category:Development      category equals
///   size&gt;2GB                  install footprint
///   priority:critical         priority at or above
///   security                  has missed security releases
///   outdated / current / unknown / ignored
/// </code>
/// Unknown terms fall through to plain text, so a typo narrows the list instead of
/// throwing an error at someone who was only trying to type a program name.
/// </summary>
public sealed class SearchQuery
{
    private readonly List<Func<InstalledApp, bool>> _predicates = new();
    private readonly List<string> _text = new();

    public bool IsEmpty => _predicates.Count == 0 && _text.Count == 0;

    private static readonly Regex Term = new(
        @"(?<key>[a-zA-Z]+)\s*(?<op>[:><]=?)\s*(?<value>""[^""]*""|\S+)",
        RegexOptions.Compiled);

    public static SearchQuery Parse(string? input)
    {
        var query = new SearchQuery();
        if (string.IsNullOrWhiteSpace(input)) return query;

        var remainder = input;

        foreach (Match m in Term.Matches(input))
        {
            var key = m.Groups["key"].Value.ToLowerInvariant();
            var op = m.Groups["op"].Value;
            var value = m.Groups["value"].Value.Trim('"');

            var predicate = Build(key, op, value);
            if (predicate is null) continue;

            query._predicates.Add(predicate);
            remainder = remainder.Replace(m.Value, " ");
        }

        foreach (var word in remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Trim().ToLowerInvariant();
            if (w.Length == 0) continue;

            // Bare keywords that read naturally as filters.
            switch (w)
            {
                case "security":
                    query._predicates.Add(a => a.SecurityReleasesMissed > 0); continue;
                case "outdated":
                    query._predicates.Add(a => a.UpdateAvailable); continue;
                case "current":
                    query._predicates.Add(a => a.LatestVersion is not null && !a.UpdateAvailable);
                    continue;
                case "unknown":
                    query._predicates.Add(a => a.LatestVersion is null); continue;
                case "ignored":
                    query._predicates.Add(a => a.IsIgnored); continue;
                case "critical":
                    query._predicates.Add(a => a.Priority == UpdatePriority.Critical); continue;
                default:
                    query._text.Add(w); continue;
            }
        }

        return query;
    }

    private static Func<InstalledApp, bool>? Build(string key, string op, string value)
    {
        switch (key)
        {
            case "publisher" or "pub":
                return a => (a.Publisher ?? "").Contains(value, StringComparison.OrdinalIgnoreCase);

            case "category" or "cat" or "type":
                return a => a.CategoryText.StartsWith(value, StringComparison.OrdinalIgnoreCase);

            case "priority" or "pri":
                if (!Enum.TryParse<UpdatePriority>(value, true, out var priority)) return null;
                // ">" reads as "more urgent than", which is a higher enum value.
                return op.StartsWith('>') ? a => a.Priority > priority
                     : op.StartsWith('<') ? a => a.Priority < priority && a.Priority != UpdatePriority.None
                     : a => a.Priority == priority;

            case "behind" or "days":
                if (!double.TryParse(value, out var days)) return null;
                return op.StartsWith('>')
                    ? a => a.Age.IsKnown && a.Age.TotalDays > days
                    : op.StartsWith('<')
                        ? a => a.Age.IsKnown && a.Age.TotalDays < days
                        : a => a.Age.IsKnown && Math.Abs(a.Age.TotalDays - days) < 1;

            case "years" or "yr":
                if (!double.TryParse(value, out var years)) return null;
                return op.StartsWith('<')
                    ? a => a.Age.IsKnown && a.Age.TotalDays < years * 365
                    : a => a.Age.IsKnown && a.Age.TotalDays > years * 365;

            case "size":
                var kb = ParseSize(value);
                if (kb is null) return null;
                return op.StartsWith('<')
                    ? a => a.EstimatedSizeKb > 0 && a.EstimatedSizeKb < kb
                    : a => a.EstimatedSizeKb > kb;

            case "source" or "src":
                return a => a.ConfidenceSources.Any(s =>
                    s.StartsWith(value, StringComparison.OrdinalIgnoreCase));

            case "confidence" or "conf":
                if (!int.TryParse(value.TrimEnd('%'), out var conf)) return null;
                return op.StartsWith('<') ? a => a.Confidence < conf
                     : op.StartsWith('>') ? a => a.Confidence > conf
                     : a => a.Confidence == conf;

            default:
                return null;
        }
    }

    /// <summary>Accepts "2GB", "500MB", "1024" (KB assumed).</summary>
    private static long? ParseSize(string value)
    {
        var m = Regex.Match(value, @"^(?<n>[\d.]+)\s*(?<unit>gb|mb|kb|g|m|k)?$",
            RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups["n"].Value, out var n)) return null;

        return m.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "gb" or "g" => (long)(n * 1024 * 1024),
            "mb" or "m" => (long)(n * 1024),
            _ => (long)n,
        };
    }

    public bool Matches(InstalledApp app)
    {
        foreach (var predicate in _predicates)
            if (!predicate(app)) return false;

        foreach (var word in _text)
            if (!app.SearchBlob.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }
}
