namespace UpdateHub.Data;

/// <summary>
/// TTL cache for raw provider responses. Providers consult this before touching the
/// network, so a rescan of 150 apps costs zero HTTP requests inside the TTL window.
/// </summary>
public sealed class CacheStore
{
    private readonly Database _db;
    public CacheStore(Database db) => _db = db;

    public sealed record Entry(string? Payload, string? ETag, DateTime FetchedAt, DateTime? ExpiresAt)
    {
        public bool IsFresh => ExpiresAt is null || ExpiresAt > DateTime.UtcNow;
        public TimeSpan Age => DateTime.UtcNow - FetchedAt;
    }

    public Entry? Get(string key)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload, etag, fetched_at, expires_at FROM provider_cache WHERE cache_key=$k";
        cmd.With("$k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Entry(r.GetStr(0), r.GetStr(1),
            r.GetDate(2) ?? DateTime.UtcNow, r.GetDate(3));
    }

    /// <summary>Returns the payload only when still inside its TTL.</summary>
    public string? GetFresh(string key)
    {
        var e = Get(key);
        return e is not null && e.IsFresh ? e.Payload : null;
    }

    public void Set(string key, string? payload, TimeSpan? ttl, string? etag = null) => _db.Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO provider_cache(cache_key,payload,etag,fetched_at,expires_at)
            VALUES($k,$p,$e,$f,$x)
            ON CONFLICT(cache_key) DO UPDATE SET
                payload=excluded.payload, etag=excluded.etag,
                fetched_at=excluded.fetched_at, expires_at=excluded.expires_at
            """;
        cmd.With("$k", key).With("$p", payload).With("$e", etag)
           .With("$f", DateTime.UtcNow.ToString("o"))
           .With("$x", ttl is { } t ? DateTime.UtcNow.Add(t).ToString("o") : null);
        cmd.ExecuteNonQuery();
    });

    /// <summary>Marks an entry fresh again without changing its body (HTTP 304 path).</summary>
    public void Touch(string key, TimeSpan ttl) => _db.Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE provider_cache SET fetched_at=$f, expires_at=$x WHERE cache_key=$k";
        cmd.With("$k", key).With("$f", DateTime.UtcNow.ToString("o"))
           .With("$x", DateTime.UtcNow.Add(ttl).ToString("o"));
        cmd.ExecuteNonQuery();
    });

    public void Remove(string key) => _db.Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM provider_cache WHERE cache_key=$k";
        cmd.With("$k", key);
        cmd.ExecuteNonQuery();
    });

    public int Count()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM provider_cache";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
