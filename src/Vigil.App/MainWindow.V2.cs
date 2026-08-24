using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Vigil.Core;
using Vigil.Infrastructure;
using MessageBox = System.Windows.MessageBox;

namespace Vigil.App;

public partial class MainWindow
{
    private IPersonalDataRepository _personal = null!;
    private ActivityTrackingService _tracker = null!;
    private IPersonalAiService _personalAi = null!;
    private PersonalDataExportService _export = null!;
    private AutomaticVisualMonitor _visualMonitor = null!;
    private readonly ObservableCollection<GoalRow> _goals = [];
    private readonly ObservableCollection<ActionItemRow> _actions = [];
    private readonly ObservableCollection<ActionDraftView> _actionDrafts = [];
    private readonly ObservableCollection<MemoryRow> _memories = [];
    private readonly ObservableCollection<ActivityRow> _activities = [];
    private readonly ObservableCollection<ReportRow> _reports = [];
    private readonly CancellationTokenSource _v2Lifetime = new();
    private MemoryRecord? _editingMemory;
    private GoalRecord? _editingGoal;

    private void InitializeV2(IPersonalDataRepository personal, ActivityTrackingService tracker, IPersonalAiService personalAi, AutomaticVisualMonitor visualMonitor)
    {
        _personal = personal;
        _tracker = tracker;
        _personalAi = personalAi;
        _export = new PersonalDataExportService(personal);
        _visualMonitor = visualMonitor;
        GoalsList.ItemsSource = _goals;
        GoalRelationInput.ItemsSource = _goals;
        ActionsList.ItemsSource = _actions;
        ActionDraftsList.ItemsSource = _actionDrafts;
        MemoriesList.ItemsSource = _memories;
        ActivitiesList.ItemsSource = _activities;
        ReportsList.ItemsSource = _reports;
        _tracker.StatusChanged += Tracker_StatusChanged;
        _tracker.ActiveModeChanged += Tracker_ActiveModeChanged;
    }

    private async Task LoadV2Async()
    {
        await Task.WhenAll(ReloadGoalsAsync(), ReloadActionsAsync(), ReloadMemoriesAsync(), ReloadActivityAsync(), ReloadReportsAsync());
        TrackerStatusText.Text = _tracker.StatusText;
        _ = MaybePromptDailyPlanAsync();
    }

    private void DisposeV2Events()
    {
        _tracker.StatusChanged -= Tracker_StatusChanged;
        _tracker.ActiveModeChanged -= Tracker_ActiveModeChanged;
        _v2Lifetime.Cancel();
        _v2Lifetime.Dispose();
    }

