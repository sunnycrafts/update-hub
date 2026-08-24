using System.IO;
using Microsoft.Data.Sqlite;

namespace UpdateHub.Data;

/// <summary>
/// Owns the SQLite file and its schema. Readers run concurrently under WAL; writes are
/// serialised through a semaphore so provider refreshes fanning out across threads never
/// collide on SQLITE_BUSY.
/// </summary>
public sealed class Database : IDisposable
{
    private const int SchemaVersion = 1;

    public string Path { get; }
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public Database(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        Path = System.IO.Path.Combine(dataRoot, "updatehub.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache. Shared-cache mode is discouraged by Microsoft.Data.Sqlite and
            // interacts badly with WAL across processes — UpdateHub is genuinely
            // multi-process, since the scheduled scan can run while the window is open.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();

        RecoverIfCorrupt();
        Initialize();
    }

    /// <summary>
    /// Verifies the file and rebuilds it if SQLite reports damage. Everything here except
    /// the ignore rules is a rebuildable cache, so recreating beats leaving the app
    /// permanently broken; the rules are salvaged first when they are still readable.
    /// </summary>
    private void RecoverIfCorrupt()
    {
        if (!File.Exists(Path)) return;

        if (IsHealthy()) return;

        var salvaged = TrySalvageIgnores();
        SqliteConnection.ClearAllPools();

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = Path + suffix;
            if (!File.Exists(path)) continue;
            try
            {
                // Kept rather than deleted so a damaged database can still be examined.
                File.Move(path, $"{Path}.corrupt-{stamp}{suffix}", overwrite: true);
            }
            catch (IOException) { try { File.Delete(path); } catch { } }
            catch (UnauthorizedAccessException) { }
        }

        RecoveredFromCorruption = true;
        SalvagedIgnoreRules = salvaged.Count;
        _pendingIgnores = salvaged;
    }

