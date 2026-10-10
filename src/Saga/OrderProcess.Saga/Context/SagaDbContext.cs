using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderProcess.Saga.StateMachine;

namespace OrderProcess.Saga.Context;

// سياق قاعدة بيانات الـ Saga (sagadb): يحفظ حالة الـ Saga + جداول الـ Outbox/Inbox.
public class SagaDbContext(DbContextOptions<SagaDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // تعيين نسخة حالة الـ Saga: المفتاح الأساسي هو CorrelationId،
        // وRowVersion مربوط بعمود xmin في Postgres للتزامن المتفائل.
        modelBuilder.Entity<OrderState>(builder =>
        {
            builder.HasKey(x => x.CorrelationId);
            builder.Property(x => x.CurrentState).HasMaxLength(64);
            // ربط RowVersion بعمود النظام xmin في Postgres (توكن تزامن متفائل بلا عمود إضافي).
            builder.Property(x => x.RowVersion)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        // جداول نمط Outbox/Inbox الخاصة بـ MassTransit:
        // Inbox: تمنع المعالجة المكرّرة للرسائل (idempotency)، Outbox: نشر معاملاتي ضمن نفس الحفظ.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
