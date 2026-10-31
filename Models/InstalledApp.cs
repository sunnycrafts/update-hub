using System.Text.Json.Serialization;

namespace UpdateHub.Models;

/// <summary>
/// One installed product, merged from every scanner that saw it and enriched by
/// whichever version providers could speak for it.
/// </summary>
public sealed class InstalledApp
{
    // ---- Identity -------------------------------------------------------

    /// <summary>Stable key used for dedupe, cache lookups and ignore rules.</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";
    public string? Publisher { get; set; }

    /// <summary>Lowercased, punctuation- and version-stripped name used for matching.</summary>
    public string NormalizedName { get; set; } = "";

    // ---- Where it came from --------------------------------------------

    public DetectionSource Sources { get; set; }
    public string? RegistryKey { get; set; }
    public string? WingetId { get; set; }
    public string? ChocoId { get; set; }
    public string? ScoopId { get; set; }
    public string? MsStoreFamilyName { get; set; }
    public string? GitHubRepo { get; set; }
    public string? InstallLocation { get; set; }
    public string? UninstallString { get; set; }
    public string? ExePath { get; set; }
    public string? Homepage { get; set; }
    public bool IsSystemComponent { get; set; }
    public long EstimatedSizeKb { get; set; }

    /// <summary>Heuristic classification, for colour-coding and grouping.</summary>
    public AppCategory Category { get; set; }
    public SizeTier SizeTier { get; set; }

    [JsonIgnore]
    public string CategoryText => Core.AppClassifier.Describe(Category);

    /// <summary>
    /// Segoe Fluent Icons glyph for the category. Monochrome on purpose: categories are
    /// context, and saturated colour is reserved for status and priority so the eye is
    /// drawn to what actually needs action.
    /// </summary>
    [JsonIgnore]
    public string CategoryGlyph => Category switch
    {
        AppCategory.Development => "",     // Code
        AppCategory.Creative => "",        // Color palette
        AppCategory.Game => "",            // Game
        AppCategory.Media => "",           // MusicNote
        AppCategory.Productivity => "",    // Document
        AppCategory.Communication => "",   // Message
        AppCategory.Browser => "",         // Globe
        AppCategory.Security => "",        // Lock
        AppCategory.Utility => "",         // Repair
        AppCategory.Runtime => "",         // Package
        AppCategory.Driver => "",          // Component
        AppCategory.System => "",          // System
        _ => "",                           // Page
    };

    /// <summary>How urgently this should be updated. Drives sorting everywhere.</summary>
    [JsonIgnore]
    public UpdatePriority Priority => Core.PriorityCalculator.For(this);

    [JsonIgnore]
    public string PriorityText => Core.PriorityCalculator.Describe(Priority);

    [JsonIgnore]
    public string PriorityReason => Core.PriorityCalculator.Explain(this);

    [JsonIgnore]
    public string SizeText => EstimatedSizeKb <= 0
        ? "—"
        : EstimatedSizeKb >= 1024 * 1024
            ? $"{EstimatedSizeKb / 1024.0 / 1024.0:F1} GB"
            : EstimatedSizeKb >= 1024
                ? $"{EstimatedSizeKb / 1024.0:F0} MB"
                : $"{EstimatedSizeKb} KB";

    // ---- Installed side -------------------------------------------------

    public string InstalledVersion { get; set; } = "";

    /// <summary>When the publisher shipped the version that is installed.</summary>
    public DateTime? InstalledReleaseDate { get; set; }
    public DatePrecision InstalledReleasePrecision { get; set; } = DatePrecision.Unknown;

    /// <summary>When this machine installed it — deliberately distinct from the release date.</summary>
    public DateTime? InstallDate { get; set; }

    // ---- Latest side ----------------------------------------------------

    public string? LatestVersion { get; set; }
    public DateTime? LatestReleaseDate { get; set; }
    public DatePrecision LatestReleasePrecision { get; set; } = DatePrecision.Unknown;
    public string? LatestNotes { get; set; }
    public string? LatestNotesUrl { get; set; }
    public string? DownloadUrl { get; set; }
    public string? DownloadPageUrl { get; set; }

    // ---- Verdict --------------------------------------------------------

    public bool UpdateAvailable { get; set; }
    public UpdateMethod UpdateMethod { get; set; } = UpdateMethod.None;
    public int MajorVersionsBehind { get; set; }
    public int MinorVersionsBehind { get; set; }

    /// <summary>Releases published between the installed version and latest, when history is known.</summary>
    public int ReleasesMissed { get; set; }
    public int SecurityReleasesMissed { get; set; }

    /// <summary>0-100. How much to trust the comparison above.</summary>
    public int Confidence { get; set; }
    public List<string> ConfidenceSources { get; set; } = new();
    public string? ConfidenceNote { get; set; }

    public DateTime? LastChecked { get; set; }

    // ---- User state -----------------------------------------------------

    public bool IsIgnored { get; set; }
    public string? IgnoreReason { get; set; }

