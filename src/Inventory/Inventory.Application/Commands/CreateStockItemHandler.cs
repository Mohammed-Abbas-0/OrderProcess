// src/Inventory/Inventory.Application/Commands/CreateStockItemHandler.cs
using Inventory.Application.Abstractions;
using Inventory.Domain.Entities;

namespace Inventory.Application.Commands;

public class CreateStockItemHandler(IStockItemRepository repository)
{
    public async Task HandleAsync(CreateStockItemCommand command, CancellationToken ct = default)
    {
        var item = StockItem.Create(command.ProductId, command.AvailableQuantity);
        await repository.AddAsync(item, ct);
        await repository.SaveChangesAsync(ct);
    }
}