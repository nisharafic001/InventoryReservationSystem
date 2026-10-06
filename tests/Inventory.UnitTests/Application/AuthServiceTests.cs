using Inventory.Application.Abstractions.Authentication;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Auth;
using Inventory.Application.Common.Exceptions;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inventory.UnitTests.Application;

public class AuthServiceTests
{
    private readonly StubUsers _users = new();
    private readonly StubHasher _hasher = new();
    private readonly StubTokens _tokens = new();
    private readonly AuthService _service;

    public AuthServiceTests()
    {
        _service = new AuthService(_users, _hasher, _tokens, NullLogger<AuthService>.Instance);
        _users.Stored = User.Create("alice", "hash-of:secret", UserRole.User);
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsBearerToken()
    {
        var response = await _service.LoginAsync(new LoginRequest("alice", "secret"), default);

        Assert.Equal("token-for:alice", response.AccessToken);
        Assert.Equal("Bearer", response.TokenType);
        Assert.Equal(_tokens.ExpiresAtUtc, response.ExpiresAtUtc);
    }

    [Fact]
    public async Task LoginAsync_TrimsUsername()
    {
        await _service.LoginAsync(new LoginRequest("  alice  ", "secret"), default);

        Assert.Equal("alice", _users.LastLookup);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ThrowsInvalidCredentials_WithoutIssuingToken()
    {
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => _service.LoginAsync(new LoginRequest("alice", "wrong"), default));

        Assert.Equal(0, _tokens.Issued);
    }

    [Fact]
    public async Task LoginAsync_UnknownUser_StillVerifiesPassword_AndThrowsSameError()
    {
        var ex = await Assert.ThrowsAsync<InvalidCredentialsException>(() => _service.LoginAsync(new LoginRequest("nobody", "secret"), default));

        // A verification against a null hash runs, so unknown usernames cost the same as wrong passwords.
        Assert.Equal([null], _hasher.VerifiedHashes);
        Assert.Equal("Invalid username or password.", ex.Message);
        Assert.Equal(0, _tokens.Issued);
    }

    [Theory]
    [InlineData(null, "secret", "Username")]
    [InlineData("  ", "secret", "Username")]
    [InlineData("alice", null, "Password")]
    [InlineData("alice", "", "Password")]
    public async Task LoginAsync_MissingField_ThrowsValidation_WithoutLookingUpUser(string? username, string? password, string field)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.LoginAsync(new LoginRequest(username, password), default));

        Assert.Equal([field], ex.Errors.Keys);
        Assert.Null(_users.LastLookup);
    }

    [Fact]
    public void LoginRequest_ToString_DoesNotContainPassword()
    {
        Assert.DoesNotContain("secret", new LoginRequest("alice", "secret").ToString());
    }

    private sealed class StubUsers : IUserRepository
    {
        public User? Stored { get; set; }
        public string? LastLookup { get; private set; }

        public Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken)
        {
            LastLookup = username;
            return Task.FromResult(Stored?.Username == username ? Stored : null);
        }

        public Task AddAsync(User user, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubHasher : IPasswordHasher
    {
        public List<string?> VerifiedHashes { get; } = [];

        public string Hash(string password) => $"hash-of:{password}";

        public bool Verify(string password, string? passwordHash)
        {
            VerifiedHashes.Add(passwordHash);
            return passwordHash == Hash(password);
        }
    }

    private sealed class StubTokens : IJwtTokenGenerator
    {
        public DateTime ExpiresAtUtc { get; } = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public int Issued { get; private set; }

        public AccessToken Generate(User user)
        {
            Issued++;
            return new AccessToken($"token-for:{user.Username}", ExpiresAtUtc);
        }
    }
}
