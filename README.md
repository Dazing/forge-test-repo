# Inventory allocation API

A .NET 10 ASP.NET Core minimal API for location-aware inventory and reservations.
Capabilities are organized as vertical slices under `src/Inventory.Api/Features/`;
the executable behavioral test harness is in `tests/Inventory.Tests/`.

## Repository layout

| Path | Purpose |
| ---- | ------- |
| `Inventory.slnx` | Solution containing the API and test harness |
| `src/Inventory.Api/` | API composition, in-memory state, and feature slices |
| `src/Inventory.Api/Features/Inventory/` | Inventory lookup, receipt, reporting, and transfer endpoints |
| `src/Inventory.Api/Features/Reservations/` | Reservation creation, lookup, confirmation, and cancellation endpoints |
| `tests/Inventory.Tests/` | Executable behavioral checks |

## API

| Method | Route | Success | Purpose |
| ------ | ----- | ------- | ------- |
| `GET` | `/health` | `200` | Return `{"status":"ok"}` |
| `GET` | `/inventory/{sku}` | `200` | Return allocatable inventory and per-location quantities |
| `GET` | `/inventory/low-stock` | `200` | Return locations at or below their safety-stock threshold |
| `POST` | `/inventory/{sku}/locations/{location}/receipts` | `200` | Add stock to a location |
| `POST` | `/inventory/{sku}/transfers` | `200` | Transfer stock between existing locations |
| `POST` | `/reservations` | `201` | Allocate stock and create a pending reservation |
| `GET` | `/reservations/{id}` | `200` | Return a reservation |
| `POST` | `/reservations/{id}/confirm` | `204` | Confirm a pending reservation |
| `POST` | `/reservations/{id}/cancel` | `204` | Cancel a pending reservation and restore its stock |

The application uses in-memory state seeded with:

| SKU | Location | Available | Safety stock | Allocatable |
| --- | -------- | --------- | ------------ | ----------- |
| `WIDGET` | `NORTH` | 10 | 2 | 8 |
| `WIDGET` | `SOUTH` | 8 | 1 | 7 |

State resets whenever the process restarts. SKU and location identity is trimmed and
case-insensitive; public representations are uppercase.

### Inventory

`GET /inventory/{sku}` returns total allocatable stock and locations ordered by name:

```json
{
  "sku": "WIDGET",
  "available": 15,
  "locations": [
    { "location": "NORTH", "available": 10, "safetyStock": 2, "allocatable": 8 },
    { "location": "SOUTH", "available": 8, "safetyStock": 1, "allocatable": 7 }
  ]
}
```

Unknown or blank SKUs return `404`.

`POST /inventory/{sku}/locations/{location}/receipts` accepts:

```json
{ "quantity": 5 }
```

A receipt can create a new SKU or location; new locations start with safety stock `0`.
Blank identifiers or non-positive quantities return `400`. An integer overflow returns
`409`.

`GET /inventory/low-stock` returns SKU/location pairs whose available quantity is at
or below safety stock. Results are ordered by SKU and location. `shortfall` is
`max(0, safetyStock - available)`; exactly-at-threshold entries have a shortfall of
`0`. The seeded state returns `[]`.

`POST /inventory/{sku}/transfers` accepts:

```json
{
  "sourceLocation": "NORTH",
  "destinationLocation": "SOUTH",
  "quantity": 3
}
```

Only stock above the source safety-stock floor can be transferred. Both locations
must already exist and must be distinct. Invalid input returns `400`, an unknown SKU
or location returns `404`, and insufficient source stock or destination overflow
returns `409`. A successful response includes the normalized identifiers, transferred
quantity, and updated available quantities.

### Reservations

`POST /reservations` accepts:

```json
{ "sku": "WIDGET", "quantity": 15 }
```

Allocation consumes only stock above each location's safety-stock floor, in location
name order. Success returns `201 Created`, a `Location` header for the new reservation,
and a pending reservation with its location allocations. Invalid input returns `400`,
an unknown SKU returns `404`, and insufficient allocatable stock returns `409`.

`GET /reservations/{id}` returns the reservation or `404`. Confirmation changes only a
pending reservation to `Confirmed`. Cancellation changes only a pending reservation to
`Cancelled` and restores all allocated stock. Confirmation and cancellation return
`204` on success, `404` for an unknown ID, and `409` when the reservation is no longer
pending; cancellation also returns `409` if restoring stock would overflow.

All stock and reservation mutations are atomic under the single `InventoryState`
lock. Failed operations do not mutate state. Every successful mutation increments the
state version exactly once.

## Run locally

From the repository root:

```bash
ASPNETCORE_URLS=http://localhost:5000 dotnet run --project src/Inventory.Api/Inventory.Api.csproj
```

Verify the running API from a second terminal:

```bash
curl -i http://localhost:5000/health
curl -i http://localhost:5000/inventory/widget
```

If `ASPNETCORE_URLS` is omitted, Kestrel uses its environment defaults
(`HTTP_PORTS`/`HTTPS_PORTS`), so the port is not reproducible.

## Build and test

From the repository root:

```bash
dotnet build Inventory.slnx
dotnet run --project tests/Inventory.Tests/Inventory.Tests.csproj
```
