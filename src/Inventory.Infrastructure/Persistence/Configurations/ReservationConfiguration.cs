using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence.Configurations;

public sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public const int UserIdMaxLength = 128;
    public const int StatusMaxLength = 16;

    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations", t =>
            t.HasCheckConstraint("CK_Reservations_Quantity_Positive", "`Quantity` > 0"));

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.ProductId).IsRequired();
        builder.Property(r => r.UserId).IsRequired().HasMaxLength(UserIdMaxLength);
        builder.Property(r => r.Quantity).IsRequired();

        // Stored as text so rows stay readable and enum reordering cannot corrupt data.
        builder.Property(r => r.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(StatusMaxLength);

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.ExpiresAtUtc).IsRequired();
        builder.Property(r => r.ConfirmedAtUtc);
        builder.Property(r => r.CancelledAtUtc);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(r => r.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Reservations_Products_ProductId");

        builder.HasIndex(r => r.ProductId).HasDatabaseName("IX_Reservations_ProductId");
        builder.HasIndex(r => r.Status).HasDatabaseName("IX_Reservations_Status");
        builder.HasIndex(r => r.ExpiresAtUtc).HasDatabaseName("IX_Reservations_ExpiresAtUtc");

        // Expiry sweep: WHERE Status = 'Active' AND ExpiresAtUtc <= @now
        builder.HasIndex(r => new { r.Status, r.ExpiresAtUtc })
            .HasDatabaseName("IX_Reservations_Status_ExpiresAtUtc");
    }
}
