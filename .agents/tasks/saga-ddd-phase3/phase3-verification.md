# Phase 3 (Resilience) — Verification Report

First iteration (no prior `phase3-review.json`). Implemented Phase 3 per
`docs/IMPLEMENTATION_PROMPT.md` plus the three user-approved folded findings (#1, #2, #4)
and the deferred #3 (documented). All verification ran against live infra (docker compose:
Postgres `localhost:5433`, RabbitMQ `localhost:5673` / mgmt `15673`) and the four hosts
(Ordering 5173, Inventory 5197, Payment 5268, Saga 5280).

Guiding principle enforced: expected/business failures use the Result pattern + an explicit
terminal state (Rejected / Cancelled); unexpected/infrastructure failures use exceptions +
retry / delayed redelivery / circuit breaker. The transient-vs-terminal boundary is crisp:
the Payment business decline publishes `PaymentFailed` and NEVER throws, while the simulated
gateway infra fault throws `PaymentGatewayException`, which is the only type the retry/breaker
act on.

---

## Build

Command (run with hosts stopped to avoid DLL file locks):

```
dotnet build OrderProcess.sln
```

Result: **Build succeeded. 0 Warning(s) / 0 Error(s).**

(When a build is attempted while the four hosts are running, MSBuild reports `MSB3021`
"file is being used by another process" copy errors — these are runtime file locks on the
output DLLs, not compile errors. The authoritative 0/0 build was taken with hosts stopped.)

## Migrations applied

New migration: `src/Inventory/Inventory.Infrastructure/Migrations/20261009221122_AddStockItemConcurrencyToken.cs`

```
dotnet ef database update --project src\Inventory\Inventory.Infrastructure --startup-project src\Inventory\Inventory.Api
# -> Applying migration '20261009221122_AddStockItemConcurrencyToken'. Done.

dotnet ef migrations list ...
#  20261003153637_InitialCreate
#  20261009191847_AddOutbox
#  20261009221122_AddStockItemConcurrencyToken
```

The migration is an intentional **no-op on the schema**: `xmin` is a Postgres system column
present on every table, so it is mapped as an EF concurrency token (shadow property) with no
DDL. The auto-generated `AddColumn("xmin")` was removed because adding the system column
manually would fail; the migration exists only to keep the model snapshot consistent.

No Ordering migration was needed for `OrderStatus.Rejected`: `Status` is persisted via
`HasConversion<string>()` (text column), so a new enum value adds no schema change.

---

## Folded findings

### #1 Determinism (payment decision is a pure function of input)

`Payment.Domain/Entities/Payment.Process` previously used `Random.Shared`. Now:
`var success = !forceFail;` — all randomness removed. `ProcessPaymentConsumer` sets
`forceFail = amount >= Payment:FailAmountThreshold` (default 1000). Outcome is fully
reproducible. Verified by the deterministic happy/compensation paths below.

### #2 Explicit Rejected terminal state (distinct from Cancelled)

- `OrderStatus` gained `Rejected`; `Order.Reject(reason)` added (guards: only a `Submitted`
  order can be rejected) raising a new `OrderRejected` domain event.
- New contract `RejectOrder`; `OrderStatusConsumer` now handles `ConfirmOrder` / `CancelOrder`
  / `RejectOrder`.
- Saga: on `StockReservationFailed` the saga publishes `RejectOrder` and finalizes (terminal,
  no stock to release) — a path separate and distinct from the
  `PaymentFailed -> ReleaseStock -> Cancelled` compensation branch.

### #4 Confirm compensation completion (wait for StockReleased)

The saga now has a `Compensating` state. On `PaymentFailed` it publishes `ReleaseStock` +
`CancelOrder` and transitions to `Compensating`; it reaches terminal (finalize) **only** when
`StockReleased` is observed `During(Compensating, When(StockReleasedEvent)...Finalize())`.
The stale "waits for StockReleased" comment on the old fire-and-forget branch is gone and the
previously dead `StockReleasedEvent` is now actually handled.

### #3 Multi-line OrderSubmitted — DEFERRED (documented)

- Known-limitations note added: `docs/KNOWN_LIMITATIONS.md`.
- TODO comment added at the `OrderSubmitted` construction site in
  `src/ordering/Ordering.Api/Program.cs` (`/orders/{id}/submit` handler) stating only the
  first order line is propagated and multi-item fan-out/fan-in is deferred.
- Event shape unchanged; no saga fan-out added.

