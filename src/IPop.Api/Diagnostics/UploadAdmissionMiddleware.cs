namespace IPop.Api.Diagnostics;

/// Bounds multipart work before endpoint form parsing. Authorization still belongs to the endpoint.
public sealed class UploadAdmissionMiddleware(RequestDelegate next, UploadAdmissionService admission)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !UploadAdmissionService.IsUpload(context.Request.Path)
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }
        var reserved = context.Request.Headers.TryGetValue(UploadAdmissionService.Header, out var values);
        var token = reserved ? values.Count == 1 ? values[0] ?? "" : "" : null;
        using var permit = admission.TryStart(UploadAdmissionService.Owner(context.User) ?? "", context.Request.Path.Value!, token);
        if (permit is null)
        {
            context.Response.StatusCode = reserved ? StatusCodes.Status409Conflict : StatusCodes.Status429TooManyRequests;
            if (!reserved) context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsJsonAsync(new
            {
                code = reserved ? "upload_reservation_invalid" : "upload_busy",
                message = reserved ? "Upload reservation expired or is no longer valid. Please select the file again."
                    : "Other uploads are still running. Please try again in a few seconds.",
            }, context.RequestAborted);
            return;
        }
        await next(context); // using releases on response, error or cancellation, never by reservation expiry
    }
}
