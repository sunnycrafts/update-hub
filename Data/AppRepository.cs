using Microsoft.Data.Sqlite;
using UpdateHub.Models;

namespace UpdateHub.Data;

/// <summary>Persists the scanned inventory, ignore rules and update history.</summary>
public sealed class AppRepository
{
    private readonly Database _db;
    public AppRepository(Database db) => _db = db;

    private const string Columns = """
        key, name, publisher, normalized_name, sources, registry_key, winget_id, choco_id,
        scoop_id, msstore_family, github_repo, install_location, uninstall_string, exe_path,
        homepage, is_system, size_kb, installed_version, installed_release_date,
        installed_release_prec, install_date, latest_version, latest_release_date,
        latest_release_prec, latest_notes, latest_notes_url, download_url, download_page_url,
        update_available, update_method, majors_behind, minors_behind, releases_missed,
        security_missed, confidence, confidence_sources, confidence_note, last_checked
        """;

    /// <summary>
    /// Carries previously learned version data forward onto a fresh scan.
    ///
    /// A scan that could not reach the network — offline, or simply out of GitHub budget —
    /// produces apps with no latest version. Saving those directly would erase everything
    /// earlier runs discovered, so each rate-limited scan would degrade the database. Only
    /// gaps are filled; anything the new scan established wins.
    /// </summary>
    public void CarryForwardKnownVersions(IEnumerable<InstalledApp> apps)
    {
        Dictionary<string, InstalledApp> previous;
        try
        {
            previous = LoadApps().GroupBy(a => a.Key)
                                 .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { return; }
        if (previous.Count == 0) return;

        foreach (var app in apps)
        {
            if (!previous.TryGetValue(app.Key, out var old)) continue;
            if (app.LatestVersion is not null) continue;   // fresh data wins

            app.LatestVersion = old.LatestVersion;
            app.LatestReleaseDate = old.LatestReleaseDate;
            app.LatestReleasePrecision = old.LatestReleasePrecision;
            app.LatestNotes ??= old.LatestNotes;
            app.LatestNotesUrl ??= old.LatestNotesUrl;
            app.DownloadUrl ??= old.DownloadUrl;
            app.DownloadPageUrl ??= old.DownloadPageUrl;
            app.GitHubRepo ??= old.GitHubRepo;
            app.Homepage ??= old.Homepage;
            app.LastChecked ??= old.LastChecked;

            // The installed version may have changed since, so the verdict is recomputed
            // rather than copied.
            if (app.LatestVersion is not null)
            {
                app.InstalledReleaseDate ??= old.InstalledReleaseDate;
                if (app.InstalledReleasePrecision == DatePrecision.Unknown)
                    app.InstalledReleasePrecision = old.InstalledReleasePrecision;

                if (Core.VersionComparer.Compare(app.InstalledVersion, app.LatestVersion) is { } cmp)
                {
                    app.UpdateAvailable = cmp < 0;
                    app.MajorVersionsBehind =
                        Core.VersionComparer.MajorsBehind(app.InstalledVersion, app.LatestVersion);
                    app.MinorVersionsBehind =
                        Core.VersionComparer.MinorsBehind(app.InstalledVersion, app.LatestVersion);
                }

                // Re-run the consistency check: the verdict was just recomputed against a
                // possibly-changed installed version, and without this a carried-forward
                // row can claim "up to date" while still being flagged as behind.
                app.DatesInconsistent =
                    app.InstalledReleaseDate is { } ird && app.LatestReleaseDate is { } lrd
                    && app.UpdateAvailable && ird.Date >= lrd.Date;

                app.ReleasesMissed = old.ReleasesMissed;
                app.SecurityReleasesMissed = old.SecurityReleasesMissed;
                if (app.Confidence == 0) app.Confidence = old.Confidence;
                if (app.ConfidenceSources.Count == 0) app.ConfidenceSources = old.ConfidenceSources;
                app.ConfidenceNote ??= old.ConfidenceNote;
                if (app.UpdateMethod == UpdateMethod.None) app.UpdateMethod = old.UpdateMethod;
            }
        }
    }

    public void SaveApps(IEnumerable<InstalledApp> apps)
    {
        var list = apps.ToList();
        _db.Write(conn =>
        {
            // A full scan replaces the inventory; ignore rules live in their own table
            // so nothing the user configured is lost here.
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM apps";
                del.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO apps ({Columns}) VALUES (
                    $key, $name, $publisher, $norm, $sources, $regkey, $winget, $choco,
                    $scoop, $store, $github, $loc, $uninst, $exe,
                    $home, $sys, $size, $iver, $irel,
                    $iprec, $idate, $lver, $lrel,
                    $lprec, $lnotes, $lnotesurl, $dlurl, $dlpage,
                    $upd, $method, $maj, $min, $missed,
                    $sec, $conf, $confsrc, $confnote, $checked)
                """;
            foreach (var p in new[] { "$key","$name","$publisher","$norm","$sources","$regkey","$winget",
                "$choco","$scoop","$store","$github","$loc","$uninst","$exe","$home","$sys","$size",
                "$iver","$irel","$iprec","$idate","$lver","$lrel","$lprec","$lnotes","$lnotesurl",
                "$dlurl","$dlpage","$upd","$method","$maj","$min","$missed","$sec","$conf",
                "$confsrc","$confnote","$checked" })
                cmd.Parameters.Add(new SqliteParameter(p, SqliteType.Text));

            foreach (var a in list)
            {
                Set(cmd, "$key", a.Key);
                Set(cmd, "$name", a.Name);
                Set(cmd, "$publisher", a.Publisher);
                Set(cmd, "$norm", a.NormalizedName);
                Set(cmd, "$sources", (int)a.Sources);
                Set(cmd, "$regkey", a.RegistryKey);
                Set(cmd, "$winget", a.WingetId);
                Set(cmd, "$choco", a.ChocoId);
                Set(cmd, "$scoop", a.ScoopId);
                Set(cmd, "$store", a.MsStoreFamilyName);
                Set(cmd, "$github", a.GitHubRepo);
                Set(cmd, "$loc", a.InstallLocation);
                Set(cmd, "$uninst", a.UninstallString);
                Set(cmd, "$exe", a.ExePath);
                Set(cmd, "$home", a.Homepage);
                Set(cmd, "$sys", a.IsSystemComponent ? 1 : 0);
                Set(cmd, "$size", a.EstimatedSizeKb);
                Set(cmd, "$iver", a.InstalledVersion);
                Set(cmd, "$irel", a.InstalledReleaseDate.ToIso());
                Set(cmd, "$iprec", (int)a.InstalledReleasePrecision);
                Set(cmd, "$idate", a.InstallDate.ToIso());
                Set(cmd, "$lver", a.LatestVersion);
                Set(cmd, "$lrel", a.LatestReleaseDate.ToIso());
                Set(cmd, "$lprec", (int)a.LatestReleasePrecision);
                Set(cmd, "$lnotes", a.LatestNotes);
                Set(cmd, "$lnotesurl", a.LatestNotesUrl);
                Set(cmd, "$dlurl", a.DownloadUrl);
                Set(cmd, "$dlpage", a.DownloadPageUrl);
                Set(cmd, "$upd", a.UpdateAvailable ? 1 : 0);
                Set(cmd, "$method", (int)a.UpdateMethod);
                Set(cmd, "$maj", a.MajorVersionsBehind);
                Set(cmd, "$min", a.MinorVersionsBehind);
                Set(cmd, "$missed", a.ReleasesMissed);
                Set(cmd, "$sec", a.SecurityReleasesMissed);
                Set(cmd, "$conf", a.Confidence);
                Set(cmd, "$confsrc", string.Join(",", a.ConfidenceSources));
                Set(cmd, "$confnote", a.ConfidenceNote);
                Set(cmd, "$checked", a.LastChecked.ToIso());
                cmd.ExecuteNonQuery();
            }
        });
    }

    private static void Set(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters[name].Value = value ?? DBNull.Value;

    public List<InstalledApp> LoadApps()
    {
        var result = new List<InstalledApp>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM apps";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new InstalledApp
            {
                Key = r.GetString(0),
                Name = r.GetString(1),
                Publisher = r.GetStr(2),
                NormalizedName = r.GetStr(3) ?? "",
                Sources = (DetectionSource)r.GetInt32(4),
                RegistryKey = r.GetStr(5),
                WingetId = r.GetStr(6),
                ChocoId = r.GetStr(7),
                ScoopId = r.GetStr(8),
                MsStoreFamilyName = r.GetStr(9),
                GitHubRepo = r.GetStr(10),
                InstallLocation = r.GetStr(11),
                UninstallString = r.GetStr(12),
                ExePath = r.GetStr(13),
                Homepage = r.GetStr(14),
                IsSystemComponent = r.GetInt32(15) != 0,
                EstimatedSizeKb = r.GetInt64(16),
                // Derived rather than stored: reclassifying on load keeps cached rows in
                // step with rule changes without needing a schema migration.
                Category = default,
                InstalledVersion = r.GetStr(17) ?? "",
                InstalledReleaseDate = r.GetDate(18),
                InstalledReleasePrecision = (DatePrecision)r.GetInt32(19),
                InstallDate = r.GetDate(20),
                LatestVersion = r.GetStr(21),
                LatestReleaseDate = r.GetDate(22),
                LatestReleasePrecision = (DatePrecision)r.GetInt32(23),
                LatestNotes = r.GetStr(24),
                LatestNotesUrl = r.GetStr(25),
                DownloadUrl = r.GetStr(26),
                DownloadPageUrl = r.GetStr(27),
                UpdateAvailable = r.GetInt32(28) != 0,
                UpdateMethod = (UpdateMethod)r.GetInt32(29),
                MajorVersionsBehind = r.GetInt32(30),
                MinorVersionsBehind = r.GetInt32(31),
                ReleasesMissed = r.GetInt32(32),
                SecurityReleasesMissed = r.GetInt32(33),
                Confidence = r.GetInt32(34),
                ConfidenceSources = (r.GetStr(35) ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                ConfidenceNote = r.GetStr(36),
                LastChecked = r.GetDate(37),
            });
        }

        // Classification is a pure function of the stored fields, so it is recomputed on
        // load instead of persisted — rule changes then apply to cached rows immediately.
        foreach (var app in result) Core.AppClassifier.Classify(app);
        return result;
    }

    // ---- Ignore rules ---------------------------------------------------

    public Dictionary<string, (string? Reason, string? Skipped)> LoadIgnores()
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, reason, skipped_version FROM ignored";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = (r.GetStr(1), r.GetStr(2));
        return map;
    }

    public void SetIgnored(InstalledApp app, bool ignored, string? reason = null) => _db.Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        if (ignored)
        {
            cmd.CommandText = """
                INSERT INTO ignored(key,name,reason,skipped_version,created_at)
                VALUES($k,$n,$r,NULL,$t)
                ON CONFLICT(key) DO UPDATE SET reason=excluded.reason, skipped_version=NULL
                """;
            cmd.With("$k", app.Key).With("$n", app.Name).With("$r", reason)
               .With("$t", DateTime.Now.ToString("o"));
        }
        else
        {
            cmd.CommandText = "DELETE FROM ignored WHERE key=$k";
            cmd.With("$k", app.Key);
        }
        cmd.ExecuteNonQuery();
    });

    /// <summary>Suppresses one specific version without ignoring the app forever.</summary>
    public void SkipVersion(InstalledApp app, string version) => _db.Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ignored(key,name,reason,skipped_version,created_at)
            VALUES($k,$n,NULL,$v,$t)
            ON CONFLICT(key) DO UPDATE SET skipped_version=excluded.skipped_version
            """;
        cmd.With("$k", app.Key).With("$n", app.Name).With("$v", version)
           .With("$t", DateTime.Now.ToString("o"));
        cmd.ExecuteNonQuery();
    });

    // ---- Update history -------------------------------------------------

    public long BeginUpdate(InstalledApp app)
    {
        long id = 0;
        _db.Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO update_history(app_key,name,from_version,to_version,method,outcome,started_at)
                VALUES($k,$n,$f,$t,$m,$o,$s);
                SELECT last_insert_rowid();
                """;
            cmd.With("$k", app.Key).With("$n", app.Name).With("$f", app.InstalledVersion)
               .With("$t", app.LatestVersion).With("$m", (int)app.UpdateMethod)
               .With("$o", (int)UpdateOutcome.Running).With("$s", DateTime.Now.ToString("o"));
            id = Convert.ToInt64(cmd.ExecuteScalar());
        });
        return id;
    }

    public void FinishUpdate(long id, UpdateOutcome outcome, string? message) => _db.Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE update_history SET outcome=$o, message=$m, finished_at=$f WHERE id=$id";
        cmd.With("$o", (int)outcome).With("$m", message)
           .With("$f", DateTime.Now.ToString("o")).With("$id", id);
        cmd.ExecuteNonQuery();
    });

    public List<UpdateRecord> LoadHistory(int limit = 500)
    {
        var list = new List<UpdateRecord>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, app_key, name, from_version, to_version, method, outcome, message,
                   started_at, finished_at
            FROM update_history ORDER BY started_at DESC LIMIT $lim
            """;
        cmd.With("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new UpdateRecord
            {
                Id = r.GetInt64(0),
                AppKey = r.GetString(1),
                Name = r.GetStr(2) ?? "",
                FromVersion = r.GetStr(3),
                ToVersion = r.GetStr(4),
                Method = (UpdateMethod)r.GetInt32(5),
                Outcome = (UpdateOutcome)r.GetInt32(6),
                Message = r.GetStr(7),
                StartedAt = r.GetDate(8) ?? DateTime.MinValue,
                FinishedAt = r.GetDate(9),
            });
        }
        return list;
    }

    public void ClearHistory() => _db.Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM update_history";
        cmd.ExecuteNonQuery();
    });
}

public sealed class UpdateRecord
{
    public long Id { get; set; }
    public string AppKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public UpdateMethod Method { get; set; }
    public UpdateOutcome Outcome { get; set; }
    public string? Message { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public string OutcomeText => Outcome switch
    {
        UpdateOutcome.Success => "Succeeded",
        UpdateOutcome.Failed => "Failed",
        UpdateOutcome.Cancelled => "Cancelled",
        UpdateOutcome.NoUpdateNeeded => "No update needed",
        UpdateOutcome.RequiresElevation => "Needs admin",
        UpdateOutcome.Running => "Running",
        _ => "Pending",
    };

    public string Duration => FinishedAt is { } f
        ? $"{(f - StartedAt).TotalSeconds:F0}s" : "—";
}
