# forge-test-repo

A .NET 10 ASP.NET Core minimal API for inventory allocation, organized as vertical
slices under `src/src/Inventory.Api/Features/`, with a `dotnet run` test harness in
`src/tests/Inventory.Tests`.

## Health endpoint

| Method | Route  | Response                                    |
| ------ | ------ | ------------------------------------------- |
| `GET`  | `/health` | `200` with JSON `{"status":"ok"}`        |

The route is defined in `src/src/Inventory.Api/Features/Health/GetHealth.cs` and
registered from `Program.cs`. It has no dependencies, authentication, or state
access.

## Low-stock report

| Method | Route                  | Response                                                              |
| ------ | ---------------------- | --------------------------------------------------------------------- |
| `GET`  | `/inventory/low-stock` | `200` with a JSON array of `{"sku","location","available","safetyStock","shortfall"}` |

The route is defined in `src/src/Inventory.Api/Features/Inventory/GetLowStockReport.cs` and
registered from `Program.cs`. The operation is read-only: it takes one snapshot of the stock map
under the `InventoryState` lock, mutates nothing, and returns explicitly mapped response records
rather than persistence records. The literal `low-stock` segment wins over `/inventory/{sku}`, so
the existing SKU lookup is unaffected.

Rules:

- A SKU/location pair is reported when its `available` quantity is **at or below** its
  `safetyStock`, so exactly-at-threshold pairs are included with `shortfall` `0`.
- `shortfall` is `max(0, safetyStock - available)`.
- `sku` and `location` keep their uppercase public representations.
- Entries are sorted by SKU and then location, independently of insertion order.
- When no location qualifies, the response is `200` with the empty array `[]`.

## Run the API locally

From the `src/` directory, pin the listener URL so the port is reproducible:

```bash
ASPNETCORE_URLS=http://localhost:5000 dotnet run --project src/Inventory.Api/Inventory.Api.csproj
```

Then verify the health endpoint from a second terminal, also from `src/`:

```bash
curl -i http://localhost:5000/health
```

Expected result: `HTTP/1.1 200 OK` with the body `{"status":"ok"}`.

Verify the low-stock report from the same terminal, also from `src/`:

```bash
curl -i http://localhost:5000/inventory/low-stock
```

Expected result on the seeded state: `HTTP/1.1 200 OK` with the body `[]`, because
`WIDGET/NORTH` (10 available, safety stock 2) and `WIDGET/SOUTH` (8 available, safety stock 1)
are both above their thresholds.

To observe qualifying entries, consume every allocatable unit first, which leaves both locations
exactly at their threshold:

```bash
curl -i -X POST -H 'Content-Type: application/json' -d '{"sku":"widget","quantity":15}' \
  http://localhost:5000/reservations
curl -i http://localhost:5000/inventory/low-stock
```

Expected result: `201 Created` for the reservation, then `HTTP/1.1 200 OK` with

```json
[
  { "sku": "WIDGET", "location": "NORTH", "available": 2, "safetyStock": 2, "shortfall": 0 },
  { "sku": "WIDGET", "location": "SOUTH", "available": 1, "safetyStock": 1, "shortfall": 0 }
]
```

Below-threshold entries (`shortfall` greater than 0) cannot be produced over HTTP with the
existing endpoints, because receipts only add stock and reservations never allocate below safety
stock. They are covered by the behavioral checks in `src/tests/Inventory.Tests/Program.cs`.

If `ASPNETCORE_URLS` is omitted, Kestrel falls back to its environment defaults
(`HTTP_PORTS`/`HTTPS_PORTS`), so the port is not reproducible.

## Build and tests

From the `src/` directory:

```bash
dotnet build src/Inventory.Api/Inventory.Api.csproj
dotnet run --project tests/Inventory.Tests/Inventory.Tests.csproj
```
