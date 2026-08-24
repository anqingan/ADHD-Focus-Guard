using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Vigil.Core;
using Vigil.Infrastructure;

namespace Vigil.App;

public sealed class LocalWebServer : IAsyncDisposable
{
    private const string SessionCookie = "vigil_local_session";
    private readonly IPersonalDataRepository _repository;
    private readonly ISessionRepository _sessions;
    private readonly IAppSettingsStore _settings;
    private readonly IFocusAiClient _ai;
    private readonly IPersonalAiService _personalAi;
    private readonly IAiBudgetTracker _budget;
    private readonly ActivityTrackingService _tracker;
    private readonly AutomaticVisualMonitor _visual;
    private readonly FocusSessionCoordinator _coordinator;
    private readonly string _token;
    private WebApplication? _application;

    public LocalWebServer(
        IPersonalDataRepository repository,
        ISessionRepository sessions,
        IAppSettingsStore settings,
        IFocusAiClient ai,
        IPersonalAiService personalAi,
        IAiBudgetTracker budget,
        ActivityTrackingService tracker,
        AutomaticVisualMonitor visual,
        FocusSessionCoordinator coordinator)
    {
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _repository = repository;
        _sessions = sessions;
        _settings = settings;
        _ai = ai;
        _personalAi = personalAi;
        _budget = budget;
        _tracker = tracker;
        _visual = visual;
        _coordinator = coordinator;
    }

