// src/Inventory/Inventory.Application/Commands/CreateStockItemCommand.cs
namespace Inventory.Application.Commands;

public record CreateStockItemCommand(Guid ProductId, int AvailableQuantity);