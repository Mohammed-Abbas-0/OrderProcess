# Phase 4 (Observability + Docs) — Verification Report

Implements Phase 4 on top of the approved Phases 0–3 and the Step A xmin close-out:
Serilog structured logging with CorrelationId enrichment across all four hosts, OpenTelemetry
distributed tracing (MassTransit + EF Core + ASP.NET Core → console), `/health` on all four
hosts (Postgres + RabbitMQ), and a complete `README.md`. All runtime checks ran against the
live infra (docker compose: Postgres `localhost:5433`, RabbitMQ `localhost:5673` / mgmt
`15673`) and the four hosts (Ordering 5173, Inventory 5197, Payment 5268, Saga 5280).

Phases 0–3 invariants preserved: per-endpoint `UseEntityFrameworkOutbox<T>` consumer-outbox on
all four hosts, MassTransit centrally pinned at 8.5.11 (no inline `Version=`), deterministic
payment, Rejected/Cancelled terminals, StockReleased-confirmed compensation, and the `xmin`
token on `StockItem`. None of these were touched by Phase 4.

---

## Build

```
dotnet build OrderProcess.sln
```

Result: **Build succeeded. 0 Warning(s) / 0 Error(s).** (hosts stopped during build to avoid
runtime DLL file locks).

## What was added

New shared building block `src/BuildingBlocks/Observability/` (plain library + a
`FrameworkReference` to `Microsoft.AspNetCore.App`), referenced by all four hosts:

- `ObservabilityExtensions.AddSerilogLogging(serviceName)` — routes each host through Serilog
  to the console, enriches every line with `ServiceName` and (from `LogContext`) `CorrelationId`.
- `ObservabilityExtensions.AddOpenTelemetryTracing(serviceName)` — OpenTelemetry tracing with
  ASP.NET Core + EF Core + MassTransit (`AddSource("MassTransit")`) instrumentation, console exporter.
- `ObservabilityExtensions.UseCorrelationIdLogging()` — registers `CorrelationIdLogObserver`.
- `CorrelationIdLogObserver` — a MassTransit `IConsumeObserver` that pushes the message
  `CorrelationId` (= OrderId by convention) onto Serilog's `LogContext` for the consume scope.
- `HealthCheckExtensions.AddAppHealthChecks(pgConn)` + `MapAppHealthChecks()` — Postgres health
  check (`AspNetCore.HealthChecks.NpgSql`) plus the MassTransit-registered `masstransit-bus`
  check (RabbitMQ), exposed at `/health` as JSON.

Each host `Program.cs` now calls `AddSerilogLogging` + `AddOpenTelemetryTracing` +
`AddAppHealthChecks`, adds `x.UseCorrelationIdLogging()` inside `AddMassTransit`, and calls
`app.UseSerilogRequestLogging()` + `app.MapAppHealthChecks()`. The Ordering submit handler
pushes `CorrelationId = OrderId` onto `LogContext` so the HTTP-side submit line is tagged too.

Central package pins added to `Directory.Packages.props`: Serilog.AspNetCore 8.0.3,
Serilog.Sinks.Console 6.0.0, OpenTelemetry.Extensions.Hosting / Instrumentation.AspNetCore /
Exporter.Console 1.9.0, OpenTelemetry.Instrumentation.EntityFrameworkCore 1.0.0-beta.12
(beta is the only release channel this package has — noted), AspNetCore.HealthChecks.NpgSql 9.0.0.
RabbitMQ health is covered by MassTransit's own bus health check (no extra package, and avoids
the RabbitMQ.Client v6↔v7 conflict with MassTransit 8.5.11's 7.2.2).

---

## Verification bar

### 1. All four /health return healthy

```
foreach ($p in 5173,5197,5268,5280) { (Invoke-WebRequest "http://localhost:$p/health").Content }
```

Each returned **HTTP 200** with `"status": "Healthy"` and both checks healthy:

