# Known Limitations

## Single-line `OrderSubmitted` (multi-item fan-out deferred)

The `POST /orders/{id}/submit` endpoint in `Ordering.Api` publishes an `OrderSubmitted`
integration event that carries only the **first** order line's `ProductId` and `Quantity`
(`order.Items.FirstOrDefault()`).

As a result, an order with several line items reserves and releases stock for only the first
product. The saga's `ReserveStock` / `ReleaseStock` commands operate on that single product id.

This is an accepted, documented limitation for the current learning-oriented single-line demo.
Multi-item support — propagating every order line and adding saga fan-out/fan-in (reserve each
line, aggregate the reservation outcomes, release every reserved line on compensation) plus the
corresponding change to the `OrderSubmitted` event shape — is **deferred to a later phase** and
is intentionally NOT implemented in Phase 3.

The construction site carries a matching `TODO` comment:
`src/ordering/Ordering.Api/Program.cs` (the `/orders/{id}/submit` handler).

See also the project [README](../README.md) for the architecture overview, run instructions,
and the pattern map.
