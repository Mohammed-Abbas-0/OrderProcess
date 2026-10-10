// src/Inventory/Inventory.Infrastructure/Context/InventoryDbContext.cs
using Inventory.Domain.Entities;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Context;

public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StockItem>(builder =>
        {
            builder.HasKey(s => s.ProductId);
            builder.Property(s => s.AvailableQuantity).IsRequired();
            builder.Property(s => s.ReservedQuantity).IsRequired();
            builder.Ignore(s => s.DomainEvents);

            // التزامن المتفائل (Optimistic Concurrency) باستخدام عمود النظام xmin في Postgres كـ concurrency token.
            // نربطه كخاصية ظل (shadow property) باسم "xmin" حتى لا نضيف حقلاً إلى الـ domain entity،
            // بنفس أسلوب تعيين الـ Saga. عند حجزين متزامنين لنفس الصنف يفشل الحفظ الثاني بـ
            // DbUpdateConcurrencyException (قيمة xmin تغيّرت)، فتعيد الرسالة المحاولة (UseMessageRetry)
            // وتقرأ أحدث قيمة فلا يُفقد أي تحديث. xmin عمود نظام موجود أصلاً فلا يحتاج عموداً إضافياً.
            builder.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        // جداول نمط Outbox/Inbox الخاصة بـ MassTransit:
        // Inbox: تمنع المعالجة المكرّرة للرسائل (idempotency) عبر إزالة التكرار بالـ MessageId.
        // Outbox: تضمن نشر الرسالة ضمن نفس المعاملة التي تحفظ تغيير قاعدة البيانات (نشر معاملاتي).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}