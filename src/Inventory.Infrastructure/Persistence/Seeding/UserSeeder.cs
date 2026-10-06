using Inventory.Application.Abstractions.Authentication;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Inventory.Infrastructure.Persistence.Seeding;

/// <summary>
/// Creates bootstrap users listed under <c>SeedUsers</c> in configuration (e.g. environment variables
/// <c>SeedUsers__0__Username</c>, <c>SeedUsers__0__Password</c>, <c>SeedUsers__0__Role</c>). Existing usernames are left
/// untouched. Nothing is seeded by default — credentials never live in source control.
/// </summary>
public static class UserSeeder
{
    public const string SectionName = "SeedUsers";

    public sealed class SeedUser
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.User;
    }

    public static async Task SeedConfiguredUsersAsync(
        this IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var seedUsers = configuration.GetSection(SectionName).Get<List<SeedUser>>() ?? [];
        if (seedUsers.Count == 0)
            return;

        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UserSeeder));

        foreach (var seed in seedUsers.Where(s => !string.IsNullOrWhiteSpace(s.Username) && !string.IsNullOrEmpty(s.Password)))
        {
            if (await users.GetByUsernameAsync(seed.Username.Trim(), cancellationToken) is not null)
                continue;

            try
            {
                await users.AddAsync(User.Create(seed.Username, hasher.Hash(seed.Password), seed.Role), cancellationToken);
                logger.LogInformation("Seeded user {Username} with role {Role}.", seed.Username, seed.Role);
            }
            catch (DbUpdateException ex) when (ex.InnerException is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
            {
                // Another instance starting at the same time inserted it first (unique username index). Not an error.
                logger.LogInformation("User {Username} was seeded concurrently by another instance.", seed.Username);
            }
        }
    }
}
