# Implementation Plan — Distributed Order Processing (Phases 0–2)

Authoritative spec: `d:\Projects\Saga_DDD\OrderProcess\docs\IMPLEMENTATION_PROMPT.md`.
Scope: Phase 0 (runnable infra), Phase 1 (Outbox/Inbox in every context), Phase 2 (Order Saga).
Phases 3–4 are OUT OF SCOPE here and begin only after a human-review pause.

All paths are absolute. The agent's cwd is the repo root `d:\Projects\Saga_DDD\OrderProcess` (NOT a worktree).
Windows PowerShell: chain commands with `;`. Do not start hosts in a blocking way (use background / separate windows).
New explanatory comments: Arabic (match existing style). Identifiers: English.
Do NOT create a git worktree. Do NOT commit unless strictly necessary.

## Grounding findings (verified by reading the code)

- **MassTransit versions are inconsistent and violate the spec.** `Inventory.Api` = `8.5.11`; `Ordering.Api` + `Payment.Api` = `9.2.3`. Spec fixes MassTransit at `8.5.11`. All projects MUST be pinned to `8.5.11` via central package management. (MassTransit 9.x is a different major with API/licensing differences; outbox wiring in this plan targets the 8.5.x API.)
- EF Core also drifts: `9.0.5` (Ordering.Infra) vs `9.0.8` (Inventory/Payment Infra). Npgsql EF = `9.0.4` everywhere. Central management unifies to EF `9.0.8` + Npgsql `9.0.4`.
- **Contracts already carry `CorrelationId`** on `StockReserved`, `StockReservationFailed`, `StockReleased`, `PaymentCompleted`, `PaymentFailed`, `ReserveStock`, `ReleaseStock`, `ProcessPayment`. The ONLY missing message is `ConfirmOrder`. The spec's CorrelationId requirement on ReleaseStock/StockReleased/PaymentCompleted/PaymentFailed is already satisfied — verify during implementation, do not assume removal.
- Integration events needed for the saga to drive order state back into Ordering: `OrderSubmitted` (integration), `ConfirmOrder` (command), and terminal signals so Ordering.Api can update the `Order` row. Plan adds `Contracts.Events.OrderSubmitted` and `Contracts.Commands.ConfirmOrder`; order terminal state is driven by the saga sending commands the Ordering consumer applies via `Order.Confirm()/Cancel()`.
- **Name clashes** between domain events and integration events: `Inventory.Domain.Events.StockReserved` vs `Contracts.Events.StockReserved`; `Payment.Domain.Events.PaymentCompleted/PaymentFailed` vs `Contracts.Events.*`. Existing consumers disambiguate via namespaces. All new publish/consume code MUST use `Contracts.*` types (alias if needed).
- `Order` aggregate already exposes `Submit()`, `Confirm()`, `Cancel()` raising domain events, and `OrderRepository.GetByIdAsync(Include(Items))` exists.
- `Payment` entity already has `Process(bool forceFail)` (90% success otherwise) and `PaymentStatus { Pending, Completed, Failed, Refunded }`.
- `ReserveStockConsumer` has the dual-write defect (`SaveChangesAsync()` then `context.Publish(...)`), plus Inventory.Api has the temp `POST /test/publish-reserve` endpoint to remove.
- `Ordering.Api` has NO MassTransit packages/wiring. `Payment.Api` references MassTransit packages but Program.cs does NOT configure the bus or any consumer.
- Databases: one Postgres (`postgres`/`postgres`), DBs `orderingdb`, `inventorydb`, `paymentdb`. Saga needs a 4th DB `sagadb`.
- Tooling present: `dotnet` 9.0.310, `dotnet ef` 9.0.5 (via Tools reference), Docker 29.x. Baseline `dotnet build OrderProcess.sln` succeeds (0/0).
- EF design-time: Api projects reference `Microsoft.EntityFrameworkCore.Design`; Infra projects reference `Microsoft.EntityFrameworkCore.Tools`. Migrations live in each `*.Infrastructure/Migrations`. Run `dotnet ef` with `--project <Infra.csproj> --startup-project <Api.csproj>`.

---

## Phase 0 — Runnable infrastructure

