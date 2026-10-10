# OrderProcess — DDD + Saga Order Processor

A learning-oriented distributed order processor built with Domain-Driven Design, an
orchestration **Saga** (MassTransit state machine), the transactional **Outbox/Inbox**
pattern, explicit **compensation**, and a resilience layer (retry, delayed redelivery,
circuit breaker, optimistic concurrency), plus Phase 4 **observability** (structured logging,
distributed tracing, health checks).

Four independent .NET 9 services communicate only through integration events/commands over
RabbitMQ. Each bounded context owns its own database. The whole system runs locally against a
single Postgres instance (four logical databases) and a single RabbitMQ broker.

> Comments in the code are written in Arabic; identifiers and public contracts are English.

---

## Architecture overview

```
                      HTTP (POST /orders, /orders/{id}/submit)
                                    │
                                    ▼
                          ┌───────────────────┐
                          │   Ordering.Api     │  Order aggregate (DDD)
                          │   :5173            │  publishes OrderSubmitted
                          └─────────┬─────────┘
                                    │ OrderSubmitted
                                    ▼
                          ┌───────────────────┐
                          │ OrderProcess.Saga  │  orchestration state machine
                          │   :5280            │  Submitted → StockReserved → Paid → Confirmed
                          └───┬───────────┬────┘  (compensation / rejection branches)
                ReserveStock  │           │  ProcessPayment
                 ReleaseStock │           │
                              ▼           ▼
                 ┌──────────────────┐ ┌──────────────────┐
                 │  Inventory.Api   │ │   Payment.Api     │
                 │   :5197          │ │   :5268           │
                 │  StockItem       │ │  deterministic    │
                 │  xmin concurrency│ │  decision + gw    │
                 └──────────────────┘ └──────────────────┘

        All hops go through RabbitMQ (:5673). Each service has its own database
        in a single Postgres instance (:5433): orderingdb, inventorydb, paymentdb, sagadb.
```

- **Ordering** owns the `Order` aggregate, exposes HTTP, and starts the saga by publishing
  `OrderSubmitted`. It also applies `ConfirmOrder` / `CancelOrder` / `RejectOrder`.
- **Saga** is the orchestrator. It holds no business rules of its own; it reacts to
  integration events and issues commands. It references only `Contracts` (context isolation).
- **Inventory** reserves/releases/confirms stock on the `StockItem` aggregate, protected by
  `xmin` optimistic concurrency.
- **Payment** makes a **deterministic** decision (fail iff `amount >= Payment:FailAmountThreshold`,
  default `1000`) after calling a simulated gateway that can inject transient faults.

### The transient-vs-terminal boundary (the rule the whole design follows)

- **Expected / business failures** → Result pattern + an explicit terminal state, never throw.
  Insufficient stock → order **Rejected**; payment declined → compensation → order **Cancelled**.
- **Unexpected / infrastructure failures** → exceptions, and these are the only thing the
  retry / delayed-redelivery / circuit-breaker act on (e.g. `PaymentGatewayException`,
  `DbUpdateConcurrencyException`).

---

## Prerequisites

- .NET SDK 9
- Docker Desktop (for Postgres + RabbitMQ)

Host ports are intentionally remapped to avoid clashing with other local projects:

| Component          | Host port | Notes                                  |
|--------------------|-----------|----------------------------------------|
| Postgres           | **5433**  | container internal 5432                |
| RabbitMQ (AMQP)    | **5673**  | container internal 5672                |
| RabbitMQ (mgmt UI) | **15673** | http://localhost:15673 (guest/guest)   |
| Ordering.Api       | 5173      |                                        |
| Inventory.Api      | 5197      |                                        |
| Payment.Api        | 5268      |                                        |
| OrderProcess.Saga  | 5280      |                                        |

---

## How to run

From the repository root (`OrderProcess/`):

```powershell
# 1) start infrastructure (Postgres + RabbitMQ)
docker compose up -d

# The four logical databases (orderingdb, inventorydb, paymentdb, sagadb) are created
# automatically on first boot by docker/postgres-init/10-create-databases.sql.

# 2) apply EF Core migrations for each context
dotnet ef database update --project src\ordering\Ordering.Infrastructure  --startup-project src\ordering\Ordering.Api
dotnet ef database update --project src\Inventory\Inventory.Infrastructure --startup-project src\Inventory\Inventory.Api
dotnet ef database update --project src\Payment\Payment.Infrastructure     --startup-project src\Payment\Payment.Api
dotnet ef database update --project src\Saga\OrderProcess.Saga             --startup-project src\Saga\OrderProcess.Saga

# 3) build
dotnet build OrderProcess.sln

# 4) run the four hosts (each in its own terminal, or background them)
dotnet run --project src\ordering\Ordering.Api        --urls http://localhost:5173
dotnet run --project src\Inventory\Inventory.Api      --urls http://localhost:5197
dotnet run --project src\Payment\Payment.Api          --urls http://localhost:5268
dotnet run --project src\Saga\OrderProcess.Saga       --urls http://localhost:5280
```

