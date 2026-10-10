using Contracts.Commands;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Payment.Api.Gateway;
using Payment.Application.Abstractions;
using PaymentEntity = Payment.Domain.Entities.Payment;

namespace Payment.Api.Consumers;

// consumer الدفع: يستهلك ProcessPayment من الـ Saga، يكتب صف Payment، وينشر النتيجة
// (PaymentCompleted/PaymentFailed) عبر الـ Outbox في نفس المعاملة (بلا dual-write).
//
// الحدود (المبدأ الموجّه للمرحلة 3):
//  - عطل بنية تحتية عابر  -> استثناء (PaymentGatewayException) -> Retry/Delayed Redelivery + Circuit Breaker.
//  - رفض دفع عمليّ (المبلغ >= الحدّ) -> نتيجة نهائية (Result) تُنشَر كـ PaymentFailed، بلا استثناء،
//    فلا تُعيد المحاولة ولا تفتح القاطع. هكذا يبقى تمييز العابر/النهائي واضحاً للطبقة المرِنة.
public class ProcessPaymentConsumer(
    IPaymentRepository repository,
    IPaymentGateway gateway,
    IConfiguration configuration)
    : IConsumer<ProcessPayment>
{
    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var command = context.Message;

        // (أ) الاتصال ببوابة الدفع (طبقة بنية تحتية): قد يرمي عطلاً عابراً.
        //     نتركه يرتفع ليتولّاه الـ Retry/Delayed Redelivery ويُحسب على الـ Circuit Breaker.
        await gateway.AuthorizeAsync(command.OrderId, command.Amount, context.CancellationToken);

        // (ب) القرار العمليّ الحتميّ: نفشل حتماً إذا كان المبلغ >= الحدّ (الافتراضي 1000)، وإلا ننجح.
        //     لا عشوائية — النتيجة دالة خالصة في المدخلات، فسلوك الطبقة المرِنة قابل لإعادة الإنتاج.
        var thresholdValue = configuration["Payment:FailAmountThreshold"];
        var forceFail = decimal.TryParse(thresholdValue, out var threshold)
            && command.Amount >= threshold;

        var payment = PaymentEntity.Create(command.OrderId, command.Amount);
        var succeeded = payment.Process(forceFail);

        await repository.AddAsync(payment);

        if (succeeded)
            await context.Publish(new Contracts.Events.PaymentCompleted(command.CorrelationId, command.OrderId));
        else
            await context.Publish(new Contracts.Events.PaymentFailed(command.CorrelationId, command.OrderId, "Payment declined."));

        await repository.SaveChangesAsync();
    }
}
