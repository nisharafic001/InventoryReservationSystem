using Inventory.Application.Abstractions.Authentication;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Infrastructure.Authentication;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "DefaultConnection";
    private const string DefaultServerVersion = "8.0.41-mysql";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Options are resolved lazily so the app can start (e.g. /health) without a database configured.
        services.AddDbContext<AppDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured. " +
                    $"Set the ConnectionStrings__{ConnectionStringName} environment variable.");

            // Explicit version avoids opening a connection at startup (ServerVersion.AutoDetect does).
            var serverVersion = ServerVersion.Parse(configuration["Database:ServerVersion"] ?? DefaultServerVersion);

            options.UseMySql(connectionString, serverVersion, mySql =>
                mySql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));

            // Optional fail-fast for lock waits (MySQL default: 50 s). Unset = MySQL default.
            if (int.TryParse(configuration["Database:LockWaitTimeoutSeconds"], out var lockWaitSeconds) && lockWaitSeconds > 0)
                options.AddInterceptors(new LockWaitTimeoutInterceptor(lockWaitSeconds));
        });

        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IReservationRepository, ReservationRepository>();
        services.AddScoped<IUserRepository, UserRepository>();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(o => o.IsValid(out _), "Invalid Jwt configuration (Issuer, Audience, SigningKey >= 32 bytes, ExpirationMinutes 1-1440).")
            .ValidateOnStart();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

        return services;
    }
}
