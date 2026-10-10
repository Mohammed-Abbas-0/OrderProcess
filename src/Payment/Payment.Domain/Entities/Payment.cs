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

    // قرار الدفع حتمي (Deterministic): النتيجة دالة خالصة في المدخلات.
    // ينجح الدفع إلا إذا طُلب إفشاله صراحةً (forceFail) بناءً على حدّ المبلغ في الـ consumer.
    // أزلنا العشوائية (Random) نهائياً حتى يصبح سلوك الـ Retry/Circuit Breaker قابلاً لإعادة الإنتاج.
    public bool Process(bool forceFail = false)
    {
        if (Status != PaymentStatus.Pending)
            throw new DomainException("Only a pending payment can be processed.");

        var success = !forceFail;

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