using Microsoft.Extensions.Configuration;

namespace Payment.Api.Gateway;

// محاكاة الاتصال ببوابة دفع خارجية (طبقة بنية تحتية).
// الغرض: مكان حتمي نَحقن فيه عطلاً عابراً (Transient) لإثبات الـ Retry والـ Circuit Breaker،
// مع إبقاء قرار الرفض/القبول العمليّ خارج هذه الطبقة (يبقى في الـ consumer كـ Result).
public interface IPaymentGateway
{
    // تتصل بالبوابة؛ ترمي PaymentGatewayException إذا كان حقن العطل العابر مفعّلاً.
    // لا ترمي أبداً بسبب قرار عمل (قبول/رفض) — ذلك ليس من مسؤوليتها.
    Task AuthorizeAsync(Guid orderId, decimal amount, CancellationToken ct = default);
}

public sealed class SimulatedPaymentGateway(IConfiguration configuration) : IPaymentGateway
{
    public Task AuthorizeAsync(Guid orderId, decimal amount, CancellationToken ct = default)
    {
        // مفتاح حقن العطل العابر (للتحقق فقط): Payment:SimulateTransientFault = true
        // يجعل كل اتصال بالبوابة يرمي استثناءً عابراً، فتظهر إعادة المحاولة ثم فتح القاطع في السجلّات.
        var simulate = configuration.GetValue<bool>("Payment:SimulateTransientFault");
        if (simulate)
            throw new PaymentGatewayException(
                $"Simulated transient payment gateway failure for order {orderId}.");

        return Task.CompletedTask;
    }
}
