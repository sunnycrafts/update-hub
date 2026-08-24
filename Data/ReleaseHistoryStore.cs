using UpdateHub.Core;
using UpdateHub.Models;

namespace UpdateHub.Data;

/// <summary>
/// The local release-history archive. Once we learn that version X shipped on date D that
/// fact never changes, so rows here are written once and reused forever — which is what
/// keeps the app usable against GitHub's 60-requests-per-hour anonymous budget.
/// </summary>
public sealed class ReleaseHistoryStore
{
    private readonly Database _db;
    public ReleaseHistoryStore(Database db) => _db = db;

    public void Upsert(string productKey, IEnumerable<ReleaseInfo> releases)
    {
        var list = releases.Where(r => !string.IsNullOrWhiteSpace(r.Version)).ToList();
        if (list.Count == 0) return;

        _db.Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            // Never downgrade a known date to null, and never let a coarser source
            // overwrite a precise one — a GitHub exact date outranks a manifest guess.
            cmd.CommandText = """
                INSERT INTO release_history
                    (product_key, version, release_date, precision, notes, notes_url,
                     download_url, source, is_security, is_prerelease)
                VALUES ($p,$v,$d,$prec,$n,$nu,$du,$s,$sec,$pre)
                ON CONFLICT(product_key, version) DO UPDATE SET
                    release_date = CASE WHEN excluded.precision >= release_history.precision
                                        AND excluded.release_date IS NOT NULL
                                   THEN excluded.release_date ELSE release_history.release_date END,
                    precision    = MAX(excluded.precision, release_history.precision),
                    notes        = COALESCE(excluded.notes, release_history.notes),
                    notes_url    = COALESCE(excluded.notes_url, release_history.notes_url),
                    download_url = COALESCE(excluded.download_url, release_history.download_url),
                    source       = COALESCE(excluded.source, release_history.source),
                    is_security  = MAX(excluded.is_security, release_history.is_security),
                    is_prerelease= excluded.is_prerelease
                """;
            foreach (var p in new[] { "$p","$v","$d","$prec","$n","$nu","$du","$s","$sec","$pre" })
                cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter(p, Microsoft.Data.Sqlite.SqliteType.Text));

            foreach (var rel in list)
            {
                cmd.Parameters["$p"].Value = productKey;
                cmd.Parameters["$v"].Value = rel.Version;
                cmd.Parameters["$d"].Value = (object?)rel.ReleaseDate.ToIso() ?? DBNull.Value;
                cmd.Parameters["$prec"].Value = (int)rel.Precision;
                cmd.Parameters["$n"].Value = (object?)Truncate(rel.Notes, 8000) ?? DBNull.Value;
                cmd.Parameters["$nu"].Value = (object?)rel.NotesUrl ?? DBNull.Value;
                cmd.Parameters["$du"].Value = (object?)rel.DownloadUrl ?? DBNull.Value;
                cmd.Parameters["$s"].Value = (object?)rel.Source ?? DBNull.Value;
                cmd.Parameters["$sec"].Value = rel.IsSecurityRelease ? 1 : 0;
                cmd.Parameters["$pre"].Value = rel.IsPrerelease ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
        });
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];

    public List<ReleaseInfo> GetHistory(string productKey)
    {
        var list = new List<ReleaseInfo>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT version, release_date, precision, notes, notes_url, download_url,
                   source, is_security, is_prerelease
            FROM release_history WHERE product_key = $p
            """;
        cmd.With("$p", productKey);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ReleaseInfo
            {
                Version = r.GetString(0),
                ReleaseDate = r.GetDate(1),
                Precision = (DatePrecision)r.GetInt32(2),
                Notes = r.GetStr(3),
                NotesUrl = r.GetStr(4),
                DownloadUrl = r.GetStr(5),
                Source = r.GetStr(6) ?? "",
                IsSecurityRelease = r.GetInt32(7) != 0,
                IsPrerelease = r.GetInt32(8) != 0,
            });
        }

        // Newest first, using real version ordering rather than string ordering.
        list.Sort((a, b) => string.CompareOrdinal(
            VersionComparer.SortKey(b.Version), VersionComparer.SortKey(a.Version)));
        return list;
    }

    /// <summary>Looks up the ship date of one specific version.</summary>
    public ReleaseInfo? GetRelease(string productKey, string version)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT version, release_date, precision, notes, notes_url, download_url, source,
                   is_security, is_prerelease
            FROM release_history WHERE product_key=$p AND version=$v
            """;
        cmd.With("$p", productKey).With("$v", version);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ReleaseInfo
        {
            Version = r.GetString(0),
            ReleaseDate = r.GetDate(1),
            Precision = (DatePrecision)r.GetInt32(2),
            Notes = r.GetStr(3),
            NotesUrl = r.GetStr(4),
            DownloadUrl = r.GetStr(5),
            Source = r.GetStr(6) ?? "",
            IsSecurityRelease = r.GetInt32(7) != 0,
            IsPrerelease = r.GetInt32(8) != 0,
        };
    }

    /// <summary>
    /// Counts stable releases in the half-open interval (installed, latest] — that is,
    /// published after the installed version and no newer than the version being compared
    /// against. Pre-releases are excluded; missing a nightly is not the same as being behind.
    ///
    /// The upper bound matters. Without it, every build newer than the installed one was
    /// counted, including versions from a separate pre-stable branch that sit *ahead* of
    /// the latest release. That reported a two-build patch bump of Everything
    /// (1.4.1.1030 to 1.4.1.1032) as 57 missed releases, because its 1.5.x alpha line was
    /// swept in. The question being answered is "how many releases between what I have and
    /// what I would be updating to", so anything past that target does not belong.
    /// </summary>
    public (int Missed, int Security) CountMissed(
        string productKey, string installedVersion, string? latestVersion = null)
    {
        var history = GetHistory(productKey);
        if (history.Count == 0) return (0, 0);

        int missed = 0, security = 0;
        foreach (var rel in history)
        {
            if (rel.IsPrerelease) continue;
            if (VersionComparer.Compare(installedVersion, rel.Version) is not { } cmp) continue;
            if (cmp >= 0) continue;                 // at or ahead of this release

            if (latestVersion is not null
                && VersionComparer.Compare(rel.Version, latestVersion) is { } toLatest
                && toLatest > 0) continue;          // newer than the update target

            missed++;
            if (rel.IsSecurityRelease) security++;
        }
        return (missed, security);
    }

    public int CountAll()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM release_history";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
