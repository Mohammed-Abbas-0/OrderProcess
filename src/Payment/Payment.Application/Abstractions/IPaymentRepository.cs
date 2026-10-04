using PaymentEntity = Payment.Domain.Entities.Payment;

namespace Payment.Application.Abstractions;

public interface IPaymentRepository
{
    Task AddAsync(PaymentEntity item, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}