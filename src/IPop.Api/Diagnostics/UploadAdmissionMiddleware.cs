namespace IPop.Api.Diagnostics;

// Admission seam. Regression tests are committed before implementing concurrency enforcement.
public sealed class UploadAdmissionMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public Task InvokeAsync(HttpContext context) => next(context);
}
