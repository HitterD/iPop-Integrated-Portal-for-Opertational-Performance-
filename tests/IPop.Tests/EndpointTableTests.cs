using System.Reflection;
using IPop.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IPop.Tests;

/// The unit tests call services directly, so a minimal-API handler with a parameter ASP.NET cannot bind (for example an
/// optional class-typed service that is not registered) compiled, passed every test and then crashed the API at startup
/// on staging ("Failure to infer one or more parameters"). Building the real endpoint table here fails the build instead.
public class EndpointTableTests
{
    [Fact]
    public void Every_Map_extension_builds_its_endpoints_without_unbindable_parameters()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication();
        using var app = builder.Build();

        var maps = typeof(AuthEndpoints).Assembly.GetTypes()
            .Where(t => t.IsAbstract && t.IsSealed && t.Namespace == typeof(AuthEndpoints).Namespace)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.Name.StartsWith("Map", StringComparison.Ordinal)
                && m.GetParameters() is [var first, ..]
                && first.ParameterType == typeof(IEndpointRouteBuilder)
                && m.GetParameters().Length == 1)
            .ToList();
        Assert.True(maps.Count >= 20, $"expected the Map* extensions to be discovered, found {maps.Count}");

        var failures = new List<string>();
        foreach (var map in maps)
        {
            try { map.Invoke(null, [app]); }
            catch (TargetInvocationException e) { failures.Add($"{map.DeclaringType!.Name}.{map.Name}: {e.InnerException?.Message}"); }
        }
        Assert.Empty(failures);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d =>
        {
            try { return d.Endpoints; }
            catch (Exception e) { failures.Add(e.Message.Split('\n')[0]); return []; }
        }).ToList();
        Assert.Empty(failures);
        Assert.True(endpoints.Count > 200, $"endpoint table looks too small ({endpoints.Count})");
    }
}
