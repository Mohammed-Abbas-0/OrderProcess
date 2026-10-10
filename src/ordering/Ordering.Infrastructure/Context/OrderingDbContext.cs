using MassTransit;
using Microsoft.EntityFrameworkCore;
using Ordering.Domain.Entities;

namespace Ordering.Infrastructure.Context;

public class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(builder =>
        {
            builder.HasKey(o => o.Id);
            builder.Property(o => o.CustomerId).IsRequired();
            builder.Property(o => o.Status).HasConversion<string>();
            builder.Ignore(o => o.DomainEvents);   
            builder.Ignore(o => o.Total);        

            builder.OwnsMany(o => o.Items, item =>
            {
                item.WithOwner().HasForeignKey("OrderId");
                item.HasKey(i => i.Id);
                // تعيين صريح لخصائص القراءة-فقط للـ OrderItem حتى تُحفَظ فعلياً في الأعمدة
                // (بدون هذا التعيين لا يُنشئ EF أعمدة ProductId/UnitPrice/Quantity فتُفقَد بيانات البند).
                item.Property(i => i.ProductId);
                item.Property(i => i.UnitPrice);
                item.Property(i => i.Quantity);
            });
        });

        // جداول نمط Outbox/Inbox الخاصة بـ MassTransit:
        // Inbox: تمنع المعالجة المكرّرة للرسائل (idempotency) عبر إزالة التكرار بالـ MessageId.
        // Outbox: تضمن نشر الرسالة ضمن نفس المعاملة التي تحفظ تغيير قاعدة البيانات (نشر معاملاتي).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }

}
