namespace Payment.Api.Gateway;

// استثناء يمثّل عطلاً عابراً (Transient/Infrastructure) في بوابة الدفع الخارجية.
// هذا هو النوع الوحيد الذي يُفعّل الـ Retry/Delayed Redelivery ويُحسب على الـ Circuit Breaker.
// ملاحظة مبدئية: رفض الدفع لسبب عمل (المبلغ >= الحدّ) ليس استثناءً — هو نتيجة عمل نهائية
// تُنشَر كـ PaymentFailed، فلا تُعيد المحاولة ولا تفتح القاطع (Circuit Breaker).
public sealed class PaymentGatewayException(string message) : Exception(message);
