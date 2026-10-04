// src/Payment/Payment.Domain/Common/IDomainEvent.cs
namespace Payment.Domain.Common;
public interface IDomainEvent { DateTime OccurredOn { get; } }