A helper to start all four in the background is in
`.agents/tasks/saga-ddd-phase4/logs/start-hosts.ps1`.

### Health checks

Each host exposes `GET /health` (JSON), checking **Postgres** (`postgres`) and the **RabbitMQ**
connection the app uses (`masstransit-bus`):

```powershell
foreach ($p in 5173,5197,5268,5280) { (Invoke-WebRequest "http://localhost:$p/health").Content }
```

Expected: HTTP 200 with `"status": "Healthy"` and both `postgres` and `masstransit-bus` healthy.

---

## Walkthrough — reproduce the three paths

Each run seeds stock, creates an order, then submits it (which starts the saga). The decision
is deterministic:

| Scenario       | Setup                              | Outcome                                        |
|----------------|------------------------------------|------------------------------------------------|
| `happy`        | stock 50, qty 2, price 100 (=200)  | order **Confirmed**, stock 50→48, payment Completed |
| `compensation` | stock 50, qty 2, price 600 (=1200) | order **Cancelled**, stock reserved then restored to 50, payment Failed |
| `rejected`     | stock 1, qty 5                     | order **Rejected**, no payment row, stock unchanged |

Run (helper script):

```powershell
powershell -File .agents\tasks\saga-ddd-phase3\logs\run-flow.ps1 -Scenario happy
powershell -File .agents\tasks\saga-ddd-phase3\logs\run-flow.ps1 -Scenario compensation
powershell -File .agents\tasks\saga-ddd-phase3\logs\run-flow.ps1 -Scenario rejected
```

Each prints `ORDER_ID` and `PRODUCT_ID`. Inspect terminal state across the four databases:

```powershell
# order status
"SELECT ""Id"",""Status"" FROM ""Orders"" WHERE ""Id""='<ORDER_ID>';" | docker exec -i saga_postgres psql -U postgres -d orderingdb
# stock
"SELECT ""AvailableQuantity"",""ReservedQuantity"" FROM ""StockItems"" WHERE ""ProductId""='<PRODUCT_ID>';" | docker exec -i saga_postgres psql -U postgres -d inventorydb
# payment
"SELECT ""Status"" FROM ""Payments"" WHERE ""OrderId""='<ORDER_ID>';" | docker exec -i saga_postgres psql -U postgres -d paymentdb
# saga (0 rows once finalized)
"SELECT ""CurrentState"" FROM ""OrderState"" WHERE ""CorrelationId""='<ORDER_ID>';" | docker exec -i saga_postgres psql -U postgres -d sagadb
```

Doing a `happy` run by hand (the underlying calls `run-flow.ps1` makes):

```powershell
$ordering  = 'http://localhost:5173'
$inventory = 'http://localhost:5197'
$productId = [guid]::NewGuid()
# seed stock
Invoke-RestMethod -Method Post -Uri "$inventory/stock" -ContentType 'application/json' -Body (@{ productId=$productId; availableQuantity=50 } | ConvertTo-Json)
# create order
$o = Invoke-RestMethod -Method Post -Uri "$ordering/orders" -ContentType 'application/json' -Body (@{ customerId=[guid]::NewGuid(); items=@(@{ productId=$productId; unitPrice=100; quantity=2 }) } | ConvertTo-Json)
# submit -> starts the saga
Invoke-RestMethod -Method Post -Uri "$ordering/orders/$($o.orderId)/submit"
```

---

## Observability (Phase 4)

### Structured logging + CorrelationId

Every host logs through **Serilog** to the console. The convention is `CorrelationId = OrderId`.
A MassTransit consume observer (`CorrelationIdLogObserver`) pushes each message's
`CorrelationId` onto Serilog's `LogContext` for the duration of the consume, so **every log
line produced while handling one order carries the same `[Corr:<orderId>]` tag across all four
services** — filter one order's whole journey by a single id:

