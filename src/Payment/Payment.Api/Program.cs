using BuildingBlocks.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Payment.Api.Consumers;
using Payment.Api.Gateway;
using Payment.Application.Abstractions;
using Payment.Application.Commands;
using Payment.Infrastructure.Context;
using Payment.Infrastructure.Repositories;
using Serilog;

const string ServiceName = "Payment.Api";

var builder = WebApplication.CreateBuilder(args);

// المراقبة (المرحلة 4): تسجيل مُهيكَل عبر Serilog + تتبّع موزّع عبر OpenTelemetry.
builder.AddSerilogLogging(ServiceName);
builder.AddOpenTelemetryTracing(ServiceName);

var paymentConnectionString = builder.Configuration.GetConnectionString("PaymentDb")!;
builder.Services.AddDbContext<PaymentDbContext>(opt =>
    opt.UseNpgsql(paymentConnectionString));

// فحوص الصحة: Postgres (NpgSql) + فحص الـ bus من MassTransit، مكشوفة على /health.
builder.Services.AddAppHealthChecks(paymentConnectionString);

builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<ProcessPaymentHandler>();
// بوابة الدفع المحاكاة: مكان حقن العطل العابر لإثبات الـ Retry/Circuit Breaker.
builder.Services.AddScoped<IPaymentGateway, SimulatedPaymentGateway>();

// ---- MassTransit ----
// بنسجّل الـ message bus + الـ Transactional Outbox + الاتصال بـ RabbitMQ
builder.Services.AddMassTransit(x =>
{
    // سجّل consumer الدفع عشان الـ Saga ترسل له ProcessPayment.
    x.AddConsumer<ProcessPaymentConsumer>();

    // إثراء سجلّ Serilog بالـ CorrelationId أثناء استهلاك أي رسالة (CorrelationId = OrderId).
    x.UseCorrelationIdLogging();

    // فعّل الـ Transactional Outbox فوق PaymentDbContext:
    // UsePostgres يستخدم جداول الـ outbox/inbox في Postgres، وUseBusOutbox يجعل أي Publish
    // يُكتب في جدول OutboxMessage ضمن نفس المعاملة ثم يُرسَل لاحقاً (نشر معاملاتي).
    x.AddEntityFrameworkOutbox<PaymentDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    // طبّق سياسات المرونة + الـ Consumer Outbox على نقطة استقبال consumer الدفع.
    // الترتيب مقصود (من الخارج للداخل): Delayed Redelivery -> Message Retry -> Circuit Breaker -> Outbox.
    //  - إعادة التسليم المؤجَّلة تعطي البوابة وقتاً لتتعافى بعد استنفاد المحاولات الفورية.
    //  - إعادة المحاولة الأسّية تعالج الأعطال العابرة القصيرة بلا رجوع للوسيط.
    //  - القاطع يتتبّع PaymentGatewayException فقط (عطل عابر) ولا يُحسب عليه رفض الدفع العمليّ (لا يرمي أصلاً).
    //  - الـ Outbox (Inbox + Outbox) يضمن حفظ/نشر النتيجة ذرياً و idempotency بالـ MessageId.
    x.AddConfigureEndpointsCallback((context, name, cfg) =>
    {
        // عطل عابر فقط هو ما يستحق إعادة المحاولة/إعادة التسليم.
        cfg.UseDelayedRedelivery(r =>
        {
            r.Handle<PaymentGatewayException>();
            r.Intervals(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30));
        });
        cfg.UseMessageRetry(r =>
        {
            r.Handle<PaymentGatewayException>();
            r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
        });
        // القاطع: يتتبّع العطل العابر فقط؛ عتبات منخفضة لإظهار الفتح أثناء التحقق.
        cfg.UseCircuitBreaker(cb =>
        {
            cb.TrackingPeriod = TimeSpan.FromMinutes(1);
            cb.TripThreshold = 15;     // نسبة الفشل % التي تفتح القاطع
            cb.ActiveThreshold = 5;    // أقل عدد رسائل قبل تقييم العتبة
            cb.ResetInterval = TimeSpan.FromMinutes(1);
            cb.Handle<PaymentGatewayException>();
        });
        cfg.UseEntityFrameworkOutbox<PaymentDbContext>(context);
    });

    // حدّد الـ transport = RabbitMQ، واقرأ بيانات الاتصال من قسم RabbitMq في الإعدادات
    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbit = builder.Configuration.GetSection("RabbitMq");

        // قيمة Host قد تحمل المنفذ بالشكل "localhost:5673"؛ نفصل الاسم عن المنفذ
        // لأن MassTransit يتوقّع اسم المضيف فقط في هذه الـ overload.
        var hostValue = rabbit["Host"]!;
        var parts = hostValue.Split(':', 2);
        var host = parts[0];
        var port = parts.Length == 2 ? ushort.Parse(parts[1]) : (ushort)5672;

        cfg.Host(host, port, "/", h =>
        {
            h.Username(rabbit["Username"]!);
            h.Password(rabbit["Password"]!);
        });

        // اعمل queues تلقائياً لكل consumer مسجّل واربطها بالرسائل الصح
        cfg.ConfigureEndpoints(context);
    });
});
// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// تسجيل طلبات HTTP عبر Serilog (سطر واحد مُهيكَل لكل طلب).
app.UseSerilogRequestLogging();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// نقطة فحص الصحة: /health (Postgres + bus).
app.MapAppHealthChecks();


app.MapPost("/payments", async (ProcessPaymentCommand command, ProcessPaymentHandler handler, CancellationToken ct) =>
{
    var result = await handler.HandleAsync(command, ct);
    return result.Succeeded ? Results.Ok(result) : Results.Conflict(result);
});

app.Run();


