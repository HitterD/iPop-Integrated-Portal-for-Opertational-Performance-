using System.Security.Cryptography;
using System.Text;
using IPop.Application.Auth;
using Konscious.Security.Cryptography;

namespace IPop.Infrastructure.Auth;

/// Argon2id, PHC-style "salt$hash" storage (Base64 each).
public class Argon2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16, HashSize = 32, Iterations = 4, MemoryKb = 64 * 1024, Parallelism = 2;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt);
        return $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string hash)
    {
        var parts = hash.Split('$');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Compute(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt, Iterations = Iterations, MemorySize = MemoryKb, DegreeOfParallelism = Parallelism
        };
        return argon.GetBytes(HashSize);
    }
}
