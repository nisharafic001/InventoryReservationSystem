using Inventory.Domain.Enums;
using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Entities;

public sealed class User
{
    public const int UsernameMaxLength = 64;
    public const int PasswordHashMaxLength = 256;

    // Parameter names match property names so EF Core can bind this constructor when materializing.
    private User(Guid id, string username, string passwordHash, UserRole role)
    {
        Id = id;
        Username = username;
        PasswordHash = passwordHash;
        Role = role;
    }

    public Guid Id { get; private set; }
    public string Username { get; private set; }

    /// <summary>A salted, slow hash produced by the infrastructure password hasher — never a plain password.</summary>
    public string PasswordHash { get; private set; }

    public UserRole Role { get; private set; }

    public static User Create(string username, string passwordHash, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new DomainException("Username is required.");
        username = username.Trim();
        if (username.Length > UsernameMaxLength)
            throw new DomainException($"Username cannot exceed {UsernameMaxLength} characters.");
        if (string.IsNullOrWhiteSpace(passwordHash) || passwordHash.Length > PasswordHashMaxLength)
            throw new DomainException("A valid password hash is required.");
        if (!Enum.IsDefined(role))
            throw new DomainException($"Unknown role '{role}'.");

        return new User(Guid.NewGuid(), username, passwordHash, role);
    }
}
