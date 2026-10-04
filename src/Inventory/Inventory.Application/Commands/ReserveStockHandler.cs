// src/Inventory/Inventory.Application/Commands/ReserveStockHandler.cs
using Inventory.Application.Abstractions;

namespace Inventory.Application.Commands;

public class ReserveStockHandler(IStockItemRepository repository)
{
    public async Task<ReserveStockResult> HandleAsync(ReserveStockCommand command, CancellationToken ct = default)
    {
        var stockItem = await repository.GetByProductIdAsync(command.ProductId, ct);

        if (stockItem is null)
            return new ReserveStockResult(false, "Product not found.");

        var result = stockItem.Reserve(command.Quantity);

        if (!result.Succeeded)
            return new ReserveStockResult(false, result.Reason);

        await repository.SaveChangesAsync(ct);
        return new ReserveStockResult(true, null);
    }
}