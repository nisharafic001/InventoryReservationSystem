using System.Text;

namespace Inventory.Infrastructure.Authentication;

/// <summary>
/// Bound from the <c>Jwt</c> configuration section. <see cref="SigningKey"/> must come from a secret store or
/// environment (<c>Jwt__SigningKey</c>); it is intentionally absent from appsettings.json.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HMAC-SHA256 needs at least a 256-bit key.</summary>
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string SigningKey { get; set; } = string.Empty;
    public int ExpirationMinutes { get; set; } = 60;

    internal bool IsValid(out string error)
    {
        error = string.IsNullOrWhiteSpace(Issuer) ? "Jwt:Issuer is required."
            : string.IsNullOrWhiteSpace(Audience) ? "Jwt:Audience is required."
            : Encoding.UTF8.GetByteCount(SigningKey) < MinimumSigningKeyBytes
                ? $"Jwt:SigningKey must be at least {MinimumSigningKeyBytes} bytes; set it via Jwt__SigningKey or user secrets."
            : ExpirationMinutes is < 1 or > 24 * 60 ? "Jwt:ExpirationMinutes must be between 1 and 1440."
            : string.Empty;
        return error.Length == 0;
    }
}
