# Implementation Prompt — Distributed Order Processing (DDD + Saga + Outbox)

> Hand this document to a coding agent as the complete build spec. It is self-contained:
> goal, architecture, patterns, file paths, acceptance criteria, and verification steps.

---

## ROLE

You are a senior .NET distributed-systems engineer. Build a production-grade,
learning-oriented order-processing system that demonstrates DDD, the Saga pattern
(orchestration), the Transactional Outbox/Inbox pattern, and the resilience patterns
that make an at-least-once message system correct. The code must compile, run via
`docker compose`, and demonstrate both the happy path and the compensation path.

## CONTEXT (existing repo)

- Solution: `d:\Projects\Saga_DDD\OrderProcess\OrderProcess.sln`
- .NET: `net9.0`. Message bus: MassTransit `8.5.11`. Transport: RabbitMQ. DB: PostgreSQL via EF Core 9.
- Three bounded contexts, each layered Domain / Application / Infrastructure / Api:
  - `src/ordering/Ordering.*`   — owns the `Order` aggregate (rich domain model, domain events, value objects already exist).
  - `src/Inventory/Inventory.*` — owns `StockItem`; has a working `ReserveStockConsumer`.
  - `src/Payment/Payment.*`     — owns `Payment`.
- Shared integration schema in `src/Contracts` (`Commands/Commands.cs`, `Events/Events.cs`).
- Known defect to fix: `Inventory.Api/Consumers/ReserveStockConsumer.cs` does
  `SaveChangesAsync()` then `context.Publish(...)` as two separate operations — a
  **dual-write**. If the process dies between them the event is lost. The Outbox fixes this.
- Comments in existing files are in Arabic; keep new explanatory comments in Arabic to match,
  code identifiers in English.

## DECISIONS (fixed — do not revisit)

1. **Orchestration**, not choreography. A single `OrderSaga` state machine coordinates the flow.
2. The saga lives in a **dedicated host project** `src/Saga/OrderProcess.Saga` (ASP.NET worker/minimal host) with its own PostgreSQL database for saga + outbox state.
3. Message delivery is **at-least-once**; every consumer must be **idempotent** (Inbox).
4. Every publish that must happen with a DB change goes through the **Transactional Outbox**
   (`MassTransit.EntityFrameworkCoreIntegration` `AddEntityFrameworkOutbox`).

## PATTERNS TO IMPLEMENT (and prove)

| Pattern | Where | How to prove it works |
|---|---|---|
| DDD aggregates + domain events | all `*.Domain` | invariants throw `DomainException`; events raised on state change |
| Saga orchestration (state machine) | `OrderProcess.Saga` | persisted saga state transitions Submitted→StockReserved→Paid→Confirmed |
| Transactional Outbox | all contexts + saga | kill broker, do business op, restart broker → message still delivered |
| Inbox / idempotent consumer | all consumers | redeliver same message twice → state changes once |
| Compensating transactions | saga | force PaymentFailed → ReleaseStock fires → order Cancelled |
| Retry + delayed redelivery | all endpoints | transient throw retried with backoff, visible in logs |
| Circuit breaker | Payment consumer | repeated payment failures open the breaker |
| Dead-letter / error queue | all | poison message lands in `_error` queue |
| Optimistic concurrency | `StockItem` | two concurrent reserves → one retried, no lost update |
| Correlation ID propagation | everywhere | one `CorrelationId` traces the whole flow in logs/traces |

## TARGET MESSAGE FLOW

```
Client POST /orders            -> Ordering creates Order (Draft)
Client POST /orders/{id}/submit-> Order.Submit() raises OrderSubmitted (via Outbox)
OrderSaga: OrderSubmitted       -> send ReserveStock
Inventory: ReserveStock         -> StockReserved | StockReservationFailed  (via Outbox)
OrderSaga: StockReserved        -> send ProcessPayment
            StockReservationFailed -> order Rejected (terminal)
Payment: ProcessPayment         -> PaymentCompleted | PaymentFailed       (via Outbox)
OrderSaga: PaymentCompleted     -> send ConfirmOrder; order Confirmed (terminal)
            PaymentFailed        -> COMPENSATE: send ReleaseStock; order Cancelled (terminal)
Inventory: ReleaseStock         -> StockReleased (via Outbox)
```

Add any missing messages to `src/Contracts` (e.g. `ConfirmOrder`, and ensure
`ReleaseStock`, `StockReleased`, `PaymentCompleted`, `PaymentFailed` carry `CorrelationId`).

## WORK PLAN (phased — build and verify each phase before the next)

### Phase 0 — Runnable infrastructure
- Add `docker-compose.yml` at repo root: `postgres:16` (one instance, 4 databases or 4 instances — your call, document it) and `rabbitmq:3-management` (ports 5672 + 15672).
- Add `Directory.Packages.props` with central package management; move all `PackageReference` versions there.
- Add `appsettings` connection strings + RabbitMQ host config to each Api and the saga host, overridable by env vars for Docker.
- **Verify:** `docker compose up -d` brings up Postgres + RabbitMQ; management UI reachable at `http://localhost:15672` (guest/guest).

