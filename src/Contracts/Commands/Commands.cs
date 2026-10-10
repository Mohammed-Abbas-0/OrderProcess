// src/Contracts/Commands.cs
namespace Contracts.Commands;

public record ReserveStock(Guid CorrelationId, Guid OrderId, Guid ProductId, int Quantity);
public record ReleaseStock(Guid CorrelationId, Guid OrderId, Guid ProductId, int Quantity);
public record ProcessPayment(Guid CorrelationId, Guid OrderId, decimal Amount);

// أمر تأكيد الطلب: تُرسله الـ Saga بعد نجاح الدفع، ويطبّقه OrderStatusConsumer عبر Order.Confirm().
public record ConfirmOrder(Guid CorrelationId, Guid OrderId);

// أمر إلغاء الطلب: تُرسله الـ Saga في مسار التعويض (فشل الدفع بعد نجاح الحجز)،
// ويطبّقه OrderStatusConsumer عبر Order.Cancel() — إلغاء بعد تعويض (ReleaseStock).
public record CancelOrder(Guid CorrelationId, Guid OrderId, string Reason);

// أمر رفض الطلب: تُرسله الـ Saga عند فشل حجز المخزون (فشل عمل نهائي لا يتطلّب تعويضاً)،
// ويطبّقه OrderStatusConsumer عبر Order.Reject(). متمايز عن CancelOrder (لا يوجد مخزون نرجّعه).
public record RejectOrder(Guid CorrelationId, Guid OrderId, string Reason);
