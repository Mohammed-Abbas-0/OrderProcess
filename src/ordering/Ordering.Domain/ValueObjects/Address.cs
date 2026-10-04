using Ordering.Domain.Common;
namespace Ordering.Domain.ValueObjects;

public sealed record Address
{
    public string Street { get; }
    public string City { get; }
    public string Country { get; }

    private Address(string street, string city, string country) =>
        (Street, City, Country) = (street, city, country);

    public static Address Of(string street, string city, string country)
    {
        if (string.IsNullOrWhiteSpace(street)) throw new DomainException("Street is required.");
        if (string.IsNullOrWhiteSpace(city)) throw new DomainException("City is required.");
        if (string.IsNullOrWhiteSpace(country)) throw new DomainException("Country is required.");
        return new Address(street.Trim(), city.Trim(), country.Trim());
    }
}