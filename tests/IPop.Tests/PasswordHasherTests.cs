using IPop.Infrastructure.Auth;
using Xunit;

namespace IPop.Tests;

public class PasswordHasherTests
{
    private readonly Argon2PasswordHasher _h = new();

    [Fact]
    public void Hash_ThenVerify_RoundTrips()
    {
        var hash = _h.Hash("MySecure9");
        Assert.True(_h.Verify("MySecure9", hash));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var hash = _h.Hash("MySecure9");
        Assert.False(_h.Verify("wrong", hash));
    }

    [Fact]
    public void Hash_SamePasswordTwice_DifferentSalts()
    {
        Assert.NotEqual(_h.Hash("MySecure9"), _h.Hash("MySecure9"));
    }

    [Fact]
    public void Verify_MalformedHash_FailsInsteadOfThrowing()
        => Assert.False(_h.Verify("x", "not-a-phc-string"));
}
