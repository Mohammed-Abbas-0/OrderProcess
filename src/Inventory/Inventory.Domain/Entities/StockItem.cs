// src/Inventory/Inventory.Domain/Entities/StockItem.cs
using Inventory.Domain.Common;
using Inventory.Domain.Events;

namespace Inventory.Domain.Entities;

public class StockItem
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public Guid ProductId { get; private set; }
    public int AvailableQuantity { get; private set; }
    public int ReservedQuantity { get; private set; }
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    private StockItem() { }   // لـ EF Core بس

    private StockItem(Guid productId, int availableQuantity, int reservedQuantity)
        => (ProductId, AvailableQuantity, ReservedQuantity) = (productId, availableQuantity, reservedQuantity);

    public static StockItem Create(Guid productId, int availableQuantity)
    {
        if (productId == Guid.Empty) throw new DomainException("ProductId is required.");
        if (availableQuantity < 0) throw new DomainException("Available quantity cannot be negative.");
        return new StockItem(productId, availableQuantity, reservedQuantity: 0);
    }

    public ReserveResult Reserve(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be greater than zero.");
        // خطأ استخدام (Bug محتمل عند الـ Caller) → Exception

        if (AvailableQuantity < quantity)
        {
            _domainEvents.Add(new StockReservationFailed(ProductId, quantity, "Insufficient available quantity."));
            return ReserveResult.Fail("Insufficient available quantity.");
            // مسار Business طبيعي (مخزون خلص) → Result، مش Exception
        }

        AvailableQuantity -= quantity;
        ReservedQuantity += quantity;
        _domainEvents.Add(new StockReserved(ProductId, quantity));
        return ReserveResult.Success();
    }

    public void Release(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be greater than zero.");
        if (ReservedQuantity < quantity)
            throw new DomainException("Insufficient reserved quantity to release.");
        // دي Exception لأن الـ Caller (الـ Saga Orchestrator) هو نفسه اللي حجز الكمية دي،
        // فلو الأرقام مش متطابقة، ده Bug في منطق الـ Saga، مش سيناريو Business عادي.

        ReservedQuantity -= quantity;
        AvailableQuantity += quantity;
        _domainEvents.Add(new StockReleased(ProductId, quantity));
    }

    public void Confirm(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be greater than zero.");
        if (ReservedQuantity < quantity)
            throw new DomainException("Insufficient reserved quantity to confirm.");
        // نفس المنطق: الـ Saga هي اللي بتنادي Confirm بعد Reserve ناجح، فأي Mismatch هنا Bug.

        ReservedQuantity -= quantity;
        _domainEvents.Add(new StockConfirmed(ProductId, quantity));
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}