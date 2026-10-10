# Phase 3 resilience + folded findings (#1, #2, #4) for the DDD/Saga order processor

Phase 3 adds the resilience layer (message retry with exponential backoff, delayed redelivery, a circuit breaker around the payment gateway, and `xmin` optimistic concurrency on `StockItem`) on top of the existing Outbox/Inbox saga, and folds in three user-approved foundation fixes before layering resilience on them: payment is now a deterministic pure function of amount (#1), stock-reservation failure drives a distinct terminal `Rejected` state (#2), and payment-failure compensation finalizes only after `StockReleased` is observed (#4). The multi-line `OrderSubmitted` limitation (#3) is deferred and documented. The whole change keys off one crisp boundary: expected/business failures publish a result and reach an explicit terminal state without throwing; unexpected/infrastructure failures throw and are the only thing retry/redelivery/breaker act on. This review reads the local diff against the recorded verification evidence at `.agents/tasks/saga-ddd-phase3/phase3-verification.md` rather than re-running the suites.

Watch for: the two-writer `xmin` concurrency race was not scripted — the mechanism is wired and model-verified but the race itself was not driven live (possible, documented by the coder, non-blocking). Everything else on the gate is confirmed in code and backed by credible live-infra evidence.

**Verdict**: APPROVED

## High-level view

The transient-vs-terminal boundary is implemented exactly as gated. `ProcessPaymentConsumer` calls the gateway first (infra layer, may throw `PaymentGatewayException`) and only then makes the business decision, which never throws — it writes a `Payment` row and publishes `PaymentCompleted`/`PaymentFailed`. Retry, delayed redelivery, and the circuit breaker on the Payment endpoint all scope to `PaymentGatewayException` via `r.Handle<...>()`, so a deterministic decline cannot trip the breaker or trigger a retry. The verification evidence shows exactly this asymmetry: a fault-injection run produced R-RETRY and open-breaker entries and zero `Payments` rows (gateway threw before any decision), while a clean business decline produced one `Failed` row and zero retries/breaker events.

Determinism (#1) is a one-line change in the domain (`var success = !forceFail`) driven by `amount >= Payment:FailAmountThreshold` (default 1000) in the consumer. No `Random` remains anywhere in the decision path.

The two terminal branches are demonstrably distinct in the state machine (#2, #4). `StockReservationFailed` publishes `RejectOrder` and finalizes immediately with no stock release — the order ends `Rejected`. `PaymentFailed` publishes `ReleaseStock` + `CancelOrder`, transitions to a new `Compensating` state, and finalizes only `During(Compensating, When(StockReleasedEvent))` — the order ends `Cancelled`. The previously dead `StockReleasedEvent` is now actually handled and the stale fire-and-forget comment is gone. Live traces confirm the saga sits in `Compensating` until `StockReleased` arrives.

The resilience wiring is present on all four receive endpoints (exponential `UseMessageRetry(3)` + `UseDelayedRedelivery(5/15/30m)`), with the breaker added only on Payment and `DbUpdateConcurrencyException`/`Immediate(5)` retry added on Inventory for the `xmin` concurrency path. Critically, the pre-existing per-endpoint `UseEntityFrameworkOutbox<T>(context)` callback is preserved on every host, context isolation holds (the saga references only `Contracts`), and packages stay centrally pinned at MassTransit 8.5.11 with no inline `Version=`.

The deferred #3 is handled correctly: `docs/KNOWN_LIMITATIONS.md` documents the single-line fan-out, a matching TODO sits at the `OrderSubmitted` construction site in `Ordering.Api`, and neither the event shape nor the saga fan-out was changed.

<details>
<summary>Issues (1)</summary>

1. **Unscripted concurrency race** — the `xmin` two-writer collision was not driven live; the token mapping, migration, and `Handle<DbUpdateConcurrencyException>` + `Immediate(5)` retry are all in place and model-verified, so this is a documented evidence gap, not a code gap. Non-blocking; a forced two-writer script would close it fully.

</details>

<details>
<summary>Details</summary>

### Transient-vs-terminal boundary in the payment path

The call ordering is what makes the boundary hold. `ProcessPaymentConsumer.Consume` calls `gateway.AuthorizeAsync(...)` before constructing the `Payment`, so an injected `PaymentGatewayException` leaves the consumer before any business decision or DB write — which is why the fault run shows zero `Payments` rows. The business decision that follows (`amount >= threshold`) runs through `Payment.Process(forceFail)`, which adds a `PaymentCompleted`/`PaymentFailed` domain event but never throws, and the consumer publishes the matching integration event through the outbox.

Because the endpoint callback scopes all three resilience filters to `r.Handle<PaymentGatewayException>()`, a decline is invisible to them. The breaker thresholds (`TripThreshold 15`, `ActiveThreshold 5`, `TrackingPeriod/ResetInterval 1m`) are tuned low to make the open state observable during verification.

```
gateway.AuthorizeAsync()      // infra: may throw PaymentGatewayException  -> retry/redelivery/breaker
payment.Process(forceFail)    // business: never throws -> PaymentCompleted | PaymentFailed via outbox
```

### Determinism (#1)

`Payment.Process` reduces to `var success = !forceFail;`. The consumer computes `forceFail` from `decimal.TryParse(configuration["Payment:FailAmountThreshold"]) && command.Amount >= threshold`. If the threshold config is missing or unparseable, `TryParse` fails and `forceFail` stays false, so payment succeeds — a benign default. Outcome is a pure function of amount; no random roll remains.

### Distinct Rejected vs Cancelled terminals (#2, #4)

The state machine encodes the two branches without overlap:

```
During(Submitted)
  StockReservationFailed -> Publish(RejectOrder) -> Finalize        // terminal: Rejected, no release
During(StockReserved)
  PaymentFailed -> Publish(ReleaseStock) + Publish(CancelOrder)
                -> TransitionTo(Compensating)
During(Compensating)
  StockReleased -> Finalize                                         // terminal: Cancelled, release confirmed
```

`Order.Reject(reason)` guards on `Status == Submitted` and raises `OrderRejected`; `OrderStatus` gained `Rejected`; `OrderStatusConsumer` handles the new `RejectOrder` command alongside `ConfirmOrder`/`CancelOrder`. The `Compensating` state means the saga does not reach terminal on fire-and-forget — the previously declared-but-unhandled `StockReleasedEvent` is now wired into a real `During` block, and the stale "waits for StockReleased" comment on the old branch is gone. The live `comp-trace.ps1` capture (sat in `Compensating`, finalized ~0.35s later when `StockReleased` arrived) is credible evidence of the wait actually happening.

The two branches are observably different in the verification DBs: the rejected path has no `Payments` row and unchanged stock (nothing was reserved to release), ending `Rejected`; the compensation path reserves then releases stock, writes a `Failed` payment, and ends `Cancelled`.

### Optimistic concurrency on StockItem

`InventoryDbContext` maps `xmin` as a shadow concurrency token (`HasColumnType("xid")`, `ValueGeneratedOnAddOrUpdate()`, `IsConcurrencyToken()`), mirroring the saga's existing approach. The migration is an intentional no-op — `xmin` is a Postgres system column, so there is no DDL; its purpose is keeping the model snapshot consistent, and the auto-generated `AddColumn` was correctly removed because adding the system column manually would fail. The Inventory endpoint retries `DbUpdateConcurrencyException` immediately up to five times, which is the right response to a lost-update collision (re-read latest `xmin`, re-apply).

The evidence verifies the mechanism at the model level and infers correctness from the single-decrement idempotency run, but explicitly states a forced two-writer race was not scripted. The gate requires the token present and the exception handled by retry — both confirmed. The missing live race is a documented evidence gap, not a code gap, so it does not block.

### Resilience wiring and preserved invariants across hosts

All four hosts apply `UseDelayedRedelivery(5/15/30m)` and `UseMessageRetry` in the endpoint callback; Ordering, Saga, and Payment use exponential `(3, 1s, 10s, 2s)`, Inventory uses `Immediate(5)` scoped to the concurrency exception. Payment adds the breaker. Every host's callback ends with `UseEntityFrameworkOutbox<T>(context)`, so the pre-existing consumer-outbox is preserved — the gated invariant holds. Filter ordering is outer-to-inner redelivery -> retry -> breaker -> outbox: transient blips are absorbed by retry without broker round-trips, longer outages fall through to delayed redelivery, and the breaker short-circuits once the gateway is clearly down.

Context isolation holds: `OrderProcess.Saga.csproj` references only `Contracts.csproj`. Central package management holds: no `Version=` attribute appears in any `src/**/*.csproj`, and `Directory.Packages.props` pins MassTransit and its RabbitMQ/EF integration at 8.5.11. New comments are Arabic, identifiers English — convention preserved.

### Deferred #3

`docs/KNOWN_LIMITATIONS.md` documents that `OrderSubmitted` carries only `order.Items.FirstOrDefault()` and that multi-item fan-out/fan-in is deferred. A matching Arabic TODO sits at the `/orders/{id}/submit` handler in `Ordering.Api/Program.cs`. The integration `OrderSubmitted` record and the saga's single-product `ReserveStock`/`ReleaseStock` flow are unchanged — the deferral is clean, nothing was half-built.

### Verification evidence credibility

The recorded build is 0/0 (taken with hosts stopped to avoid the DLL file-lock MSB3021 noise, which is explained and is a runtime lock, not a compile error). The live-infra bar covers every gated scenario with concrete observations: deterministic happy (stock 50->48, payment Completed), compensation (stock reserved then restored to 50, payment Failed, order Cancelled), rejected (no payment row, stock unchanged, order Rejected), retry-with-backoff (19 R-RETRY entries on injected faults), redelivery no-double-effect (`InboxState` max ReceiveCount 4 but every row Consumed once, stock single-decremented), breaker opening (19 OpenBehavior short-circuits), and business-decline-does-not-trip (0 R-RETRY / 0 OpenBehavior on the clean run). The manual reproduction steps name the helper scripts and expected observable results. The only item resting on mechanism-plus-inference rather than a driven scenario is the concurrency race, which the coder flagged honestly.

</details>

<details>
<summary>File map</summary>

- `src/Payment/Payment.Api/Program.cs` — Payment endpoint callback: redelivery + exponential retry + circuit breaker (all `Handle<PaymentGatewayException>`) + outbox.
- `src/Payment/Payment.Api/Gateway/PaymentGateway.cs`, `PaymentGatewayException.cs` — simulated gateway with transient-fault switch; transient marker exception.
- `src/Payment/Payment.Api/Consumers/ProcessPaymentConsumer.cs` — gateway-call-then-deterministic-decision ordering; publishes result via outbox.
- `src/Payment/Payment.Domain/Entities/Payment.cs` — `Process(forceFail)` deterministic decision, randomness removed.
- `src/Saga/OrderProcess.Saga/StateMachine/OrderStateMachine.cs` — `Compensating` state; `RejectOrder` on reservation failure; finalize-on-`StockReleased`.
- `src/Saga/OrderProcess.Saga/Program.cs` — saga endpoint callback with retry/redelivery + outbox preserved; references only Contracts.
- `src/ordering/Ordering.Domain/Entities/Order.cs`, `Enums/OrderStatus.cs`, `Events/DomainEvent.cs` — `Reject(reason)`, `OrderStatus.Rejected`, `OrderRejected` event.
- `src/ordering/Ordering.Api/Consumers/OrderStatusConsumer.cs` — handles `RejectOrder` alongside Confirm/Cancel.
- `src/ordering/Ordering.Api/Program.cs` — endpoint callback + single-line `OrderSubmitted` TODO.
- `src/Inventory/Inventory.Infrastructure/Context/InventoryDbContext.cs` — `xmin` concurrency token on `StockItem`.
- `src/Inventory/Inventory.Infrastructure/Migrations/20261009221122_AddStockItemConcurrencyToken.cs` — intentional no-op migration (model-snapshot consistency).
- `src/Inventory/Inventory.Api/Program.cs` — endpoint callback: redelivery + `DbUpdateConcurrencyException`/`Immediate(5)` retry + outbox.
- `src/Contracts/Commands/Commands.cs`, `Events/Events.cs` — `RejectOrder` command; existing events carry `CorrelationId`.
- `Directory.Packages.props` — central pins, MassTransit 8.5.11.
- `docs/KNOWN_LIMITATIONS.md` — deferred multi-line `OrderSubmitted` note.

Full diff: `git diff HEAD` from `d:\Projects\Saga_DDD\OrderProcess` (Phase 3 scaffolding files are untracked; see `git status`).

</details>
