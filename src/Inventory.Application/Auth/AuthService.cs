using Inventory.Application.Abstractions.Authentication;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Common.Exceptions;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Auth;

public interface IAuthService
{
    /// <exception cref="ValidationException">Username or password is missing.</exception>
    /// <exception cref="InvalidCredentialsException">The credentials are wrong.</exception>
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
}

public sealed class AuthService(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator tokenGenerator,
    ILogger<AuthService> logger) : IAuthService
{
    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Username))
            errors[nameof(request.Username)] = ["Username is required."];
        if (string.IsNullOrEmpty(request.Password))
            errors[nameof(request.Password)] = ["Password is required."];
        if (errors.Count > 0)
            throw new ValidationException(errors);

        var user = await users.GetByUsernameAsync(request.Username!.Trim(), cancellationToken);
        // Always verify, even for unknown users, so timing does not reveal which usernames exist.
        var passwordValid = passwordHasher.Verify(request.Password!, user?.PasswordHash);

        if (user is null || !passwordValid)
        {
            // Username only: never the password, and nothing that says which of the two was wrong.
            logger.LogWarning("Login failed for username {Username}.", request.Username.Trim());
            throw new InvalidCredentialsException();
        }

        logger.LogInformation("User {UserId} ({Username}) logged in with role {Role}.", user.Id, user.Username, user.Role);

        var token = tokenGenerator.Generate(user);
        return new LoginResponse(token.Token, "Bearer", token.ExpiresAtUtc);
    }
}
