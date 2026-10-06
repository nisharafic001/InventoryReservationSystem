using Inventory.Domain.Entities;

namespace Inventory.Application.Abstractions.Authentication;

public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);

public interface IJwtTokenGenerator
{
    /// <summary>Issues a signed JWT carrying the user's <c>sub</c>, <c>username</c> and <c>role</c> claims.</summary>
    AccessToken Generate(User user);
}
