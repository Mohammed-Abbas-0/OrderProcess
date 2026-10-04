using Inventory.Application.Abstractions;
using Inventory.Application.Commands;
using Inventory.Infrastructure.Context;
using Inventory.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<InventoryDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("InventoryDb")));
builder.Services.AddScoped<CreateStockItemHandler>();   // في قسم الـ Services فوق
builder.Services.AddScoped<IStockItemRepository, StockItemRepository>();
builder.Services.AddScoped<ReserveStockHandler>();
// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

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