    /// <summary>A specific version the user chose to skip; suppresses the update flag for it alone.</summary>
    public string? SkippedVersion { get; set; }

    // ---- Derived --------------------------------------------------------

    /// <summary>
    /// Set when the version numbers and the release dates disagree about which build is
    /// newer. That happens when the two dates come from different sources — a vendor API
    /// for the latest build and manifest history for the installed one — and it means the
    /// dates cannot be subtracted meaningfully, so the age is withheld rather than guessed.
    /// </summary>
    public bool DatesInconsistent { get; set; }

    [JsonIgnore]
    public AgeBehind Age
    {
        get
        {
            if (DatesInconsistent) return AgeBehind.Unknown;
            // The version comparison is the authority on direction; dates only quantify
            // a gap that comparison has already established.
            if (LatestVersion is not null && !UpdateAvailable) return AgeBehind.UpToDate;
            return AgeBehind.Between(InstalledReleaseDate, LatestReleaseDate);
        }
    }

    /// <summary>Whether a Start Menu shortcut points at this program.</summary>
    [JsonIgnore]
    public string StartMenuText =>
        !Sources.HasFlag(DetectionSource.StartMenu) ? "Not in the Start Menu"
        : Sources == DetectionSource.StartMenu ? "Start Menu only (no install record)"
        : "In the Start Menu";

    /// <summary>
    /// Plain-English explanation for apps whose latest version could not be found, so a
    /// blank "Latest" column reads as a known limitation rather than a bug. Null when a
    /// latest version is known.
    /// </summary>
    [JsonIgnore]
    public string? NoSourceReason
    {
        get
        {
            if (!string.IsNullOrEmpty(LatestVersion)) return null;
            if (IsIgnored) return null;
            if (Sources.HasFlag(DetectionSource.MsStore))
                return "This is a Microsoft Store app and winget's Store source did not list a newer version. " +
                       "The Store updates it itself.";
            if (Sources == DetectionSource.Portable || Sources == DetectionSource.CustomFolder)
                return "This is a portable app with no install record, so UpdateHub cannot tell which " +
                       "project or package it belongs to.";
            return "UpdateHub found no winget package, GitHub project or vendor rule for this program, " +
                   "so there is nothing to compare against. You can add a rule in Settings > Plugins and catalog.";
        }
    }

    /// <summary>
    /// Where to look by hand: the publisher's own page if known, otherwise a web search
    /// for the program's download page. Always an https URL.
    /// </summary>
    [JsonIgnore]
    public string ManualCheckUrl
    {
        get
        {
            foreach (var u in new[] { DownloadPageUrl, Homepage })
                if (!string.IsNullOrWhiteSpace(u) && u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return u;
            return "https://duckduckgo.com/?q=" + Uri.EscapeDataString($"{Name} {Publisher} download latest version");
        }
    }

    [JsonIgnore]
    public bool CanWingetUpdate =>
        !string.IsNullOrEmpty(WingetId) && UpdateMethod == UpdateMethod.Winget;

    [JsonIgnore]
    public AppStatus Status
    {
        get
        {
            if (IsIgnored) return AppStatus.Ignored;
            if (string.IsNullOrEmpty(LatestVersion)) return AppStatus.Unknown;
            if (!UpdateAvailable) return AppStatus.Current;
            if (MajorVersionsBehind > 0) return AppStatus.Major;

            return Age.Band switch
            {
                AgeBand.VeryOutdated => AppStatus.VeryOutdated,
                AgeBand.Old => AppStatus.VeryOutdated,
                AgeBand.GettingOld => AppStatus.Outdated,
                AgeBand.Unknown => AppStatus.Minor,
                _ => AppStatus.Minor,
            };
        }
    }

    [JsonIgnore]
    public string StatusText => Status switch
    {
        AppStatus.Current => "Current",
        AppStatus.Minor => "Minor",
        AppStatus.Outdated => "Outdated",
        AppStatus.VeryOutdated => "Very outdated",
        AppStatus.Major => "Major",
        AppStatus.Ignored => "Ignored",
        _ => "Unknown",
    };

    /// <summary>Formats a release date, flagging it when the date is only approximate.</summary>
    public static string FormatDate(DateTime? date, DatePrecision precision)
    {
        if (date is null) return "Unknown";
        var text = date.Value.ToString("d MMM yyyy");
        return precision switch
        {
            DatePrecision.Exact => text,
            DatePrecision.Approximate => $"~{text}",
            DatePrecision.Estimated => $"~{text} (est.)",
            _ => text,
        };
    }

    [JsonIgnore]
    public string InstalledReleaseText => FormatDate(InstalledReleaseDate, InstalledReleasePrecision);

    [JsonIgnore]
    public string LatestReleaseText => FormatDate(LatestReleaseDate, LatestReleasePrecision);

    [JsonIgnore]
    public string SearchBlob =>
        $"{Name} {Publisher} {WingetId} {InstalledVersion} {LatestVersion}".ToLowerInvariant();

    public override string ToString() => $"{Name} {InstalledVersion}";
}
