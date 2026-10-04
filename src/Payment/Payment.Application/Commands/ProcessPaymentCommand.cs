// src/Payment/Payment.Application/Commands/ProcessPaymentCommand.cs
namespace Payment.Application.Commands;

public record ProcessPaymentCommand(Guid OrderId, decimal Amount, bool ForceFail = false);
public record ProcessPaymentResult(bool Succeeded, string? Reason);