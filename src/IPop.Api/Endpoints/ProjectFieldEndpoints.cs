using IPop.Api.Rbac;
using IPop.Application.Projects;
using IPop.Application.Realtime;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace IPop.Api.Endpoints;

public static class ProjectFieldEndpoints
{
    public static IEndpointRouteBuilder MapProjectFields(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/fields")
            .RequireAuthorization().RequirePermission("projects.read");

        group.MapGet("", async (Guid projectId, HttpContext http, IPopDbContext db, IProjectFieldService service, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Response(await service.ListAsync(actor.Value, projectId, ct));
        });
        group.MapPost("", async (Guid projectId, CreateProjectFieldRequest request, HttpContext http, IPopDbContext db, IProjectFieldService service, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var result = await service.CreateAsync(actor.Value, projectId, request, ct);
            if (result.Success) await PushAsync(realtime, projectId);
            return Response(result);
        }).RequirePermission("projects.write");
        group.MapPut("/{fieldId:guid}", async (Guid projectId, Guid fieldId, UpdateProjectFieldRequest request, HttpContext http, IPopDbContext db, IProjectFieldService service, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var result = await service.UpdateAsync(actor.Value, projectId, fieldId, request, ct);
            if (result.Success) await PushAsync(realtime, projectId);
            return Response(result);
        }).RequirePermission("projects.write");
        group.MapDelete("/{fieldId:guid}", async (Guid projectId, Guid fieldId, HttpContext http, IPopDbContext db, IProjectFieldService service, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var result = await service.DeleteAsync(actor.Value, projectId, fieldId, ct);
            if (result.Success) await PushAsync(realtime, projectId);
            return Response(result);
        }).RequirePermission("projects.write");

        var values = app.MapGroup("/api/projects/{projectId:guid}/tasks/{taskId:guid}/fields")
            .RequireAuthorization().RequirePermission("projects.read");
        group.MapGet("/values", async (Guid projectId, [FromQuery] Guid[] taskIds, HttpContext http, IPopDbContext db, IProjectFieldService service, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Response(await service.GetValuesForTasksAsync(actor.Value, projectId, taskIds, ct));
        });
        values.MapGet("", async (Guid projectId, Guid taskId, HttpContext http, IPopDbContext db, IProjectFieldService service, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Response(await service.GetTaskValuesAsync(actor.Value, projectId, taskId, ct));
        });
        values.MapPut("/{fieldId:guid}", async (Guid projectId, Guid taskId, Guid fieldId, SetProjectFieldValueRequest request, HttpContext http, IPopDbContext db, IProjectFieldService service, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var result = await service.SetTaskValueAsync(actor.Value, projectId, taskId, fieldId, request.Value, ct);
            if (result.Success) await PushAsync(realtime, projectId);
            return Response(result);
        }).RequirePermission("projects.write");
        values.MapDelete("/{fieldId:guid}", async (Guid projectId, Guid taskId, Guid fieldId, HttpContext http, IPopDbContext db, IProjectFieldService service, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var result = await service.SetTaskValueAsync(actor.Value, projectId, taskId, fieldId, null, ct);
            if (result.Success) await PushAsync(realtime, projectId);
            return Response(result);
        }).RequirePermission("projects.write");
        return app;
    }

    private static async Task<Guid?> ActorAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        return string.IsNullOrEmpty(nik) ? null : await db.Users.AsNoTracking()
            .Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    private static IResult Response<T>(ProjectFieldResult<T> result) => result.Success
        ? Results.Ok(result.Data)
        : Results.Json(new { code = result.Error, message = result.Error }, statusCode: result.Error switch
        {
            "forbidden" => 403,
            "field_not_found" or "task_not_found" => 404,
            _ => 422
        });

    private static async Task PushAsync(IRealtime realtime, Guid projectId)
    {
        try { await realtime.ToAllAsync(RealtimeEvents.ProjectChanged, projectId); }
        catch (Exception) { /* Committed mutations must not fail on advisory push. */ }
    }
}
