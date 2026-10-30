using Quotalis.Core.Models;
using Microsoft.Data.Sqlite;

namespace Quotalis.Core.Hermes;

/// <summary>
/// Reads a local Hermes install's <c>state.db</c> (read-only, one snapshot) into the same <see cref="HermesUsage"/>
/// the export gives. The queries match tools/hermes/hermes-usage-export.py and touch no message text, reasoning,
/// prompt, title or chat column.
/// </summary>
public static class HermesStateDb
{
    /// <summary>The local install's database: <c>HERMES_HOME</c> when set, then the first of the usual folders that has one.</summary>
    public static string DefaultPath()
    {
        if (Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } home)
        {
            return Path.Combine(home, "state.db");
        }

        var dotFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes", "state.db");
        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "state.db");
        return File.Exists(dotFolder) || !File.Exists(dataFolder) ? dotFolder : dataFolder;
    }

    /// <summary>Null when the file does not exist or has none of the expected tables.</summary>
    public static HermesUsage? Read(string path, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 10 };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);

        var rows = new List<HermesModelRow>();
        using (var command = Command(connection, transaction, """
            SELECT session_id, model, task, api_call_count, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                   reasoning_tokens, estimated_cost_usd, actual_cost_usd, last_seen
            FROM session_model_usage
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new HermesModelRow(
                    Text(reader, 0), Text(reader, 1), Text(reader, 2), (int)Number(reader, 3),
                    new TokenCounts(Number(reader, 4), Number(reader, 5), Number(reader, 7), 0, Number(reader, 6)),
                    Number(reader, 8), Money(reader, 9), Money(reader, 10), Seconds(reader, 11)));
            }
        }

        string? model = null;
        DateTimeOffset? modelSeen = null;
        using (var command = Command(connection, transaction, "SELECT model, last_seen FROM session_model_usage WHERE model IS NOT NULL ORDER BY last_seen DESC LIMIT 1"))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                model = Text(reader, 0);
                modelSeen = Seconds(reader, 1);
            }
        }

        int? schema = null;
        try
        {
            using var command = Command(connection, transaction, "SELECT MAX(version) FROM schema_version");
            schema = command.ExecuteScalar() is long v ? (int)v : null;
        }
        catch (SqliteException)
        {
            // Older or newer layouts without the table: the version is only shown, never relied on.
        }

        return new HermesUsage
        {
            GeneratedAt = now,
            SchemaVersion = schema,
            GatewayHeartbeat = Scalar(connection, transaction, "SELECT MAX(last_heartbeat) FROM gateway_heartbeats"),
            LastActivity = Scalar(connection, transaction, "SELECT MAX(last_activity_at) FROM sessions"),
            CurrentModel = model,
            CurrentModelSeen = modelSeen,
            Days = HermesUsageParser.Aggregate(rows, zone),
        };
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static DateTimeOffset? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        try
        {
            using var command = Command(connection, transaction, sql);
            return command.ExecuteScalar() switch
            {
                double d => ToTime(d),
                long l => ToTime(l),
                _ => null,
            };
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i)?.ToString();

    private static long Number(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Math.Max(0, (long)r.GetDouble(i));

    private static decimal Money(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0m : (decimal)r.GetDouble(i);

    private static DateTimeOffset? Seconds(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ToTime(r.GetDouble(i));

    private static DateTimeOffset? ToTime(double seconds) =>
        seconds is > 946684800 and < 32503680000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : null;
}
