// src/Contracts/Events.cs
namespace Contracts.Events;

public record StockReserved(Guid CorrelationId, Guid OrderId);
public record StockReservationFailed(Guid CorrelationId, Guid OrderId, string Reason);
public record StockReleased(Guid CorrelationId, Guid OrderId);
public record PaymentCompleted(Guid CorrelationId, Guid OrderId);
public record PaymentFailed(Guid CorrelationId, Guid OrderId, string Reason);

// حدث التكامل الذي يبدأ الـ Saga: يُنشَر من Ordering عند تنفيذ Order.Submit().
// نحمل ProductId و Quantity هنا لأن الـ Saga تحتاجهما لإصدار أمر ReserveStock/ReleaseStock،
// بينما Amount يُستخدم لاحقاً في أمر ProcessPayment. CorrelationId = OrderId (اصطلاح الربط).
public record OrderSubmitted(Guid CorrelationId, Guid OrderId, decimal Amount, Guid ProductId, int Quantity);
