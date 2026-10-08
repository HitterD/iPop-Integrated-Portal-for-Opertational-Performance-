using IPop.Api.Rbac;
using IPop.Application.Projects;
using IPop.Application.Realtime;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Phase 3: task timers, time entries and weekly timesheets.
public static class ProjectTimeEndpoints
{
    public static IEndpointRouteBuilder MapProjectTime(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects").RequireAuthorization().RequirePermission("projects.read");

        g.MapGet("/timer", async (HttpContext http, IPopDbContext db, IProjectTimeService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var running = await svc.GetRunningAsync(actor.Value.Id, ct);
            return running is null ? Results.NoContent() : Results.Ok(running);
        });
        g.MapPost("/tasks/{taskId:guid}/timer/start", async (Guid taskId, HttpContext http, IPopDbContext db, IProjectTimeService svc, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var previous = await svc.GetRunningAsync(actor.Value.Id, ct);
            var result = await svc.StartAsync(actor.Value.Id, taskId, ct);
            if (result.Success)
            {
                await PushAsync(realtime, actor.Value.Nik, result.Data!.ProjectId, previous?.ProjectId);
            }
            return Response(result);
        }).RequirePermission("projects.write");
        g.MapPost("/timer/stop", async (HttpContext http, IPopDbContext db, IProjectTimeService svc, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var previous = await svc.GetRunningAsync(actor.Value.Id, ct);
            var result = await svc.StopAsync(actor.Value.Id, ct);
            if (!result.Success) return Response(result);
            if (previous is not null) await PushAsync(realtime, actor.Value.Nik, previous.ProjectId);
            return result.Data is null ? Results.NoContent() : Results.Ok(result.Data);
        }).RequirePermission("projects.write");

        g.MapGet("/tasks/{taskId:guid}/time", async (Guid taskId, HttpContext http, IPopDbContext db, IProjectTimeService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Response(await svc.GetTaskTimeAsync(actor.Value.Id, taskId, ct));
        });
        g.MapPost("/tasks/{taskId:guid}/time", async (Guid taskId, AddTimeEntryRequest req, HttpContext http, IPopDbContext db, IProjectTimeService svc, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var result = await svc.AddManualAsync(actor.Value.Id, taskId, req, ct);
            if (result.Success && await svc.ProjectOfTaskAsync(taskId, ct) is { } pid) await PushAsync(realtime, null, pid);
            return Response(result);
        }).RequirePermission("projects.write");
        g.MapPut("/time/{entryId:guid}", async (Guid entryId, UpdateTimeEntryRequest req, HttpContext http, IPopDbContext db, IProjectTimeService svc, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var result = await svc.UpdateEntryAsync(actor.Value.Id, entryId, req, ct);
            if (result.Success && await svc.ProjectOfTaskAsync(result.Data!.TaskId, ct) is { } pid) await PushAsync(realtime, null, pid);
            return Response(result);
        }).RequirePermission("projects.write");
        g.MapDelete("/time/{entryId:guid}", async (Guid entryId, HttpContext http, IPopDbContext db, IProjectTimeService svc, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var result = await svc.DeleteEntryAsync(actor.Value.Id, entryId, ct);
            if (!result.Success) return Response(result);
            await PushAsync(realtime, null, result.Data);
            return Results.NoContent();
        }).RequirePermission("projects.write");

        // weekStart = the viewer's local Monday 00:00 as a UTC instant (ISO with Z).
        g.MapGet("/{projectId:guid}/timesheet", async (Guid projectId, DateTime weekStart, HttpContext http, IPopDbContext db, IProjectTimeService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Response(await svc.GetProjectTimesheetAsync(actor.Value.Id, projectId, weekStart, ct));
        });
        g.MapGet("/portfolio/timesheet", async (DateTime weekStart, HttpContext http, IPopDbContext db, IProjectTimeService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Results.Ok(await svc.GetPortfolioTimesheetAsync(actor.Value.Id, weekStart, ct));
        });

        return app;
    }

    private static async Task<(Guid Id, string Nik)?> ActorAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var id = await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        return id is null ? null : (id.Value, nik);
    }

    private static IResult Response<T>(TimeResult<T> result) => result.Success
        ? Results.Ok(result.Data)
        : Results.Json(new { code = result.Error, message = result.Error }, statusCode: result.Error switch
        {
            "forbidden" => 403,
            "task_not_found" or "entry_not_found" => 404,
            "timer_conflict" => 409,
            _ => 422
        });

    /// Tells the actor's other tabs (topbar indicator) and every viewer of the affected projects.
    private static async Task PushAsync(IRealtime realtime, string? nik, params Guid?[] projectIds)
    {
        try
        {
            if (nik is not null) await realtime.ToUserAsync(nik, RealtimeEvents.TimerChanged);
            foreach (var pid in projectIds.OfType<Guid>().Distinct())
                await realtime.ToAllAsync(RealtimeEvents.ProjectChanged, pid);
        }
        catch (Exception) { /* Committed mutations must not fail on advisory push. */ }
    }
}
