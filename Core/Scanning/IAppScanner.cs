using UpdateHub.Models;

namespace UpdateHub.Core.Scanning;

public interface IAppScanner
{
    string Name { get; }
    DetectionSource Source { get; }
    Task<List<InstalledApp>> ScanAsync(CancellationToken ct);
}