---

## Verification bar

Terminal state inspected across the four databases via `psql` in the `saga_postgres`
container. Helper scripts live in `.agents/tasks/saga-ddd-phase3/logs/`
(`run-flow.ps1`, `check.sql`, `comp-trace.ps1`, `drive-faults.ps1`).

### 1. Deterministic HAPPY path (amount < 1000)

ProductId `6b899565-...`, stock 50, order qty 2, unit price 100 (total 200 < 1000).

Observed (all four DBs):

```
ORDER   | 1aaebec9-... | Confirmed
STOCK   | 6b899565-... | Available 48 | Reserved 2      (decremented from 50 by exactly 2)
PAYMENT | 1aaebec9-... | Amount 200   | Completed
SAGA    | (0 rows)                                       (finalized)
```

### 2. Deterministic COMPENSATION path (amount >= 1000)

ProductId `a40f5919-...`, stock 50, order qty 2, unit price 600 (total 1200 >= 1000).

Observed:

```
ORDER   | 7208ceef-... | Cancelled
STOCK   | a40f5919-... | Available 50 | Reserved 0      (reserved then released -> restored)
PAYMENT | 7208ceef-... | Amount 1200  | Failed          (deterministic business decline)
SAGA    | (0 rows)                                       (finalized)
```

**Saga finalizes ONLY after StockReleased** (not fire-and-forget) — captured live with
`comp-trace.ps1` polling the saga state right after submit:

```
01:21:27.164  STATE=Compensating
01:21:27.511  STATE=(finalized/no-row)
TRANSITIONS: Compensating -> (finalized/no-row)
```

The saga sat in `Compensating` and only finalized ~0.35s later when `StockReleased` arrived
(RabbitMQ round-trip for `ReleaseStock` -> `StockReleased`).

### 3. Deterministic REJECTED path (stock reservation failure) — distinct from Cancelled

ProductId `34c4f86d-...`, stock seeded = 1, order qty 5 (reserve 5 > available 1).

Observed:

```
ORDER   | d55df525-... | Rejected                        (NOT Cancelled)
STOCK   | 34c4f86d-... | Available 1 | Reserved 0         (unchanged -> nothing released)
PAYMENT | (0 rows)                                        (payment never attempted)
SAGA    | (0 rows)                                        (finalized on the rejected branch)
```

Demonstrably distinct from path #2: no payment row, no stock release cycle, order ends
`Rejected` rather than `Cancelled`.

### 4. Retry with backoff (transient fault injected)

Payment host restarted with `Payment__SimulateTransientFault=true`; the simulated gateway
throws `PaymentGatewayException` on every call. 8 sub-threshold orders (amount 200 — which
would normally SUCCEED) submitted via `drive-faults.ps1`.

Payment host log (`logs/payment.fault.out.log`) shows message retry firing on the transient
fault:

```
warn: MassTransit.ReceiveTransport[0]
      R-RETRY rabbitmq://localhost:5673/ProcessPayment e0570000-...-08df2653ee15 MassTransit.RetryPolicies.RetryConsumeContext<Contracts.Commands.ProcessPayment>
      Payment.Api.Gateway.PaymentGatewayException: Simulated transient payment gateway failure for order afed1d6d-...
```

Count in that run: **19 `R-RETRY` entries**. Retry policy configured on the ProcessPayment
endpoint: `UseMessageRetry(r => r.Exponential(3, 1s, 10s, 2s))` + `UseDelayedRedelivery(5m/15m/30m)`,
both scoped with `r.Handle<PaymentGatewayException>()`.

### 5. Redelivery / idempotency (no double effect)

The inbox dedups by MessageId. During the retry run the same messages were redelivered to the
transport multiple times; the `InboxState` table in `inventorydb` proves single consumption:

```
SELECT max("ReceiveCount"), count(*) FILTER (WHERE "Consumed" IS NOT NULL), count(*) FROM "InboxState";
 max_receivecount | consumed_count | total
------------------+----------------+-------
                4 |             18 |    18
```

`max ReceiveCount = 4` (messages redelivered up to 4 times) yet every one of the 18 inbox
rows is `Consumed` exactly once. The happy-path product stayed at `Available 48 / Reserved 2`
(single decrement of 2 — not doubled) despite the redeliveries:

```
STOCK | 6b899565-... | Available 48 | Reserved 2
```

So an already-processed message that is redelivered causes no double stock decrement and no
duplicate payment.

