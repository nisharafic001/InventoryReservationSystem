using Inventory.Infrastructure.Authentication;

namespace Inventory.IntegrationTests.Authentication;

public class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_ThenVerify_Succeeds_AndHashHidesPassword()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.StartsWith("PBKDF2-SHA256.600000.", hash);
        Assert.DoesNotContain("correct horse", hash);
        Assert.True(_hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        Assert.False(_hasher.Verify("wrong", _hasher.Hash("right")));
    }

    [Fact]
    public void Hash_SamePassword_UsesDifferentSalts()
    {
        Assert.NotEqual(_hasher.Hash("same"), _hasher.Hash("same"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("PBKDF2-SHA256.abc.AAAA.AAAA")]
    [InlineData("PBKDF2-SHA256.1000.@@@.AAAA")]
    public void Verify_MissingOrMalformedHash_ReturnsFalse(string? hash)
    {
        Assert.False(_hasher.Verify("anything", hash));
    }
}
