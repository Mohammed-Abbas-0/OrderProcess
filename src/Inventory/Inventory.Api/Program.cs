using BuildingBlocks.Observability;
using Inventory.Api.Consumers;
using Inventory.Application.Abstractions;
using Inventory.Application.Commands;
using Inventory.Infrastructure.Context;
using Inventory.Infrastructure.Repositories;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "Inventory.Api";

var builder = WebApplication.CreateBuilder(args);

// المراقبة (المرحلة 4): تسجيل مُهيكَل عبر Serilog + تتبّع موزّع عبر OpenTelemetry.
builder.AddSerilogLogging(ServiceName);
builder.AddOpenTelemetryTracing(ServiceName);

var inventoryConnectionString = builder.Configuration.GetConnectionString("InventoryDb")!;
builder.Services.AddDbContext<InventoryDbContext>(opt =>
    opt.UseNpgsql(inventoryConnectionString));

// فحوص الصحة: Postgres (NpgSql) + فحص الـ bus من MassTransit، مكشوفة على /health.
builder.Services.AddAppHealthChecks(inventoryConnectionString);
builder.Services.AddScoped<CreateStockItemHandler>();   // في قسم الـ Services فوق
builder.Services.AddScoped<IStockItemRepository, StockItemRepository>();
builder.Services.AddScoped<ReserveStockHandler>();

// ---- MassTransit ----
// بنسجّل الـ message bus + الـ consumers + الاتصال بـ RabbitMQ
builder.Services.AddMassTransit(x =>
{
    // (1) سجّل الـ consumers في الـ DI عشان MassTransit يعرف يحلّهم بنفسه (مش بـ new بإيدنا)
    x.AddConsumer<ReserveStockConsumer>();
    x.AddConsumer<ReleaseStockConsumer>();   // مسار التعويض: إرجاع المخزون

    // إثراء سجلّ Serilog بالـ CorrelationId أثناء استهلاك أي رسالة (CorrelationId = OrderId).
    x.UseCorrelationIdLogging();

    // (2) فعّل الـ Transactional Outbox فوق InventoryDbContext:
    //     UsePostgres يستخدم جداول الـ outbox/inbox في Postgres، وUseBusOutbox يجعل أي Publish
    //     داخل الـ consumer يُكتب في جدول OutboxMessage ضمن نفس المعاملة ثم يُرسَل لاحقاً (نشر معاملاتي).
    x.AddEntityFrameworkOutbox<InventoryDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    // طبّق سياسات المرونة + الـ Consumer Outbox على نقاط استقبال الـ consumers.
    // الترتيب (من الخارج للداخل): Delayed Redelivery -> Message Retry -> Outbox.
    //  - إعادة المحاولة تعالج الأعطال العابرة، وأهمّها هنا تعارض التزامن المتفائل على StockItem:
    //    عند حجزين متزامنين يفشل أحدهما بـ DbUpdateConcurrencyException (رمز xmin تغيّر)، فنعيد المحاولة
    //    فوراً فيعيد القراءة بأحدث قيمة ويطبّق التغيير بلا فقدان تحديث.
    //  - الـ Inbox يمنع تكرار المعالجة بنفس الـ MessageId (idempotency)، فإعادة التسليم لا تُنقص المخزون مرتين.
    x.AddConfigureEndpointsCallback((context, name, cfg) =>
    {
        cfg.UseDelayedRedelivery(r =>
            r.Intervals(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)));
        cfg.UseMessageRetry(r =>
        {
            // تعارض التزامن: أعِد المحاولة فوراً عدّة مرات (حلّ السباق بين حجزين متزامنين).
            r.Handle<DbUpdateConcurrencyException>();
            r.Immediate(5);
        });
        cfg.UseEntityFrameworkOutbox<InventoryDbContext>(context);
    });

    // (3) حدّد الـ transport = RabbitMQ، واقرأ بيانات الاتصال من قسم RabbitMq في الإعدادات
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

        // (4) اعمل queues تلقائياً لكل consumer مسجّل واربطها بالرسائل الصح
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

app.MapPost("/stock", async (CreateStockItemCommand command, CreateStockItemHandler handler, CancellationToken ct) =>
{
    await handler.HandleAsync(command, ct);
    return Results.Created();
});
app.MapPost("/stock/reserve", async (ReserveStockCommand command, ReserveStockHandler handler, CancellationToken ct) =>
{
    var result = await handler.HandleAsync(command, ct);
    return result.Succeeded
        ? Results.Ok(result)
        : Results.Conflict(result);
});

app.Run();
