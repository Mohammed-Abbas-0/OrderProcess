namespace Ordering.Application.Commands;
public record CreateOrderCommand(Guid CustomerId, List<CreateOrderItemDto> Items);
