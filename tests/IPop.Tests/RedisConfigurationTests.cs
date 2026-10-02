using Xunit;

namespace IPop.Tests;

public sealed class RedisConfigurationTests
{
    [Fact]
    public void Production_appsettings_declares_redis_connection_string()
    {
        // Walk up from the test output directory to the repo root instead of counting
        // "..", which breaks whenever BaseOutputPath adds or removes a nesting level.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "IPop.Api")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "IPop.Api", "appsettings.json");
        Assert.True(File.Exists(path), $"appsettings.json not found at {path}");
        var settings = File.ReadAllText(path);

        Assert.Contains("\"Redis\"", settings);
        // 127.0.0.1, not localhost: on the server localhost resolves to ::1 first and the
        // docker-published port hangs on IPv6.
        Assert.Contains("127.0.0.1:6399", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:6379", settings, StringComparison.Ordinal);
    }
}