### 6. Circuit breaker opens on repeated transient failures

Same fault-injection run. Once repeated `PaymentGatewayException`s crossed the breaker
thresholds (`TrackingPeriod 1m`, `TripThreshold 15`, `ActiveThreshold 5`, `ResetInterval 1m`,
`Handle<PaymentGatewayException>()`), the breaker opened and short-circuited further calls.
Log stack frames show the open breaker rejecting at `PreSend`:

```
at MassTransit.Middleware.CircuitBreaker.OpenBehavior...PreSend() ... OpenBehavior.cs:line 35
at MassTransit.Middleware.CircuitBreakerFilter`1.Send(...) ... CircuitBreakerFilter.cs:line 69
```

Count of open-breaker short-circuits in that run: **19 `OpenBehavior` occurrences**.

### 7. Business decline does NOT trip the breaker or retry

Payment host restarted WITHOUT fault injection. A business-decline order (amount 1200 >= 1000)
submitted. Result: order `Cancelled`, stock restored, one `PaymentFailed` row, saga finalized.

Clean payment log (`logs/payment.clean.out.log`) occurrence counts:

```
R-RETRY: 0
OpenBehavior: 0
PaymentGatewayException: 0
```

The deterministic business decline publishes `PaymentFailed` without throwing, so it triggers
zero retries and never engages the circuit breaker — exactly the transient-vs-terminal
boundary required. Confirmed further: the 8 faulted (transient) orders produced **0** rows in
`Payments` (the gateway throws before any business decision), whereas the business decline
produced exactly one `Failed` row.

### Optimistic concurrency on StockItem (xmin) — DEMONSTRATED (post-approval close-out)

`InventoryDbContext` maps the Postgres `xmin` system column as a concurrency token (shadow
property, `IsConcurrencyToken()`), matching the saga's existing `xmin` mapping. The Inventory
endpoint callback adds `UseMessageRetry(r => { r.Handle<DbUpdateConcurrencyException>(); r.Immediate(5); })`
so that when two concurrent reserves collide, the loser fails with
`DbUpdateConcurrencyException`, is retried immediately, re-reads the latest `xmin`, and
re-applies — no lost update.

The previously-inferred race is now **demonstrated** by a repeatable, non-interactive,
asserted integration test suite driven against the live Postgres (`localhost:5433`,
`inventorydb`): `tests/Inventory.ConcurrencyTests/StockItemConcurrencyTests.cs`. It uses the
real `InventoryDbContext`, the real `xmin` token mapping, and the real `StockItem.Reserve`
domain method (no mechanism was weakened). The collision is forced **deterministically**, not
by timing: two separate `DbContext` instances each read the same `StockItem` row — the test
asserts both captured the identical `xmin` value before any commit — then both attempt to
reserve. The winner commits (xmin bumps); the loser's commit must fail every run.

The retry resolution loop mirrors production exactly: on `DbUpdateConcurrencyException`,
discard the stale context, re-read the fresh row, re-apply `Reserve`, re-save — up to the same
`Immediate(5)` ceiling as the Inventory endpoint.

Run command (hosts may be stopped or running; the test talks only to Postgres):

```
dotnet test tests\Inventory.ConcurrencyTests\Inventory.ConcurrencyTests.csproj --logger "console;verbosity=detailed"
```

Result: **3 passed / 0 failed** (~5.8s). Captured evidence per case:

1. `Concurrent_reserve_loser_hits_concurrency_exception_then_retry_resolves_when_stock_sufficient`
   — stock 100, two writers each reserve 10. Both read the same `xmin` (asserted equal).
   Winner commits; loser throws `DbUpdateConcurrencyException`
   (`expected to affect 1 row(s), but actually affected 0 row(s)`). Retry re-reads and
   succeeds. Final asserted invariants: `AvailableQuantity 80`, `ReservedQuantity 20`,
   total conserved at 100 — **both** reservations applied, **no lost update**.
   Captured: `[CASE1] Loser threw DbUpdateConcurrencyException as expected: ...`,
   `[CASE1] Retry resolved: succeeded=True, reason=, retries=1`.

2. `Concurrent_reserve_loser_retry_resolves_to_business_rejection_when_stock_insufficient`
   — stock 10, two writers each reserve 7 (14 > 10, only one can win). Same deterministic
   collision; loser throws `DbUpdateConcurrencyException`. Retry re-reads against post-winner
   state (available now 3 < 7) and resolves to the **business** `ReserveResult.Fail`
   ("Insufficient available quantity.") — not an exception, not a lost update. Final asserted
   invariants: `AvailableQuantity 3`, `ReservedQuantity 7`, total conserved at 10 — exactly
   one reservation, correctly rejected the second.
   Captured: `[CASE2] Loser threw DbUpdateConcurrencyException as expected: ...`,
   `[CASE2] Retry resolved: succeeded=False, reason=Insufficient available quantity., retries=1`.

3. `Naive_last_writer_wins_is_prevented_by_concurrency_token` — guard proving the mechanism is
   not a no-op: a blind second write over a stale read would otherwise overwrite the winner
   (last-writer-wins = lost update). The test asserts EF raises `DbUpdateConcurrencyException`
   and that the winner's reservation survives (`AvailableQuantity 45`, `ReservedQuantity 5`),
   so the naive case demonstrably breaks and the token catches it.

This drives the concurrency collision at the DbContext/repository level directly (the layer
where `xmin` is actually enforced), as permitted when the EF/MassTransit retry wrapper cannot
be exercised end-to-end non-interactively. The MassTransit `Immediate(5)` filter wiring on the
Inventory endpoint is unchanged and still handles `DbUpdateConcurrencyException`; the test
reproduces that exact handle-and-re-read behavior in code and asserts the outcome. No
concurrency token or reserve logic was relaxed to make the run green — the suite instead
proves the existing mechanism works.

---

## Resilience wiring summary (where each pattern lives)

- `src/Payment/Payment.Api/Program.cs` — endpoint callback: `UseDelayedRedelivery` +
  `UseMessageRetry` + `UseCircuitBreaker` (all `Handle<PaymentGatewayException>()`) +
  `UseEntityFrameworkOutbox<PaymentDbContext>` (ordered outer->inner: redelivery, retry,
  breaker, outbox). The pre-existing consumer-outbox callback is preserved.
- `src/Payment/Payment.Api/Gateway/*` — `IPaymentGateway` + `SimulatedPaymentGateway`
  (`Payment:SimulateTransientFault` switch) and `PaymentGatewayException` (transient marker).
- `src/Inventory/Inventory.Api/Program.cs` — endpoint callback: `UseDelayedRedelivery` +
  `UseMessageRetry(Handle<DbUpdateConcurrencyException>, Immediate(5))` + outbox.
- `src/ordering/Ordering.Api/Program.cs` and `src/Saga/OrderProcess.Saga/Program.cs` —
  endpoint callback: `UseDelayedRedelivery` + exponential `UseMessageRetry` + outbox.
- `src/Inventory/Inventory.Infrastructure/Context/InventoryDbContext.cs` — `xmin` concurrency
  token on `StockItem`.
- `src/Saga/OrderProcess.Saga/StateMachine/OrderStateMachine.cs` — `Compensating` state;
  `RejectOrder` on reservation failure; finalize-on-`StockReleased`.

All four hosts' pre-existing EF transactional outbox + the per-endpoint consumer-outbox
callback (`UseEntityFrameworkOutbox<T>`) are preserved and still active (observed in startup
logs: `SELECT *, xmin FROM "OutboxState" ... FOR UPDATE SKIP LOCKED`).

## How to reproduce manually

1. `docker compose up -d` (from repo root `d:\Projects\Saga_DDD\OrderProcess`).
2. Apply migrations: `dotnet ef database update` for each context (Inventory shown above).
3. Start the four hosts (PowerShell, non-blocking), each with its port:
   `dotnet run --project <proj> --urls http://localhost:<port>`
   (saga 5280, ordering 5173, inventory 5197, payment 5268).
4. Happy / compensation / rejected: `powershell -File .agents\tasks\saga-ddd-phase3\logs\run-flow.ps1 -Scenario happy|compensation|rejected`
   then inspect state with `check.sql` piped to `docker exec -i saga_postgres psql`.
5. Compensation wait proof: `comp-trace.ps1`.
6. Retry + breaker: restart payment with env `Payment__SimulateTransientFault=true`, run
   `drive-faults.ps1`, inspect `payment.fault.out.log` for `R-RETRY` and `OpenBehavior`.
7. Business-decline-does-not-trip: restart payment without the env var, run a compensation
   order, confirm `payment.clean.out.log` has 0 `R-RETRY` / 0 `OpenBehavior`.
8. Idempotency: query `InboxState` (`ReceiveCount` vs `Consumed`) in `inventorydb`.
