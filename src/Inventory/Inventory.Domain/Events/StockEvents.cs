using Inventory.Domain.Common;

namespace Inventory.Domain.Events;

public sealed record StockReserved(Guid ProductId, int Quantity) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

public sealed record StockReservationFailed(Guid ProductId, int RequestedQuantity, string Reason) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

public sealed record StockReleased(Guid ProductId, int Quantity) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

public sealed record StockConfirmed(Guid ProductId, int Quantity) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}
