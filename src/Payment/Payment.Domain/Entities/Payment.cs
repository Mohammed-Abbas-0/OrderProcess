// src/Payment/Payment.Domain/Entities/Payment.cs
using Payment.Domain.Common;
using Payment.Domain.Enums;
using Payment.Domain.Events;

namespace Payment.Domain.Entities;

public class Payment
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public decimal Amount { get; private set; }
    public PaymentStatus Status { get; private set; }
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    private Payment() { }   // لـ EF Core

    private Payment(Guid id, Guid orderId, decimal amount)
        => (Id, OrderId, Amount, Status) = (id, orderId, amount, PaymentStatus.Pending);

    public static Payment Create(Guid orderId, decimal amount)
    {
        if (orderId == Guid.Empty) throw new DomainException("OrderId is required.");
        if (amount <= 0) throw new DomainException("Amount must be greater than zero.");
        return new Payment(Guid.NewGuid(), orderId, amount);
    }

    // Simulated processing: 90% success rate, عشان نقدر نختبر الـ Compensation بعدين
    public bool Process(bool forceFail = false)
    {
        if (Status != PaymentStatus.Pending)
            throw new DomainException("Only a pending payment can be processed.");

        var success = !forceFail && Random.Shared.Next(1, 11) <= 9;

        if (success)
        {
            Status = PaymentStatus.Completed;
            _domainEvents.Add(new PaymentCompleted(OrderId, Amount));
        }
        else
        {
            Status = PaymentStatus.Failed;
            _domainEvents.Add(new PaymentFailed(OrderId, "Payment declined."));
        }

        return success;
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}