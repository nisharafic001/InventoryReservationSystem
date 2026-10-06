using System.Security.Claims;
using System.Text;
using Inventory.Application.Abstractions.Authentication;
using Inventory.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Inventory.Infrastructure.Authentication;

public sealed class JwtTokenGenerator(IOptions<JwtOptions> options, TimeProvider timeProvider) : IJwtTokenGenerator
{
    public const string UsernameClaim = "username";
    public const string RoleClaim = "role";

    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Generate(User user)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(settings.ExpirationMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(UsernameClaim, user.Username),
                new Claim(RoleClaim, user.Role.ToString())
            ]),
            SigningCredentials = new SigningCredentials(CreateSigningKey(settings), SecurityAlgorithms.HmacSha256)
        };

        return new AccessToken(_handler.CreateToken(descriptor), expires);
    }

    public static SymmetricSecurityKey CreateSigningKey(JwtOptions settings) =>
        new(Encoding.UTF8.GetBytes(settings.SigningKey));
}
