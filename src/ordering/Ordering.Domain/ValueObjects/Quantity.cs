using Ordering.Domain.Common;
namespace Ordering.Domain.ValueObjects;

public sealed record Quantity
{
    public const int Max = 100;

    public int Value { get; }

    private Quantity(int value) => Value = value;

    public static Quantity Of(int value)
    {
        if (value <= 0) throw new DomainException("Quantity must be greater than zero.");
        if (value > Max) throw new DomainException($"Quantity cannot exceed {Max}.");
        return new Quantity(value);
    }

    public Quantity Add(Quantity other) => Of(Value + other.Value);
}