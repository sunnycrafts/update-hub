namespace UpdateHub.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Light";

    /// <summary>Hide the driver/redistributable noise that clutters Add-Remove Programs.</summary>
    public bool HideSystemComponents { get; set; } = true;

    public bool IncludeMsStore { get; set; } = true;
    public bool IncludePortable { get; set; } = true;
    public bool IncludeChocolatey { get; set; } = true;
    public bool IncludeScoop { get; set; } = true;

    /// <summary>Extra folders scanned for portable executables.</summary>
    public List<string> CustomFolders { get; set; } = new();

    /// <summary>Raises the GitHub API budget from 60/hr to 5000/hr. Optional.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GitHubToken { get; set; }

    /// <summary>The token as stored on disk: encrypted with Windows DPAPI for this user only.</summary>
    public string? GitHubTokenProtected { get; set; }

    /// <summary>
    /// Read-only migration hook. Older versions wrote the token here in plain text under
    /// the name "GitHubToken"; it is read once, re-saved encrypted, and never written again.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("GitHubToken")]
    public string? LegacyPlainGitHubToken { get; set; }

    /// <summary>How long a "latest version" lookup stays fresh before we re-query.</summary>
    public int CacheHours { get; set; } = 12;

    public bool CheckOnStartup { get; set; } = true;
    public bool NotifyOnUpdates { get; set; } = true;

    public ScheduleFrequency Schedule { get; set; } = ScheduleFrequency.Never;
    public int ScheduleHour { get; set; } = 9;
    public bool ScheduleAutoUpdate { get; set; }

    public DateTime? LastScan { get; set; }
    public DateTime? LastUpdate { get; set; }

    /// <summary>Max provider lookups running at once. Keeps us polite to the APIs.</summary>
    public int MaxConcurrency { get; set; } = 6;

    /// <summary>
    /// How many updates install at once. Downloads overlap, while Windows Installer
    /// serialises the install phase itself and busy attempts are retried.
    /// </summary>
    public int UpdateConcurrency { get; set; } = 3;

    /// <summary>
    /// When true, winget updates run through a single elevated, hidden PowerShell session
    /// (one UAC prompt for the whole batch) instead of trying unelevated first and
    /// re-prompting per app that turns out to need administrator rights. Trades one
    /// up-front admin prompt, every time, for never being interrupted mid-batch.
    /// </summary>
    public bool UseElevatedBackgroundUpdates { get; set; } = true;

    public string? LastExportFolder { get; set; }
}
