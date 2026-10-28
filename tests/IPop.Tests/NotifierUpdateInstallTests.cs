using IPop.Application.Notifier;
using Xunit;

namespace IPop.Tests;

public sealed class NotifierUpdateInstallTests
{
    [Theory]
    [InlineData(true, NotifierUpdatePolicy.InstallResult.Busy)]
    [InlineData(null, NotifierUpdatePolicy.InstallResult.UnknownCallStatus)]
    [InlineData(false, NotifierUpdatePolicy.InstallResult.Installed)]
    public async Task Every_install_requires_a_fresh_known_idle_call_status(bool? call, NotifierUpdatePolicy.InstallResult expected)
    {
        var probes = 0;
        var applied = 0;
        var result = await NotifierUpdatePolicy.TryInstallAsync(
            () => { probes++; return Task.FromResult(call); }, () => false, () => applied++);
        Assert.Equal(expected, result);
        Assert.Equal(1, probes);
        Assert.Equal(call == false ? 1 : 0, applied);
    }

    [Theory]
    [InlineData(200, "", false)]
    [InlineData(200, "null", false)]
    [InlineData(200, "{\"id\":\"call-id\"}", true)]
    [InlineData(503, "", null)]
    [InlineData(401, "null", null)]
    [InlineData(200, "invalid json", null)]
    [InlineData(200, "{}", null)]
    [InlineData(200, "[]", null)]
    public void Call_response_distinguishes_idle_from_failed_or_invalid_responses(int status, string body, bool? expected)
        => Assert.Equal(expected, NotifierUpdatePolicy.ReadCallStatus(status, body));

    [Fact]
    public async Task Remote_starting_during_the_call_probe_blocks_installation()
    {
        var remote = false;
        var applied = false;
        var result = await NotifierUpdatePolicy.TryInstallAsync(
            () => { remote = true; return Task.FromResult<bool?>(false); }, () => remote, () => applied = true);
        Assert.Equal(NotifierUpdatePolicy.InstallResult.Busy, result);
        Assert.False(applied);
    }

    [Fact]
    public async Task A_previous_idle_check_is_not_reused_for_the_next_attempt()
    {
        bool? call = false;
        var applied = 0;
        Assert.Equal(NotifierUpdatePolicy.InstallResult.Installed,
            await NotifierUpdatePolicy.TryInstallAsync(() => Task.FromResult(call), () => false, () => applied++));
        call = true;
        Assert.Equal(NotifierUpdatePolicy.InstallResult.Busy,
            await NotifierUpdatePolicy.TryInstallAsync(() => Task.FromResult(call), () => false, () => applied++));
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task Install_errors_reach_the_caller_for_retry_feedback()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => NotifierUpdatePolicy.TryInstallAsync(
            () => Task.FromResult<bool?>(false), () => false, () => throw new InvalidOperationException("apply failed")));
    }

    [Fact]
    public async Task A_failed_call_probe_fails_closed()
    {
        var applied = false;
        var result = await NotifierUpdatePolicy.TryInstallAsync(
            () => throw new HttpRequestException("offline"), () => false, () => applied = true);
        Assert.Equal(NotifierUpdatePolicy.InstallResult.UnknownCallStatus, result);
        Assert.False(applied);
    }
}
