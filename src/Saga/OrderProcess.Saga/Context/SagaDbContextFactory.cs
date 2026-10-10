using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace OrderProcess.Saga.Context;

// مصنع وقت التصميم لأوامر dotnet ef: ينشئ SagaDbContext بسلسلة الاتصال من appsettings
// دون الحاجة لتشغيل كامل مضيف MassTransit/RabbitMQ أثناء توليد/تطبيق الـ migrations.
public class SagaDbContextFactory : IDesignTimeDbContextFactory<SagaDbContext>
{
    public SagaDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<SagaDbContext>()
            .UseNpgsql(configuration.GetConnectionString("SagaDb"))
            .Options;

        return new SagaDbContext(options);
    }
}
