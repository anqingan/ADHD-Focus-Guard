using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class PersonalDataExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IPersonalDataRepository _repository;
    public PersonalDataExportService(IPersonalDataRepository repository) => _repository = repository;

    public async Task ExportAsync(string destination, CancellationToken cancellationToken = default)
    {
        var data = new ExportDocument(1, DateTimeOffset.Now, await _repository.GetGoalsAsync(true, cancellationToken), await _repository.GetActionItemsAsync(true, cancellationToken), await _repository.GetMemoriesAsync(true, cancellationToken), await _repository.GetActivitySegmentsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, cancellationToken), await _repository.GetClassificationRulesAsync(cancellationToken), await _repository.GetReportsAsync(cancellationToken));
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                await WriteAsync(archive, "vigil-data.json", JsonSerializer.Serialize(data, JsonOptions), cancellationToken);
                var csv = new StringBuilder("started_at,ended_at,duration_seconds,category,source,application,domain,display_name\r\n");
                foreach (var a in data.Activities) csv.AppendLine(string.Join(',', Csv(a.StartedAt.ToString("O")), Csv(a.EndedAt.ToString("O")), a.DurationSeconds, Csv(a.Category.ToString()), Csv(a.Source.ToString()), Csv(a.Application), Csv(a.Domain), Csv(a.DisplayName)));
                await WriteAsync(archive, "activities.csv", csv.ToString(), cancellationToken);
                var reports = new StringBuilder("# Vigil 报告导出\n\n"); foreach (var r in data.Reports) { reports.AppendLine($"## {r.Period} {r.PeriodStart:yyyy-MM-dd} v{r.Version}\n\n### 事实\n\n{r.FactsText}\n\n### AI 推断\n\n{r.InferenceText}\n\n### 建议\n\n{r.SuggestionsText}\n"); }
                await WriteAsync(archive, "reports.md", reports.ToString(), cancellationToken);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task ImportAsync(string source, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(source); var entry = archive.GetEntry("vigil-data.json") ?? throw new InvalidDataException("备份中缺少 vigil-data.json。");
        await using var stream = entry.Open(); var data = await JsonSerializer.DeserializeAsync<ExportDocument>(stream, JsonOptions, cancellationToken) ?? throw new InvalidDataException("备份数据为空。");
        if (data.Version != 1) throw new InvalidDataException("不支持此备份版本。");
        foreach (var goal in data.Goals) await _repository.SaveGoalAsync(goal, "imported", cancellationToken);
        foreach (var item in data.Actions) await _repository.SaveActionItemAsync(item, cancellationToken);
        foreach (var memory in data.Memories) await _repository.SaveMemoryAsync(memory, cancellationToken);
        foreach (var activity in data.Activities) await _repository.SaveActivitySegmentAsync(activity, cancellationToken);
        foreach (var rule in data.Rules) await _repository.SaveClassificationRuleAsync(rule, cancellationToken);
        foreach (var report in data.Reports) await _repository.SaveReportAsync(report, cancellationToken);
    }

    private static async Task WriteAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken) { var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize); await using var stream = entry.Open(); await using var writer = new StreamWriter(stream, new UTF8Encoding(false)); await writer.WriteAsync(content.AsMemory(), cancellationToken); }
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private sealed record ExportDocument(int Version, DateTimeOffset ExportedAt, IReadOnlyList<GoalRecord> Goals, IReadOnlyList<ActionItemRecord> Actions, IReadOnlyList<MemoryRecord> Memories, IReadOnlyList<ActivitySegment> Activities, IReadOnlyList<ClassificationRule> Rules, IReadOnlyList<ReportRecord> Reports);
}
