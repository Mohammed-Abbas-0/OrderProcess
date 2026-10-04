namespace Ordering.Application.Commands;
public record CreateOrderItemDto(Guid ProductId, decimal UnitPrice, int Quantity);

