using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Domain.Exceptions;

namespace Inventory.UnitTests.Domain;

public class UserTests
{
    private const string Hash = "PBKDF2-SHA256.600000.c2FsdA==.aGFzaA==";

    [Fact]
    public void User_Create_SetsStateAndTrimsUsername()
    {
        var user = User.Create("  alice ", Hash, UserRole.Admin);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("alice", user.Username);
        Assert.Equal(Hash, user.PasswordHash);
        Assert.Equal(UserRole.Admin, user.Role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void User_Create_MissingUsername_Throws(string? username)
    {
        Assert.Throws<DomainException>(() => User.Create(username!, Hash, UserRole.User));
    }

    [Fact]
    public void User_Create_TooLongUsername_Throws()
    {
        Assert.Throws<DomainException>(() => User.Create(new string('u', User.UsernameMaxLength + 1), Hash, UserRole.User));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void User_Create_MissingPasswordHash_Throws(string? hash)
    {
        Assert.Throws<DomainException>(() => User.Create("alice", hash!, UserRole.User));
    }

    [Fact]
    public void User_Create_TooLongPasswordHash_Throws()
    {
        Assert.Throws<DomainException>(() => User.Create("alice", new string('h', User.PasswordHashMaxLength + 1), UserRole.User));
    }

    [Fact]
    public void User_Create_UndefinedRole_Throws()
    {
        Assert.Throws<DomainException>(() => User.Create("alice", Hash, (UserRole)42));
    }
}
