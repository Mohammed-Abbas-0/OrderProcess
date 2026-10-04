// src/Payment/Payment.Domain/Events/PaymentEvents.cs
using Payment.Domain.Common;
namespace Payment.Domain.Events;

public sealed record PaymentCompleted(Guid OrderId, decimal Amount) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

public sealed record PaymentFailed(Guid OrderId, string Reason) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}