namespace Inventory.Domain.Exceptions;

/// <summary>
/// Base type for violations of domain invariants or business rules.
/// </summary>
public class DomainException(string message) : Exception(message);