    public Uri? Address { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_application is not null) return;

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LocalWebServer).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = webRoot,
            Args = []
        });
        var port = ReserveFreePort();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
            var isInputError = error is Microsoft.AspNetCore.Http.BadHttpRequestException or ArgumentException or InvalidDataException;
            context.Response.StatusCode = isInputError ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { message = isInputError ? error!.Message : "本地服务处理失败，请稍后重试。" });
        }));
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";

            if (context.Request.Query.TryGetValue("token", out var supplied)
                && CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(supplied.ToString()),
                    System.Text.Encoding.UTF8.GetBytes(_token)))
            {
                context.Response.Cookies.Append(SessionCookie, _token, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = false,
                    IsEssential = true
                });
                context.Response.Redirect(context.Request.Path.HasValue ? context.Request.Path.Value! : "/");
                return;
            }

            if (!context.Request.Cookies.TryGetValue(SessionCookie, out var cookie)
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(cookie),
                    System.Text.Encoding.UTF8.GetBytes(_token)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("ADHD Focus Guard local session required.");
                return;
            }

            if (!HttpMethods.IsGet(context.Request.Method)
                && !HttpMethods.IsHead(context.Request.Method)
                && !IsSameOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store"
        });
        MapApi(app);

        await app.StartAsync(cancellationToken);
        var address = app.Urls.Single(value => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        Address = new Uri(address.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase));
        _application = app;
    }

    public void OpenInBrowser()
    {
        if (Address is null) return;
        var launch = new UriBuilder(Address) { Query = "token=" + Uri.EscapeDataString(_token) }.Uri.AbsoluteUri;
        Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true });
    }

    private void MapApi(WebApplication app)
    {
        app.MapGet("/api/dashboard", async (int? days, CancellationToken cancellationToken) =>
        {
            var count = Math.Clamp(days ?? 7, 1, 30);
            var end = CurrentActivityBoundary().AddDays(1);
            var start = end.AddDays(-count);
            var segments = await _repository.GetActivitySegmentsAsync(start, end, cancellationToken);
            var goals = await _repository.GetGoalsAsync(true, cancellationToken);
            var budget = await _budget.GetSnapshotAsync(cancellationToken);
            return Results.Ok(BuildDashboard(start, end, count, segments, goals, budget));
        });

        app.MapGet("/api/status", () => Results.Ok(new
        {
            tracker = _tracker.StatusText,
            visual = _visual.StatusText,
            focusPhase = _coordinator.Phase.ToString(),
            focus = _coordinator.Snapshot
        }));

        app.MapGet("/api/goals", async (CancellationToken cancellationToken) =>
            Results.Ok(await _repository.GetGoalsAsync(true, cancellationToken)));
        app.MapPost("/api/goals/analyze-daily", async (TextInput input, CancellationToken cancellationToken) =>
        {
            var source = Required(input.Text, 8_000, "今日目标");
            var goals = await _repository.GetGoalsAsync(false, cancellationToken);
            return Results.Ok(await _personalAi.AnalyzeDailyGoalsAsync(source, goals, cancellationToken));
        });
        app.MapPost("/api/goals", async (GoalInput input, CancellationToken cancellationToken) =>
        {
            var now = DateTimeOffset.Now;
            var existing = input.Id is null
                ? null
                : (await _repository.GetGoalsAsync(true, cancellationToken)).FirstOrDefault(goal => goal.Id == input.Id);
            var goal = new GoalRecord
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Horizon = input.Horizon,
                Title = Required(input.Title, 500, "目标标题"),
                ExpectedOutcome = Limited(input.ExpectedOutcome, 4_000),
                Status = input.Status,
                Priority = Math.Clamp(input.Priority, 1, 3),
                EstimatedMinutes = input.EstimatedMinutes is null ? null : Math.Clamp(input.EstimatedMinutes.Value, 1, 100_000),
                DueAt = input.DueAt,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
                CompletionEvidence = existing?.CompletionEvidence ?? "",
                RelatedGoalIds = input.RelatedGoalIds?.Distinct().Take(20).ToArray() ?? []
            };
            await _repository.SaveGoalAsync(goal, existing is null ? "created-from-web" : "updated-from-web", cancellationToken);
            if (goal.Horizon == GoalHorizon.Today)
            {
                var activityDate = DateOnly.FromDateTime(CurrentActivityBoundary().LocalDateTime.Date);
                await _repository.SaveDailyPlanStateAsync(new(activityDate, true, null, now), cancellationToken);
            }
            return Results.Ok(goal);
        });
        app.MapGet("/api/actions", async (CancellationToken cancellationToken) =>
            Results.Ok(await _repository.GetActionItemsAsync(true, cancellationToken)));
        app.MapPost("/api/actions/organize", async (TextInput input, CancellationToken cancellationToken) =>
        {
            var text = Required(input.Text, 8_000, "事务内容");
            var goals = await _repository.GetGoalsAsync(false, cancellationToken);
            return Results.Ok(await _personalAi.OrganizeActionsAsync(text, goals, cancellationToken));
        });
        app.MapPost("/api/actions", async (ActionInput input, CancellationToken cancellationToken) =>
        {
            var now = DateTimeOffset.Now;
            var existing = input.Id is null
                ? null
                : (await _repository.GetActionItemsAsync(true, cancellationToken)).FirstOrDefault(item => item.Id == input.Id);
            var item = new ActionItemRecord
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Title = Required(input.Title, 500, "事务标题"),
                ExpectedOutcome = Limited(input.ExpectedOutcome, 4_000),
                Status = input.Status,
                Priority = Math.Clamp(input.Priority, 1, 3),
                EstimatedMinutes = input.EstimatedMinutes is null ? null : Math.Clamp(input.EstimatedMinutes.Value, 1, 100_000),
                DueAt = input.DueAt,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
                SourceText = Limited(input.SourceText, 8_000),
                RelatedGoalIds = input.RelatedGoalIds?.Distinct().Take(20).ToArray() ?? []
            };
            await _repository.SaveActionItemAsync(item, cancellationToken);
            return Results.Ok(item);
        });
        app.MapDelete("/api/actions/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
        {
            await _repository.DeleteActionItemAsync(id, cancellationToken);
            return Results.Ok(new { ok = true });
        });
        app.MapGet("/api/memories", async (CancellationToken cancellationToken) =>
            Results.Ok(await _repository.GetMemoriesAsync(true, cancellationToken)));
        app.MapPost("/api/memories", async (MemoryInput input, CancellationToken cancellationToken) =>
        {
            var now = DateTimeOffset.Now;
            var existing = input.Id is null
                ? null
                : (await _repository.GetMemoriesAsync(true, cancellationToken)).FirstOrDefault(memory => memory.Id == input.Id);
            var memory = new MemoryRecord
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Text = Required(input.Text, 8_000, "记忆内容"),
                Author = existing?.Author ?? MemoryAuthor.User,
                Status = input.Status,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
                Tags = Limited(input.Tags, 1_000),
                SourceReference = existing?.SourceReference ?? "web-user",
                RelatedGoalId = input.RelatedGoalId,
                IsPinned = input.IsPinned
            };
            await _repository.SaveMemoryAsync(memory, cancellationToken);
            return Results.Ok(memory);
        });
        app.MapDelete("/api/memories/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
        {
            await _repository.DeleteMemoryAsync(id, cancellationToken);
            return Results.Ok(new { ok = true });
        });
        app.MapGet("/api/reports", async (CancellationToken cancellationToken) =>
            Results.Ok(await _repository.GetReportsAsync(cancellationToken)));
        app.MapGet("/api/sessions", async (CancellationToken cancellationToken) =>
            Results.Ok((await _sessions.GetAllAsync(cancellationToken)).Take(50)));
        app.MapGet("/api/settings", async (CancellationToken cancellationToken) =>
        {
            var provider = await _settings.LoadProviderAsync(cancellationToken);
            return Results.Ok(new { provider.BaseUrl, provider.TextModel, visionModel = provider.Model, provider.HasApiKey });
        });
        app.MapPost("/api/settings", async (SettingsInput input, CancellationToken cancellationToken) =>
        {
            await _settings.SaveProviderModelsAsync(
                input.BaseUrl,
                input.TextModel,
                input.VisionModel,
                input.ApiKey ?? "",
                cancellationToken);
            return Results.Ok(new { ok = true });
        });
        app.MapPost("/api/settings/test", async (CancellationToken cancellationToken) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await _ai.TestAsync(timeout.Token);
            return Results.Ok(new { result });
        });
        app.MapPost("/api/focus/start", async (FocusInput input, CancellationToken cancellationToken) =>
        {
            await _coordinator.StartAsync(Required(input.Goal, 2_000, "专注目标"), Math.Clamp(input.Minutes, 1, 300), cancellationToken);
            return Results.Ok(_coordinator.Snapshot);
        });
        app.MapPost("/api/focus/stop", async () =>
        {
            if (_coordinator.Phase == SessionPhase.Running) await _coordinator.StopAsync();
            return Results.Ok(_coordinator.Snapshot);
        });
        app.MapGet("/api/activities", async (int? days, CancellationToken cancellationToken) =>
        {
            var end = CurrentActivityBoundary().AddDays(1);
            var start = end.AddDays(-Math.Clamp(days ?? 1, 1, 30));
            return Results.Ok(await _repository.GetActivitySegmentsAsync(start, end, cancellationToken));
        });
        app.MapGet("/api/data/export", async (CancellationToken cancellationToken) =>
        {
            var temporary = Path.Combine(Path.GetTempPath(), "vigil-export-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await new PersonalDataExportService(_repository).ExportAsync(temporary, cancellationToken);
                var bytes = await File.ReadAllBytesAsync(temporary, cancellationToken);
                return Results.File(bytes, "application/zip", $"ADHD-Focus-Guard-{DateTime.Now:yyyyMMdd-HHmm}.zip");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
        app.MapPost("/api/data/import", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            const long maximum = 25 * 1024 * 1024;
            if (request.ContentLength is null or <= 0 or > maximum)
                return Results.BadRequest(new { message = "备份文件必须小于 25 MiB。" });
            var temporary = Path.Combine(Path.GetTempPath(), "vigil-import-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await request.Body.CopyToAsync(file, cancellationToken);
                await new PersonalDataExportService(_repository).ImportAsync(temporary, cancellationToken);
                return Results.Ok(new { ok = true });
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
        app.MapDelete("/api/data", async (CancellationToken cancellationToken) =>
        {
            await _repository.DeleteAllPersonalDataAsync(cancellationToken);
            return Results.Ok(new { ok = true });
        });
    }

    private static string Required(string? value, int maximum, string field)
    {
        var text = value?.Trim() ?? "";
        if (text.Length is 0 || text.Length > maximum) throw new Microsoft.AspNetCore.Http.BadHttpRequestException($"{field}长度无效。");
        return text;
    }

    private static string Limited(string? value, int maximum)
    {
        var text = value?.Trim() ?? "";
        return text.Length <= maximum ? text : text[..maximum];
    }

    private static object BuildDashboard(
        DateTimeOffset start,
        DateTimeOffset end,
        int dayCount,
        IReadOnlyList<ActivitySegment> segments,
        IReadOnlyList<GoalRecord> goals,
        AiBudgetSnapshot budget)
    {
        var daily = new List<object>(dayCount);
        var work = 0;
        var entertainment = 0;
        var other = 0;
        var hourly = new int[24];

        for (var index = 0; index < dayCount; index++)
        {
            var dayStart = start.AddDays(index);
            var dayEnd = dayStart.AddDays(1);
            var dayWork = 0;
            var dayEntertainment = 0;
            var dayOther = 0;
            foreach (var segment in segments)
            {
                var seconds = OverlapSeconds(segment.StartedAt, segment.EndedAt, dayStart, dayEnd);
                if (seconds <= 0) continue;
                if (segment.Category == ActivityCategory.WorkAndStudy) dayWork += seconds;
                else if (segment.Category == ActivityCategory.Entertainment) dayEntertainment += seconds;
                else dayOther += seconds;
            }
            work += dayWork;
            entertainment += dayEntertainment;
            other += dayOther;
            daily.Add(new
            {
                date = DateOnly.FromDateTime(dayStart.LocalDateTime.Date),
                label = dayStart.LocalDateTime.ToString(dayCount <= 7 ? "ddd" : "M/d"),
                work = dayWork,
                entertainment = dayEntertainment,
                other = dayOther,
                total = dayWork + dayEntertainment + dayOther
            });
        }

        foreach (var segment in segments)
        {
            var cursor = segment.StartedAt < start ? start : segment.StartedAt;
            var limit = segment.EndedAt > end ? end : segment.EndedAt;
            while (cursor < limit)
            {
                var nextHour = new DateTimeOffset(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0, cursor.Offset).AddHours(1);
                var sliceEnd = nextHour < limit ? nextHour : limit;
                hourly[cursor.LocalDateTime.Hour] += Math.Max(0, (int)(sliceEnd - cursor).TotalSeconds);
                cursor = sliceEnd;
            }
        }

        var topActivities = segments
            .GroupBy(segment => segment.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                name = group.Key,
                seconds = group.Sum(segment => OverlapSeconds(segment.StartedAt, segment.EndedAt, start, end)),
                category = group.GroupBy(segment => segment.Category).OrderByDescending(category => category.Count()).First().Key
            })
            .Where(item => item.seconds > 0)
            .OrderByDescending(item => item.seconds)
            .Take(8)
            .ToArray();

        var activeGoals = goals.Where(goal => goal.Status is GoalStatus.NotStarted or GoalStatus.InProgress).ToArray();
        var completedGoals = goals.Count(goal => goal.Status == GoalStatus.Completed);
        var observed = work + entertainment + other;
        return new
        {
            range = new { start, end, days = dayCount },
            summary = new
            {
                work,
                entertainment,
                other,
                observed,
                workRatio = observed == 0 ? 0 : Math.Round(work * 100d / observed, 1),
                averagePerDay = dayCount == 0 ? 0 : observed / dayCount
            },
            daily,
            hourly = hourly.Select((seconds, hour) => new { hour, seconds }).ToArray(),
            topActivities,
            timeline = segments.OrderByDescending(segment => segment.EndedAt).Take(12).ToArray(),
            goals = new
            {
                active = activeGoals.Take(6),
                activeCount = activeGoals.Length,
                completedCount = completedGoals,
                totalCount = goals.Count
            },
            budget
        };
    }

    private static DateTimeOffset CurrentActivityBoundary()
    {
        var now = DateTimeOffset.Now;
        var boundary = new DateTimeOffset(now.Year, now.Month, now.Day, 8, 0, 0, now.Offset);
        return now < boundary ? boundary.AddDays(-1) : boundary;
    }

    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static int OverlapSeconds(DateTimeOffset firstStart, DateTimeOffset firstEnd, DateTimeOffset secondStart, DateTimeOffset secondEnd)
    {
        var start = firstStart > secondStart ? firstStart : secondStart;
        var end = firstEnd < secondEnd ? firstEnd : secondEnd;
        return end <= start ? 0 : (int)(end - start).TotalSeconds;
    }

    private bool IsSameOrigin(HttpRequest request)
    {
        if (Address is null) return false;
        var origin = request.Headers.Origin.ToString();
        return string.Equals(origin, Address.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await _application.StopAsync(timeout.Token);
        await _application.DisposeAsync();
        _application = null;
        Address = null;
    }

    public sealed record GoalInput(
        Guid? Id,
        GoalHorizon Horizon,
        string Title,
        string? ExpectedOutcome,
        GoalStatus Status,
        int Priority,
        int? EstimatedMinutes,
        DateTimeOffset? DueAt,
        IReadOnlyList<Guid>? RelatedGoalIds);

    public sealed record TextInput(string Text);
    public sealed record ActionInput(
        Guid? Id,
        string Title,
        string? ExpectedOutcome,
        ActionItemStatus Status,
        int Priority,
        int? EstimatedMinutes,
        DateTimeOffset? DueAt,
        string? SourceText,
        IReadOnlyList<Guid>? RelatedGoalIds);
    public sealed record MemoryInput(
        Guid? Id,
        string Text,
        string? Tags,
        MemoryStatus Status,
        Guid? RelatedGoalId,
        bool IsPinned);
    public sealed record SettingsInput(string BaseUrl, string TextModel, string VisionModel, string? ApiKey);
    public sealed record FocusInput(string Goal, int Minutes);
}
