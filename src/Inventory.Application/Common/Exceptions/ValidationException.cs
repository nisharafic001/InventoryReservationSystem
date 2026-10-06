namespace Inventory.Application.Common.Exceptions;

/// <summary>
/// Request input is invalid. <see cref="Errors"/> maps field names to error messages.
/// </summary>
public sealed class ValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
