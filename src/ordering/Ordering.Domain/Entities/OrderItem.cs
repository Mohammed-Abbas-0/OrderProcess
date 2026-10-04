using Ordering.Domain.Common;
namespace Ordering.Domain.Entities;

public class OrderItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid ProductId { get; }
    public decimal UnitPrice { get; }
    public int Quantity { get; }
    public decimal LineTotal => UnitPrice * Quantity;
    public OrderItem()
    {
        
    }
    internal OrderItem(Guid productId, decimal unitPrice, int quantity)
    {
        if (productId == Guid.Empty) throw new DomainException("ProductId is required.");
        if (unitPrice < 0) throw new DomainException("Price cannot be negative.");
        if (quantity <= 0) throw new DomainException("Quantity must be greater than zero.");
        (ProductId, UnitPrice, Quantity) = (productId, unitPrice, quantity);
    }
}