using Microsoft.Data.Sqlite;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class SqliteSessionRepository : ISessionRepository
{
    private static int _providerInitialized;
    private readonly string _connectionString;

    public SqliteSessionRepository(string? databaseFile = null)
    {
        if (Interlocked.Exchange(ref _providerInitialized, 1) == 0)
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        }
        AppPaths.EnsureCreated();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile ?? AppPaths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            // Sessions are small and writes are infrequent. Disabling pooling also
            // guarantees that the database file is released after every operation.
            Pooling = false
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                goal TEXT NOT NULL,
                planned_seconds INTEGER NOT NULL,
                actual_seconds INTEGER NOT NULL,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT NULL,
                completion_kind TEXT NOT NULL,
                focused_seconds INTEGER NOT NULL,
                wandering_seconds INTEGER NOT NULL,
                distracted_seconds INTEGER NOT NULL,
                away_seconds INTEGER NOT NULL,
                unknown_seconds INTEGER NOT NULL,
                summary_text TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_started_at ON sessions(started_at_utc DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkRunningSessionsInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions
            SET completion_kind = 'Interrupted',
                ended_at_utc = COALESCE(ended_at_utc, $now),
                summary_text = CASE WHEN summary_text = '' THEN '应用意外退出，本轮会话未生成复盘。' ELSE summary_text END
            WHERE completion_kind = 'Running';
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateAsync(SessionSummary session, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteUpsertAsync(connection, session, cancellationToken);
    }

    public async Task UpdateAsync(SessionSummary session, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteUpsertAsync(connection, session, cancellationToken);
    }

    public async Task<IReadOnlyList<SessionSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sessions ORDER BY started_at_utc DESC;";
        var result = new List<SessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SessionSummary
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Goal = reader.GetString(reader.GetOrdinal("goal")),
                PlannedSeconds = reader.GetInt32(reader.GetOrdinal("planned_seconds")),
                ActualSeconds = reader.GetInt32(reader.GetOrdinal("actual_seconds")),
                StartedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at_utc"))),
                EndedAtUtc = reader.IsDBNull(reader.GetOrdinal("ended_at_utc"))
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("ended_at_utc"))),
                CompletionKind = Enum.TryParse<SessionCompletionKind>(
                    reader.GetString(reader.GetOrdinal("completion_kind")), true, out var kind)
                    ? kind
                    : SessionCompletionKind.Interrupted,
                FocusedSeconds = reader.GetInt32(reader.GetOrdinal("focused_seconds")),
                WanderingSeconds = reader.GetInt32(reader.GetOrdinal("wandering_seconds")),
                DistractedSeconds = reader.GetInt32(reader.GetOrdinal("distracted_seconds")),
                AwaySeconds = reader.GetInt32(reader.GetOrdinal("away_seconds")),
                UnknownSeconds = reader.GetInt32(reader.GetOrdinal("unknown_seconds")),
                SummaryText = reader.GetString(reader.GetOrdinal("summary_text"))
            });
        }
        return result;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteUpsertAsync(
        SqliteConnection connection,
        SessionSummary session,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (
                id, goal, planned_seconds, actual_seconds, started_at_utc, ended_at_utc,
                completion_kind, focused_seconds, wandering_seconds, distracted_seconds,
                away_seconds, unknown_seconds, summary_text)
            VALUES (
                $id, $goal, $planned, $actual, $started, $ended,
                $kind, $focused, $wandering, $distracted, $away, $unknown, $summary)
            ON CONFLICT(id) DO UPDATE SET
                goal = excluded.goal,
                planned_seconds = excluded.planned_seconds,
                actual_seconds = excluded.actual_seconds,
                ended_at_utc = excluded.ended_at_utc,
                completion_kind = excluded.completion_kind,
                focused_seconds = excluded.focused_seconds,
                wandering_seconds = excluded.wandering_seconds,
                distracted_seconds = excluded.distracted_seconds,
                away_seconds = excluded.away_seconds,
                unknown_seconds = excluded.unknown_seconds,
                summary_text = excluded.summary_text;
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString());
        command.Parameters.AddWithValue("$goal", session.Goal);
        command.Parameters.AddWithValue("$planned", session.PlannedSeconds);
        command.Parameters.AddWithValue("$actual", session.ActualSeconds);
        command.Parameters.AddWithValue("$started", session.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$ended", session.EndedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$kind", session.CompletionKind.ToString());
        command.Parameters.AddWithValue("$focused", session.FocusedSeconds);
        command.Parameters.AddWithValue("$wandering", session.WanderingSeconds);
        command.Parameters.AddWithValue("$distracted", session.DistractedSeconds);
        command.Parameters.AddWithValue("$away", session.AwaySeconds);
        command.Parameters.AddWithValue("$unknown", session.UnknownSeconds);
        command.Parameters.AddWithValue("$summary", session.SummaryText ?? "");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
