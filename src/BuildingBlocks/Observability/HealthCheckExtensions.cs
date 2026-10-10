using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingBlocks.Observability;

// فحوص الصحة لكل مضيف. نفحص:
//  - Postgres: عبر AspNetCore.HealthChecks.NpgSql باستخدام سلسلة اتصال المضيف.
//  - RabbitMQ: عبر فحص الـ bus الذي يسجّله MassTransit تلقائياً عند AddMassTransit،
//    فيعكس حالة اتصال الوسيط الفعلي الذي يستخدمه التطبيق.
// نكشف النتيجة على /health بصيغة JSON تُظهر كل فحص على حدة وحالته.
public static class HealthCheckExtensions
{
    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services, string postgresConnectionString)
    {
        services
            .AddHealthChecks()
            .AddNpgSql(postgresConnectionString, name: "postgres", tags: ["db", "ready"]);
        // ملاحظة: فحص "masstransit-bus" يُضاف تلقائياً من MassTransit ويظهر ضمن /health.

        return services;
    }

    public static WebApplication MapAppHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthResponseAsync
        });

        return app;
    }

    // كاتب استجابة JSON: الحالة الكلية + قائمة بكل فحص (الاسم، الحالة، الوصف إن وُجد).
    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                error = e.Value.Exception?.Message
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
