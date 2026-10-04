// src/Inventory/Inventory.Domain/Common/IDomainEvent.cs
namespace Inventory.Domain.Common;

public interface IDomainEvent
{
    DateTime OccurredOn { get; }
}