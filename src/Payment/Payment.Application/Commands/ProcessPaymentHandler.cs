using PaymentEntity = Payment.Domain.Entities.Payment;
using Payment.Application.Abstractions;

namespace Payment.Application.Commands;

public class ProcessPaymentHandler(IPaymentRepository repository)
{
    public async Task<ProcessPaymentResult> HandleAsync(ProcessPaymentCommand command, CancellationToken ct = default)
    {
        var payment = PaymentEntity.Create(command.OrderId, command.Amount);
        var succeeded = payment.Process(command.ForceFail);

        await repository.AddAsync(payment, ct);
        await repository.SaveChangesAsync(ct);

        return new ProcessPaymentResult(succeeded, succeeded ? null : "Payment declined.");
    }
}