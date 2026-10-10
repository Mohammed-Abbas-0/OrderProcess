using Contracts.Commands;
using Inventory.Application.Abstractions;
using MassTransit;

namespace Inventory.Api.Consumers;

// consumer مسار التعويض: يستهلك ReleaseStock من الـ Saga ويرجّع الكمية المحجوزة إلى المخزون،
// ثم ينشر StockReleased عبر الـ Outbox (الحفظ + النشر في معاملة واحدة، بلا dual-write).
public class ReleaseStockConsumer(IStockItemRepository repository) : IConsumer<ReleaseStock>
{
    public async Task Consume(ConsumeContext<ReleaseStock> context)
    {
        var command = context.Message;

        var stockItem = await repository.GetByProductIdAsync(command.ProductId);

        // لو الصنف غير موجود لا شيء نرجّعه؛ نُبلغ الـ Saga بالإفراج لإنهاء التدفق بأمان.
        if (stockItem is not null)
            stockItem.Release(command.Quantity);

        await context.Publish(new Contracts.Events.StockReleased(command.CorrelationId, command.OrderId));
        await repository.SaveChangesAsync();
    }
}
