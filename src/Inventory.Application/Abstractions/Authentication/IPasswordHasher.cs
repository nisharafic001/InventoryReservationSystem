namespace Inventory.Application.Abstractions.Authentication;

public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>
    /// Constant-time verification of <paramref name="password"/> against a hash from <see cref="Hash"/>.
    /// A <c>null</c> hash (unknown user) still does the full key derivation and returns <c>false</c>,
    /// so response timing does not reveal whether a username exists.
    /// </summary>
    bool Verify(string password, string? passwordHash);
}
