using Ordering.Domain.Common;

namespace Ordering.Domain.Events;
public sealed record OrderSubmitted(Guid OrderId, decimal Total) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}
public sealed record OrderCancelled(Guid OrderId) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

public sealed record OrderConfirmed(Guid OrderId, Guid CustomerId, decimal Total) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}


// Stock Items Events
public sealed record StockItemReserved(Guid ProductId, decimal Quantity) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

public sealed record StockItemConfirmed(Guid ProductId, decimal Quantity) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

public sealed record StockItemReleased(Guid ProductId, decimal Quantity) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}