    private bool IsHealthy()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // quick_check catches structural damage without the full-scan cost.
            cmd.CommandText = "PRAGMA quick_check(1)";
            var result = cmd.ExecuteScalar()?.ToString();
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private List<(string Key, string? Name, string? Reason, string? Skipped, string Created)> TrySalvageIgnores()
    {
        var rows = new List<(string, string?, string?, string?, string)>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, name, reason, skipped_version, created_at FROM ignored";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.GetStr(1), r.GetStr(2), r.GetStr(3),
                          r.GetStr(4) ?? DateTime.Now.ToString("o")));
        }
        catch (Exception) { /* the damaged region may well be this table */ }
        return rows;
    }

    private List<(string Key, string? Name, string? Reason, string? Skipped, string Created)> _pendingIgnores = new();

    /// <summary>True when the database had to be rebuilt on this run.</summary>
    public bool RecoveredFromCorruption { get; private set; }

    public int SalvagedIgnoreRules { get; private set; }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Runs a write inside the serialising lock and a transaction.</summary>
    public async Task WriteAsync(Action<SqliteConnection> work, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            work(conn);
            tx.Commit();
        }
        finally { _writeLock.Release(); }
    }

    public void Write(Action<SqliteConnection> work)
    {
        _writeLock.Wait();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            work(conn);
            tx.Commit();
        }
        finally { _writeLock.Release(); }
    }

    private void Initialize()
    {
        Write(conn =>
        {
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS meta (
                    key   TEXT PRIMARY KEY,
                    value TEXT
                );

                CREATE TABLE IF NOT EXISTS apps (
                    key                        TEXT PRIMARY KEY,
                    name                       TEXT NOT NULL,
                    publisher                  TEXT,
                    normalized_name            TEXT,
                    sources                    INTEGER NOT NULL DEFAULT 0,
                    registry_key               TEXT,
                    winget_id                  TEXT,
                    choco_id                   TEXT,
                    scoop_id                   TEXT,
                    msstore_family             TEXT,
                    github_repo                TEXT,
                    install_location           TEXT,
                    uninstall_string           TEXT,
                    exe_path                   TEXT,
                    homepage                   TEXT,
                    is_system                  INTEGER NOT NULL DEFAULT 0,
                    size_kb                    INTEGER NOT NULL DEFAULT 0,
                    installed_version          TEXT,
                    installed_release_date     TEXT,
                    installed_release_prec     INTEGER NOT NULL DEFAULT 0,
                    install_date               TEXT,
                    latest_version             TEXT,
                    latest_release_date        TEXT,
                    latest_release_prec        INTEGER NOT NULL DEFAULT 0,
                    latest_notes               TEXT,
                    latest_notes_url           TEXT,
                    download_url               TEXT,
                    download_page_url          TEXT,
                    update_available           INTEGER NOT NULL DEFAULT 0,
                    update_method              INTEGER NOT NULL DEFAULT 0,
                    majors_behind              INTEGER NOT NULL DEFAULT 0,
                    minors_behind              INTEGER NOT NULL DEFAULT 0,
                    releases_missed            INTEGER NOT NULL DEFAULT 0,
                    security_missed            INTEGER NOT NULL DEFAULT 0,
                    confidence                 INTEGER NOT NULL DEFAULT 0,
                    confidence_sources         TEXT,
                    confidence_note            TEXT,
                    last_checked               TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_apps_norm ON apps(normalized_name);

                -- Every published version we have ever learned about, for any product.
                -- Keyed by product rather than by installed app so the history survives
                -- uninstall/reinstall and is shared between machines' exports.
                CREATE TABLE IF NOT EXISTS release_history (
                    product_key   TEXT NOT NULL,
                    version       TEXT NOT NULL,
                    release_date  TEXT,
                    precision     INTEGER NOT NULL DEFAULT 0,
                    notes         TEXT,
                    notes_url     TEXT,
                    download_url  TEXT,
                    source        TEXT,
                    is_security   INTEGER NOT NULL DEFAULT 0,
                    is_prerelease INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (product_key, version)
                );
                CREATE INDEX IF NOT EXISTS ix_hist_product ON release_history(product_key);

                -- Raw provider responses with TTL, so a rescan does not re-hit the network.
                CREATE TABLE IF NOT EXISTS provider_cache (
                    cache_key  TEXT PRIMARY KEY,
                    payload    TEXT,
                    etag       TEXT,
                    fetched_at TEXT NOT NULL,
                    expires_at TEXT
                );

                -- Ignore rules live apart from `apps` so they persist even when an app
                -- disappears from a scan and comes back later.
                CREATE TABLE IF NOT EXISTS ignored (
                    key             TEXT PRIMARY KEY,
                    name            TEXT,
                    reason          TEXT,
                    skipped_version TEXT,
                    created_at      TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS update_history (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    app_key      TEXT NOT NULL,
                    name         TEXT,
                    from_version TEXT,
                    to_version   TEXT,
                    method       INTEGER NOT NULL DEFAULT 0,
                    outcome      INTEGER NOT NULL DEFAULT 0,
                    message      TEXT,
                    started_at   TEXT NOT NULL,
                    finished_at  TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_hist_started ON update_history(started_at DESC);
                """);

            Exec(conn, $"INSERT INTO meta(key,value) VALUES('schema','{SchemaVersion}') " +
                       "ON CONFLICT(key) DO UPDATE SET value=excluded.value;");

            // Put back any ignore rules rescued from a corrupt predecessor.
            if (_pendingIgnores.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO ignored(key,name,reason,skipped_version,created_at)
                    VALUES($k,$n,$r,$v,$t) ON CONFLICT(key) DO NOTHING
                    """;
                foreach (var p in new[] { "$k", "$n", "$r", "$v", "$t" })
                    cmd.Parameters.Add(new SqliteParameter(p, SqliteType.Text));
                foreach (var (key, name, reason, skipped, created) in _pendingIgnores)
                {
                    cmd.Parameters["$k"].Value = key;
                    cmd.Parameters["$n"].Value = (object?)name ?? DBNull.Value;
                    cmd.Parameters["$r"].Value = (object?)reason ?? DBNull.Value;
                    cmd.Parameters["$v"].Value = (object?)skipped ?? DBNull.Value;
                    cmd.Parameters["$t"].Value = created;
                    cmd.ExecuteNonQuery();
                }
                _pendingIgnores.Clear();
            }
        });
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Drops cached provider responses and release history; keeps apps and ignores.</summary>
    public void ClearCache() => Write(conn =>
        Exec(conn, "DELETE FROM provider_cache; DELETE FROM release_history;"));

    public long SizeBytes => File.Exists(Path) ? new FileInfo(Path).Length : 0;

    public void Dispose()
    {
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

internal static class SqliteExtensions
{
    public static SqliteCommand With(this SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static string? GetStr(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static DateTime? GetDate(this SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return DateTime.TryParse(r.GetString(i), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }

    public static string? ToIso(this DateTime? d) => d?.ToString("o");
}
