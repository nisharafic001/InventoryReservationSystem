using Inventory.Application.Abstractions.Persistence;
using Inventory.Domain.Entities;

namespace Inventory.IntegrationTests.Support;

public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly Lock _gate = new();
    private readonly List<User> _users = [];

    public Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_users.SingleOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)));
    }

    public Task AddAsync(User user, CancellationToken cancellationToken)
    {
        lock (_gate)
            _users.Add(user);
        return Task.CompletedTask;
    }
}
