namespace Inventory.Application.Products;

/// <summary>A new product.</summary>
/// <param name="Sku">Unique stock-keeping unit, case-insensitive, max 64 characters. Example: <c>IPHONE-001</c>.</param>
/// <param name="Name">Display name, max 200 characters.</param>
/// <param name="TotalQuantity">Units in stock; zero or more.</param>
// Members are nullable so missing fields reach validation instead of defaulting silently.
public sealed record CreateProductRequest(string? Sku, string? Name, int? TotalQuantity);
