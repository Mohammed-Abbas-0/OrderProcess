// src/Inventory/Inventory.Application/Commands/ReserveStockResult.cs
namespace Inventory.Application.Commands;

public record ReserveStockResult(bool Succeeded, string? Reason);