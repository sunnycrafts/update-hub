using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UpdateHub.Core;

/// <summary>
/// Reduces display names to a matching key. Add/Remove Programs entries carry versions,
/// bitness tags, language suffixes and trademark symbols that all have to come off before
/// "Audacity 3.7.7" and "Audacity" can be recognised as the same product.
/// </summary>
public static class NameNormalizer
{
    private static readonly Regex Bitness = new(
        @"\s*[\(\[]?\s*(x64|x86|64[\s\-]?bit|32[\s\-]?bit|amd64|arm64|win64|win32)\s*[\)\]]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingVersion = new(
        @"\s+v?\d+(\.\d+)*([\s\-]*(beta|alpha|rc|build)\s*\d*)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LanguageSuffix = new(
        @"\s*[-–]\s*(english|deutsch|german|français|french|español|spanish|italiano|
            italian|português|portuguese|русский|russian|日本語|japanese|简体中文|中文|
            chinese|한국어|korean)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex Parenthetical = new(
        @"\s*\((?:remove\s+only|user|machine|per[- ]user|current\s+user|all\s+users)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EditionYear = new(
        @"\s(19[9]\d|20\d\d)\s*$", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Human-facing cleanup: keeps spacing and casing, strips only the noise. Bitness is
    /// deliberately preserved — "Apple Application Support (32-bit)" and its 64-bit twin
    /// are separate installs, and dropping the tag renders two rows indistinguishable.
    /// Matching ignores it; display must not.
    /// </summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim();

        s = s.Replace("™", "").Replace("®", "").Replace("©", "");
        s = Parenthetical.Replace(s, "");
        s = LanguageSuffix.Replace(s, "");

        // Set the bitness tag aside before stripping the trailing version, then put it
        // back. "7-Zip 25.00 (x64)" hides the version from an end-anchored pattern, so
        // leaving the tag in place would preserve "25.00" as part of the name.
        var bitness = Bitness.Match(s);
        var tag = bitness.Success ? bitness.Value.Trim().Trim('(', ')', '[', ']').Trim() : "";
        if (bitness.Success) s = Bitness.Replace(s, " ");

        // A bare trailing year is an edition, not a version: "AutoCAD 2022" and
        // "AutoCAD 2024" are different products and must not collapse together.
        if (!EditionYear.IsMatch(s)) s = TrailingVersion.Replace(s, "");
        s = Whitespace.Replace(s, " ").Trim(' ', '-', '–', ',', '.');

        if (s.Length == 0) return name.Trim();
        return tag.Length > 0 ? $"{s} ({tag})" : s;
    }

    /// <summary>
    /// Aggressive matching key: lowercase, alphanumeric only, no spaces, bitness removed
    /// so an x86 and x64 build of the same product resolve to the same catalog entry.
    /// </summary>
    public static string Normalize(string? name)
    {
        var cleaned = Bitness.Replace(Clean(name), " ").ToLowerInvariant();
        if (cleaned.Length == 0) return "";

        // Fold accents so "café" and "cafe" match.
        var decomposed = cleaned.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Drops a leading publisher token so "Autodesk AutoCAD" can match a catalog entry
    /// filed under "AutoCAD". Only used as a fallback after an exact match fails.
    /// </summary>
    public static string StripPublisher(string normalizedName, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return normalizedName;
        var p = Normalize(publisher);
        if (p.Length < 3 || p.Length >= normalizedName.Length) return normalizedName;
        return normalizedName.StartsWith(p, StringComparison.Ordinal)
            ? normalizedName[p.Length..]
            : normalizedName;
    }

    /// <summary>
    /// 0..1 similarity used to rank fuzzy catalog candidates. Token-overlap based, which
    /// handles reordering ("Studio Android" / "Android Studio") better than edit distance.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            return 0.85 * Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length) + 0.15;

        // Dice coefficient over character bigrams.
        var setA = Bigrams(a);
        var setB = Bigrams(b);
        if (setA.Count == 0 || setB.Count == 0) return 0;
        int shared = setA.Count(x => setB.Contains(x));
        return 2.0 * shared / (setA.Count + setB.Count);
    }

    private static List<string> Bigrams(string s)
    {
        var list = new List<string>(Math.Max(0, s.Length - 1));
        for (int i = 0; i < s.Length - 1; i++) list.Add(s.Substring(i, 2));
        return list;
    }
}