### Phase 1 — Outbox + Inbox in every context
- Add `MassTransit` + `MassTransit.EntityFrameworkCoreIntegration` to all three Apis and the saga host.
- In each DbContext, add the MassTransit outbox entity sets (`AddInboxStateEntity`, `AddOutboxMessageEntity`, `AddOutboxStateEntity` in `OnModelCreating`).
- Configure `AddEntityFrameworkOutbox<TDbContext>` with `UsePostgres()` + `UseBusOutbox()`.
- Rewrite `ReserveStockConsumer` so the publish and the `SaveChanges` are one transaction via the outbox (removes the dual-write).
- Add EF migrations for the new outbox/inbox tables in every context.
- **Verify:** migrations apply; with RabbitMQ stopped, perform a stock reserve, confirm the row is written and the outbox row is pending; start RabbitMQ; confirm the message is delivered.

### Phase 2 — Order Saga (the core)
- Create `src/Saga/OrderProcess.Saga` host project; register it in the solution.
- Implement `OrderState : SagaStateMachineInstance` (CorrelationId, CurrentState, OrderId, Amount, timestamps) and `OrderStateMachine : MassTransitStateMachine<OrderState>` with states and transitions matching the flow above, including compensation.
- Persist saga state with EF (`EntityFrameworkSagaRepository`) in the saga's own database; add its migration.
- Add MassTransit to `Ordering.Api`; add `POST /orders/{id}/submit` that calls `Order.Submit()` and publishes `OrderSubmitted` through the Ordering outbox.
- Create `Payment` consumer for `ProcessPayment` that writes a `Payment` row and publishes the result via outbox. Add a configurable "fail payment" switch (e.g. amount threshold or random) to exercise compensation.
- Remove the temporary `/test/publish-reserve` endpoint in `Inventory.Api`.
- **Verify (happy path):** POST order → submit → saga drives Reserve→Pay→Confirm; order ends `Confirmed`, saga state terminal, stock decremented, payment row `Completed`.
- **Verify (compensation):** force payment failure → `ReleaseStock` fires → stock restored → order `Cancelled`.

### Phase 3 — Resilience
- On each receive endpoint: `UseMessageRetry` (exponential backoff, e.g. 3 tries) + `UseDelayedRedelivery` (e.g. 5m/15m/30m).
- `UseCircuitBreaker` (or Polly) around the payment gateway call.
- Add optimistic concurrency to `StockItem` (Postgres `xmin` as concurrency token) and handle `DbUpdateConcurrencyException` by letting the message retry.
- Ensure consumer idempotency relies on the Inbox (dedup by `MessageId`/`CorrelationId`).
- **Verify:** inject a transient fault and observe retries in logs; redeliver a processed message and confirm no double effect; open the breaker with repeated failures.

### Phase 4 — Observability & docs
- Serilog structured logging with `CorrelationId` enriched on every message scope.
- OpenTelemetry tracing (MassTransit + EF + ASP.NET instrumentation) exported to console or an OTLP collector; document how to view a cross-service trace.
- Health checks (`/health`) in every host checking DB + broker.
- Write `README.md`: architecture diagram, how to run, how to trigger happy path and compensation path, what each pattern is and where to see it in the code.

### Phase 5 — Optional follow-ups (do not build unless asked)
- Choreography variant for contrast; CQRS read model for order status; integration tests with Testcontainers.

## CONSTRAINTS

- Keep bounded contexts isolated: no project may reference another context's Domain/Infrastructure. Cross-context communication is **only** through `Contracts` messages over the bus.
- Do not add tests unless explicitly requested, but every phase must be manually verifiable per its Verify steps.
- Pin exact package versions (central management). Prefer well-known packages.
- Use parameterized EF queries only; validate inputs; no secrets in source (use env vars / user-secrets).

## ACCEPTANCE CRITERIA (definition of done)

1. `dotnet build OrderProcess.sln` succeeds with zero errors.
2. `docker compose up -d` + running the hosts yields a working happy path end-to-end.
3. Forcing a payment failure yields a complete compensation (stock released, order cancelled).
4. Stopping RabbitMQ during a business operation does **not** lose the event (outbox proves it).
5. Redelivering an already-processed message causes no duplicate state change (inbox proves it).
6. One `CorrelationId` is traceable across all services in logs/traces.
7. `README.md` lets a newcomer reproduce items 2–6 from scratch.

## DELIVERY

Work phase by phase. After each phase: build, run its Verify steps, and report what you
changed, how you verified it, and anything that could not be verified and why. Pause for
review after Phase 2 (the core) before continuing to Phase 3.
