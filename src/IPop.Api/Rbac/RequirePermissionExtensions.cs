using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using IPop.Application.Rbac;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Rbac;

public static class RequirePermissionExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permissionCode)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (http.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();

            Guid? userId = null;
            var sub = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                   ?? http.User.FindFirstValue("sub")
                   ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(sub, out var parsed))
                userId = parsed;

            if (userId is null)
            {
                var nik = http.User.FindFirstValue("nik");
                if (!string.IsNullOrEmpty(nik))
                {
                    var db = http.RequestServices.GetRequiredService<IPopDbContext>();
                    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik);
                    if (user is not null) userId = user.Id;
                }
            }

            if (userId is null) return Results.Unauthorized();

            // Several RequirePermission filters can stack on one endpoint (group + endpoint); the answer is the
            // same within a request, so it is computed once. Under load each evaluation is 4-6 queries.
            var key = $"ipop.perm:{userId.Value:N}:{permissionCode}";
            bool allowed;
            if (http.Items.TryGetValue(key, out var cached) && cached is bool b) allowed = b;
            else
            {
                var evaluator = http.RequestServices.GetRequiredService<IPermissionEvaluator>();
                allowed = await evaluator.IsAllowedAsync(userId.Value, permissionCode, null, http.RequestAborted);
                http.Items[key] = allowed;
            }
            if (!allowed)
                return Results.Json(new { code = "forbidden", message = $"Missing permission: {permissionCode}" }, statusCode: 403);

            return await next(ctx);
        });
        return builder;
    }
}
