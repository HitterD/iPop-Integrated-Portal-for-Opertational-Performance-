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

            var evaluator = http.RequestServices.GetRequiredService<IPermissionEvaluator>();
            // ponytail: one evaluator call per request; cache per-request if this ever shows on hot paths
            var allowed = await evaluator.IsAllowedAsync(userId.Value, permissionCode, null);
            if (!allowed)
                return Results.Json(new { code = "forbidden", message = $"Missing permission: {permissionCode}" }, statusCode: 403);

            return await next(ctx);
        });
        return builder;
    }
}
