using Inventory.Domain.Entities;

namespace Inventory.Application.Products;

/// <summary>A product and its stock levels.</summary>
/// <param name="Id">Product id.</param>
/// <param name="Sku">Unique stock-keeping unit.</param>
/// <param name="Name">Display name.</param>
/// <param name="TotalQuantity">Units owned in total.</param>
/// <param name="ReservedQuantity">Units held by Active reservations.</param>
/// <param name="SoldQuantity">Units sold through confirmed reservations.</param>
/// <param name="AvailableQuantity">Units that can still be reserved: total − reserved − sold.</param>
public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    int TotalQuantity,
    int ReservedQuantity,
    int SoldQuantity,
    int AvailableQuantity)
{
    public static ProductResponse From(Product product) => new(
        product.Id,
        product.Sku,
        product.Name,
        product.TotalQuantity,
        product.ReservedQuantity,
        product.SoldQuantity,
        product.AvailableQuantity);
}
