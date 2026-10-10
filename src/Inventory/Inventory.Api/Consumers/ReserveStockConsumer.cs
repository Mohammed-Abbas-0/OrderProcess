using Contracts.Commands;
using Inventory.Application.Abstractions;
using MassTransit;

namespace Inventory.Api.Consumers;

public class ReserveStockConsumer(IStockItemRepository repository) : IConsumer<ReserveStock>
{
    public async Task Consume(ConsumeContext<ReserveStock> context)
    {
        var command = context.Message;

        var stockItem = await repository.GetByProductIdAsync(command.ProductId);

        // ملاحظة: الـ Publish هنا يُكتب في جدول OutboxMessage (مش يُرسَل مباشرة)،
        // ثم SaveChangesAsync واحد يحفظ تغيير StockItem ورسالة الـ outbox في معاملة واحدة
        // فيختفي الـ dual-write القديم (حفظ ثم نشر منفصلين).
        if (stockItem is null)
        {
            await context.Publish(new Contracts.Events.StockReservationFailed(command.CorrelationId, command.OrderId, "Product not found."));
            await repository.SaveChangesAsync();
            return;
        }

        var result = stockItem.Reserve(command.Quantity);

        if (!result.Succeeded)
        {
            await context.Publish(new Contracts.Events.StockReservationFailed(command.CorrelationId, command.OrderId, result.Reason!));
            await repository.SaveChangesAsync();
            return;
        }

        await context.Publish(new Contracts.Events.StockReserved(command.CorrelationId, command.OrderId));
        await repository.SaveChangesAsync();
    }
}
