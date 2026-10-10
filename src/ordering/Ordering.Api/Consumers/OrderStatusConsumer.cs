using Contracts.Commands;
using MassTransit;
using Ordering.Application.Abstractions;

namespace Ordering.Api.Consumers;

// consumer حالة الطلب: يطبّق قرارات الـ Saga على الـ Order aggregate.
// ConfirmOrder -> Order.Confirm()؛ CancelOrder -> Order.Cancel()؛ RejectOrder -> Order.Reject().
// نعتمد اصطلاح CorrelationId = OrderId. الحفظ يمرّ عبر الـ Outbox (معاملة واحدة).
public class OrderStatusConsumer(IOrderRepository repository) :
    IConsumer<ConfirmOrder>,
    IConsumer<CancelOrder>,
    IConsumer<RejectOrder>
{
    public async Task Consume(ConsumeContext<ConfirmOrder> context)
    {
        var order = await repository.GetByIdAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null) return;

        order.Confirm();
        await repository.SaveChangesAsync(context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<CancelOrder> context)
    {
        var order = await repository.GetByIdAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null) return;

        order.Cancel();
        await repository.SaveChangesAsync(context.CancellationToken);
    }

    // رفض الطلب (مسار فشل حجز المخزون): حالة نهائية متمايزة عن الإلغاء، بلا تعويض.
    public async Task Consume(ConsumeContext<RejectOrder> context)
    {
        var order = await repository.GetByIdAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null) return;

        order.Reject(context.Message.Reason);
        await repository.SaveChangesAsync(context.CancellationToken);
    }
}