```json
{ "status": "Healthy",
  "checks": [ { "name": "postgres", "status": "Healthy" },
              { "name": "masstransit-bus", "status": "Healthy", "description": "Ready" } ] }
```

(Confirmed for Ordering 5173, Inventory 5197, Payment 5268, Saga 5280.)

### 2. One order → single CorrelationId across all four hosts

Submitted one happy-path order `974846e2-9d0b-45c3-9083-1f1d42a2f2fb`. The same
`[Corr:974846e2-…]` tag appears in all four hosts' Serilog console output:

```
[Ordering.Api]      [Corr:974846e2-…] Order 974846e2-… submitted; publishing OrderSubmitted…
[OrderProcess.Saga] [Corr:974846e2-…] … (OrderState process / ReserveStock / ProcessPayment / ConfirmOrder)
[Inventory.Api]     [Corr:974846e2-…] … (ReserveStock process)
[Payment.Api]       [Corr:974846e2-…] … (ProcessPayment process)
```

Captured in `.agents/tasks/saga-ddd-phase4/logs/correlationid-across-hosts.txt` (and the raw
`*.out.log` files).

### 3. One cross-service distributed trace (Ordering → Saga → Inventory → Payment)

The same order produced a **single OpenTelemetry `TraceId = a39efba4f162425da203c90049d5401c`**
spanning all four services (console exporter). Representative spans under that one TraceId:

- Ordering: `POST /orders` (ASP.NET), `orderingdb` (EF Core), `Contracts.Events:OrderSubmitted send`, `OrderStatus receive/process`
- Saga: `OrderState receive/process`, `Contracts.Commands:ReserveStock send`, `ProcessPayment send`, `ConfirmOrder send`, `sagadb`
- Inventory: `ReserveStock receive/process`, `Contracts.Events:StockReserved send`, `inventorydb`
- Payment: `ProcessPayment receive/process`, `Contracts.Events:PaymentCompleted send`, `paymentdb`

Captured in `.agents/tasks/saga-ddd-phase4/logs/cross-service-trace.txt`.

### 4. README walkthrough reproduces the three paths (Phases 0–3 preserved)

Ran all three scenarios via `run-flow.ps1` against the Phase-4-wired hosts and inspected the
four databases:

| Scenario | ORDER | STOCK | PAYMENT |
|---|---|---|---|
| happy (`974846e2…`) | Confirmed | 50→48 / reserved 2 | Completed (200) |
| compensation (`7e3b8386…`) | Cancelled | reserved then restored to 50 / 0 | Failed |
| rejected (`62bbf333…`) | Rejected | unchanged 1 / 0 | 0 rows |

Saga row = 0 (finalized) in every case. This confirms deterministic payment, the distinct
Rejected vs Cancelled terminals, and StockReleased-confirmed compensation all still hold under
the new observability wiring.

---

## How to reproduce manually

1. `docker compose up -d` (repo root).
2. Apply migrations for each context (see README "How to run" step 2).
3. `dotnet build OrderProcess.sln` → expect 0/0.
4. Start the four hosts (or `.agents/tasks/saga-ddd-phase4/logs/start-hosts.ps1`).
5. Health: `foreach ($p in 5173,5197,5268,5280) { (Invoke-WebRequest "http://localhost:$p/health").Content }` → all `Healthy`.
6. Submit one order (`run-flow.ps1 -Scenario happy`), note `ORDER_ID`.
7. CorrelationId: grep each host log for `[Corr:<ORDER_ID>]` → appears in all four.
8. Trace: grep each host log for `Activity.TraceId:` of the message spans → one shared TraceId
   covers all four services.
9. Three paths: `run-flow.ps1 -Scenario happy|compensation|rejected`, inspect the four DBs.

## Notes on non-interactive limits

The console-exporter trace and the cross-host CorrelationId require the four hosts running
against live infra; the exact start commands and grep steps above make this reproducible. The
`OpenTelemetry.Instrumentation.EntityFrameworkCore` package only ships as beta from the
official source, so a prerelease pin is unavoidable for EF Core span instrumentation.
