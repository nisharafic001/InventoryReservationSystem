using System.Security.Cryptography;
using Inventory.Application.Abstractions.Authentication;

namespace Inventory.Infrastructure.Authentication;

/// <summary>
/// PBKDF2-HMAC-SHA256 with a per-password random salt (OWASP-recommended work factor).
/// Format: <c>PBKDF2-SHA256.{iterations}.{base64 salt}.{base64 hash}</c>, so the work factor can be raised later
/// without invalidating existing hashes.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Algorithm = "PBKDF2-SHA256";
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Algorithm}.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string? passwordHash)
    {
        if (!TryParse(passwordHash, out var iterations, out var salt, out var expected))
        {
            // Unknown user or unreadable hash: burn the same work so timing does not leak which.
            Rfc2898DeriveBytes.Pbkdf2(password, new byte[SaltSize], Iterations, HashAlgorithmName.SHA256, HashSize);
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryParse(string? value, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = [];

        var parts = value?.Split('.');
        if (parts is not [Algorithm, var iterationText, var saltText, var hashText]
            || !int.TryParse(iterationText, out iterations) || iterations <= 0)
            return false;

        try
        {
            salt = Convert.FromBase64String(saltText);
            hash = Convert.FromBase64String(hashText);
            return salt.Length > 0 && hash.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
