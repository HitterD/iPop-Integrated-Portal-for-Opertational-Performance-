using IPop.Application.Sja;

namespace IPop.Api.Endpoints;

public record DeviceLinkRequest(string Code);

public static class DeviceLinkEndpoints
{
    public static IEndpointRouteBuilder MapDeviceLink(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/devices");

        // Authenticated: NIK diambil dari token, sehingga pemanggil hanya dapat
        // menghubungkan perangkat ke akunnya sendiri — pola yang sama dengan
        // /api/auth/change-password.
        g.MapPost("/link", async (DeviceLinkRequest req, ISjaGateway sja, HttpContext http, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            if (string.IsNullOrWhiteSpace(nik)) return Results.Unauthorized();

            var code = (req.Code ?? string.Empty).Trim();
            if (code.Length != 6 || !code.All(char.IsAsciiDigit))
                return Results.BadRequest(new { message = "Activation code must be 6 digits." });

            var result = await sja.ApproveDeviceActivationAsync(code, nik, ct);
            if (!result.Success)
                return Results.BadRequest(new { message = result.ErrorMessage ?? "Failed to link device." });

            return Results.Ok(new
            {
                success = true,
                deviceName = result.DeviceName,
                employeeName = result.EmployeeName,
                capabilities = result.Capabilities,
            });
        }).RequireAuthorization();

        return app;
    }
}
