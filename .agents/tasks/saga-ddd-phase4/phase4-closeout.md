# Phase 4 (Observability + Docs) — close-out for semantic review

Implements the Phase 4 brief on top of approved Phases 0–3 and the Step A xmin close-out.
Reviewer should confirm: (a) Serilog + CorrelationId enrichment on all four hosts; (b)
OpenTelemetry tracing (MassTransit + EF Core + ASP.NET Core) with a demonstrated single
cross-service trace; (c) `/health` on all four hosts covering Postgres + RabbitMQ; (d) a
complete README cross-linked to KNOWN_LIMITATIONS; (e) Phases 0–3 invariants preserved; (f)
live verification evidence.

## Deliverables

- `src/BuildingBlocks/Observability/` — shared library (FrameworkReference to
  `Microsoft.AspNetCore.App`) with `ObservabilityExtensions` (Serilog + OTel +
  `UseCorrelationIdLogging`), `CorrelationIdLogObserver`, and `HealthCheckExtensions`.
  Referenced by all four hosts. Infra-only — the Saga still references only `Contracts` for
  its domain contracts; Observability carries no bounded-context domain, so context isolation
  holds.
- Four host `Program.cs` wired: `AddSerilogLogging` + `AddOpenTelemetryTracing` +
  `AddAppHealthChecks`, `x.UseCorrelationIdLogging()` inside `AddMassTransit`,
  `app.UseSerilogRequestLogging()` + `app.MapAppHealthChecks()`. The per-endpoint
  `UseEntityFrameworkOutbox<T>` consumer-outbox callback is untouched on all four.
- `README.md` — architecture + ASCII diagram, run instructions (ports 5433/5673/15673 and
  hosts 5173/5197/5268/5280, migrations), the happy/compensation/rejected walkthrough, the
  pattern-location map, cross-link to `docs/KNOWN_LIMITATIONS.md` (and back).
- `Directory.Packages.props` — central pins for the observability stack; MassTransit stays
  8.5.11; no inline `Version=` in any `src/**/*.csproj`.

## Preserved from Phases 0–3

Consumer-outbox callback on all four hosts; MassTransit 8.5.11 pinned; deterministic payment
(`amount >= Payment:FailAmountThreshold`); distinct Rejected vs Cancelled terminals;
StockReleased-confirmed compensation; `xmin` token on `StockItem`. All re-verified live (three
paths reproduced). Build 0/0; concurrency tests 3/3.

## Live verification (see phase4-verification.md for full detail)

1. Build `dotnet build OrderProcess.sln` → 0 warnings / 0 errors.
2. All four `/health` → HTTP 200 `Healthy`, with `postgres` + `masstransit-bus` healthy.
3. One order (`974846e2-…`) → single `[Corr:974846e2-…]` across all four hosts' Serilog output
   (`logs/correlationid-across-hosts.txt`).
4. Same order → single OpenTelemetry `TraceId a39efba4…` spanning Ordering → Saga → Inventory →
   Payment via MassTransit + EF Core + ASP.NET Core spans (`logs/cross-service-trace.txt`).
5. README walkthrough reproduces happy (Confirmed), compensation (Cancelled, stock restored),
   rejected (Rejected, no payment) — confirmed in the four databases.

## Known limitation (deferred #3)

Multi-line `OrderSubmitted` still single-line; documented in `docs/KNOWN_LIMITATIONS.md` with
the `TODO` at the submit handler, now cross-linked from the README.
