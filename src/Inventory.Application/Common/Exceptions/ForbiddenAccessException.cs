namespace Inventory.Application.Common.Exceptions;

/// <summary>
/// The caller is authenticated but not allowed to act on the resource.
/// </summary>
public sealed class ForbiddenAccessException(string message) : Exception(message);
