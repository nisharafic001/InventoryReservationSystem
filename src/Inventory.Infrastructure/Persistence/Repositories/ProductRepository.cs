using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Products;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Inventory.Infrastructure.Persistence.Repositories;

public sealed class ProductRepository(AppDbContext dbContext) : IProductRepository
{
    private const string SkuIndexName = "UX_Products_Sku";

    public Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<bool> SkuExistsAsync(string sku, CancellationToken cancellationToken) =>
        dbContext.Products.AnyAsync(p => p.Sku == sku, cancellationToken);

    public async Task AddAsync(Product product, CancellationToken cancellationToken)
    {
        dbContext.Products.Add(product);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateSku(ex))
        {
            dbContext.Entry(product).State = EntityState.Detached;
            throw new DuplicateSkuException(product.Sku);
        }
    }

    private static bool IsDuplicateSku(DbUpdateException ex) =>
        ex.InnerException is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry } mySqlException
        && mySqlException.Message.Contains(SkuIndexName, StringComparison.Ordinal);
}