    private async void AddGoal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var title = NewGoalTitle.Text.Trim();
            if (title.Length == 0) throw new ArgumentException("请填写目标标题。");
            var horizonText = (GoalHorizonInput.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (!Enum.TryParse<GoalHorizon>(horizonText, out var horizon)) horizon = GoalHorizon.Today;
            DateTimeOffset? due = null;
            if (!string.IsNullOrWhiteSpace(NewGoalDue.Text))
            {
                if (!DateOnly.TryParseExact(NewGoalDue.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new ArgumentException("截止日期格式应为 yyyy-MM-dd。");
                due = AtLocalTime(date, new TimeOnly(23, 59));
            }
            var now = DateTimeOffset.Now;
            var related = GoalRelationInput.SelectedItem is GoalRow relatedRow ? (IReadOnlyList<Guid>)[relatedRow.Value.Id] : _editingGoal?.RelatedGoalIds ?? [];
            var goal = _editingGoal is null ? new GoalRecord { Id = Guid.NewGuid(), Horizon = horizon, Title = title, ExpectedOutcome = NewGoalOutcome.Text.Trim(), Status = GoalStatus.NotStarted, Priority = 2, DueAt = due, CreatedAt = now, UpdatedAt = now, RelatedGoalIds = related } : _editingGoal with { Horizon = horizon, Title = title, ExpectedOutcome = NewGoalOutcome.Text.Trim(), DueAt = due, UpdatedAt = now, RelatedGoalIds = related };
            await _personal.SaveGoalAsync(goal, _editingGoal is null ? "created" : "edited"); _editingGoal = null;
            if (horizon == GoalHorizon.Today) await _personal.SaveDailyPlanStateAsync(new(CurrentActivityDate(), true, null, now));
            NewGoalTitle.Clear(); NewGoalOutcome.Clear(); NewGoalDue.Clear(); GoalRelationInput.SelectedItem = null;
            await ReloadGoalsAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "保存目标失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void EditGoal_Click(object sender, RoutedEventArgs e)
    {
        if (GoalsList.SelectedItem is not GoalRow row) return; _editingGoal = row.Value; NewGoalTitle.Text = row.Value.Title; NewGoalOutcome.Text = row.Value.ExpectedOutcome; NewGoalDue.Text = row.Value.DueAt?.ToString("yyyy-MM-dd") ?? "";
        foreach (ComboBoxItem item in GoalHorizonInput.Items) if (item.Tag?.ToString() == row.Value.Horizon.ToString()) { GoalHorizonInput.SelectedItem = item; break; }
        GoalRelationInput.SelectedItem = _goals.FirstOrDefault(g => row.Value.RelatedGoalIds.Contains(g.Value.Id)); NewGoalTitle.Focus();
    }

    private async void SetGoalStatus_Click(object sender, RoutedEventArgs e)
    {
        if (GoalsList.SelectedItem is not GoalRow row || sender is not System.Windows.Controls.Button { Tag: string tag } || !Enum.TryParse<GoalStatus>(tag, out var status)) return;
        var updated = row.Value with { Status = status, UpdatedAt = DateTimeOffset.Now };
        await _personal.SaveGoalAsync(updated, "status:" + status);
        await ReloadGoalsAsync();
    }

    private async Task ReloadGoalsAsync()
    {
        var goals = await _personal.GetGoalsAsync();
        await Dispatcher.InvokeAsync(() => { _goals.Clear(); foreach (var goal in goals) _goals.Add(new(goal)); });
    }

    private async void OrganizeActions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ActionStatusText.Text = "AI 正在整理…";
            var goals = await _personal.GetGoalsAsync(false);
            var drafts = await _personalAi.OrganizeActionsAsync(ActionInboxInput.Text, goals);
            _actionDrafts.Clear(); foreach (var draft in drafts) _actionDrafts.Add(new(draft));
            ConfirmDraftsButton.Visibility = _actionDrafts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ActionStatusText.Text = $"得到 {_actionDrafts.Count} 条候选，请检查后确认。";
        }
        catch (Exception ex) { ActionStatusText.Text = "整理失败：" + ex.Message; }
    }

    private void DeleteActionDraft_Click(object sender, RoutedEventArgs e)
    {
        if (ActionDraftsList.SelectedItem is ActionDraftView draft) _actionDrafts.Remove(draft);
        ConfirmDraftsButton.Visibility = _actionDrafts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ConfirmActionDrafts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var source = ActionInboxInput.Text.Trim(); var now = DateTimeOffset.Now;
            foreach (var draft in _actionDrafts)
            {
                if (string.IsNullOrWhiteSpace(draft.Title)) continue;
                await _personal.SaveActionItemAsync(new ActionItemRecord { Id = Guid.NewGuid(), Title = draft.Title.Trim(), ExpectedOutcome = draft.ExpectedOutcome.Trim(), Status = ActionItemStatus.Pending, Priority = Math.Clamp(draft.Priority, 1, 3), EstimatedMinutes = draft.EstimatedMinutes, DueAt = draft.ParseDue(), CreatedAt = now, UpdatedAt = now, SourceText = source, RelatedGoalIds = draft.RelatedGoalIds });
            }
            _actionDrafts.Clear(); ActionInboxInput.Clear(); ConfirmDraftsButton.Visibility = Visibility.Collapsed; ActionStatusText.Text = "事务已保存。"; await ReloadActionsAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "保存事务失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task ReloadActionsAsync() { var values = await _personal.GetActionItemsAsync(); await Dispatcher.InvokeAsync(() => { _actions.Clear(); foreach (var value in values) _actions.Add(new(value)); }); }
    private async void CompleteAction_Click(object sender, RoutedEventArgs e) { if (ActionsList.SelectedItem is not ActionItemRow row) return; await _personal.SaveActionItemAsync(row.Value with { Status = ActionItemStatus.Completed, UpdatedAt = DateTimeOffset.Now }); await ReloadActionsAsync(); }
    private async void DeleteAction_Click(object sender, RoutedEventArgs e) { if (ActionsList.SelectedItem is not ActionItemRow row) return; await _personal.DeleteActionItemAsync(row.Value.Id); await ReloadActionsAsync(); }

    private async void AddMemory_Click(object sender, RoutedEventArgs e)
    {
        var text = MemoryInput.Text.Trim(); if (text.Length == 0) return; var now = DateTimeOffset.Now;
        var memory = _editingMemory is null ? new MemoryRecord { Id = Guid.NewGuid(), Text = text, Author = MemoryAuthor.User, Status = MemoryStatus.Confirmed, CreatedAt = now, UpdatedAt = now, Tags = MemoryTagsInput.Text.Trim() } : _editingMemory with { Text = text, Tags = MemoryTagsInput.Text.Trim(), UpdatedAt = now };
        await _personal.SaveMemoryAsync(memory); _editingMemory = null; MemoryInput.Clear(); MemoryTagsInput.Clear(); await ReloadMemoriesAsync();
    }

    private async Task ReloadMemoriesAsync() { var values = await _personal.GetMemoriesAsync(); await Dispatcher.InvokeAsync(() => { _memories.Clear(); foreach (var value in values) _memories.Add(new(value)); }); }
    private void EditMemory_Click(object sender, RoutedEventArgs e) { if (MemoriesList.SelectedItem is not MemoryRow row) return; _editingMemory = row.Value; MemoryInput.Text = row.Value.Text; MemoryTagsInput.Text = row.Value.Tags; MemoryInput.Focus(); }
    private async void SearchMemories_Click(object sender, RoutedEventArgs e) { var query = MemorySearchInput.Text.Trim(); var values = await _personal.GetMemoriesAsync(); if (query.Length > 0) values = values.Where(m => m.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Tags.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList(); _memories.Clear(); foreach (var value in values) _memories.Add(new(value)); }
    private async void ClearMemorySearch_Click(object sender, RoutedEventArgs e) { MemorySearchInput.Clear(); await ReloadMemoriesAsync(); }
    private async void ConfirmMemory_Click(object sender, RoutedEventArgs e) { if (MemoriesList.SelectedItem is not MemoryRow row || row.Value.Author != MemoryAuthor.Ai) return; await _personal.SaveMemoryAsync(row.Value with { Status = MemoryStatus.Confirmed, UpdatedAt = DateTimeOffset.Now }); await ReloadMemoriesAsync(); }
    private async void PinMemory_Click(object sender, RoutedEventArgs e) { if (MemoriesList.SelectedItem is not MemoryRow row) return; await _personal.SaveMemoryAsync(row.Value with { IsPinned = !row.Value.IsPinned, UpdatedAt = DateTimeOffset.Now }); await ReloadMemoriesAsync(); }
    private async void ArchiveMemory_Click(object sender, RoutedEventArgs e) { if (MemoriesList.SelectedItem is not MemoryRow row) return; await _personal.SaveMemoryAsync(row.Value with { Status = MemoryStatus.Archived, UpdatedAt = DateTimeOffset.Now }); await ReloadMemoriesAsync(); }
    private async void DeleteMemory_Click(object sender, RoutedEventArgs e) { if (MemoriesList.SelectedItem is not MemoryRow row) return; await _personal.DeleteMemoryAsync(row.Value.Id); await ReloadMemoriesAsync(); }

    private async void RefreshActivity_Click(object sender, RoutedEventArgs e) => await ReloadActivityAsync();
    private async void DeleteTodayActivity_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show("确定删除当前活动日的全部活动段？报告不会自动删除，可重新生成新版本。", "删除今日活动", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; var (start, end) = CurrentActivityDay(); await _personal.DeleteActivityRangeAsync(start, end); await ReloadActivityAsync(); }
    private async Task ReloadActivityAsync()
    {
        var (start, end) = CurrentActivityDay(); var segments = await _personal.GetActivitySegmentsAsync(start, end); var totals = await _personal.GetActivityTotalsAsync(start, end);
        await Dispatcher.InvokeAsync(() => { _activities.Clear(); foreach (var segment in segments.OrderByDescending(s => s.StartedAt)) _activities.Add(new(segment)); TodayTotalsText.Text = $"学习与工作 {Minutes(totals.WorkAndStudySeconds)} · 娱乐 {Minutes(totals.EntertainmentSeconds)} · 其它 {Minutes(totals.OtherSeconds)} · 共 {Minutes(totals.ObservedSeconds)}"; var total = Math.Max(1, totals.ObservedSeconds); WorkProgress.Value = totals.WorkAndStudySeconds * 100d / total; EntertainmentProgress.Value = totals.EntertainmentSeconds * 100d / total; OtherProgress.Value = totals.OtherSeconds * 100d / total; });
    }

    private async void AddManualActivity_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = ManualActivityName.Text.Trim(); if (name.Length == 0) throw new ArgumentException("请填写线下活动名称。");
            if (!int.TryParse(ManualActivityMinutes.Text.Trim(), out var minutes) || minutes < 1 || minutes > 1440) throw new ArgumentException("补录分钟数必须为 1–1440。");
            var tag = (ManualActivityCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString(); if (!Enum.TryParse<ActivityCategory>(tag, out var category)) category = ActivityCategory.Other;
            var end = DateTimeOffset.Now; var start = end.AddMinutes(-minutes);
            var overlaps = await _personal.GetActivitySegmentsAsync(start, end);
            if (overlaps.Count > 0)
            {
                var choice = MessageBox.Show($"补录时段与 {overlaps.Count} 条自动记录重叠。\n\n选择“是”：删除重叠记录并用补录覆盖。\n选择“否”：只填补没有记录的空白。\n选择“取消”：不保存。", "补录时间冲突", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes) { foreach (var overlap in overlaps) await _personal.DeleteActivitySegmentAsync(overlap.Id); await SaveManualRangeAsync(name, category, start, end); }
                else
                {
                    var cursor = start; foreach (var overlap in overlaps.OrderBy(o => o.StartedAt)) { if (overlap.StartedAt > cursor) await SaveManualRangeAsync(name, category, cursor, overlap.StartedAt < end ? overlap.StartedAt : end); if (overlap.EndedAt > cursor) cursor = overlap.EndedAt; if (cursor >= end) break; }
                    if (cursor < end) await SaveManualRangeAsync(name, category, cursor, end);
                }
            }
            else await SaveManualRangeAsync(name, category, start, end);
            ManualActivityName.Clear(); ManualActivityMinutes.Clear(); await ReloadActivityAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "补录失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private Task SaveManualRangeAsync(string name, ActivityCategory category, DateTimeOffset start, DateTimeOffset end) => end <= start ? Task.CompletedTask : _personal.SaveActivitySegmentAsync(new ActivitySegment { Id = Guid.NewGuid(), StartedAt = start, EndedAt = end, Application = "线下", DisplayName = name, Category = category, Source = ActivitySource.User, ClassificationSource = ClassificationSource.User, Confidence = 1 });

    private async void CorrectActivity_Click(object sender, RoutedEventArgs e)
    {
        if (ActivitiesList.SelectedItem is not ActivityRow row || sender is not System.Windows.Controls.Button { Tag: string categoryText } || !Enum.TryParse<ActivityCategory>(categoryText, out var category)) return;
        var scopeText = (CorrectionScopeInput.SelectedItem as ComboBoxItem)?.Tag?.ToString(); if (!Enum.TryParse<RuleScope>(scopeText, out var scope)) scope = RuleScope.Similar;
        var segment = row.Value; await _personal.SaveActivitySegmentAsync(segment with { Category = category, ClassificationSource = ClassificationSource.User, Confidence = 1 });
        if (scope != RuleScope.Exact)
        {
            var rule = new ClassificationRule { Id = Guid.NewGuid(), Scope = scope, Application = segment.Application, Domain = segment.Domain, TitleKeywords = scope == RuleScope.Similar ? segment.DisplayName : "", Category = category, CreatedAt = DateTimeOffset.Now, LastMatchedAt = DateTimeOffset.Now }; await _personal.SaveClassificationRuleAsync(rule);
            if (CorrectHistoryCheck.IsChecked == true)
            {
                var all = await _personal.GetActivitySegmentsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
                foreach (var old in all.Where(a => a.Id != segment.Id && a.Application.Equals(segment.Application, StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(segment.Domain) || a.Domain.Equals(segment.Domain, StringComparison.OrdinalIgnoreCase)) && (scope == RuleScope.ApplicationOrDomain || a.DisplayName.Contains(segment.DisplayName, StringComparison.OrdinalIgnoreCase))))
                    await _personal.SaveActivitySegmentAsync(old with { Category = category, ClassificationSource = ClassificationSource.UserRule, Confidence = 1 });
            }
        }
        await ReloadActivityAsync();
    }

    private async void GenerateDailyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReportStatusText.Text = "正在生成…"; var (start, end) = CurrentActivityDay(); var totals = await _personal.GetActivityTotalsAsync(start, end); var goals = await _personal.GetGoalsAsync(false);
            var facts = $"统计范围：{start.LocalDateTime:yyyy-MM-dd HH:mm} 至 {end.LocalDateTime:yyyy-MM-dd HH:mm}\n学习与工作：{Minutes(totals.WorkAndStudySeconds)}\n娱乐：{Minutes(totals.EntertainmentSeconds)}\n其它：{Minutes(totals.OtherSeconds)}\n可观察总时长：{Minutes(totals.ObservedSeconds)}";
            string inference = "AI 分析暂不可用。", suggestions = "可先依据上面的确定性统计检查时间分配。";
            try { (inference, suggestions) = await _personalAi.GenerateReportNarrativeAsync(ReportPeriod.Daily, facts, goals); } catch (Exception ex) { ReportStatusText.Text = "已保存本地统计版：" + ex.Message; }
            var previous = (await _personal.GetReportsAsync()).Where(r => r.Period == ReportPeriod.Daily && r.PeriodStart == start).Select(r => r.Version).DefaultIfEmpty(0).Max();
            var elapsed = Math.Max(1, (int)(DateTimeOffset.Now - start).TotalSeconds); var coverage = Math.Clamp((double)totals.ObservedSeconds / elapsed, 0, 1);
            await _personal.SaveReportAsync(new ReportRecord { Id = Guid.NewGuid(), Period = ReportPeriod.Daily, PeriodStart = start, PeriodEnd = end, Version = previous + 1, CreatedAt = DateTimeOffset.Now, FactsText = facts, InferenceText = inference, SuggestionsText = suggestions, GoalSnapshotJson = JsonSerializer.Serialize(goals), Coverage = coverage });
            ReportStatusText.Text = "日报已保存。"; await ReloadReportsAsync();
        }
        catch (Exception ex) { ReportStatusText.Text = "生成失败：" + ex.Message; }
    }

    private async Task ReloadReportsAsync() { var values = await _personal.GetReportsAsync(); await Dispatcher.InvokeAsync(() => { _reports.Clear(); foreach (var value in values) _reports.Add(new(value)); }); }
    private void ReportsList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ReportsList.SelectedItem is not ReportRow row) return; ReportFactsText.Text = "事实\n" + row.Value.FactsText; ReportInferenceText.Text = "AI 推断\n" + row.Value.InferenceText; ReportSuggestionsText.Text = "建议\n" + row.Value.SuggestionsText; }

