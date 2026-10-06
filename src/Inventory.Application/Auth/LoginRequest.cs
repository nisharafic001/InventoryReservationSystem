namespace Inventory.Application.Auth;

/// <summary>Credentials.</summary>
/// <param name="Username">Case-insensitive username.</param>
/// <param name="Password">The user's password.</param>
public sealed record LoginRequest(string? Username, string? Password)
{
    // Keeps the password out of logs and debugger displays should the record ever be printed.
    public override string ToString() => $"LoginRequest {{ Username = {Username} }}";
}

/// <summary>An issued access token.</summary>
/// <param name="AccessToken">Signed JWT carrying <c>sub</c>, <c>username</c> and <c>role</c>. Send as <c>Authorization: Bearer {token}</c>.</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
/// <param name="ExpiresAtUtc">When the token stops being accepted (UTC).</param>
public sealed record LoginResponse(string AccessToken, string TokenType, DateTime ExpiresAtUtc);
