using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentEntity = Payment.Domain.Entities.Payment;

namespace Payment.Infrastructure.Context;

public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<PaymentEntity> Payments => Set<PaymentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentEntity>(builder =>
        {
            builder.HasKey(p => p.Id);
            builder.Property(p => p.Status).HasConversion<string>();
            builder.Ignore(p => p.DomainEvents);
        });

        // جداول نمط Outbox/Inbox الخاصة بـ MassTransit:
        // Inbox: تمنع المعالجة المكرّرة للرسائل (idempotency) عبر إزالة التكرار بالـ MessageId.
        // Outbox: تضمن نشر الرسالة ضمن نفس المعاملة التي تحفظ تغيير قاعدة البيانات (نشر معاملاتي).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}