    private void Tracker_StatusChanged(object? sender, string status) { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(() => TrackerStatusText.Text = status); }
    private void Tracker_ActiveModeChanged(object? sender, bool active) { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(() => TrackerStatusText.Text = (active ? "主动识别中 · " : "被动记录中 · ") + _tracker.StatusText); }

    private async Task MaybePromptDailyPlanAsync()
    {
        try { while (!_tracker.HasBeenPresentFor(TimeSpan.FromMinutes(2))) await Task.Delay(TimeSpan.FromSeconds(10), _v2Lifetime.Token); } catch (OperationCanceledException) { return; }
        if (Dispatcher.HasShutdownStarted) return; var date = CurrentActivityDate(); var state = await _personal.GetDailyPlanStateAsync(date);
        if (state?.CompletedAt is not null || state?.SnoozedUntil > DateTimeOffset.Now) return;
        if (state?.HasBeenPrompted == true && state.SnoozedUntil is null) return;
        var suggestion = ""; try { suggestion = await _personalAi.SuggestDailyPlanAsync(await _personal.GetGoalsAsync(false), await _personal.GetActionItemsAsync(false), _v2Lifetime.Token); } catch (Exception) { }
        await Dispatcher.InvokeAsync(async () =>
        {
            var dialog = new DailyPlanWindow { Owner = this }; dialog.SetSuggestion(suggestion); var saved = dialog.ShowDialog() == true; var now = DateTimeOffset.Now;
            if (saved)
            {
                try
                {
                    var activeGoals = await _personal.GetGoalsAsync(false);
                    var drafts = await _personalAi.AnalyzeDailyGoalsAsync(dialog.Goal, activeGoals, _v2Lifetime.Token);
                    var names = activeGoals.ToDictionary(goal => goal.Id, goal => goal.Title);
                    var preview = string.Join("\n\n", drafts.Select((draft, index) =>
                    {
                        var parents = draft.RelatedGoalIds.Where(names.ContainsKey).Select(id => names[id]).ToArray();
                        return $"{index + 1}. [{draft.Classification}] {draft.Title}\n完成标准：{draft.ExpectedOutcome}\n关联：{(parents.Length == 0 ? "未关联上级目标" : string.Join("、", parents))}\n依据：{draft.Reasoning}";
                    }));
                    if (MessageBox.Show(preview + "\n\n确认按以上分析保存今日目标吗？", "确认 AI 整理结果", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        await _personal.SaveDailyPlanStateAsync(new(date, true, now.AddMinutes(10), null));
                        return;
                    }
                    foreach (var draft in drafts)
                    {
                        await _personal.SaveGoalAsync(new GoalRecord
                        {
                            Id = Guid.NewGuid(),
                            Horizon = GoalHorizon.Today,
                            Title = draft.Title,
                            ExpectedOutcome = draft.ExpectedOutcome,
                            Status = GoalStatus.NotStarted,
                            Priority = draft.Priority,
                            EstimatedMinutes = draft.EstimatedMinutes,
                            CreatedAt = now,
                            UpdatedAt = now,
                            RelatedGoalIds = draft.RelatedGoalIds
                        }, "daily-plan-ai-classified");
                    }
                    await _personal.SaveDailyPlanStateAsync(new(date, true, null, now)); await ReloadGoalsAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("AI 暂时无法整理今日目标：" + ex.Message + "\n\n本次没有保存，请稍后重试。", "今日目标", MessageBoxButton.OK, MessageBoxImage.Warning);
                    await _personal.SaveDailyPlanStateAsync(new(date, true, now.AddMinutes(10), null));
                }
            }
            else if (dialog.SnoozeMinutes is int minutes)
            {
                await _personal.SaveDailyPlanStateAsync(new(date, true, now.AddMinutes(minutes), null));
                _ = Task.Run(async () => { try { await Task.Delay(TimeSpan.FromMinutes(minutes), _v2Lifetime.Token); await MaybePromptDailyPlanAsync(); } catch (OperationCanceledException) { } }, _v2Lifetime.Token);
            }
            else await _personal.SaveDailyPlanStateAsync(new(date, true, null, null));
        }).Task.Unwrap();
    }

    private async void ExportData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Vigil 备份 (*.zip)|*.zip", FileName = $"Vigil-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip" }; if (dialog.ShowDialog() != true) return;
        try { BackupStatusText.Text = "正在导出…"; await _export.ExportAsync(dialog.FileName); BackupStatusText.Text = "导出完成。请注意：导出 ZIP 本身未加密。"; } catch (Exception ex) { BackupStatusText.Text = "导出失败：" + ex.Message; }
    }

