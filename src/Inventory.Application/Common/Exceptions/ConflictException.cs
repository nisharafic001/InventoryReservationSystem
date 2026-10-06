namespace Inventory.Application.Common.Exceptions;

/// <summary>
/// The request conflicts with the current state of a resource.
/// </summary>
public abstract class ConflictException(string message) : Exception(message);
