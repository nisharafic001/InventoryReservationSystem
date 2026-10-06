using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", t =>
        {
            t.HasCheckConstraint("CK_Products_TotalQuantity_NonNegative", "`TotalQuantity` >= 0");
            t.HasCheckConstraint("CK_Products_ReservedQuantity_NonNegative", "`ReservedQuantity` >= 0");
            t.HasCheckConstraint("CK_Products_SoldQuantity_NonNegative", "`SoldQuantity` >= 0");
            t.HasCheckConstraint(
                "CK_Products_AvailableQuantity_NonNegative",
                "`ReservedQuantity` + `SoldQuantity` <= `TotalQuantity`");
        });

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Sku).IsRequired().HasMaxLength(Product.SkuMaxLength);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(Product.NameMaxLength);

        builder.Property(p => p.TotalQuantity).IsRequired();
        builder.Property(p => p.ReservedQuantity).IsRequired();
        builder.Property(p => p.SoldQuantity).IsRequired();

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc).IsRequired();

        builder.Ignore(p => p.AvailableQuantity);

        builder.HasIndex(p => p.Sku).IsUnique().HasDatabaseName("UX_Products_Sku");
    }
}