- [ ] 1. Add `docker-compose.yml` at repo root with `postgres:16` (single instance, four logical databases: `orderingdb`, `inventorydb`, `paymentdb`, `sagadb`) and `rabbitmq:3-management` (5672 + 15672). Create four DBs via an init script `d:\Projects\Saga_DDD\OrderProcess\docker\postgres-init\10-create-databases.sql` mounted to `/docker-entrypoint-initdb.d`. Postgres creds `postgres`/`postgres` to match existing connection strings; RabbitMQ `guest`/`guest`. Add named volumes for durability. Document the "one instance, four databases" choice in a top comment.
      Files: `d:\Projects\Saga_DDD\OrderProcess\docker-compose.yml`, `d:\Projects\Saga_DDD\OrderProcess\docker\postgres-init\10-create-databases.sql`
      Verify: `cd d:\Projects\Saga_DDD\OrderProcess; docker compose up -d` → `docker compose ps` shows both healthy; `http://localhost:15672` reachable (guest/guest); `docker compose exec postgres psql -U postgres -lqt` lists all four DBs.

- [ ] 2. Add central package management: `Directory.Packages.props` at repo root with `ManagePackageVersionsCentrally=true` and `PackageVersion` entries for every package currently referenced plus the ones Phases 1–2 need. Pin exact versions: `MassTransit` + `MassTransit.RabbitMQ` + `MassTransit.EntityFrameworkCoreIntegration` = `8.5.11`; `Microsoft.EntityFrameworkCore` + `.Tools` + `.Design` = `9.0.8`; `Npgsql.EntityFrameworkCore.PostgreSQL` = `9.0.4`; `Microsoft.AspNetCore.OpenApi` = `9.0.12`. Then edit every `.csproj` to drop the `Version=` attribute from each `PackageReference` (versions now come from the props file). Critically, this flips `Ordering.Api` and `Payment.Api` from MassTransit `9.2.3` → `8.5.11`.
      Files: `d:\Projects\Saga_DDD\OrderProcess\Directory.Packages.props`, and remove `Version=` from PackageReferences in all 13 existing `.csproj` under `d:\Projects\Saga_DDD\OrderProcess\src` (Contracts has none).
      Verify: `dotnet restore d:\Projects\Saga_DDD\OrderProcess\OrderProcess.sln` succeeds; `dotnet build OrderProcess.sln` → 0 errors; confirm no NU1008 (missing central version) and no leftover inline versions.

- [ ] 3. Make connection strings and RabbitMQ host env-overridable. Add a `RabbitMq` config section (`Host`, `Username`, `Password`, default `localhost`/`guest`/`guest`) to `appsettings.json` of each Api; keep `ConnectionStrings` as-is for local. Rely on ASP.NET env-var binding (`ConnectionStrings__InventoryDb`, `RabbitMq__Host`, etc.) for Docker override — no code change beyond reading `builder.Configuration`.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\ordering\Ordering.Api\appsettings.json`, `d:\Projects\Saga_DDD\OrderProcess\src\Inventory\Inventory.Api\appsettings.json`, `d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Api\appsettings.json`
      Verify: `dotnet build OrderProcess.sln` → 0 errors; each `appsettings.json` parses as valid JSON (hosts start without config-binding errors in Phase 2 run).

---

## Phase 1 — Outbox + Inbox in every context

Depends on Phase 0 (central versions + running infra). Order: Contracts → Infra DbContexts → Api bus wiring → consumer rewrite → migrations.

- [ ] 4. Add MassTransit EF outbox integration to the three Infrastructure projects and reference it from the three Apis. Because versions are central, just add `<PackageReference Include="MassTransit.EntityFrameworkCoreIntegration" />` (no version) to each `*.Infrastructure.csproj`; the Apis already transit it. The outbox entity config lives in Infrastructure (where the DbContext is).
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\ordering\Ordering.Infrastructure\Ordering.Infrastructure.csproj`, `d:\Projects\Saga_DDD\OrderProcess\src\Inventory\Inventory.Infrastructure\Inventory.Infrastructure.csproj`, `d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Infrastructure\Payment.Infrastructure.csproj`
      Verify: `dotnet build OrderProcess.sln` → 0 errors; `MassTransit.EntityFrameworkCoreIntegration` resolves at `8.5.11`.

