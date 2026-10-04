// src/Ordering/Ordering.Application/Commands/CreateOrderHandler.cs
using Ordering.Application.Abstractions;
using Ordering.Domain.Entities;

namespace Ordering.Application.Commands;

public class CreateOrderHandler(IOrderRepository repository)
{
    public async Task<Guid> HandleAsync(CreateOrderCommand command, CancellationToken ct = default)
    {
        var order = Order.Create(command.CustomerId);

        foreach (var item in command.Items)
            order.AddItem(item.ProductId, item.UnitPrice, item.Quantity);

        await repository.AddAsync(order, ct);
        await repository.SaveChangesAsync(ct);

        return order.Id;
    }
}