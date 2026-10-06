using Inventory.Domain.Entities;
using Inventory.Domain.Exceptions;

namespace Inventory.UnitTests.Domain;

public class ProductTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Product NewProduct(int total = 10) => Product.Create("SKU-1", "Widget", total, Now);

    [Fact]
    public void Product_Create_SetsInitialState()
    {
        var product = NewProduct(total: 7);

        Assert.NotEqual(Guid.Empty, product.Id);
        Assert.Equal("SKU-1", product.Sku);
        Assert.Equal("Widget", product.Name);
        Assert.Equal(7, product.TotalQuantity);
        Assert.Equal(7, product.AvailableQuantity);
        Assert.Equal(0, product.ReservedQuantity);
        Assert.Equal(0, product.SoldQuantity);
        Assert.Equal(Now, product.CreatedAtUtc);
        Assert.Equal(Now, product.UpdatedAtUtc);
    }

    [Fact]
    public void Product_Create_TrimsSkuAndName()
    {
        var product = Product.Create("  SKU-1 ", " Widget  ", 1, Now);

        Assert.Equal("SKU-1", product.Sku);
        Assert.Equal("Widget", product.Name);
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
}
