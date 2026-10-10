# Step A — xmin optimistic-concurrency evidence close-out (post-Phase-3-approval gate)

Closes the single non-blocking finding from `phase3-review.json` ("Unscripted concurrency
race") by turning the previously *inferred* xmin guarantee into a *demonstrated* one, to the
same "prove the naive case breaks and the fix catches it" bar used for the
payment-failure/compensation path. No production code was changed and the concurrency
mechanism was NOT weakened; this is testing only.

## What was added

- `tests/Inventory.ConcurrencyTests/` — a new xUnit integration-test project added to
  `OrderProcess.sln`. It references the real `Inventory.Infrastructure` (the real
  `InventoryDbContext` with the real `xmin` token mapping) and `Inventory.Domain` (the real
  `StockItem.Reserve`). It runs against the live Postgres from docker compose
  (`Host=localhost;Port=5433;Database=inventorydb`).
- `Directory.Packages.props` — central pins added for the test stack
  (`Microsoft.NET.Test.Sdk` 17.11.1, `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2) plus an
  explicit `Microsoft.EntityFrameworkCore.Relational` 9.0.8 pin to resolve a transitive version
  conflict pulled by the test SDK (removes the MSB3277 warning; full solution now builds 0/0).

No `src/**` files were modified. MassTransit stays centrally pinned at 8.5.11. The Inventory
endpoint's `UseMessageRetry(Handle<DbUpdateConcurrencyException>, Immediate(5))` wiring and the
`xmin` token mapping are untouched.

## How the race is forced DETERMINISTICALLY (not timing luck)

Two separate `InventoryDbContext` instances each read the same `StockItem` row, so both capture
the identical `xmin` snapshot. The test **asserts the two captured `xmin` values are equal**
before any commit, guaranteeing the collision every run. The winner commits (Postgres bumps
`xmin`); the loser then attempts to commit against the stale `xmin`, which must fail with
`DbUpdateConcurrencyException` on every run. There is no `Task.Delay`, no thread-timing, no
sleep — the collision is structural.

## The retry path mirrors production

`RetryReserveAsync` reproduces the Inventory endpoint's `Immediate(5)` retry exactly: on
`DbUpdateConcurrencyException`, discard the stale context, re-read the fresh row in a new
context, re-apply `StockItem.Reserve`, re-save — up to 5 immediate attempts. Each attempt
resolves against the latest post-winner state, never stale state.

## Evidence (demonstrated, asserted in code)

Run:

```
dotnet test tests\Inventory.ConcurrencyTests\Inventory.ConcurrencyTests.csproj --logger "console;verbosity=detailed"
```

Result: **3 passed / 0 failed** (~5.8s).

1. **Loser hits exception, retry resolves when stock sufficient**
   (`Concurrent_reserve_loser_hits_concurrency_exception_then_retry_resolves_when_stock_sufficient`)
   — stock 100, both writers reserve 10. Same `xmin` asserted. Winner commits; loser throws
   `DbUpdateConcurrencyException` ("expected to affect 1 row(s), but actually affected 0
   row(s)"). Retry re-reads and succeeds. Final asserted: available 80, reserved 20, total
   conserved 100 — both reservations applied, no lost update.

2. **Loser retry resolves to business rejection when stock insufficient**
   (`Concurrent_reserve_loser_retry_resolves_to_business_rejection_when_stock_insufficient`)
   — stock 10, both writers reserve 7 (only one can win). Same deterministic collision; loser
   throws `DbUpdateConcurrencyException`. Retry re-reads against post-winner state (available 3
   < 7) and resolves to the business `ReserveResult.Fail("Insufficient available quantity.")`
   — not an exception, not a lost update. Final asserted: available 3, reserved 7, total
   conserved 10 — exactly one reservation, the second correctly rejected.

3. **Naive last-writer-wins is prevented by the token** (guard — proves non-no-op)
   (`Naive_last_writer_wins_is_prevented_by_concurrency_token`) — a blind second write over a
   stale read would overwrite the winner (lost update). The test asserts EF raises
   `DbUpdateConcurrencyException` and the winner's reservation survives (available 45, reserved
   5). The naive case demonstrably breaks; the token catches it.

## Scope note (level at which the collision is driven)

The collision is driven at the `DbContext`/repository level — the exact layer where `xmin`
concurrency is enforced — because the full MassTransit receive-endpoint retry wrapper cannot be
exercised end-to-end non-interactively (it needs the four running hosts + a broker-driven
double delivery). This is the explicitly permitted fallback: the test reproduces the production
handle-and-re-read behavior in code and asserts exception + retry-resolves + no-lost-update.
The MassTransit `Immediate(5)` filter that handles `DbUpdateConcurrencyException` on the
Inventory endpoint is unchanged and still active.

## Verification artifact updated

`phase3-verification.md` → the "Optimistic concurrency on StockItem (xmin)" section is upgraded
from inferred to **DEMONSTRATED**, with the run command, the three asserted cases, and the
captured `DbUpdateConcurrencyException` + retry-resolve output.
