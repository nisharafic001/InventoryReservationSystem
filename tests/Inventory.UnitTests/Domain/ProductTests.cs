using Inventory.Domain.Entities;
using Inventory.Domain.Exceptions;

namespace Inventory.UnitTests.Domain;

public class ProductTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Product NewProduct(int total = 10) => Product.Create("SKU-1", "Widget", total, Now);

    [Fact]
    public void Product_AvailableQuantity_IsCalculatedCorrectly()
    {
        var product = NewProduct(total: 10);

        product.Reserve(5, Now);
        product.ConfirmSale(2, Now);

        Assert.Equal(10, product.TotalQuantity);
        Assert.Equal(3, product.ReservedQuantity);
        Assert.Equal(2, product.SoldQuantity);
        Assert.Equal(5, product.AvailableQuantity);
    }

    [Fact]
    public void Product_Create_SetsInitialState()
    {
        var product = NewProduct(total: 7);

        Assert.NotEqual(Guid.Empty, product.Id);
        Assert.Equal("SKU-1", product.Sku);
        Assert.Equal("Widget", product.Name);
        Assert.Equal(7, product.AvailableQuantity);
        Assert.Equal(0, product.ReservedQuantity);
        Assert.Equal(0, product.SoldQuantity);
        Assert.Equal(Now, product.CreatedAtUtc);
        Assert.Equal(Now, product.UpdatedAtUtc);
    }

    [Fact]
    public void Product_Create_NegativeTotal_Throws()
    {
        Assert.Throws<DomainException>(() => NewProduct(total: -1));
    }

    [Theory]
    [InlineData("", "Widget")]
    [InlineData("SKU-1", " ")]
    public void Product_Create_MissingSkuOrName_Throws(string sku, string name)
    {
        Assert.Throws<DomainException>(() => Product.Create(sku, name, 1, Now));
    }

    [Fact]
    public void Product_Create_TooLongSkuOrName_Throws()
    {
        Assert.Throws<DomainException>(() => Product.Create(new string('S', Product.SkuMaxLength + 1), "Widget", 1, Now));
        Assert.Throws<DomainException>(() => Product.Create("SKU-1", new string('N', Product.NameMaxLength + 1), 1, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Product_Reserve_NonPositiveQuantity_Throws(int quantity)
    {
        var product = NewProduct();

        Assert.Throws<DomainException>(() => product.Reserve(quantity, Now));
    }

    [Fact]
    public void Product_Reserve_MoreThanAvailable_ThrowsAndLeavesStockUnchanged()
    {
        var product = NewProduct(total: 3);

        var ex = Assert.Throws<InsufficientStockException>(() => product.Reserve(4, Now));

        Assert.Equal(3, ex.Available);
        Assert.Equal(0, product.ReservedQuantity);
        Assert.Equal(3, product.AvailableQuantity);
    }

    [Fact]
    public void Product_Reserve_AllAvailable_LeavesZeroAvailable()
    {
        var product = NewProduct(total: 3);

        product.Reserve(3, Now);

        Assert.Equal(0, product.AvailableQuantity);
        Assert.Throws<InsufficientStockException>(() => product.Reserve(1, Now));
    }

    [Fact]
    public void Product_ReleaseReservation_ReturnsStockToAvailable()
    {
        var product = NewProduct(total: 10);
        product.Reserve(4, Now);
        var later = Now.AddMinutes(1);

        product.ReleaseReservation(4, later);

        Assert.Equal(0, product.ReservedQuantity);
        Assert.Equal(10, product.AvailableQuantity);
        Assert.Equal(later, product.UpdatedAtUtc);
    }

    [Fact]
    public void Product_ReleaseReservation_MoreThanReserved_Throws()
    {
        var product = NewProduct();
        product.Reserve(2, Now);

        Assert.Throws<DomainException>(() => product.ReleaseReservation(3, Now));
        Assert.Equal(2, product.ReservedQuantity);
    }

    [Fact]
    public void Product_ConfirmSale_MoreThanReserved_Throws()
    {
        var product = NewProduct();
        product.Reserve(2, Now);

        Assert.Throws<DomainException>(() => product.ConfirmSale(3, Now));
        Assert.Equal(2, product.ReservedQuantity);
        Assert.Equal(0, product.SoldQuantity);
    }
}
