using Contracts.Commands;
using Contracts.Events;
using MassTransit;

namespace OrderProcess.Saga.StateMachine;

// آلة الحالة التي تنسّق الـ Saga (Orchestration): تستقبل أحداث التكامل وتصدر الأوامر للسياقات.
// التدفق: Submitted -> StockReserved -> Paid -> Confirmed، مع فرع تعويض عند فشل الدفع.
public class OrderStateMachine : MassTransitStateMachine<OrderState>
{
    // الحالات الممكنة لنسخة الـ Saga.
    public State Submitted { get; private set; } = default!;
    public State StockReserved { get; private set; } = default!;
    public State Paid { get; private set; } = default!;
    // حالة انتقالية أثناء التعويض: ننتظر فيها تأكيد StockReleased قبل الإنهاء النهائي (Cancelled).
    public State Compensating { get; private set; } = default!;

    // الأحداث (مربوطة بالـ CorrelationId القادم في كل رسالة).
    public Event<OrderSubmitted> OrderSubmitted { get; private set; } = default!;
    public Event<StockReserved> StockReservedEvent { get; private set; } = default!;
    public Event<StockReservationFailed> StockReservationFailedEvent { get; private set; } = default!;
    public Event<PaymentCompleted> PaymentCompletedEvent { get; private set; } = default!;
    public Event<PaymentFailed> PaymentFailedEvent { get; private set; } = default!;
    public Event<StockReleased> StockReleasedEvent { get; private set; } = default!;

    public OrderStateMachine()
    {
        // خزّن اسم الحالة الحالية في عمود CurrentState.
        InstanceState(x => x.CurrentState);

        // اربط كل حدث عبر CorrelationId (موجود على جميع عقود Contracts المستخدمة هنا).
        Event(() => OrderSubmitted, e => e.CorrelateById(m => m.Message.CorrelationId));
        Event(() => StockReservedEvent, e => e.CorrelateById(m => m.Message.CorrelationId));
        Event(() => StockReservationFailedEvent, e => e.CorrelateById(m => m.Message.CorrelationId));
        Event(() => PaymentCompletedEvent, e => e.CorrelateById(m => m.Message.CorrelationId));
        Event(() => PaymentFailedEvent, e => e.CorrelateById(m => m.Message.CorrelationId));
        Event(() => StockReleasedEvent, e => e.CorrelateById(m => m.Message.CorrelationId));

        // (1) بدء الـ Saga: عند استلام OrderSubmitted نحفظ بيانات الطلب ونرسل أمر ReserveStock.
        Initially(
            When(OrderSubmitted)
                .Then(ctx =>
                {
                    ctx.Saga.OrderId = ctx.Message.OrderId;
                    ctx.Saga.Amount = ctx.Message.Amount;
                    ctx.Saga.ProductId = ctx.Message.ProductId;
                    ctx.Saga.Quantity = ctx.Message.Quantity;
                    ctx.Saga.SubmittedAt = DateTime.UtcNow;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                // نستخدم Publish بدل Send: التوجيه عبر exchange نوع الرسالة يصل إلى طابور الـ consumer
                // المرتبط به (ConfigureEndpoints)، فلا نحتاج عنونة endpoint صريحة.
                .Publish(ctx => new ReserveStock(ctx.Saga.CorrelationId, ctx.Saga.OrderId, ctx.Saga.ProductId, ctx.Saga.Quantity))
                .TransitionTo(Submitted));

        // (2) بعد الحجز: نجاح الحجز -> أرسل ProcessPayment؛
        //     فشل الحجز -> فشل عمل نهائي: أرسل RejectOrder (الطلب -> Rejected) وأنهِ الـ Saga.
        //     لا يوجد مخزون محجوز نرجّعه هنا، لذا هذا المسار متمايز تماماً عن مسار التعويض (Cancelled).
        During(Submitted,
            When(StockReservedEvent)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Publish(ctx => new ProcessPayment(ctx.Saga.CorrelationId, ctx.Saga.OrderId, ctx.Saga.Amount))
                .TransitionTo(StockReserved),
            When(StockReservationFailedEvent)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                // فشل عمل نهائي (مخزون غير كافٍ / صنف غير موجود): رفض الطلب، لا تعويض.
                .Publish(ctx => new RejectOrder(ctx.Saga.CorrelationId, ctx.Saga.OrderId, ctx.Message.Reason))
                .Finalize());

        // (3) بعد الدفع: نجاح -> أرسل ConfirmOrder وأنهِ (Confirmed)؛
        //     فشل -> تعويض: أرسل ReleaseStock لإرجاع المخزون + CancelOrder، ثم انتقل إلى Compensating
        //     ولا تُنهِ الـ Saga إلا بعد رصد StockReleased (إكمال التعويض فعلياً، لا fire-and-forget).
        During(StockReserved,
            When(PaymentCompletedEvent)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Publish(ctx => new ConfirmOrder(ctx.Saga.CorrelationId, ctx.Saga.OrderId))
                .TransitionTo(Paid)
                .Finalize(),
            When(PaymentFailedEvent)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                // التعويض: أرجِع الكمية المحجوزة إلى المخزون.
                .Publish(ctx => new ReleaseStock(ctx.Saga.CorrelationId, ctx.Saga.OrderId, ctx.Saga.ProductId, ctx.Saga.Quantity))
                // ألغِ الطلب لأن الدفع فشل.
                .Publish(ctx => new CancelOrder(ctx.Saga.CorrelationId, ctx.Saga.OrderId, ctx.Message.Reason))
                // انتظر تأكيد تحرير المخزون قبل الإنهاء.
                .TransitionTo(Compensating));

        // (4) إكمال التعويض: لا نصل إلى الحالة النهائية (Cancelled) إلا بعد رصد StockReleased فعلياً.
        During(Compensating,
            When(StockReleasedEvent)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Finalize());

        // عند الإنهاء احذف نسخة الـ Saga من قاعدة البيانات.
        SetCompletedWhenFinalized();
    }
}
