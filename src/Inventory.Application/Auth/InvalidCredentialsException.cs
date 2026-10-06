using Inventory.Application.Common.Exceptions;

namespace Inventory.Application.Auth;

/// <summary>Deliberately does not say whether the username or the password was wrong.</summary>
public sealed class InvalidCredentialsException() : UnauthorizedException("Invalid username or password.");
