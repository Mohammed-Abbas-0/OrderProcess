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
    }
}