- [ ] 5. Register the MassTransit outbox entity sets in all three DbContexts' `OnModelCreating` via `modelBuilder.AddInboxStateEntity()`, `AddOutboxMessageEntity()`, `AddOutboxStateEntity()` (extension on `ModelBuilder` from the integration package). Keep existing entity config intact. Arabic comments explaining the Inbox (idempotency) and Outbox (transactional publish) tables.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\ordering\Ordering.Infrastructure\Context\OrderingDbContext.cs`, `d:\Projects\Saga_DDD\OrderProcess\src\Inventory\Inventory.Infrastructure\Context\InventoryDbContext.cs`, `d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Infrastructure\Context\PaymentDbContext.cs`
      Verify: `dotnet build OrderProcess.sln` → 0 errors (the three new sets compile against the DbContext).

- [ ] 6. Wire `AddEntityFrameworkOutbox<TDbContext>` with `UsePostgres()` + `UseBusOutbox()` inside `AddMassTransit(...)` in each Api, and add full MassTransit setup where missing. Inventory.Api: add the outbox block to its existing `AddMassTransit` and keep `ReserveStockConsumer` registered. Payment.Api: add `AddMassTransit` + `UsingRabbitMq` (reading the `RabbitMq` config section) + `AddEntityFrameworkOutbox<PaymentDbContext>` (consumer added in Phase 2). Ordering.Api: add `AddMassTransit` + `UsingRabbitMq` + `AddEntityFrameworkOutbox<OrderingDbContext>` (publish-only for now). Read Rabbit host/user/pass from configuration, not hard-coded. Keep `ConfigureEndpoints(context)` last.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\Inventory\Inventory.Api\Program.cs`, `d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Api\Program.cs`, `d:\Projects\Saga_DDD\OrderProcess\src\ordering\Ordering.Api\Program.cs`
      Verify: `dotnet build OrderProcess.sln` → 0 errors; with infra up, each host starts (`dotnet run --project <Api>` briefly in background) and logs MassTransit bus started with the Postgres outbox delivery service; stop the host after confirming.

- [ ] 7. Rewrite `ReserveStockConsumer` to remove the dual-write: publish `Contracts.Events.StockReserved` / `StockReservationFailed` through the outbox so the `SaveChanges` and the publish commit in ONE transaction. With `UseBusOutbox`, calling `context.Publish(...)` then a single `SaveChangesAsync` through the scoped DbContext enlists the message in the outbox; ensure the consumer uses the scoped `InventoryDbContext`/repository so the outbox interceptor captures the publish. Keep business logic (`stockItem.Reserve`) and the not-found/insufficient branches; just make persistence + publish atomic. Use `Contracts.Events.*` types explicitly.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\Inventory\Inventory.Api\Consumers\ReserveStockConsumer.cs` (and, if needed, `IStockItemRepository`/`StockItemRepository` to expose the DbContext's SaveChanges within the MassTransit scope — reuse existing `SaveChangesAsync`).
      Verify: `dotnet build OrderProcess.sln` → 0 errors; code review confirms a single `SaveChangesAsync` commits both the StockItem change and the outbox row (no bare `context.Publish` followed by a separate save that could be lost).

- [ ] 8. Add EF migrations for the outbox/inbox tables in all three contexts. One migration per context, named `AddOutbox`.
      Commands (run each; `;`-separated acceptable):
      `dotnet ef migrations add AddOutbox --project d:\Projects\Saga_DDD\OrderProcess\src\ordering\Ordering.Infrastructure\Ordering.Infrastructure.csproj --startup-project d:\Projects\Saga_DDD\OrderProcess\src\ordering\Ordering.Api\Ordering.Api.csproj`
      `dotnet ef migrations add AddOutbox --project d:\Projects\Saga_DDD\OrderProcess\src\Inventory\Inventory.Infrastructure\Inventory.Infrastructure.csproj --startup-project d:\Projects\Saga_DDD\OrderProcess\src\Inventory\Inventory.Api\Inventory.Api.csproj`
      `dotnet ef migrations add AddOutbox --project d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Infrastructure\Payment.Infrastructure.csproj --startup-project d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Api\Payment.Api.csproj`
      Files (generated): `...\Ordering.Infrastructure\Migrations\*_AddOutbox.cs`, `...\Inventory.Infrastructure\Migrations\*_AddOutbox.cs`, `...\Payment.Infrastructure\Migrations\*_AddOutbox.cs` (+ Designer + snapshot updates).
      Verify: each `dotnet ef migrations add` succeeds; generated migration creates `InboxState`, `OutboxMessage`, `OutboxState` tables; `dotnet build OrderProcess.sln` → 0 errors.

- [ ] 9. Apply the three migrations and prove the outbox survives a broker outage. With infra up, run `dotnet ef database update` for each context (same `--project/--startup-project` pairs as item 8). Then: stop RabbitMQ (`docker compose stop rabbitmq`), POST a stock reserve so a StockReserved should publish, confirm the StockItem row changed AND an `OutboxMessage` row is pending (unsent); start RabbitMQ (`docker compose start rabbitmq`); confirm the pending outbox row drains (message delivered).
      Files: none (data/infra operation).
      Verify (outbox durability): seed stock via `POST http://localhost:5xxx/stock`; stop rabbitmq; trigger a reserve (in Phase 1, use a temporary direct publish or the existing reserve path) ; `docker compose exec postgres psql -U postgres -d inventorydb -c "select count(*) from \"OutboxMessage\";"` shows a pending row while broker is down; start rabbitmq; re-query shows the row drained to zero. Record exact ports/commands in findings.

---

## Phase 2 — Order Saga (the core)

Depends on Phase 1. Order: Contracts additions → Saga host project → OrderStateMachine → saga EF + migration → Ordering submit endpoint + consumer → Payment consumer → remove temp endpoint → verify happy + compensation.

- [ ] 10. Extend `src/Contracts`: add `Contracts.Commands.ConfirmOrder(Guid CorrelationId, Guid OrderId)`; add `Contracts.Events.OrderSubmitted(Guid CorrelationId, Guid OrderId, decimal Amount)`. Verify (do NOT assume) that `ReleaseStock`, `StockReleased`, `PaymentCompleted`, `PaymentFailed` already carry `CorrelationId` — they currently do; add it only if a read shows otherwise. Do not break existing record shapes used by `ReserveStockConsumer`.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\Contracts\Commands\Commands.cs`, `d:\Projects\Saga_DDD\OrderProcess\src\Contracts\Events\Events.cs`
      Verify: `dotnet build OrderProcess.sln` → 0 errors; existing Inventory consumer still compiles against unchanged `Contracts.Events.StockReserved`.

- [ ] 11. Create the saga host project `OrderProcess.Saga` (`Microsoft.NET.Sdk.Web`, net9.0, minimal host) at `d:\Projects\Saga_DDD\OrderProcess\src\Saga\OrderProcess.Saga` and register it in `OrderProcess.sln` under a new `Saga` solution folder. References: `Contracts` only (no other context's Domain/Infrastructure — keep bounded contexts isolated). PackageReferences (versionless, central): `MassTransit`, `MassTransit.RabbitMQ`, `MassTransit.EntityFrameworkCoreIntegration`, `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Design`, `Npgsql.EntityFrameworkCore.PostgreSQL`. Add `SagaDb` connection string (`Database=sagadb`) + `RabbitMq` section to its `appsettings.json`.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\Saga\OrderProcess.Saga\OrderProcess.Saga.csproj`, `...\Program.cs`, `...\appsettings.json`, `...\appsettings.Development.json`, `...\Properties\launchSettings.json`; edit `d:\Projects\Saga_DDD\OrderProcess\OrderProcess.sln`; add `MassTransit.EntityFrameworkCoreIntegration` + the two EF/MassTransit entries to `Directory.Packages.props` if not already present from Phase 1.
      Verify: `dotnet build OrderProcess.sln` → 0 errors; `dotnet sln list` shows `OrderProcess.Saga`.

- [ ] 12. Implement `OrderState : SagaStateMachineInstance` (`CorrelationId`, `CurrentState` string, `OrderId`, `Amount`, `ProductId`, `Quantity`, `SubmittedAt`/`UpdatedAt` timestamps, `RowVersion` for EF concurrency) and `OrderStateMachine : MassTransitStateMachine<OrderState>` with states `Submitted`, `StockReserved`, `Paid` and transitions matching the TARGET MESSAGE FLOW including compensation. Events: `OrderSubmitted` → send `ReserveStock`, go `Submitted`; `StockReserved` → send `ProcessPayment`, go `StockReserved`; `StockReservationFailed` → send `ConfirmOrder`-style reject (order Rejected), Finalize; `PaymentCompleted` → send `ConfirmOrder`, order Confirmed, Finalize; `PaymentFailed` → COMPENSATE: send `ReleaseStock`, order Cancelled, Finalize; `StockReleased` observed during compensation to complete cleanup. Correlate every event by `CorrelationId`. `SetCompletedWhenFinalized()`.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\Saga\OrderProcess.Saga\StateMachine\OrderState.cs`, `...\StateMachine\OrderStateMachine.cs`
      Verify: `dotnet build OrderProcess.sln` → 0 errors; state machine compiles with all `Contracts` events wired and the compensation branch present.

- [ ] 13. Persist saga state with EF (`EntityFrameworkSagaRepository<OrderState>`, pessimistic/optimistic per MassTransit 8.5 default) in the saga's own `SagaDbContext` (maps `OrderState` + the outbox entity sets) against `sagadb`; wire `AddMassTransit` in the saga `Program.cs` with `AddSagaStateMachine<OrderStateMachine, OrderState>().EntityFrameworkRepository(...)` using Postgres, `AddEntityFrameworkOutbox<SagaDbContext>` + `UseBusOutbox()`, `UsingRabbitMq` from config, `ConfigureEndpoints`. Add the saga migration.
      Command: `dotnet ef migrations add InitialSaga --project d:\Projects\Saga_DDD\OrderProcess\src\Saga\OrderProcess.Saga\OrderProcess.Saga.csproj --startup-project d:\Projects\Saga_DDD\OrderProcess\src\Saga\OrderProcess.Saga\OrderProcess.Saga.csproj` (self-hosted DbContext in the host project).
      Files: `...\OrderProcess.Saga\Context\SagaDbContext.cs`, `...\OrderProcess.Saga\Program.cs`, generated `...\OrderProcess.Saga\Migrations\*_InitialSaga.cs`.
      Verify: migration generates `OrderState` + outbox tables; `dotnet build OrderProcess.sln` → 0 errors; with infra up, `dotnet ef database update` on the saga succeeds and `sagadb` gains the tables.

- [ ] 14. Add the Ordering submit flow and the order-state consumer. In `Ordering.Api`: add `POST /orders/{id}/submit` that loads the order (`IOrderRepository.GetByIdAsync`), calls `Order.Submit()`, and publishes `Contracts.Events.OrderSubmitted(CorrelationId=order.Id, order.Id, order.Total)` through the Ordering outbox (publish + SaveChanges one transaction). Add an `OrderStatusConsumer` in `Ordering.Api` that consumes `Contracts.Commands.ConfirmOrder` (and the reject/cancel signals the saga sends) and applies `Order.Confirm()` / `Order.Cancel()` to persist terminal state via the outbox-enlisted DbContext. Register the consumer in `AddMassTransit`. Use `CorrelationId = OrderId` convention so one id traces the whole flow.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\ordering\Ordering.Api\Program.cs`, `d:\Projects\Saga_DDD\OrderProcess\src\ordering\Ordering.Api\Consumers\OrderStatusConsumer.cs`
      Verify: `dotnet build OrderProcess.sln` → 0 errors; submit endpoint maps; consumer registered.

- [ ] 15. Create the Payment consumer for `Contracts.Commands.ProcessPayment` in `Payment.Api`: write a `Payment` row via `ProcessPaymentHandler`/`IPaymentRepository` and publish `Contracts.Events.PaymentCompleted` / `PaymentFailed` through the Payment outbox (atomic). Add a configurable "fail payment" switch read from configuration (e.g. `Payment:FailAmountThreshold` — fail when `Amount >= threshold`, default off) passed into `Payment.Process(forceFail)`, so compensation is deterministically triggerable without relying on the 90% random. Register the consumer in `AddMassTransit`.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Api\Consumers\ProcessPaymentConsumer.cs`, `d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Api\Program.cs`, `d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Api\appsettings.json` (add `Payment:FailAmountThreshold`); if the switch needs plumbing, `d:\Projects\Saga_DDD\OrderProcess\src\Payment\Payment.Application\Commands\ProcessPaymentHandler.cs` (already accepts `ForceFail`).
      Verify: `dotnet build OrderProcess.sln` → 0 errors; consumer registered; configuration switch bound.

- [ ] 16. Remove the temporary `POST /test/publish-reserve` endpoint and its now-unused `IPublishEndpoint` test wiring from `Inventory.Api/Program.cs`. Leave the real `ReserveStockConsumer` path intact.
      Files: `d:\Projects\Saga_DDD\OrderProcess\src\Inventory\Inventory.Api\Program.cs`
      Verify: `dotnet build OrderProcess.sln` → 0 errors; `grep` confirms no `/test/publish-reserve` remains (sanity check only — build is the real gate).

- [ ] 17. End-to-end HAPPY PATH verification. Bring up infra; apply all migrations (ordering/inventory/payment/saga); start all four hosts (Ordering.Api, Inventory.Api, Payment.Api, OrderProcess.Saga) in background. Seed stock (`POST /stock`), create an order referencing that product with a `ProcessPayment` amount BELOW the fail threshold (`POST /orders`), then `POST /orders/{id}/submit`. Expect: saga drives OrderSubmitted→ReserveStock→StockReserved→ProcessPayment→PaymentCompleted→ConfirmOrder; order ends `Confirmed`; saga `OrderState` row terminal/completed; stock decremented; `Payments` row `Completed`.
      Files: none (runtime verification).
      Verify: query each DB — `orderingdb.Orders.Status = Confirmed`; `inventorydb.StockItems` reserved/available reflect the reserve; `paymentdb.Payments.Status = Completed`; saga instance finalized. Record HTTP ports and exact curl/Invoke-RestMethod calls in findings. Stop all hosts afterward.

- [ ] 18. End-to-end COMPENSATION verification. Same setup; create+submit an order with amount AT/ABOVE the fail threshold (or `ForceFail`). Expect: PaymentFailed → saga sends `ReleaseStock` → Inventory restores stock and publishes `StockReleased` → order ends `Cancelled`; saga finalized.
      Files: none (runtime verification).
      Verify: `orderingdb.Orders.Status = Cancelled`; `inventorydb.StockItems` restored to pre-reserve quantities; `paymentdb.Payments.Status = Failed`; saga instance finalized on the compensation branch. Record exact calls in findings. Stop all hosts afterward.

---

## Phase boundary

STOP after Phase 2. Do not begin Phase 3 (resilience) or Phase 4 (observability/docs). A human review gate follows this plan; Phase 3+ proceeds only after that review approves.

## Open items / assumptions (flag during implementation, do not stall)

- MassTransit 8.5.11 outbox: `AddEntityFrameworkOutbox` + `UseBusOutbox` requires the publish to occur on the consumer's scoped `DbContext` and a single `SaveChangesAsync`; verify the Inventory repository shares the MassTransit consume scope's DbContext so the publish is captured. Mark "needs verification during implementation."
- Order terminal state (Confirmed/Cancelled/Rejected) is applied in Ordering via a consumer reacting to saga-sent commands, because the `Order` aggregate lives in Ordering's context and must not be referenced by the saga. If the reviewer prefers the saga to publish distinct `OrderConfirmed`/`OrderCancelled`/`OrderRejected` integration events instead of reusing `ConfirmOrder`+a cancel command, that is an equivalent wiring — chosen approach: saga sends commands, Ordering consumer applies them.
- "Rejected" is not a distinct `OrderStatus` value (enum has Draft/Submitted/Confirmed/Cancelled — verify). On `StockReservationFailed` the order is cancelled (treated as terminal rejection) unless the enum is extended; mark for reviewer.
- Exact HTTP ports come from each `launchSettings.json`; capture them at run time rather than hard-coding in the plan.
