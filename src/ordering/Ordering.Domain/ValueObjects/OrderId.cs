using Ordering.Domain.Common;

namespace Ordering.Domain.ValueObjects;

public sealed record OrderId
{
    public Guid Value { get; }

    private OrderId(Guid value) => Value = value;

    public static OrderId New() => new(Guid.NewGuid());

    public static OrderId From(Guid value) =>
        value == Guid.Empty
            ? throw new DomainException("OrderId cannot be empty.")
            : new(value);
}