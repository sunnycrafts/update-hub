using System.Text;
using System.Text.RegularExpressions;

namespace UpdateHub.Core;

/// <summary>
/// A version string parsed into comparable pieces. Windows version strings are wildly
/// inconsistent — "3.0.18", "139.0.7258.128", "25.00", "ad 9.7.9", "2025.3",
/// "1.0.0-beta.2", "26.1.5.0370" are all real examples from a single machine — so this
/// is deliberately forgiving about what it accepts.
/// </summary>
public sealed class ParsedVersion
{
    public string Original { get; init; } = "";
    public int[] Numbers { get; init; } = Array.Empty<int>();
    public string? PreRelease { get; init; }
    public bool IsValid => Numbers.Length > 0;

    public int Major => Numbers.Length > 0 ? Numbers[0] : 0;
    public int Minor => Numbers.Length > 1 ? Numbers[1] : 0;
    public int Patch => Numbers.Length > 2 ? Numbers[2] : 0;

    private static readonly Regex LeadingJunk = new(@"^[^\d]*", RegexOptions.Compiled);
    private static readonly Regex NumericRun = new(@"\d+", RegexOptions.Compiled);

    public static ParsedVersion Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ParsedVersion { Original = raw ?? "" };

        var original = raw.Trim();
        var work = original;

        // Drop vendor prefixes: "v1.2.3", "ad 9.7.9", "Version 4.1".
        work = LeadingJunk.Replace(work, "");
        if (work.Length == 0)
            return new ParsedVersion { Original = original };

        // Split the numeric core from any pre-release / build tail.
        string? pre = null;
        int tailAt = work.IndexOfAny(new[] { '-', '+' });
        if (tailAt >= 0)
        {
            pre = work[(tailAt + 1)..].Trim();
            work = work[..tailAt];
        }

        // Words like "build", "beta", "rc" after a space also mark the tail.
        int spaceAt = work.IndexOf(' ');
        if (spaceAt >= 0)
        {
            var rest = work[(spaceAt + 1)..].Trim();
            if (rest.Length > 0 && !char.IsDigit(rest[0]))
                pre ??= rest;
            work = work[..spaceAt];
        }

        var numbers = new List<int>(4);
        foreach (Match m in NumericRun.Matches(work))
        {
            // Absurdly long runs are hashes or dates crammed in; clamp rather than overflow.
            if (!int.TryParse(m.Value.Length > 9 ? m.Value[..9] : m.Value, out var n)) continue;
            numbers.Add(n);
            if (numbers.Count >= 6) break;
        }

        if (string.IsNullOrWhiteSpace(pre)) pre = null;
        // "beta"/"rc"/"alpha" mean lower-than-release; anything else (a build hash,
        // an architecture tag) is not an ordering signal, so drop it.
        if (pre is not null && !IsPreReleaseTag(pre)) pre = null;

        return new ParsedVersion
        {
            Original = original,
            Numbers = numbers.ToArray(),
            PreRelease = pre,
        };
    }

    private static bool IsPreReleaseTag(string tag)
    {
        var t = tag.ToLowerInvariant();
        return t.StartsWith("alpha") || t.StartsWith("beta") || t.StartsWith("rc")
            || t.StartsWith("pre") || t.StartsWith("dev") || t.StartsWith("nightly")
            || t.StartsWith("canary") || t.StartsWith("insider") || t.StartsWith("snapshot");
    }

    /// <summary>Rank used to order pre-release tags below releases.</summary>
    private int PreReleaseRank()
    {
        if (PreRelease is null) return 100;          // a real release outranks every tag
        var t = PreRelease.ToLowerInvariant();
        if (t.StartsWith("rc")) return 40;
        if (t.StartsWith("beta")) return 30;
        if (t.StartsWith("alpha")) return 20;
        return 10;                                    // dev / nightly / canary
    }

    public int CompareTo(ParsedVersion other)
    {
        int len = Math.Max(Numbers.Length, other.Numbers.Length);
        for (int i = 0; i < len; i++)
        {
            // A missing trailing segment reads as 0 so "3.0" == "3.0.0".
            int a = i < Numbers.Length ? Numbers[i] : 0;
            int b = i < other.Numbers.Length ? other.Numbers[i] : 0;
            if (a != b) return a.CompareTo(b);
        }

        int pa = PreReleaseRank(), pb = other.PreReleaseRank();
        if (pa != pb) return pa.CompareTo(pb);

        return 0;
    }

    public override string ToString() => Original;
}

