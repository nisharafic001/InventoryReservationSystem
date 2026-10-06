using Inventory.Domain.Entities;

namespace Inventory.Application.Abstractions.Persistence;

public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken);

    Task AddAsync(User user, CancellationToken cancellationToken);
}
