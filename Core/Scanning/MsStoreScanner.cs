using System.Text.Json;
using UpdateHub.Models;

namespace UpdateHub.Core.Scanning;

/// <summary>
/// Enumerates MSIX/Store packages via Get-AppxPackage. winget lists these too, but only
/// as opaque family names — this fills in real display names, publishers and install dates.
/// </summary>
public sealed class MsStoreScanner : IAppScanner
{
    public string Name => "Microsoft Store";
    public DetectionSource Source => DetectionSource.MsStore;

    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        Get-AppxPackage |
          Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage } |
          ForEach-Object {
            $name = $_.Name
            try {
              $m = Get-AppxPackageManifest $_ -ErrorAction SilentlyContinue
              $d = $m.Package.Properties.DisplayName
              if ($d -and -not $d.StartsWith('ms-resource')) { $name = $d }
            } catch {}
            [PSCustomObject]@{
              Name        = $name
              Id          = $_.Name
              Family      = $_.PackageFamilyName
              Version     = $_.Version
              Publisher   = $_.Publisher
              Location    = $_.InstallLocation
              SignedBy    = $_.SignatureKind
            }
          } | ConvertTo-Json -Compress -Depth 3
        """;

    public async Task<List<InstalledApp>> ScanAsync(CancellationToken ct)
    {
        var results = new List<InstalledApp>();
        var r = await ProcessRunner.PowerShellAsync(Script, ct, 120_000).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(r.StdOut)) return results;

        JsonElement root;
        try { root = JsonDocument.Parse(r.StdOut.Trim()).RootElement; }
        catch (JsonException) { return results; }

        // ConvertTo-Json emits a bare object rather than an array when there is one result.
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : new List<JsonElement> { root };

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var name = Get(item, "Name");
            var family = Get(item, "Family");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(family)) continue;

            var location = Get(item, "Location");
            results.Add(new InstalledApp
            {
                Key = "msix:" + family,
                Name = NameNormalizer.Clean(name),
                NormalizedName = NameNormalizer.Normalize(name),
                Publisher = CleanPublisher(Get(item, "Publisher")),
                InstalledVersion = Get(item, "Version") ?? "",
                MsStoreFamilyName = family,
                InstallLocation = location,
                Sources = DetectionSource.MsStore,
                // Store packages are serviced by the Store itself; offering a winget
                // upgrade here would just fail.
                UpdateMethod = UpdateMethod.MsStore,
                InstallDate = FolderDate(location),
                IsSystemComponent = IsMicrosoftSystemApp(Get(item, "Id"), Get(item, "SignedBy")),
            });
        }

        return results;
    }

    private static string? Get(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Publisher comes back as an X.500 string: "CN=Microsoft Corporation, O=..., C=US".</summary>
    private static string? CleanPublisher(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var part in raw.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return p[3..].Trim();
        }
        return raw;
    }

    /// <summary>System-signed Microsoft packages are OS components, not user software.</summary>
    private static bool IsMicrosoftSystemApp(string? id, string? signedBy) =>
        string.Equals(signedBy, "System", StringComparison.OrdinalIgnoreCase)
        || (id?.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) ?? false)
        || (id?.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase) ?? false);

    private static DateTime? FolderDate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var di = new System.IO.DirectoryInfo(path);
            return di.Exists ? di.CreationTime : null;
        }
        catch { return null; }
    }
}
