using Inventory.Domain.Enums;

namespace Inventory.Application.Abstractions;

/// <summary>
/// The authenticated caller of the current request.
/// </summary>
public interface ICurrentUser
{
    string UserId { get; }

    bool IsInRole(UserRole role);
}
