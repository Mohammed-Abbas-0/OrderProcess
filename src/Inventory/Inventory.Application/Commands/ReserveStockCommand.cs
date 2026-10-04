// src/Inventory/Inventory.Application/Commands/ReserveStockCommand.cs
namespace Inventory.Application.Commands;

public record ReserveStockCommand(Guid ProductId, int Quantity);