```
[02:41:31 INF] [Ordering.Api]       [Corr:974846e2-…] Order 974846e2-… submitted; publishing OrderSubmitted…
[02:41:32 INF] [OrderProcess.Saga]  [Corr:974846e2-…] …
[02:41:32 INF] [Inventory.Api]      [Corr:974846e2-…] …
[02:41:33 INF] [Payment.Api]        [Corr:974846e2-…] …
```

### Distributed tracing (OpenTelemetry)

Each host configures OpenTelemetry tracing with **MassTransit + EF Core + ASP.NET Core**
instrumentation, exported to the **console**. MassTransit propagates the trace context across
RabbitMQ hops, so **one order produces a single `TraceId` that spans all four services**:

```
TraceId = a39efba4…  (one order, all four services)
  Ordering : POST /orders · orderingdb · Contracts.Events:OrderSubmitted send · OrderStatus receive/process
  Saga     : OrderState receive/process · ReserveStock send · ProcessPayment send · ConfirmOrder send
  Inventory: ReserveStock receive/process · StockReserved send · inventorydb
  Payment  : ProcessPayment receive/process · PaymentCompleted send · paymentdb
```

To view one cross-service trace: run the four hosts, submit one order, and grep each host's
console output for the shared `Activity.TraceId`. A captured example is in
`.agents/tasks/saga-ddd-phase4/logs/cross-service-trace.txt`.

---

## Where each pattern lives (code map)

| Pattern | Location |
|---|---|
| **DDD aggregates** | `src/ordering/Ordering.Domain/Entities/Order.cs`, `src/Inventory/Inventory.Domain/Entities/StockItem.cs`, `src/Payment/Payment.Domain/Entities/Payment.cs` |
| **Saga orchestration** | `src/Saga/OrderProcess.Saga/StateMachine/OrderStateMachine.cs` |
| **Outbox/Inbox (transactional messaging + idempotency)** | `AddEntityFrameworkOutbox` + per-endpoint `UseEntityFrameworkOutbox` in each host `Program.cs`; EF model via `AddInboxStateEntity/AddOutboxMessageEntity/AddOutboxStateEntity` in each `DbContext` |
| **Compensation (StockReleased-confirmed)** | `OrderStateMachine` `During(StockReserved, When(PaymentFailedEvent))` → `Compensating` → `During(Compensating, When(StockReleasedEvent)).Finalize()` |
| **Rejected terminal (distinct from Cancelled)** | `OrderStateMachine` `When(StockReservationFailedEvent) → Publish(RejectOrder) → Finalize`; `Order.Reject`, `OrderStatus.Rejected` |
| **Deterministic payment** | `src/Payment/Payment.Domain/Entities/Payment.cs` (`Process(forceFail)`), threshold in `src/Payment/Payment.Api/Consumers/ProcessPaymentConsumer.cs` |
| **Retry + delayed redelivery** | `AddConfigureEndpointsCallback` in each host `Program.cs` (`UseMessageRetry` + `UseDelayedRedelivery`) |
| **Circuit breaker** | `src/Payment/Payment.Api/Program.cs` (`UseCircuitBreaker`, scoped to `PaymentGatewayException`) |
| **xmin optimistic concurrency** | `src/Inventory/Inventory.Infrastructure/Context/InventoryDbContext.cs` (token) + `Immediate(5)` retry on `DbUpdateConcurrencyException` in `src/Inventory/Inventory.Api/Program.cs`; proof suite `tests/Inventory.ConcurrencyTests/` |
| **Idempotency (inbox)** | Inbox dedup by `MessageId` via the EF outbox/inbox integration (per host) |
| **Correlation id** | `CorrelationId = OrderId` convention; enrichment in `src/BuildingBlocks/Observability/` |
| **Observability (Serilog + OTel + health)** | `src/BuildingBlocks/Observability/` used by all four hosts |

---

## Known limitations

Multi-line `OrderSubmitted` fan-out is deferred (only the first order line is propagated).
See [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md); the construction-site `TODO` is in
`src/ordering/Ordering.Api/Program.cs` (`/orders/{id}/submit`).

---

## Tests

Optimistic-concurrency proof (requires Postgres from `docker compose up -d`):

```powershell
dotnet test tests\Inventory.ConcurrencyTests\Inventory.ConcurrencyTests.csproj
```

Three asserted integration tests force a deterministic two-writer `xmin` collision, prove the
loser hits `DbUpdateConcurrencyException`, that the immediate-retry re-reads fresh state and
resolves (success or the correct "insufficient stock" business rejection), that no update is
lost, and that the naive last-writer-wins case is caught by the token.
