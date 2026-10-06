using Inventory.Application.Abstractions;
using Inventory.Domain.Enums;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Inventory.Api.Auth;

/// <summary>Reads the caller from the validated JWT on the current request.</summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public string UserId =>
        httpContextAccessor.HttpContext?.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? throw new InvalidOperationException("No authenticated user on the current request.");

    public bool IsInRole(UserRole role) =>
        httpContextAccessor.HttpContext?.User.IsInRole(role.ToString()) ?? false;
}
