using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Inventory.IntegrationTests.Persistence;

/// <summary>
/// Verifies the EF Core model mapping. Building the model does not open a database connection.
/// </summary>
public class AppDbContextModelTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql("Server=localhost;Database=model_only", ServerVersion.Parse("8.0.41-mysql"))
            .Options;

        using var context = new AppDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType Entity<T>() => BuildModel().FindEntityType(typeof(T))!;

    [Fact]
    public void Product_Sku_HasUniqueIndex()
    {
        var index = Assert.Single(Entity<Product>().GetIndexes(), i => i.GetDatabaseName() == "UX_Products_Sku");

        Assert.True(index.IsUnique);
        Assert.Equal(nameof(Product.Sku), Assert.Single(index.Properties).Name);
    }

    [Fact]
    public void Product_AvailableQuantity_IsNotMapped()
    {
        Assert.Null(Entity<Product>().FindProperty(nameof(Product.AvailableQuantity)));
    }

    [Theory]
    [InlineData("IX_Reservations_ProductId", new[] { nameof(Reservation.ProductId) })]
    [InlineData("IX_Reservations_Status", new[] { nameof(Reservation.Status) })]
    [InlineData("IX_Reservations_ExpiresAtUtc", new[] { nameof(Reservation.ExpiresAtUtc) })]
    [InlineData("IX_Reservations_Status_ExpiresAtUtc", new[] { nameof(Reservation.Status), nameof(Reservation.ExpiresAtUtc) })]
    public void Reservation_HasExpectedIndexes(string name, string[] columns)
    {
        var index = Assert.Single(Entity<Reservation>().GetIndexes(), i => i.GetDatabaseName() == name);

        Assert.Equal(columns, index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Reservation_ProductId_IsRestrictForeignKeyToProduct()
    {
        var fk = Assert.Single(Entity<Reservation>().GetForeignKeys());

        Assert.Equal(typeof(Product), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void Reservation_Status_IsStoredAsString()
    {
        var status = Entity<Reservation>().FindProperty(nameof(Reservation.Status))!;

        var converter = status.GetTypeMapping().Converter;

        Assert.Equal(typeof(string), status.GetProviderClrType());
        Assert.NotNull(converter);
        Assert.Equal("Confirmed", converter.ConvertToProvider(ReservationStatus.Confirmed));
    }

    [Fact]
    public void DateTimeProperties_UseUtcConverters()
    {
        var dateProperties = BuildModel().GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
            .ToList();

        Assert.NotEmpty(dateProperties);
        Assert.All(dateProperties, p =>
            Assert.True(p.GetValueConverter() is UtcDateTimeConverter or NullableUtcDateTimeConverter, p.Name));
    }

    [Fact]
    public void UtcDateTimeConverter_MarksValuesReadFromDatabaseAsUtc()
    {
        var converter = new UtcDateTimeConverter();
        var fromDb = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);

        var value = (DateTime)converter.ConvertFromProvider(fromDb)!;

        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(fromDb.Ticks, value.Ticks);
    }
}
