using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace BuildingBlocks.Observability;

// لبنة المراقبة المشتركة بين المضيفات الأربعة. الهدف توحيد ثلاثة أمور:
//  (1) تسجيل مُهيكَل (Serilog) إلى الـ Console مع إثراء كل سطر بـ ServiceName و CorrelationId،
//      بحيث يمكن تتبّع تدفّق طلب واحد عبر كل الخدمات بترشيح CorrelationId واحد.
//  (2) تتبّع موزّع (OpenTelemetry) يغطّي ASP.NET Core + EF Core + MassTransit، ويُصدَّر للـ Console،
//      فيظهر أثر واحد (trace) يمتدّ عبر الخدمات لنفس الطلب (نفس الـ TraceId).
//  (3) تمرير الـ CorrelationId من رسالة MassTransit إلى سياق تسجيل Serilog أثناء الـ Consume.
public static class ObservabilityExtensions
{
    // ملاحظة: نعتمد اصطلاح المشروع CorrelationId = OrderId. ويضبط MassTransit نفسه
    // ActivityContext الموزّع عبر رؤوس الرسائل، فيشترك المرسِل والمستهلك في نفس الـ TraceId.

    // يضبط Serilog كمزوّد تسجيل للمضيف، مع إثراء ثابت باسم الخدمة ودعم إثراء ديناميكي من LogContext.
    public static WebApplicationBuilder AddSerilogLogging(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog((context, loggerConfig) =>
        {
            loggerConfig
                .ReadFrom.Configuration(context.Configuration)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("ServiceName", serviceName)
                // قالب يُبرز CorrelationId في كل سطر لتسهيل الترشيح عبر الخدمات.
                .WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] [{ServiceName}] [Corr:{CorrelationId}] {Message:lj}{NewLine}{Exception}");
        });

        return builder;
    }

    // يضبط OpenTelemetry للتتبّع الموزّع ويصدّره للـ Console.
    //  - ASP.NET Core: يبدأ الأثر عند استقبال طلب HTTP (مثل POST /orders/{id}/submit).
    //  - EF Core: يضيف spans لاستعلامات قاعدة البيانات.
    //  - MassTransit: يضيف spans للنشر/الاستهلاك ويمرّر سياق الأثر عبر رؤوس الرسائل،
    //    فيبقى نفس TraceId من Ordering -> Saga -> Inventory -> Payment لنفس الطلب.
    public static WebApplicationBuilder AddOpenTelemetryTracing(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    // مصدر النشاط الخاص بـ MassTransit (اسم ActivitySource الثابت = "MassTransit")؛
                    // منه تُلتقَط spans النشر والاستهلاك، فيُمرَّر نفس TraceId عبر الخدمات.
                    .AddSource("MassTransit")
                    .AddConsoleExporter();
            });

        return builder;
    }

    // يسجّل CorrelationId على سياق Serilog أثناء استهلاك أي رسالة، فتحمل كل أسطر السجل
    // أثناء المعالجة نفس المعرّف المستخدَم في تدفّق HTTP (اصطلاح CorrelationId = OrderId).
    public static void UseCorrelationIdLogging(this IBusRegistrationConfigurator cfg)
        => cfg.AddConsumeObserver<CorrelationIdLogObserver>();
}
