namespace Inventory.Application.Common.Exceptions;

/// <summary>
/// The operation could not complete because of transient contention (e.g. database lock timeouts or deadlocks)
/// even after retrying. Nothing was changed; the client may retry later.
/// </summary>
public sealed class TemporarilyUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
