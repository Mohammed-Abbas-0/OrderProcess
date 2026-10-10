using BuildingBlocks.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderProcess.Saga.Context;
using OrderProcess.Saga.StateMachine;
using Serilog;

const string ServiceName = "OrderProcess.Saga";

var builder = WebApplication.CreateBuilder(args);

// المراقبة (المرحلة 4): تسجيل مُهيكَل عبر Serilog + تتبّع موزّع عبر OpenTelemetry.
builder.AddSerilogLogging(ServiceName);
builder.AddOpenTelemetryTracing(ServiceName);

// سياق قاعدة بيانات الـ Saga (sagadb) لحفظ حالة الـ Saga + جداول الـ Outbox.
var sagaConnectionString = builder.Configuration.GetConnectionString("SagaDb")!;
builder.Services.AddDbContext<SagaDbContext>(opt =>
    opt.UseNpgsql(sagaConnectionString));

// فحوص الصحة: Postgres (NpgSql) + فحص الـ bus من MassTransit، مكشوفة على /health.
builder.Services.AddAppHealthChecks(sagaConnectionString);

// ---- MassTransit ----
// نسجّل الـ state machine + مستودع EF لحفظ الحالة + Outbox + الاتصال بـ RabbitMQ.
builder.Services.AddMassTransit(x =>
{
    // (1) آلة حالة الـ Saga محفوظة في SagaDbContext عبر مستودع EF Core.
    x.AddSagaStateMachine<OrderStateMachine, OrderState>()
        .EntityFrameworkRepository(r =>
        {
            r.ExistingDbContext<SagaDbContext>();
            r.UsePostgres();
        });

    // إثراء سجلّ Serilog بالـ CorrelationId أثناء استهلاك أي رسالة (CorrelationId = OrderId).
    x.UseCorrelationIdLogging();

    // (2) الـ Transactional Outbox فوق SagaDbContext: أي Send/Publish من الـ Saga يُكتب
    //     في جدول OutboxMessage ضمن نفس المعاملة التي تحفظ حالة الـ Saga ثم يُرسَل لاحقاً.
    x.AddEntityFrameworkOutbox<SagaDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    // (2b) طبّق الـ Consumer Outbox (Inbox + Outbox) على نقطة استقبال الـ Saga:
    //      يضمن ألا تُرسَل أوامر الـ Saga (ReserveStock/ProcessPayment/...) قبل حفظ حالة الـ Saga،
    //      فيختفي سباق استلام الرد (StockReserved) قبل أن تصبح الحالة Submitted، ويضيف idempotency بالـ MessageId.
    // طبّق سياسات المرونة + الـ Consumer Outbox على نقطة استقبال الـ Saga.
    // الترتيب (من الخارج للداخل): Delayed Redelivery -> Message Retry -> Outbox.
    //  - إعادة المحاولة الأسّية تعالج الأعطال العابرة أثناء حفظ حالة الـ Saga/نشر الأوامر.
    //  - الـ Inbox يضمن idempotency لأحداث التكامل الواردة (StockReserved/PaymentFailed/...).
    x.AddConfigureEndpointsCallback((context, name, cfg) =>
    {
        cfg.UseDelayedRedelivery(r =>
            r.Intervals(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)));
        cfg.UseMessageRetry(r =>
            r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)));
        cfg.UseEntityFrameworkOutbox<SagaDbContext>(context);
    });

    // (3) الـ transport = RabbitMQ، نقرأ بيانات الاتصال من قسم RabbitMq في الإعدادات.
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

        // اعمل queues تلقائياً للـ saga endpoint واربطها بالأحداث الصح.
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

// تسجيل طلبات HTTP عبر Serilog (سطر واحد مُهيكَل لكل طلب).
app.UseSerilogRequestLogging();

// نقطة فحص بسيطة للتأكد أن مضيف الـ Saga شغّال.
app.MapGet("/", () => "OrderProcess.Saga host is running.");

// نقطة فحص الصحة: /health (Postgres + bus).
app.MapAppHealthChecks();

app.Run();
