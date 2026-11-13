using Microsoft.EntityFrameworkCore;

namespace MarketFlow.Api;

public sealed class MarketFlowDbContext(DbContextOptions<MarketFlowDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.Property(product => product.Name).HasMaxLength(120).IsRequired();
            entity.Property(product => product.Sku).HasMaxLength(40).IsRequired();
            entity.HasIndex(product => product.Sku).IsUnique().HasDatabaseName("uq_products_sku");
        });

        modelBuilder.Entity<Warehouse>(entity => entity.Property(warehouse => warehouse.Name).HasMaxLength(120).IsRequired());
    }
}
