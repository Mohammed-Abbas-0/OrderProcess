// src/Inventory/Inventory.Infrastructure/Repositories/StockItemRepository.cs
using Microsoft.EntityFrameworkCore;
using Inventory.Application.Abstractions;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Context;

namespace Inventory.Infrastructure.Repositories;

public class StockItemRepository(InventoryDbContext context) : IStockItemRepository
{
    public async Task<StockItem?> GetByProductIdAsync(Guid productId, CancellationToken ct = default) =>
        await context.StockItems.FirstOrDefaultAsync(s => s.ProductId == productId, ct);

    public async Task AddAsync(StockItem item, CancellationToken ct = default) =>
        await context.StockItems.AddAsync(item, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await context.SaveChangesAsync(ct);
}