namespace UpdateHub.Models;

/// <summary>
/// One published version of a product. These accumulate in the local release-history
/// table so we can answer "how many releases have I missed" offline.
/// </summary>
public sealed class ReleaseInfo
{
    public string Version { get; set; } = "";
    public DateTime? ReleaseDate { get; set; }
    public DatePrecision Precision { get; set; } = DatePrecision.Unknown;

    /// <summary>Release notes body, when the source provides one.</summary>
    public string? Notes { get; set; }
    public string? NotesUrl { get; set; }
    public string? DownloadUrl { get; set; }

    /// <summary>Provider that supplied this row (e.g. "github", "winget").</summary>
    public string Source { get; set; } = "";

    /// <summary>Vendor flagged this as a security release, or the notes mention a CVE.</summary>
    public bool IsSecurityRelease { get; set; }

    public bool IsPrerelease { get; set; }

    public override string ToString() =>
        ReleaseDate is null ? Version : $"{Version} ({ReleaseDate:d MMM yyyy})";
}
