using Inventory.Domain.Enums;

namespace Inventory.Api.Auth;

public static class AuthorizationPolicies
{
    public const string AdminOnly = nameof(AdminOnly);

    public static readonly string AdminRole = UserRole.Admin.ToString();
}
