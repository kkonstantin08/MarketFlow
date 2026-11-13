using Microsoft.EntityFrameworkCore;

namespace MarketFlow.Api;

public sealed class MarketFlowDbContext(DbContextOptions<MarketFlowDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Supply> Supplies => Set<Supply>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.Property(product => product.Name).HasMaxLength(120).IsRequired();
            entity.Property(product => product.Sku).HasMaxLength(40).IsRequired();
            entity.HasIndex(product => product.Sku).IsUnique().HasDatabaseName("uq_products_sku");
        });

        modelBuilder.Entity<Warehouse>(entity => entity.Property(warehouse => warehouse.Name).HasMaxLength(120).IsRequired());

        modelBuilder.Entity<Supply>(entity =>
        {
            entity.Property(supply => supply.Reference).HasMaxLength(40).IsRequired();
            entity.Property(supply => supply.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(supply => supply.Reference).IsUnique().HasDatabaseName("uq_supplies_reference");
            entity.ToTable(table => table.HasCheckConstraint("ck_supplies_quantity_positive", "\"Quantity\" > 0"));
            entity.HasOne<Product>().WithMany().HasForeignKey(supply => supply.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(supply => supply.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.Property(reservation => reservation.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.ToTable(table => table.HasCheckConstraint("ck_reservations_quantity_positive", "\"Quantity\" > 0"));
            entity.HasOne<Product>().WithMany().HasForeignKey(reservation => reservation.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(reservation => reservation.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Stock>(entity =>
        {
            entity.HasKey(stock => new { stock.ProductId, stock.WarehouseId });
            entity.ToTable(table => table.HasCheckConstraint("ck_stocks_quantities", "\"OnHand\" >= 0 AND \"Reserved\" >= 0 AND \"Reserved\" <= \"OnHand\""));
            entity.HasOne<Product>().WithMany().HasForeignKey(stock => stock.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(stock => stock.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockMovement>(entity =>
        {
            entity.Property(movement => movement.Type).HasMaxLength(20).IsRequired();
            entity.Property(movement => movement.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(movement => movement.SupplyId).IsUnique();
            entity.HasIndex(movement => movement.ReservationId).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint("ck_stock_movements_operation", "(\"Type\" = 'supply' AND \"SupplyId\" IS NOT NULL AND \"ReservationId\" IS NULL AND \"OnHandDelta\" > 0 AND \"ReservedDelta\" = 0) OR (\"Type\" = 'reservation' AND \"SupplyId\" IS NULL AND \"ReservationId\" IS NOT NULL AND \"OnHandDelta\" = 0 AND \"ReservedDelta\" > 0)"));
            entity.HasOne<Supply>().WithMany().HasForeignKey(movement => movement.SupplyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Reservation>().WithMany().HasForeignKey(movement => movement.ReservationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Product>().WithMany().HasForeignKey(movement => movement.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(movement => movement.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
