namespace Inventory.Application.Common.Exceptions;

/// <summary>
/// A resource referenced by the request does not exist.
/// </summary>
public abstract class NotFoundException(string message) : Exception(message);