public static class VersionComparer
{
    /// <summary>
    /// Compares two version strings. Returns null when the comparison would be meaningless —
    /// either side unparseable, or the two use obviously different numbering schemes.
    /// Callers treat null as "can't tell" rather than "equal", which keeps us from
    /// claiming an update exists when we're really just confused.
    /// </summary>
    public static int? Compare(string? installed, string? latest)
    {
        var a = ParsedVersion.Parse(installed);
        var b = ParsedVersion.Parse(latest);
        if (!a.IsValid || !b.IsValid) return null;
        if (!SchemesLookComparable(a, b)) return null;
        return a.CompareTo(b);
    }

    /// <summary>
    /// Guards against comparing a calendar version against a semantic one — e.g. an app
    /// reporting "2022.1" in the registry while the vendor publishes "24.1". Without this
    /// the app would confidently report a 2000-major-version gap.
    /// </summary>
    private static bool SchemesLookComparable(ParsedVersion a, ParsedVersion b)
    {
        bool aYear = LooksLikeYear(a.Major);
        bool bYear = LooksLikeYear(b.Major);
        if (aYear != bYear) return false;

        // A leap of more than 100 majors between two non-calendar schemes is a mismatch,
        // not an update (e.g. build number vs product version).
        if (!aYear && Math.Abs(a.Major - b.Major) > 100) return false;

        return true;
    }

    private static bool LooksLikeYear(int n) => n >= 1990 && n <= 2100;

    /// <summary>
    /// Whether two version strings plausibly describe the same installed build, allowing
    /// one to carry more precision than the other.
    ///
    /// The registry and winget disagree about how many segments a version has: the
    /// registry records "8.0.29.36224" where winget reports "8.0.29". An exact comparison
    /// treats those as different, so a winget row failed to match its registry row and was
    /// added as a second, sizeless copy of the same product.
    /// </summary>
    public static bool IsSameProductVersion(string? a, string? b)
    {
        var pa = ParsedVersion.Parse(a);
        var pb = ParsedVersion.Parse(b);
        if (!pa.IsValid || !pb.IsValid) return false;

        // Every segment they both have must agree; extra trailing segments on either side
        // are revision detail and are ignored.
        int shared = Math.Min(pa.Numbers.Length, pb.Numbers.Length);
        if (shared == 0) return false;
        for (int i = 0; i < shared; i++)
            if (pa.Numbers[i] != pb.Numbers[i]) return false;

        // Guard against "8" matching "8.0.29": require at least two shared segments
        // unless both are genuinely single-segment versions.
        return shared >= 2 || (pa.Numbers.Length == 1 && pb.Numbers.Length == 1);
    }

    public static bool IsNewer(string? candidate, string? baseline) =>
        Compare(baseline, candidate) is { } c && c < 0;

    /// <summary>Major versions between the two, floored at 0.</summary>
    public static int MajorsBehind(string? installed, string? latest)
    {
        var a = ParsedVersion.Parse(installed);
        var b = ParsedVersion.Parse(latest);
        if (!a.IsValid || !b.IsValid || !SchemesLookComparable(a, b)) return 0;
        return Math.Max(0, b.Major - a.Major);
    }

    /// <summary>
    /// Minor versions behind. Only meaningful inside the same major — across a major
    /// boundary the minor number resets, so we report 0 and let MajorsBehind carry it.
    /// </summary>
    public static int MinorsBehind(string? installed, string? latest)
    {
        var a = ParsedVersion.Parse(installed);
        var b = ParsedVersion.Parse(latest);
        if (!a.IsValid || !b.IsValid || !SchemesLookComparable(a, b)) return 0;
        if (a.Major != b.Major) return 0;
        return Math.Max(0, b.Minor - a.Minor);
    }

    /// <summary>Sort key that orders version strings correctly in a data grid.</summary>
    public static string SortKey(string? version)
    {
        var p = ParsedVersion.Parse(version);
        if (!p.IsValid) return "zzzz" + (version ?? "");
        var sb = new StringBuilder(40);
        for (int i = 0; i < 5; i++)
            sb.Append((i < p.Numbers.Length ? p.Numbers[i] : 0).ToString("D7")).Append('.');
        return sb.ToString();
    }
}
