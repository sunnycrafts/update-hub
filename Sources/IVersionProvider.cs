using UpdateHub.Models;

namespace UpdateHub.Sources;

/// <summary>What a provider managed to learn about a product.</summary>
public sealed class ProviderResult
{
    public string Source { get; init; } = "";
    public string? LatestVersion { get; set; }
    public DateTime? LatestReleaseDate { get; set; }
    public DatePrecision Precision { get; set; } = DatePrecision.Unknown;
    public string? Notes { get; set; }
    public string? NotesUrl { get; set; }
    public string? DownloadUrl { get; set; }
    public string? DownloadPageUrl { get; set; }
    public string? Homepage { get; set; }
    public string? Publisher { get; set; }

    /// <summary>Every release this provider could enumerate, newest first where known.</summary>
    public List<ReleaseInfo> History { get; set; } = new();

    /// <summary>False when the provider had nothing useful to say.</summary>
    public bool HasData => LatestVersion is not null || History.Count > 0;

    /// <summary>Set when the lookup was answered from the local cache.</summary>
    public bool FromCache { get; set; }

    public static ProviderResult Empty(string source) => new() { Source = source };
}

public interface IVersionProvider
{
    string Name { get; }

    /// <summary>Higher wins when two providers disagree about the latest version.</summary>
    int Priority { get; }

    /// <summary>Cheap check so we skip providers that cannot speak for this app.</summary>
    bool CanHandle(InstalledApp app);

    Task<ProviderResult> QueryAsync(InstalledApp app, bool forceRefresh, CancellationToken ct);
}
