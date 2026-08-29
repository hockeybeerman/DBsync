using DBsync.Contracts;
using DBsync.Contracts.Ipc;
using Microsoft.Data.Sqlite;

namespace DBsync.Service.Storage;

/// <summary>Append-only audit trail behind the Activity &amp; history window.</summary>
public sealed class ActivityLog
{
    private readonly Database _database;

    public ActivityLog(Database database) => _database = database;

    /// <summary>Raised for every appended row so the IPC server can push it to subscribers.</summary>
    public event Action<ActivityEntry>? Appended;

    public ActivityEntry Append(
        string pairId,
        string pairName,
        ActivityEventKind kind,
        string file,
        ActivityResult result,
        long bytes = 0,
        string? message = null)
    {
        var entry = new ActivityEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = kind,
            File = file,
            PairId = pairId,
            PairName = pairName,
            Result = result,
            Bytes = bytes,
            Message = message,
        };

        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO activity (ts, kind, file, pair_id, pair_name, result, message, bytes) " +
                "VALUES ($ts, $kind, $file, $pair, $name, $result, $message, $bytes); " +
                "SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$ts", entry.TimestampUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$kind", (int)kind);
            command.Parameters.AddWithValue("$file", file);
            command.Parameters.AddWithValue("$pair", pairId);
            command.Parameters.AddWithValue("$name", pairName);
            command.Parameters.AddWithValue("$result", (int)result);
            command.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
            command.Parameters.AddWithValue("$bytes", bytes);
            entry.Id = Convert.ToInt64(command.ExecuteScalar());
        }

        Appended?.Invoke(entry);
        return entry;
    }

    public List<ActivityEntry> Query(ActivityQuery query)
    {
        var since = DateTimeOffset.UtcNow - (query.Since ?? TimeSpan.FromHours(24));
        var limit = Math.Clamp(query.Limit <= 0 ? 200 : query.Limit, 1, 5000);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, ts, kind, file, pair_id, pair_name, result, message, bytes FROM activity " +
            "WHERE ts >= $since AND ($pair IS NULL OR pair_id = $pair) " +
            "AND ($kind IS NULL OR kind = $kind) " +
            "AND ($search IS NULL OR instr(lower(file), $search) > 0 " +
            "OR instr(lower(pair_name), $search) > 0) " +
            "ORDER BY ts DESC, id DESC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$pair", (object?)NullIfEmpty(query.PairId) ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (object?)(int?)query.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", (object?)SearchTerm(query.Search) ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

        var entries = new List<ActivityEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new ActivityEntry
            {
                Id = reader.GetInt64(0),
                TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                Kind = (ActivityEventKind)reader.GetInt32(2),
                File = reader.GetString(3),
                PairId = reader.GetString(4),
                PairName = reader.GetString(5),
                Result = (ActivityResult)reader.GetInt32(6),
                Message = reader.IsDBNull(7) ? null : reader.GetString(7),
                Bytes = reader.GetInt64(8),
            });
        }

        return entries;
    }

    /// <summary>Header counters: "1,284 files sent", "312 received", "2 conflicts".</summary>
    public ActivitySummary Summarise(ActivityQuery query, int pairCount)
    {
        var since = DateTimeOffset.UtcNow - (query.Since ?? TimeSpan.FromHours(24));

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT kind, COUNT(*) FROM activity " +
            "WHERE ts >= $since AND ($pair IS NULL OR pair_id = $pair) AND result IN ($ok, $pending) " +
            "AND ($search IS NULL OR instr(lower(file), $search) > 0 " +
            "OR instr(lower(pair_name), $search) > 0) " +
            "GROUP BY kind;";
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$pair", (object?)NullIfEmpty(query.PairId) ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", (object?)SearchTerm(query.Search) ?? DBNull.Value);
        command.Parameters.AddWithValue("$ok", (int)ActivityResult.Ok);
        command.Parameters.AddWithValue("$pending", (int)ActivityResult.Pending);

        var summary = new ActivitySummary { PairCount = pairCount };
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var count = reader.GetInt32(1);
            switch ((ActivityEventKind)reader.GetInt32(0))
            {
                case ActivityEventKind.Sent: summary.FilesSent = count; break;
                case ActivityEventKind.Received: summary.FilesReceived = count; break;
                case ActivityEventKind.Conflict: summary.Conflicts = count; break;
            }
        }

        return summary;
    }

    /// <summary>Drops rows older than the retention window. Called at startup and once a day.</summary>
    public int Trim(int retentionDays)
    {
        if (retentionDays <= 0) return 0;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM activity WHERE ts < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return command.ExecuteNonQuery();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Prepares a search term for the instr() comparisons above.
    /// <para>
    /// instr() is a plain substring test, which is what a search box means. LIKE would have
    /// treated % and _ in a file name as wildcards, needing an escape character threaded through
    /// every clause to stop it.
    /// </para>
    /// </summary>
    private static string? SearchTerm(string? search)
    {
        var term = NullIfEmpty(search);
        return term?.Trim().ToLowerInvariant();
    }
}