    private async void ImportData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Vigil 备份 (*.zip)|*.zip" }; if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show("恢复会合并同 ID 数据。确定继续？", "恢复备份", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { BackupStatusText.Text = "正在恢复…"; await _export.ImportAsync(dialog.FileName); await LoadV2Async(); BackupStatusText.Text = "恢复完成。"; } catch (Exception ex) { BackupStatusText.Text = "恢复失败：" + ex.Message; }
    }

    private async void ClearPersonalData_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("这会清空全部目标、事务、记忆、活动、规则和报告，且无法撤销。专注会话历史不受影响。确定继续？", "清空个人数据", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _personal.DeleteAllPersonalDataAsync(); await LoadV2Async(); BackupStatusText.Text = "个人数据已清空。";
    }

    private static DateOnly CurrentActivityDate() { var now = DateTime.Now; return DateOnly.FromDateTime(now.Hour < 8 ? now.AddDays(-1) : now); }
    private static (DateTimeOffset Start, DateTimeOffset End) CurrentActivityDay() { var date = CurrentActivityDate(); var start = AtLocalTime(date, new TimeOnly(8, 0)); return (start, start.AddDays(1)); }
    private static DateTimeOffset AtLocalTime(DateOnly date, TimeOnly time) { var local = date.ToDateTime(time, DateTimeKind.Unspecified); return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)); }
    private static string Minutes(int seconds) => $"{seconds / 60.0:0.#} 分钟";

    private sealed class GoalRow { public GoalRow(GoalRecord value) => Value = value; public GoalRecord Value { get; } public override string ToString() { var value = Value; var prefix = value.Status switch { GoalStatus.Completed => "✓ ", GoalStatus.Paused => "⏸ ", GoalStatus.Abandoned => "× ", _ => "" }; return $"{prefix}[{HorizonName(value.Horizon)}] {value.Title}\n{StatusName(value.Status)}{(value.DueAt is null ? "" : $" · 截止 {value.DueAt:yyyy-MM-dd}")}"; } }
    private sealed class ActionItemRow { public ActionItemRow(ActionItemRecord value) => Value = value; public ActionItemRecord Value { get; } public override string ToString() { var value = Value; return $"[{ActionStatusName(value.Status)}] {value.Title}{(value.DueAt is null ? "" : $" · {value.DueAt:MM-dd}")}"; } }
    private sealed class MemoryRow { public MemoryRow(MemoryRecord value) => Value = value; public MemoryRecord Value { get; } public override string ToString() { var value = Value; return $"{(value.IsPinned ? "📌 " : "")}{(value.Author == MemoryAuthor.Ai ? "AI 推断" : "用户记录")}{(value.Status == MemoryStatus.PendingReview ? " · 待确认" : "")}\n{value.Text}"; } }
    private sealed class ActivityRow { public ActivityRow(ActivitySegment value) => Value = value; public ActivitySegment Value { get; } public override string ToString() { var value = Value; return $"{value.StartedAt.LocalDateTime:HH:mm}–{value.EndedAt.LocalDateTime:HH:mm} · {CategoryName(value.Category)} · {value.DurationSeconds / 60.0:0.#} 分钟\n{value.DisplayName}"; } }
    private sealed class ReportRow { public ReportRow(ReportRecord value) => Value = value; public ReportRecord Value { get; } public override string ToString() { var value = Value; return $"{PeriodName(value.Period)} · {value.PeriodStart.LocalDateTime:yyyy-MM-dd} · v{value.Version}\n覆盖率 {value.Coverage:P0}"; } }
    private sealed class ActionDraftView(ActionDraft draft) { public string Title { get; set; } = draft.Title; public string ExpectedOutcome { get; set; } = draft.ExpectedOutcome; public string DueText { get; set; } = draft.DueAt?.ToString("yyyy-MM-dd") ?? ""; public int Priority { get; set; } = draft.Priority; public int? EstimatedMinutes { get; set; } = draft.EstimatedMinutes; public IReadOnlyList<Guid> RelatedGoalIds { get; } = draft.RelatedGoalIds; public DateTimeOffset? ParseDue() => DateOnly.TryParseExact(DueText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? AtLocalTime(date, new TimeOnly(23, 59)) : null; }
    private static string HorizonName(GoalHorizon h) => h switch { GoalHorizon.Direction => "长期", GoalHorizon.Stage => "阶段", GoalHorizon.Week => "本周", _ => "今日" };
    private static string StatusName(GoalStatus s) => s switch { GoalStatus.NotStarted => "未开始", GoalStatus.InProgress => "进行中", GoalStatus.Completed => "已完成", GoalStatus.Paused => "暂停", GoalStatus.Abandoned => "放弃", _ => "归档" };
    private static string ActionStatusName(ActionItemStatus s) => s switch { ActionItemStatus.Pending => "待处理", ActionItemStatus.InProgress => "进行中", ActionItemStatus.Completed => "已完成", ActionItemStatus.Paused => "暂停", _ => "放弃" };
    private static string CategoryName(ActivityCategory c) => c switch { ActivityCategory.WorkAndStudy => "学习与工作", ActivityCategory.Entertainment => "娱乐", _ => "其它" };
    private static string PeriodName(ReportPeriod p) => p switch { ReportPeriod.Daily => "日报", ReportPeriod.Weekly => "周报", _ => "月报" };
}
