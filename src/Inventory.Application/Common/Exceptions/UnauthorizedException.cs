namespace Inventory.Application.Common.Exceptions;

/// <summary>
/// The caller could not be authenticated.
/// </summary>
public abstract class UnauthorizedException(string message) : Exception(message);
