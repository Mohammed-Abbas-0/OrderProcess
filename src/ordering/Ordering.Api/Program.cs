using BuildingBlocks.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Ordering.Api.Consumers;
using Ordering.Application.Abstractions;
using Ordering.Application.Commands;
using Ordering.Infrastructure;
using Ordering.Infrastructure.Context;
using Serilog;

const string ServiceName = "Ordering.Api";

var builder = WebApplication.CreateBuilder(args);

// المراقبة (المرحلة 4): تسجيل مُهيكَل عبر Serilog + تتبّع موزّع عبر OpenTelemetry.
builder.AddSerilogLogging(ServiceName);
builder.AddOpenTelemetryTracing(ServiceName);

var orderingConnectionString = builder.Configuration.GetConnectionString("OrderingDb")!;
builder.Services.AddDbContext<OrderingDbContext>(opt =>
    opt.UseNpgsql(orderingConnectionString));

// فحوص الصحة: Postgres (NpgSql) + فحص الـ bus من MassTransit، مكشوفة على /health.
builder.Services.AddAppHealthChecks(orderingConnectionString);

builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<CreateOrderHandler>();

// ---- MassTransit ----
// بنسجّل الـ message bus + الـ Transactional Outbox + الاتصال بـ RabbitMQ
builder.Services.AddMassTransit(x =>
{
    // سجّل consumer حالة الطلب ليطبّق ConfirmOrder/CancelOrder القادمة من الـ Saga.
    x.AddConsumer<OrderStatusConsumer>();

    // إثراء سجلّ Serilog بالـ CorrelationId أثناء استهلاك أي رسالة (CorrelationId = OrderId).
    x.UseCorrelationIdLogging();

    // فعّل الـ Transactional Outbox فوق OrderingDbContext:
    // UsePostgres يستخدم جداول الـ outbox/inbox في Postgres، وUseBusOutbox يجعل أي Publish
    // يُكتب في جدول OutboxMessage ضمن نفس المعاملة ثم يُرسَل لاحقاً (نشر معاملاتي).
    x.AddEntityFrameworkOutbox<OrderingDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    // طبّق سياسات المرونة + الـ Consumer Outbox على نقطة استقبال OrderStatusConsumer.
    // الترتيب (من الخارج للداخل): Delayed Redelivery -> Message Retry -> Outbox.
    //  - إعادة المحاولة الأسّية تعالج الأعطال العابرة (مثل تعثّر قاعدة البيانات لحظياً).
    //  - الـ Inbox يضمن idempotency: إعادة تطبيق Confirm/Cancel/Reject بنفس الرسالة لا تُغيّر الحالة مرتين.
    x.AddConfigureEndpointsCallback((context, name, cfg) =>
    {
        cfg.UseDelayedRedelivery(r =>
            r.Intervals(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)));
        cfg.UseMessageRetry(r =>
            r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)));
        cfg.UseEntityFrameworkOutbox<OrderingDbContext>(context);
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


app.MapPost("/orders", async (CreateOrderCommand command, CreateOrderHandler handler, CancellationToken ct) =>
{
    var orderId = await handler.HandleAsync(command, ct);
    return Results.Created($"/orders/{orderId}", new { orderId });
});

// تقديم الطلب: ينفّذ Order.Submit() ثم ينشر OrderSubmitted عبر الـ Outbox لبدء الـ Saga.
// النشر + SaveChanges في معاملة واحدة (UseBusOutbox يكتب الرسالة في OutboxMessage).
// نعتمد اصطلاح CorrelationId = OrderId، ونمرّر أول صنف في الطلب لإصدار ReserveStock.
app.MapPost("/orders/{id:guid}/submit",
    async (Guid id, IOrderRepository repository, IPublishEndpoint publishEndpoint, CancellationToken ct) =>
{
    var order = await repository.GetByIdAsync(id, ct);
    if (order is null) return Results.NotFound();

    var firstItem = order.Items.FirstOrDefault();
    if (firstItem is null) return Results.Conflict(new { error = "Order has no items." });

    order.Submit();

    // اصطلاح الربط: CorrelationId = OrderId. نضعه في سياق Serilog حتى يحمل سطر هذا الطلب
    // على جانب HTTP نفس المعرّف الذي ستحمله سجلّات المستهلكين عبر الخدمات.
    using (Serilog.Context.LogContext.PushProperty("CorrelationId", order.Id))
    {
        Log.Information("Order {OrderId} submitted; publishing OrderSubmitted to start the saga.", order.Id);
    }

    // TODO (قيد معروف — مؤجَّل لمرحلة لاحقة): يُنشَر أول صنف فقط من الطلب (firstItem) في OrderSubmitted،
    // فالطلبات متعددة الأصناف تحجز/تحرّر مخزون الصنف الأول فقط. توزيع الحجز على كل الأصناف (fan-out/fan-in)
    // داخل الـ Saga وتغيير شكل الحدث مؤجَّلان؛ انظر docs/KNOWN_LIMITATIONS.md.
    await publishEndpoint.Publish(new Contracts.Events.OrderSubmitted(
        order.Id, order.Id, order.Total, firstItem.ProductId, firstItem.Quantity), ct);

    await repository.SaveChangesAsync(ct);

    return Results.Accepted($"/orders/{order.Id}", new { orderId = order.Id, status = order.Status.ToString() });
});

app.Run();
