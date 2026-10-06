using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Entities;

public sealed class Product
{
    public const int SkuMaxLength = 64;
    public const int NameMaxLength = 200;

    // Parameter names match property names so EF Core can bind this constructor when materializing.
    private Product(Guid id, string sku, string name, int totalQuantity, DateTime createdAtUtc)
    {
        Id = id;
        Sku = sku;
        Name = name;
        TotalQuantity = totalQuantity;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public string Sku { get; private set; }
    public string Name { get; private set; }
    public int TotalQuantity { get; private set; }
    public int ReservedQuantity { get; private set; }
    public int SoldQuantity { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public int AvailableQuantity => TotalQuantity - ReservedQuantity - SoldQuantity;

    public static Product Create(string sku, string name, int totalQuantity, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new DomainException("SKU is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Name is required.");

        sku = sku.Trim();
        name = name.Trim();
        if (sku.Length > SkuMaxLength)
            throw new DomainException($"SKU cannot exceed {SkuMaxLength} characters.");
        if (name.Length > NameMaxLength)
            throw new DomainException($"Name cannot exceed {NameMaxLength} characters.");
        EnsureNotNegative(totalQuantity, nameof(TotalQuantity));

        return new Product(Guid.NewGuid(), sku, name, totalQuantity, nowUtc);
    }

    /// <summary>Moves stock from available to reserved.</summary>
    public void Reserve(int quantity, DateTime nowUtc)
    {
        EnsurePositive(quantity);
        if (quantity > AvailableQuantity)
            throw new InsufficientStockException(Id, quantity, AvailableQuantity);

        ReservedQuantity += quantity;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Returns reserved stock to available (reservation cancelled or expired).</summary>
    public void ReleaseReservation(int quantity, DateTime nowUtc)
    {
        EnsurePositive(quantity);
        if (quantity > ReservedQuantity)
            throw new DomainException($"Cannot release {quantity}; only {ReservedQuantity} reserved.");

        ReservedQuantity -= quantity;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Moves stock from reserved to sold (reservation confirmed).</summary>
    public void ConfirmSale(int quantity, DateTime nowUtc)
    {
        EnsurePositive(quantity);
        if (quantity > ReservedQuantity)
            throw new DomainException($"Cannot sell {quantity}; only {ReservedQuantity} reserved.");

        ReservedQuantity -= quantity;
        SoldQuantity += quantity;
        UpdatedAtUtc = nowUtc;
    }

    private static void EnsureNotNegative(int value, string name)
    {
        if (value < 0)
            throw new DomainException($"{name} cannot be negative.");
    }

    private static void EnsurePositive(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity must be greater than zero.");
    }
}
