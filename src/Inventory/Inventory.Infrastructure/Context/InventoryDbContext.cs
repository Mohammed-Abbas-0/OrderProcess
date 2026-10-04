// src/Inventory/Inventory.Infrastructure/Context/InventoryDbContext.cs
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Context;

public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StockItem>(builder =>
        {
            builder.HasKey(s => s.ProductId);
            builder.Property(s => s.AvailableQuantity).IsRequired();
            builder.Property(s => s.ReservedQuantity).IsRequired();
            builder.Ignore(s => s.DomainEvents);
        });
    }
}