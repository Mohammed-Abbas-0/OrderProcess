using Ordering.Domain.Common;

namespace Ordering.Domain.Entities;
public class StockItem
{
    public Guid ProductId { get;  }
    public decimal AvailableQuantity { get; private set; }
    public decimal ReservedQuantity { get; private set; }


    private StockItem(Guid productId, decimal availableQuantity, decimal reservedQuantity)
        => (ProductId, AvailableQuantity, ReservedQuantity) = (productId, availableQuantity, reservedQuantity);
    

    public static StockItem Create(Guid productId, decimal availableQuantity, decimal reservedQuantity)
    {
        if (productId == Guid.Empty) throw new DomainException("ProductId is required.");
        if (availableQuantity < 0) throw new DomainException("Available quantity cannot be negative.");
        return new StockItem(productId, availableQuantity, reservedQuantity:0);
    }

    public ReserveResult Reserve(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be greater than zero.");
        // ده لسه Exception، لأنه غلط استخدام (Caller بعت رقم غلط)، مش قرار Business

        if (AvailableQuantity < quantity)
            return ReserveResult.Fail("Insufficient available quantity.");
        // ده Result، لأنه مسار متوقع

        AvailableQuantity -= quantity;
        ReservedQuantity += quantity;
        return ReserveResult.Success();
    }

    public void Release(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be greater than zero.");
        if (ReservedQuantity < quantity)
            throw new DomainException("Insufficient reserved quantity to release.");

        ReservedQuantity -= quantity;
        AvailableQuantity += quantity;
    }

    public void Confirm(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be greater than zero.");
        if (ReservedQuantity < quantity)
            throw new DomainException("Insufficient reserved quantity to confirm.");

        ReservedQuantity -= quantity;
    }
}
public sealed record ReserveResult
{
    public bool Succeeded { get; }
    public string? Reason { get; }

    private ReserveResult(bool succeeded, string? reason) => (Succeeded, Reason) = (succeeded, reason);

    public static ReserveResult Success() => new(true, null);
    public static ReserveResult Fail(string reason) => new(false, reason);
}