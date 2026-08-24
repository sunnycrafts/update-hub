namespace UpdateHub.Models;

/// <summary>Where an installed app was discovered.</summary>
[Flags]
public enum DetectionSource
{
    None = 0,
    Registry = 1 << 0,
    Winget = 1 << 1,
    MsStore = 1 << 2,
    Portable = 1 << 3,
    CustomFolder = 1 << 4,
    Chocolatey = 1 << 5,
    Scoop = 1 << 6,
}

/// <summary>How trustworthy a release date is. Drives the "approx" markers in the UI.</summary>
public enum DatePrecision
{
    /// <summary>No date could be determined.</summary>
    Unknown = 0,
    /// <summary>Derived from a file timestamp or install date — could be off by a lot.</summary>
    Estimated = 1,
    /// <summary>From winget-pkgs manifest history — typically within 1-2 weeks of the real release.</summary>
    Approximate = 2,
    /// <summary>Published date straight from the vendor (GitHub release, vendor API).</summary>
    Exact = 3,
}

/// <summary>Severity bucket shown as the coloured status pill.</summary>
public enum AppStatus
{
    /// <summary>No latest version known — we can't say anything.</summary>
    Unknown = 0,
    Current = 1,
    Minor = 2,
    Outdated = 3,
    VeryOutdated = 4,
    Major = 5,
    Ignored = 6,
}

/// <summary>Coarse age band used for the visual age bar.</summary>
public enum AgeBand
{
    Unknown = 0,
    /// <summary>0-30 days.</summary>
    Current = 1,
    /// <summary>1-3 months.</summary>
    Recent = 2,
    /// <summary>3-12 months.</summary>
    GettingOld = 3,
    /// <summary>1-2 years.</summary>
    Old = 4,
    /// <summary>2+ years.</summary>
    VeryOutdated = 5,
}

/// <summary>How an update can be applied.</summary>
public enum UpdateMethod
{
    None = 0,
    Winget = 1,
    Chocolatey = 2,
    Scoop = 3,
    /// <summary>We know the download URL but must hand off to the user / run an installer.</summary>
    Installer = 4,
    /// <summary>Store apps update through the Store.</summary>
    MsStore = 5,
    /// <summary>We can only point at a download page.</summary>
    Manual = 6,
}

public enum UpdateOutcome
{
    Pending = 0,
    Running = 1,
    Success = 2,
    Failed = 3,
    Cancelled = 4,
    NoUpdateNeeded = 5,
    RequiresElevation = 6,
}

/// <summary>
/// How urgently an update should be applied. This is the answer to "what needs my
/// attention today", and it drives the default ordering of every list.
/// </summary>
public enum UpdatePriority
{
    /// <summary>No update, or nothing known.</summary>
    None = 0,
    Optional = 1,
    Recommended = 2,
    High = 3,
    Critical = 4,
    /// <summary>Excluded by the user; never surfaces as work to do.</summary>
    Ignored = 5,
}

/// <summary>Overall software-health band, mirroring the Windows Security wording.</summary>
public enum HealthBand
{
    Excellent = 0,
    Good = 1,
    Fair = 2,
    NeedsAttention = 3,
}

/// <summary>What kind of software this is. Used for colour and grouping only.</summary>
public enum AppCategory
{
    Other = 0,
    Development,
    Creative,
    Game,
    Media,
    Productivity,
    Communication,
    Browser,
    Security,
    Utility,
    Runtime,
    Driver,
    System,
}

/// <summary>Rough install footprint, so heavyweight software is visible at a glance.</summary>
public enum SizeTier
{
    Unknown = 0,
    /// <summary>Under 50 MB.</summary>
    Small,
    /// <summary>50 MB to 250 MB.</summary>
    Medium,
    /// <summary>250 MB to 2 GB.</summary>
    Large,
    /// <summary>Over 2 GB.</summary>
    VeryLarge,
}

public enum ExportFormat
{
    Csv,
    Json,
    Excel,
    Pdf,
}

public enum ScheduleFrequency
{
    Never = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
}
