// src/Inventory/Inventory.Application/Abstractions/IStockItemRepository.cs
using Inventory.Domain.Entities;

namespace Inventory.Application.Abstractions;

public interface IStockItemRepository
{
    Task<StockItem?> GetByProductIdAsync(Guid productId, CancellationToken ct = default);
    Task AddAsync(StockItem item, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}