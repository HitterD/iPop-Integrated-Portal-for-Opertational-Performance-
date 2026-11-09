using IPop.Api.Diagnostics;

namespace IPop.Api.Endpoints;

public static class UploadAdmissionEndpoints
{
    public static void MapUploadAdmission(this WebApplication app)
    {
        // Route and reservation travel in query/header, never a bound request body.
        app.MapPost("/api/uploads/reservations", Reserve).RequireAuthorization();
        app.MapDelete("/api/uploads/reservations", Cancel).RequireAuthorization();
    }

    public static async Task Reserve(HttpContext context, UploadAdmissionService admission)
    {
        context.Response.Headers.CacheControl = "no-store";
        var owner = UploadAdmissionService.Owner(context.User);
        if (owner is null) { context.Response.StatusCode = 401; return; }
        var route = context.Request.Query["route"].ToString();
        if (!UploadAdmissionService.IsUpload(route)) { context.Response.StatusCode = 400; return; }
        var token = admission.Reserve(owner, route);
        if (token is null)
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsJsonAsync(new { code = "upload_busy", message = "Other uploads are still running. Please try again shortly." }, context.RequestAborted);
            return;
        }
        try
        {
            await context.Response.WriteAsJsonAsync(new { token, expiresInSeconds = (int)UploadAdmissionService.ReservationLifetime.TotalSeconds }, context.RequestAborted);
        }
        catch
        {
            admission.Cancel(owner, route, token);
            throw;
        }
    }

    public static Task Cancel(HttpContext context, UploadAdmissionService admission)
    {
        var owner = UploadAdmissionService.Owner(context.User);
        if (owner is null) { context.Response.StatusCode = 401; return Task.CompletedTask; }
        // Idempotent, no disclosure of other users' reservations; active permits cannot be cancelled here.
        admission.Cancel(owner, context.Request.Query["route"].ToString(), context.Request.Headers[UploadAdmissionService.Header].ToString());
        context.Response.StatusCode = 204;
        return Task.CompletedTask;
    }
}
