using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class SqlitePersonalDataRepository : IPersonalDataRepository
{
    private static int _providerInitialized;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private static readonly HashSet<string> SensitiveParameters = new(StringComparer.Ordinal)
    {
        "$title", "$outcome", "$evidence", "$json", "$source", "$text", "$tags",
        "$app", "$domain", "$name", "$keywords", "$facts", "$inference", "$suggestions", "$goals"
    };
    private static readonly HashSet<string> SensitiveColumns = new(StringComparer.Ordinal)
    {
        "title", "expected_outcome", "completion_evidence", "snapshot_json", "source_text",
        "text", "tags", "source_reference", "application", "domain", "display_name",
        "title_keywords", "facts_text", "inference_text", "suggestions_text", "goal_snapshot_json"
    };

    public SqlitePersonalDataRepository(string? databaseFile = null)
    {
        if (Interlocked.Exchange(ref _providerInitialized, 1) == 0)
        {
            try { SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3()); }
            catch (InvalidOperationException) { }
        }
        AppPaths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile ?? AppPaths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS goals(
              id TEXT PRIMARY KEY, horizon TEXT NOT NULL, title TEXT NOT NULL,
              expected_outcome TEXT NOT NULL, status TEXT NOT NULL, priority INTEGER NOT NULL,
              estimated_minutes INTEGER NULL, due_at TEXT NULL, created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL, completion_evidence TEXT NOT NULL, related_goal_ids TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS goal_history(
              id TEXT PRIMARY KEY, goal_id TEXT NOT NULL, changed_at TEXT NOT NULL,
              change_kind TEXT NOT NULL, snapshot_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_goal_history_goal ON goal_history(goal_id, changed_at DESC);
            CREATE TABLE IF NOT EXISTS action_items(
              id TEXT PRIMARY KEY, title TEXT NOT NULL, expected_outcome TEXT NOT NULL,
              status TEXT NOT NULL, priority INTEGER NOT NULL, estimated_minutes INTEGER NULL,
              due_at TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
              source_text TEXT NOT NULL, related_goal_ids TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS memories(
              id TEXT PRIMARY KEY, text TEXT NOT NULL, author TEXT NOT NULL, status TEXT NOT NULL,
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL, tags TEXT NOT NULL,
              source_reference TEXT NOT NULL, related_goal_id TEXT NULL, is_pinned INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS activity_segments(
              id TEXT PRIMARY KEY, started_at TEXT NOT NULL, ended_at TEXT NOT NULL,
              application TEXT NOT NULL, domain TEXT NOT NULL, display_name TEXT NOT NULL,
              category TEXT NOT NULL, source TEXT NOT NULL, classification_source TEXT NOT NULL,
              confidence REAL NOT NULL, related_goal_id TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_activity_time ON activity_segments(started_at, ended_at);
            CREATE TABLE IF NOT EXISTS classification_rules(
              id TEXT PRIMARY KEY, scope TEXT NOT NULL, application TEXT NOT NULL,
              domain TEXT NOT NULL, title_keywords TEXT NOT NULL, category TEXT NOT NULL,
              created_at TEXT NOT NULL, last_matched_at TEXT NULL, is_enabled INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS reports(
              id TEXT PRIMARY KEY, period TEXT NOT NULL, period_start TEXT NOT NULL,
              period_end TEXT NOT NULL, version INTEGER NOT NULL, created_at TEXT NOT NULL,
              facts_text TEXT NOT NULL, inference_text TEXT NOT NULL, suggestions_text TEXT NOT NULL,
              goal_snapshot_json TEXT NOT NULL, coverage REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_reports_period ON reports(period_start DESC, version DESC);
            CREATE TABLE IF NOT EXISTS daily_plan_state(
              activity_date TEXT PRIMARY KEY, has_been_prompted INTEGER NOT NULL,
              snoozed_until TEXT NULL, completed_at TEXT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GoalRecord>> GetGoalsAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = includeInactive
            ? "SELECT * FROM goals ORDER BY horizon, priority, created_at;"
            : "SELECT * FROM goals WHERE status IN ('NotStarted','InProgress') ORDER BY horizon, priority, created_at;";
        var result = new List<GoalRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadGoal(reader));
        return result;
    }

    public async Task SaveGoalAsync(GoalRecord goal, string changeKind, CancellationToken cancellationToken = default)
    {
        ValidateText(goal.Title, 500, nameof(goal.Title));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO goals VALUES($id,$horizon,$title,$outcome,$status,$priority,$minutes,$due,$created,$updated,$evidence,$related)
                ON CONFLICT(id) DO UPDATE SET horizon=excluded.horizon,title=excluded.title,
                expected_outcome=excluded.expected_outcome,status=excluded.status,priority=excluded.priority,
                estimated_minutes=excluded.estimated_minutes,due_at=excluded.due_at,updated_at=excluded.updated_at,
                completion_evidence=excluded.completion_evidence,related_goal_ids=excluded.related_goal_ids;
                """;
            Add(command, "$id", goal.Id); Add(command, "$horizon", goal.Horizon); Add(command, "$title", goal.Title.Trim());
            Add(command, "$outcome", goal.ExpectedOutcome); Add(command, "$status", goal.Status); Add(command, "$priority", goal.Priority);
            Add(command, "$minutes", goal.EstimatedMinutes); Add(command, "$due", goal.DueAt); Add(command, "$created", goal.CreatedAt);
            Add(command, "$updated", goal.UpdatedAt); Add(command, "$evidence", goal.CompletionEvidence);
            Add(command, "$related", JsonSerializer.Serialize(goal.RelatedGoalIds, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        using (var history = connection.CreateCommand())
        {
            history.Transaction = (SqliteTransaction)transaction;
            history.CommandText = "INSERT INTO goal_history VALUES($id,$goal,$at,$kind,$json);";
            Add(history, "$id", Guid.NewGuid()); Add(history, "$goal", goal.Id); Add(history, "$at", goal.UpdatedAt);
            Add(history, "$kind", string.IsNullOrWhiteSpace(changeKind) ? "updated" : changeKind.Trim());
            Add(history, "$json", JsonSerializer.Serialize(goal, JsonOptions));
            await history.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GoalHistoryRecord>> GetGoalHistoryAsync(Guid goalId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM goal_history WHERE goal_id=$id ORDER BY changed_at DESC;";
        Add(command, "$id", goalId);
        var result = new List<GoalHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(ParseGuid(reader, "id"), ParseGuid(reader, "goal_id"), ParseTime(reader, "changed_at"), S(reader, "change_kind"), S(reader, "snapshot_json")));
        return result;
    }

    public async Task<IReadOnlyList<ActionItemRecord>> GetActionItemsAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = includeInactive ? "SELECT * FROM action_items ORDER BY status,priority,created_at DESC;"
            : "SELECT * FROM action_items WHERE status IN ('Pending','InProgress') ORDER BY priority,created_at DESC;";
        var result = new List<ActionItemRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadAction(reader));
        return result;
    }

    public async Task SaveActionItemAsync(ActionItemRecord item, CancellationToken cancellationToken = default)
    {
        ValidateText(item.Title, 500, nameof(item.Title));
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO action_items VALUES($id,$title,$outcome,$status,$priority,$minutes,$due,$created,$updated,$source,$related)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title,expected_outcome=excluded.expected_outcome,
            status=excluded.status,priority=excluded.priority,estimated_minutes=excluded.estimated_minutes,
            due_at=excluded.due_at,updated_at=excluded.updated_at,source_text=excluded.source_text,
            related_goal_ids=excluded.related_goal_ids;
            """;
        Add(command, "$id", item.Id); Add(command, "$title", item.Title.Trim()); Add(command, "$outcome", item.ExpectedOutcome);
        Add(command, "$status", item.Status); Add(command, "$priority", item.Priority); Add(command, "$minutes", item.EstimatedMinutes);
        Add(command, "$due", item.DueAt); Add(command, "$created", item.CreatedAt); Add(command, "$updated", item.UpdatedAt);
        Add(command, "$source", item.SourceText); Add(command, "$related", JsonSerializer.Serialize(item.RelatedGoalIds, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task DeleteActionItemAsync(Guid id, CancellationToken cancellationToken = default) => DeleteAsync("action_items", id, cancellationToken);

    public async Task<IReadOnlyList<MemoryRecord>> GetMemoriesAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = includeArchived ? "SELECT * FROM memories ORDER BY is_pinned DESC,created_at DESC;"
            : "SELECT * FROM memories WHERE status <> 'Archived' ORDER BY is_pinned DESC,created_at DESC;";
        var result = new List<MemoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new MemoryRecord
        {
            Id = ParseGuid(reader, "id"),
            Text = S(reader, "text"),
            Author = E<MemoryAuthor>(reader, "author"),
            Status = E<MemoryStatus>(reader, "status"),
            CreatedAt = ParseTime(reader, "created_at"),
            UpdatedAt = ParseTime(reader, "updated_at"),
            Tags = S(reader, "tags"),
            SourceReference = S(reader, "source_reference"),
            RelatedGoalId = NullableGuid(reader, "related_goal_id"),
            IsPinned = reader.GetInt64(reader.GetOrdinal("is_pinned")) != 0
        });
        return result;
    }

    public async Task SaveMemoryAsync(MemoryRecord memory, CancellationToken cancellationToken = default)
    {
        ValidateText(memory.Text, 10_000, nameof(memory.Text));
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memories VALUES($id,$text,$author,$status,$created,$updated,$tags,$source,$goal,$pinned)
            ON CONFLICT(id) DO UPDATE SET text=excluded.text,status=excluded.status,updated_at=excluded.updated_at,
            tags=excluded.tags,source_reference=excluded.source_reference,related_goal_id=excluded.related_goal_id,is_pinned=excluded.is_pinned;
            """;
        Add(command, "$id", memory.Id); Add(command, "$text", memory.Text.Trim()); Add(command, "$author", memory.Author); Add(command, "$status", memory.Status);
        Add(command, "$created", memory.CreatedAt); Add(command, "$updated", memory.UpdatedAt); Add(command, "$tags", memory.Tags);
        Add(command, "$source", memory.SourceReference); Add(command, "$goal", memory.RelatedGoalId); Add(command, "$pinned", memory.IsPinned ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task DeleteMemoryAsync(Guid id, CancellationToken cancellationToken = default) => DeleteAsync("memories", id, cancellationToken);

    public async Task SaveActivitySegmentAsync(ActivitySegment segment, CancellationToken cancellationToken = default)
    {
        if (segment.EndedAt <= segment.StartedAt) throw new ArgumentException("活动结束时间必须晚于开始时间。", nameof(segment));
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activity_segments VALUES($id,$start,$end,$app,$domain,$name,$category,$source,$classSource,$confidence,$goal)
            ON CONFLICT(id) DO UPDATE SET ended_at=excluded.ended_at,application=excluded.application,domain=excluded.domain,
            display_name=excluded.display_name,category=excluded.category,classification_source=excluded.classification_source,
            confidence=excluded.confidence,related_goal_id=excluded.related_goal_id;
            """;
        Add(command, "$id", segment.Id); Add(command, "$start", segment.StartedAt); Add(command, "$end", segment.EndedAt);
        Add(command, "$app", segment.Application); Add(command, "$domain", segment.Domain); Add(command, "$name", segment.DisplayName);
        Add(command, "$category", segment.Category); Add(command, "$source", segment.Source); Add(command, "$classSource", segment.ClassificationSource);
        Add(command, "$confidence", segment.Confidence); Add(command, "$goal", segment.RelatedGoalId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task DeleteActivitySegmentAsync(Guid id, CancellationToken cancellationToken = default) => DeleteAsync("activity_segments", id, cancellationToken);

    public async Task DeleteActivityRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default) { await using var connection = await OpenAsync(cancellationToken); using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM activity_segments WHERE ended_at>$start AND started_at<$end;"; Add(command, "$start", start); Add(command, "$end", end); await command.ExecuteNonQueryAsync(cancellationToken); }

    public async Task<IReadOnlyList<ActivitySegment>> GetActivitySegmentsAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM activity_segments WHERE ended_at>$start AND started_at<$end ORDER BY started_at;";
        Add(command, "$start", start); Add(command, "$end", end);
        var result = new List<ActivitySegment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSegment(reader));
        return result;
    }

    public async Task<ActivityTotals> GetActivityTotalsAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        var segments = await GetActivitySegmentsAsync(start, end, cancellationToken);
        var work = 0; var entertainment = 0; var other = 0;
        foreach (var segment in segments)
        {
            var seconds = segment.DurationSeconds;
            if (segment.Category == ActivityCategory.WorkAndStudy) work += seconds;
            else if (segment.Category == ActivityCategory.Entertainment) entertainment += seconds;
            else other += seconds;
        }
        return new(work, entertainment, other, work + entertainment + other);
    }

    public async Task<IReadOnlyList<ClassificationRule>> GetClassificationRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM classification_rules ORDER BY created_at DESC;";
        var result = new List<ClassificationRule>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new ClassificationRule { Id = ParseGuid(reader, "id"), Scope = E<RuleScope>(reader, "scope"), Application = S(reader, "application"), Domain = S(reader, "domain"), TitleKeywords = S(reader, "title_keywords"), Category = E<ActivityCategory>(reader, "category"), CreatedAt = ParseTime(reader, "created_at"), LastMatchedAt = NullableTime(reader, "last_matched_at"), IsEnabled = reader.GetInt64(reader.GetOrdinal("is_enabled")) != 0 });
        return result;
    }

    public async Task SaveClassificationRuleAsync(ClassificationRule rule, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO classification_rules VALUES($id,$scope,$app,$domain,$keywords,$category,$created,$matched,$enabled) ON CONFLICT(id) DO UPDATE SET scope=excluded.scope,application=excluded.application,domain=excluded.domain,title_keywords=excluded.title_keywords,category=excluded.category,last_matched_at=excluded.last_matched_at,is_enabled=excluded.is_enabled;";
        Add(command, "$id", rule.Id); Add(command, "$scope", rule.Scope); Add(command, "$app", rule.Application); Add(command, "$domain", rule.Domain); Add(command, "$keywords", rule.TitleKeywords); Add(command, "$category", rule.Category); Add(command, "$created", rule.CreatedAt); Add(command, "$matched", rule.LastMatchedAt); Add(command, "$enabled", rule.IsEnabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task DeleteClassificationRuleAsync(Guid id, CancellationToken cancellationToken = default) => DeleteAsync("classification_rules", id, cancellationToken);

    public async Task SaveReportAsync(ReportRecord report, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reports VALUES($id,$period,$start,$end,$version,$created,$facts,$inference,$suggestions,$goals,$coverage) ON CONFLICT(id) DO UPDATE SET inference_text=excluded.inference_text,suggestions_text=excluded.suggestions_text;";
        Add(command, "$id", report.Id); Add(command, "$period", report.Period); Add(command, "$start", report.PeriodStart); Add(command, "$end", report.PeriodEnd); Add(command, "$version", report.Version); Add(command, "$created", report.CreatedAt); Add(command, "$facts", report.FactsText); Add(command, "$inference", report.InferenceText); Add(command, "$suggestions", report.SuggestionsText); Add(command, "$goals", report.GoalSnapshotJson); Add(command, "$coverage", report.Coverage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReportRecord>> GetReportsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM reports ORDER BY period_start DESC,version DESC;";
        var result = new List<ReportRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new ReportRecord { Id = ParseGuid(reader, "id"), Period = E<ReportPeriod>(reader, "period"), PeriodStart = ParseTime(reader, "period_start"), PeriodEnd = ParseTime(reader, "period_end"), Version = reader.GetInt32(reader.GetOrdinal("version")), CreatedAt = ParseTime(reader, "created_at"), FactsText = S(reader, "facts_text"), InferenceText = S(reader, "inference_text"), SuggestionsText = S(reader, "suggestions_text"), GoalSnapshotJson = S(reader, "goal_snapshot_json"), Coverage = reader.GetDouble(reader.GetOrdinal("coverage")) });
        return result;
    }

    public async Task<DailyPlanState?> GetDailyPlanStateAsync(DateOnly activityDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM daily_plan_state WHERE activity_date=$date;"; Add(command, "$date", activityDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(activityDate, reader.GetInt64(reader.GetOrdinal("has_been_prompted")) != 0, NullableTime(reader, "snoozed_until"), NullableTime(reader, "completed_at"));
    }

    public async Task SaveDailyPlanStateAsync(DailyPlanState state, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO daily_plan_state VALUES($date,$prompted,$snoozed,$completed) ON CONFLICT(activity_date) DO UPDATE SET has_been_prompted=excluded.has_been_prompted,snoozed_until=excluded.snoozed_until,completed_at=excluded.completed_at;";
        Add(command, "$date", state.ActivityDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); Add(command, "$prompted", state.HasBeenPrompted ? 1 : 0); Add(command, "$snoozed", state.SnoozedUntil); Add(command, "$completed", state.CompletedAt); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAllPersonalDataAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); using var command = connection.CreateCommand(); command.CommandText = "BEGIN IMMEDIATE; DELETE FROM goal_history; DELETE FROM goals; DELETE FROM action_items; DELETE FROM memories; DELETE FROM activity_segments; DELETE FROM classification_rules; DELETE FROM reports; DELETE FROM daily_plan_state; COMMIT;"; try { await command.ExecuteNonQueryAsync(cancellationToken); } catch { using var rollback = connection.CreateCommand(); rollback.CommandText = "ROLLBACK;"; try { await rollback.ExecuteNonQueryAsync(CancellationToken.None); } catch { } throw; }
    }

    private async Task DeleteAsync(string table, Guid id, CancellationToken cancellationToken)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "action_items", "memories", "classification_rules", "activity_segments" };
        if (!allowed.Contains(table)) throw new ArgumentOutOfRangeException(nameof(table));
        await using var connection = await OpenAsync(cancellationToken); using var command = connection.CreateCommand(); command.CommandText = $"DELETE FROM {table} WHERE id=$id;"; Add(command, "$id", id); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) { var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken); using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;"; await pragma.ExecuteNonQueryAsync(cancellationToken); return connection; }
    private static GoalRecord ReadGoal(SqliteDataReader r) => new() { Id = ParseGuid(r, "id"), Horizon = E<GoalHorizon>(r, "horizon"), Title = S(r, "title"), ExpectedOutcome = S(r, "expected_outcome"), Status = E<GoalStatus>(r, "status"), Priority = r.GetInt32(r.GetOrdinal("priority")), EstimatedMinutes = NullableInt(r, "estimated_minutes"), DueAt = NullableTime(r, "due_at"), CreatedAt = ParseTime(r, "created_at"), UpdatedAt = ParseTime(r, "updated_at"), CompletionEvidence = S(r, "completion_evidence"), RelatedGoalIds = DeserializeIds(S(r, "related_goal_ids")) };
    private static ActionItemRecord ReadAction(SqliteDataReader r) => new() { Id = ParseGuid(r, "id"), Title = S(r, "title"), ExpectedOutcome = S(r, "expected_outcome"), Status = E<ActionItemStatus>(r, "status"), Priority = r.GetInt32(r.GetOrdinal("priority")), EstimatedMinutes = NullableInt(r, "estimated_minutes"), DueAt = NullableTime(r, "due_at"), CreatedAt = ParseTime(r, "created_at"), UpdatedAt = ParseTime(r, "updated_at"), SourceText = S(r, "source_text"), RelatedGoalIds = DeserializeIds(S(r, "related_goal_ids")) };
    private static ActivitySegment ReadSegment(SqliteDataReader r) => new() { Id = ParseGuid(r, "id"), StartedAt = ParseTime(r, "started_at"), EndedAt = ParseTime(r, "ended_at"), Application = S(r, "application"), Domain = S(r, "domain"), DisplayName = S(r, "display_name"), Category = E<ActivityCategory>(r, "category"), Source = E<ActivitySource>(r, "source"), ClassificationSource = E<ClassificationSource>(r, "classification_source"), Confidence = r.GetDouble(r.GetOrdinal("confidence")), RelatedGoalId = NullableGuid(r, "related_goal_id") };
    private static IReadOnlyList<Guid> DeserializeIds(string value) { try { return JsonSerializer.Deserialize<List<Guid>>(value, JsonOptions) ?? []; } catch (JsonException) { return []; } }
    private static string S(SqliteDataReader r, string name) { var value = r.GetString(r.GetOrdinal(name)); return SensitiveColumns.Contains(name) ? LocalTextProtector.Unprotect(value) : value; }
    private static Guid ParseGuid(SqliteDataReader r, string name) => Guid.Parse(S(r, name));
    private static Guid? NullableGuid(SqliteDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i)); }
    private static DateTimeOffset ParseTime(SqliteDataReader r, string name) => DateTimeOffset.Parse(S(r, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? NullableTime(SqliteDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); }
    private static int? NullableInt(SqliteDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : r.GetInt32(i); }
    private static T E<T>(SqliteDataReader r, string name) where T : struct, Enum => Enum.TryParse<T>(S(r, name), true, out var value) ? value : default;
    private static void ValidateText(string value, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException($"{name} 长度必须为 1–{max}。", name); }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value switch { null => DBNull.Value, Guid g => g.ToString(), Enum e => e.ToString(), DateTimeOffset t => t.ToString("O"), string s when SensitiveParameters.Contains(name) => LocalTextProtector.Protect(s), _ => value });
}
