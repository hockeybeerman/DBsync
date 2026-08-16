namespace DBsync.Service.Storage;

/// <summary>
/// The last state both sides were known to agree on for one file. Change detection is
/// "differs from this baseline", which is what makes two-way sync able to tell an edit from a
/// deletion, and a one-sided edit from a genuine conflict.
/// </summary>
public sealed class FileBaseline
{
    public string RelativePath { get; set; } = "";

    /// <summary>-1 when the file did not exist on that side at baseline time.</summary>
    public long LocalBytes { get; set; } = -1;

    /// <summary>Last-write time in UTC ticks, truncated to whole seconds.</summary>
    public long LocalModified { get; set; }

    public long ShareBytes { get; set; } = -1;
    public long ShareModified { get; set; }

    public DateTimeOffset SyncedUtc { get; set; }

    public bool LocalExisted => LocalBytes >= 0;
    public bool ShareExisted => ShareBytes >= 0;
}

/// <summary>Persistent per-pair baseline index. Survives restarts so a reboot is not a full re-copy.</summary>
public sealed class SyncStateStore
{
    private readonly Database _database;

    public SyncStateStore(Database database) => _database = database;

    /// <summary>Loads every baseline row for a pair, keyed case-insensitively like NTFS.</summary>
    public Dictionary<string, FileBaseline> Load(string pairId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT rel_path, local_bytes, local_mtime, share_bytes, share_mtime, synced " +
            "FROM file_state WHERE pair_id = $p;";
        command.Parameters.AddWithValue("$p", pairId);

        var map = new Dictionary<string, FileBaseline>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relativePath = reader.GetString(0);
            map[relativePath] = new FileBaseline
            {
                RelativePath = relativePath,
                LocalBytes = reader.GetInt64(1),
                LocalModified = reader.GetInt64(2),
                ShareBytes = reader.GetInt64(3),
                ShareModified = reader.GetInt64(4),
                SyncedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            };
        }

        return map;
    }

    public void Save(string pairId, FileBaseline baseline)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        WriteUpsert(command, pairId, baseline);
        command.ExecuteNonQuery();
    }

    /// <summary>Batched upsert used by the initial scan, where per-row round trips would dominate.</summary>
    public void SaveMany(string pairId, IEnumerable<FileBaseline> baselines)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var baseline in baselines)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            WriteUpsert(command, pairId, baseline);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Drops every baseline row for a pair — used when a pair is repointed or deleted.</summary>
    public void Clear(string pairId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM file_state WHERE pair_id = $p;";
        command.Parameters.AddWithValue("$p", pairId);
        command.ExecuteNonQuery();
    }

    public void Remove(string pairId, string relativePath)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM file_state WHERE pair_id = $p AND rel_key = $k;";
        command.Parameters.AddWithValue("$p", pairId);
        command.Parameters.AddWithValue("$k", Key(relativePath));
        command.ExecuteNonQuery();
    }

    private static void WriteUpsert(Microsoft.Data.Sqlite.SqliteCommand command, string pairId, FileBaseline baseline)
    {
        command.CommandText =
            "INSERT INTO file_state (pair_id, rel_key, rel_path, local_bytes, local_mtime, " +
            "share_bytes, share_mtime, synced) VALUES ($p, $k, $r, $lb, $lm, $sb, $sm, $s) " +
            "ON CONFLICT(pair_id, rel_key) DO UPDATE SET rel_path = $r, local_bytes = $lb, " +
            "local_mtime = $lm, share_bytes = $sb, share_mtime = $sm, synced = $s;";
        command.Parameters.AddWithValue("$p", pairId);
        command.Parameters.AddWithValue("$k", Key(baseline.RelativePath));
        command.Parameters.AddWithValue("$r", baseline.RelativePath);
        command.Parameters.AddWithValue("$lb", baseline.LocalBytes);
        command.Parameters.AddWithValue("$lm", baseline.LocalModified);
        command.Parameters.AddWithValue("$sb", baseline.ShareBytes);
        command.Parameters.AddWithValue("$sm", baseline.ShareModified);
        command.Parameters.AddWithValue("$s",
            (baseline.SyncedUtc == default ? DateTimeOffset.UtcNow : baseline.SyncedUtc)
            .ToUnixTimeMilliseconds());
    }

    private static string Key(string relativePath) => relativePath.ToLowerInvariant();
}
