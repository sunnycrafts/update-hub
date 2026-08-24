using System.Globalization;
using UpdateHub.Models;

namespace UpdateHub.Core.Scanning;

/// <summary>
/// Parses `winget list`. winget already unifies registry, MSIX and its own catalogue, and
/// the Id column tells us which of those a row came from, so this scanner supplies the
/// winget package IDs and the "Available" upgrade column that the registry cannot.
/// </summary>
public sealed class WingetScanner : IAppScanner
{
    public string Name => "Winget";
    public DetectionSource Source => DetectionSource.Winget;

    public bool WingetAvailable { get; private set; }

    public async Task<List<InstalledApp>> ScanAsync(CancellationToken ct)
    {
        var results = new List<InstalledApp>(256);

        var probe = await ProcessRunner.WingetAsync("--version", ct, 15_000).ConfigureAwait(false);
        WingetAvailable = probe.Ok;
        if (!WingetAvailable) return results;

        var r = await ProcessRunner.WingetAsync(
            "list --disable-interactivity --accept-source-agreements", ct, 180_000).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(r.StdOut)) return results;

        foreach (var row in ParseTable(r.StdOut))
        {
            ct.ThrowIfCancellationRequested();
            if (row.Count < 3) continue;

            var name = row.Get(0);
            var id = row.Get(1);
            var version = row.Get(2);
            var available = row.Count > 3 ? row.Get(3) : "";
            var source = row.Count > 4 ? row.Get(4) : "";

            if (string.IsNullOrWhiteSpace(name)) continue;

            var app = new InstalledApp
            {
                Name = NameNormalizer.Clean(name),
                NormalizedName = NameNormalizer.Normalize(name),
                InstalledVersion = CleanVersion(version),
                Sources = DetectionSource.Winget,
            };

            // The Id column encodes provenance: ARP\... is a registry-only entry winget
            // is merely echoing, MSIX\... is a Store/MSIX package, anything else is a
            // real winget package identifier we can upgrade with.
            if (id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase))
            {
                app.Key = "winget-arp:" + id;
            }
            else if (id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase))
            {
                app.Key = "winget-msix:" + id;
                app.Sources |= DetectionSource.MsStore;
                app.MsStoreFamilyName = ToFamilyName(id[5..]);
            }
            else if (id.Length > 0 && id != "-")
            {
                app.Key = "winget:" + id;
                app.WingetId = id;
            }
            else continue;

            if (!string.IsNullOrWhiteSpace(available) && available != "-")
            {
                app.LatestVersion = CleanVersion(available);
                // Trust winget's own upgrade verdict; it knows about pinned and
                // architecture-specific packages that our comparison cannot see.
                app.UpdateAvailable = true;
                app.UpdateMethod = UpdateMethod.Winget;
            }

            if (source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
            {
                app.Sources |= DetectionSource.MsStore;
                app.UpdateMethod = UpdateMethod.MsStore;
            }

            results.Add(app);
        }

        return results;
    }

    /// <summary>
    /// Converts an MSIX package *full* name into its *family* name, which is what
    /// Get-AppxPackage reports. Without this the two sources never join and every Store
    /// app is listed twice.
    /// <code>
    /// full:   21676OptimiliaStudios.AquileReader_1.1.65.0_x64__k42naep6bwmrc
    /// family: 21676OptimiliaStudios.AquileReader_k42naep6bwmrc
    /// </code>
    /// The doubled underscore in the full name is an empty resource-id field; the family
    /// name joins name and publisher with a single underscore.
    /// </summary>
    public static string ToFamilyName(string packageName)
    {
        int lastUnderscore = packageName.LastIndexOf('_');
        if (lastUnderscore < 0) return packageName;

        var publisherId = packageName[(lastUnderscore + 1)..];
        if (publisherId.Length == 0) return packageName;

        int firstUnderscore = packageName.IndexOf('_');
        if (firstUnderscore < 0 || firstUnderscore == lastUnderscore) return packageName;

        var name = packageName[..firstUnderscore];
        return name.Length == 0 ? packageName : $"{name}_{publisherId}";
    }

    /// <summary>
    /// Tidies a version cell. winget prefixes a version with "&gt; " when several versions
    /// of the package are installed and it is showing one of them — that marker is
    /// winget's own annotation, not part of the version, and storing it verbatim showed
    /// users "&gt; 1.8.9" as their installed version. Vendor tags such as AnyDesk's
    /// "ad 9.7.9" are left alone; the version parser already tolerates those.
    /// </summary>
    private static string CleanVersion(string v)
    {
        v = v.Trim();
        if (v.StartsWith('>') || v.StartsWith('<')) v = v[1..].Trim();
        if (v is "" or "-" or "Unknown") return "";
        return v;
    }

    // ---- Column-aligned table parsing -----------------------------------

    public sealed class Row
    {
        private readonly string[] _cells;
        public Row(string[] cells) => _cells = cells;
        public int Count => _cells.Length;
        public string Get(int i) => i < _cells.Length ? _cells[i].Trim() : "";
    }

    /// <summary>
    /// winget renders a fixed-width table with no delimiters, so columns are located by
    /// the header's start offsets. Offsets are measured in terminal cells rather than
    /// chars: CJK names occupy two cells each, and slicing by char index would shear
    /// every row containing one.
    /// </summary>
    public static List<Row> ParseTable(string output)
    {
        var rows = new List<Row>();
        var lines = output.Split('\n', StringSplitOptions.None)
                          .Select(l => l.TrimEnd('\r'))
                          .ToList();

        int headerIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Contains("Name", StringComparison.Ordinal) &&
                l.Contains("Id", StringComparison.Ordinal) &&
                l.Contains("Version", StringComparison.Ordinal))
            { headerIdx = i; break; }
        }
        if (headerIdx < 0) return rows;

        var header = lines[headerIdx];
        var starts = ColumnStarts(header);
        if (starts.Count < 3) return rows;

        for (int i = headerIdx + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            // The separator rule under the header.
            if (line.TrimStart().StartsWith("---", StringComparison.Ordinal)) continue;
            // Trailing summary lines such as "5 upgrades available."
            if (DisplayWidth(line) < starts[1]) continue;

            rows.Add(new Row(SliceByDisplayColumns(line, starts)));
        }

        return rows;
    }

    /// <summary>Finds the cell offset at which each header word begins.</summary>
    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        int col = 0;
        bool inGap = true;
        foreach (var ch in header)
        {
            if (ch == ' ')
            {
                inGap = true;
            }
            else
            {
                if (inGap) starts.Add(col);
                inGap = false;
            }
            col += CharWidth(ch);
        }
        return starts;
    }

    private static string[] SliceByDisplayColumns(string line, List<int> starts)
    {
        var cells = new string[starts.Count];
        int cell = 0;
        int col = 0;
        var current = new System.Text.StringBuilder();

        foreach (var ch in line)
        {
            // Advance to the next column when we reach its starting cell.
            while (cell + 1 < starts.Count && col >= starts[cell + 1])
            {
                cells[cell] = current.ToString();
                current.Clear();
                cell++;
            }
            current.Append(ch);
            col += CharWidth(ch);
        }
        cells[cell] = current.ToString();

        for (int i = 0; i < cells.Length; i++) cells[i] ??= "";
        return cells;
    }

    private static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (var ch in s) w += CharWidth(ch);
        return w;
    }

    /// <summary>
    /// Terminal cell width of a character. Covers the East Asian Wide and Fullwidth
    /// ranges plus emoji, which are the cases that actually appear in program names.
    /// </summary>
    private static int CharWidth(char ch)
    {
        if (char.IsControl(ch)) return 0;
        // Combining marks occupy no cell of their own.
        if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) return 0;

        int c = ch;
        if (c < 0x1100) return 1;
        if (c <= 0x115F) return 2;                       // Hangul Jamo
        if (c is 0x2329 or 0x232A) return 2;
        if (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) return 2;   // CJK, Kana, Yi
        if (c >= 0xAC00 && c <= 0xD7A3) return 2;        // Hangul syllables
        if (c >= 0xF900 && c <= 0xFAFF) return 2;        // CJK compatibility ideographs
        if (c >= 0xFE30 && c <= 0xFE6F) return 2;        // CJK compatibility forms
        if (c >= 0xFF00 && c <= 0xFF60) return 2;        // Fullwidth forms
        if (c >= 0xFFE0 && c <= 0xFFE6) return 2;
        return 1;
    }
}
