using DBsync.Contracts;
using DBsync.Contracts.Ipc;

namespace DBsync.Service.Storage;

/// <summary>
/// Parked conflicts awaiting a decision in the conflict dialog. A file with an open conflict is
/// excluded from normal reconciliation until it is resolved, so the two copies stay put.
/// </summary>
public sealed class ConflictStore
{
    private readonly Database _database;

    public ConflictStore(Database database) => _database = database;

    /// <summary>
    /// Records (or refreshes) the open conflict for a file. Returns the stored record; the
    /// <c>isNew</c> flag lets the caller decide whether to raise a toast.
    /// </summary>
    public (ConflictRecord Record, bool IsNew) Raise(
        string pairId, string pairName, string sharePath, string relativePath,
        ConflictSide local, ConflictSide share)
    {
        using var connection = _database.Open();

        long? existingId;
        using (var find = connection.CreateCommand())
        {
            find.CommandText =
                "SELECT id FROM conflict WHERE pair_id = $p AND rel_path = $r AND resolved = 0;";
            find.Parameters.AddWithValue("$p", pairId);
            find.Parameters.AddWithValue("$r", relativePath);
            var scalar = find.ExecuteScalar();
            existingId = scalar is null or DBNull ? null : Convert.ToInt64(scalar);
        }

        var detected = existingId is null ? DateTimeOffset.UtcNow : ReadDetected(existingId.Value);

        using (var write = connection.CreateCommand())
        {
            if (existingId is null)
            {
                write.CommandText =
                    "INSERT INTO conflict (pair_id, rel_path, detected, local_mtime, local_bytes, local_owner, " +
                    "share_mtime, share_bytes, share_owner, resolved) " +
                    "VALUES ($p, $r, $d, $lm, $lb, $lo, $sm, $sb, $so, 0); SELECT last_insert_rowid();";
            }
            else
            {
                write.CommandText =
                    "UPDATE conflict SET local_mtime = $lm, local_bytes = $lb, local_owner = $lo, " +
                    "share_mtime = $sm, share_bytes = $sb, share_owner = $so WHERE id = $id; SELECT $id;";
                write.Parameters.AddWithValue("$id", existingId.Value);
            }

            write.Parameters.AddWithValue("$p", pairId);
            write.Parameters.AddWithValue("$r", relativePath);
            write.Parameters.AddWithValue("$d", detected.ToUnixTimeMilliseconds());
            write.Parameters.AddWithValue("$lm", local.ModifiedUtc.ToUnixTimeMilliseconds());
            write.Parameters.AddWithValue("$lb", local.Bytes);
            write.Parameters.AddWithValue("$lo", local.Owner);
            write.Parameters.AddWithValue("$sm", share.ModifiedUtc.ToUnixTimeMilliseconds());
            write.Parameters.AddWithValue("$sb", share.Bytes);
            write.Parameters.AddWithValue("$so", share.Owner);

            var id = Convert.ToInt64(write.ExecuteScalar());
            var record = new ConflictRecord
            {
                Id = id,
                PairId = pairId,
                PairName = pairName,
                SharePath = sharePath,
                RelativePath = relativePath,
                Local = local,
                Share = share,
                DetectedUtc = detected,
            };
            return (record, existingId is null);
        }
    }

    private DateTimeOffset ReadDetected(long id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT detected FROM conflict WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(value));
    }

    public List<ConflictRecord> Query(ConflictQuery query, Func<string, FolderPair?> pairLookup)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, pair_id, rel_path, detected, local_mtime, local_bytes, local_owner, " +
            "share_mtime, share_bytes, share_owner, resolved FROM conflict " +
            "WHERE ($pair IS NULL OR pair_id = $pair) AND ($all = 1 OR resolved = 0) " +
            "ORDER BY detected DESC;";
        command.Parameters.AddWithValue("$pair",
            string.IsNullOrWhiteSpace(query.PairId) ? DBNull.Value : query.PairId);
        command.Parameters.AddWithValue("$all", query.IncludeResolved ? 1 : 0);

        var records = new List<ConflictRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var pairId = reader.GetString(1);
            var pair = pairLookup(pairId);
            records.Add(new ConflictRecord
            {
                Id = reader.GetInt64(0),
                PairId = pairId,
                PairName = pair?.Name ?? pairId,
                SharePath = pair?.SharePath ?? "",
                RelativePath = reader.GetString(2),
                DetectedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                Local = new ConflictSide
                {
                    ModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    Bytes = reader.GetInt64(5),
                    Owner = reader.GetString(6),
                },
                Share = new ConflictSide
                {
                    ModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                    Bytes = reader.GetInt64(8),
                    Owner = reader.GetString(9),
                },
                Resolved = reader.GetInt32(10) != 0,
            });
        }

        return records;
    }

    public ConflictRecord? Find(long id, Func<string, FolderPair?> pairLookup) =>
        Query(new ConflictQuery { IncludeResolved = true }, pairLookup).FirstOrDefault(c => c.Id == id);

    /// <summary>Every other open conflict in the same pair — backs "Do this for the other N conflicts".</summary>
    public List<ConflictRecord> OpenInPair(string pairId, Func<string, FolderPair?> pairLookup) =>
        Query(new ConflictQuery { PairId = pairId }, pairLookup);

    public int CountOpen(string pairId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conflict WHERE pair_id = $p AND resolved = 0;";
        command.Parameters.AddWithValue("$p", pairId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public Dictionary<string, int> CountOpenByPair()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pair_id, COUNT(*) FROM conflict WHERE resolved = 0 GROUP BY pair_id;";

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    /// <summary>Open conflict paths for a pair, so the reconciler can skip them.</summary>
    public HashSet<string> OpenPaths(string pairId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rel_path FROM conflict WHERE pair_id = $p AND resolved = 0;";
        command.Parameters.AddWithValue("$p", pairId);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    public void MarkResolved(long id, ConflictResolution resolution)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conflict SET resolved = 1, resolution = $r WHERE id = $id;";
        command.Parameters.AddWithValue("$r", (int)resolution);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Clears an open conflict without recording a decision — used when a side disappears.</summary>
    public void Withdraw(string pairId, string relativePath)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM conflict WHERE pair_id = $p AND rel_path = $r AND resolved = 0;";
        command.Parameters.AddWithValue("$p", pairId);
        command.Parameters.AddWithValue("$r", relativePath);
        command.ExecuteNonQuery();
    }
}
