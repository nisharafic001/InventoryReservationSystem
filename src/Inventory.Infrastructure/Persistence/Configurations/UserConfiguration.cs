using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public const int RoleMaxLength = 16;

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.Username).IsRequired().HasMaxLength(User.UsernameMaxLength);
        builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(User.PasswordHashMaxLength);
        builder.Property(u => u.Role).IsRequired().HasConversion<string>().HasMaxLength(RoleMaxLength);

        // Case-insensitive under MySQL's default collation, so "Admin" and "admin" are the same user.
        builder.HasIndex(u => u.Username).IsUnique().HasDatabaseName("UX_Users_Username");
    }
}
