using PaymentEntity = Payment.Domain.Entities.Payment;
using Payment.Application.Abstractions;
using Payment.Infrastructure.Context;

namespace Payment.Infrastructure.Repositories;

public class PaymentRepository(PaymentDbContext context) : IPaymentRepository
{
    public async Task AddAsync(PaymentEntity item, CancellationToken ct = default) =>
        await context.Payments.AddAsync(item, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await context.SaveChangesAsync(ct);
}