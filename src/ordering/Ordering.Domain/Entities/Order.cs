using Ordering.Domain.Common;
using Ordering.Domain.Enums;
using Ordering.Domain.Events;

namespace Ordering.Domain.Entities;

public class Order
{
    private readonly List<OrderItem> _items = [];
    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyList<OrderItem> Items => _items.AsReadOnly();
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    public Guid Id { get; }
    public Guid CustomerId { get; }
    public OrderStatus Status { get; private set; } = OrderStatus.Draft;
   
    public decimal Total => _items.Sum(i => i.LineTotal);

    private Order(Guid id, Guid customerId) => (Id, CustomerId) = (id, customerId);

    public static Order Create(Guid customerId)
    {
        if (customerId == Guid.Empty) throw new DomainException("CustomerId is required.");
        return new Order(Guid.NewGuid(), customerId);
    }

    public void AddItem(Guid productId, decimal unitPrice, int quantity)
    {
        if (Status is not OrderStatus.Draft)
            throw new DomainException("Items can only be added to a draft order.");
        _items.Add(new OrderItem(productId, unitPrice, quantity));
    }


    public void Confirm()
    {
        if (Status != OrderStatus.Submitted)
            throw new DomainException("Only a submitted order can be confirmed.");
        Status = OrderStatus.Confirmed;
        _domainEvents.Add(new OrderConfirmed(Id, CustomerId, Total));
    }

    public void Submit()
    {
        if (Status != OrderStatus.Draft)
            throw new DomainException("Only a draft order can be submitted.");
        if (_items.Count == 0)
            throw new DomainException("Cannot submit an empty order.");
        Status = OrderStatus.Submitted;
        _domainEvents.Add(new OrderSubmitted(Id, Total));
    }

    public void Cancel()
    {
        if (Status is OrderStatus.Confirmed or OrderStatus.Cancelled or OrderStatus.Rejected)
            throw new DomainException($"A {Status} order cannot be cancelled.");
        Status = OrderStatus.Cancelled;
        _domainEvents.Add(new OrderCancelled(Id));
    }

    // رفض الطلب: حالة نهائية لفشل عمل لا يتطلّب تعويضاً (فشل حجز المخزون قبل أي حجز فعلي).
    // متمايزة عن Cancel: لا يُفترض وجود مخزون محجوز نرجّعه، فالطلب لم يتجاوز مرحلة الحجز بنجاح.
    public void Reject(string reason)
    {
        if (Status != OrderStatus.Submitted)
            throw new DomainException("Only a submitted order can be rejected.");
        Status = OrderStatus.Rejected;
        _domainEvents.Add(new OrderRejected(Id, reason));
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}