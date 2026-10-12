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

## Transfer stock between locations

| Method | Route | Request body |
| ------ | ----- | ------------ |
| `POST` | `/inventory/{sku}/transfers` | `{"sourceLocation":"NORTH","destinationLocation":"SOUTH","quantity":3}` |

A successful transfer answers `200` with the transferred quantity and the updated
stock of both locations:

```json
{
  "sku": "WIDGET",
  "sourceLocation": "NORTH",
  "destinationLocation": "SOUTH",
  "quantity": 3,
  "sourceAvailable": 7,
  "destinationAvailable": 11
}
```

The route is defined in `src/src/Inventory.Api/Features/Inventory/TransferStock.cs`
and registered from `Program.cs`. It uses the seeded in-memory `InventoryState` and
has no dependencies or external persistence.

### Identity

`sku`, `sourceLocation`, and `destinationLocation` are trimmed and matched with
case-insensitive identity, so `" north "` resolves the seeded `NORTH` location. The
response always uses uppercase SKU and location representations.

### Safety-stock limit and atomicity

Only stock above the source location's safety-stock floor is transferable
(`available - safetyStock`). A transfer that leaves the source exactly at its floor
succeeds; a positive transfer when the source is at or below its floor does not.
Eligibility and both stock updates are checked and applied in one critical section
under the single `InventoryState` lock, so competing transfers from the same source
are serialized: only transfers affordable at their atomic commit succeed, the source
never crosses its floor, total stock is conserved, and the state version increments
exactly once per successful transfer. Every failure is mutation-free — inventory,
both safety-stock values, and the state version stay unchanged, and missing SKUs or
locations are never created implicitly.

### Error responses

| Condition | Response |
| --------- | -------- |
| Blank source or destination, non-positive quantity, non-integer quantity, or source and destination identifying the same location case-insensitively | `400` validation problem naming the offending field |
| Unknown SKU, unknown source location, or unknown destination location | `404` |
| Requested quantity above the transferable stock, source at or below its safety floor, or a destination sum that would overflow `int.MaxValue` | `409` |

Verify it from a second terminal, also from `src/`, with the API running on the
pinned listener URL:

```bash
curl -i -X POST -H 'Content-Type: application/json' \
  -d '{"sourceLocation":" north ","destinationLocation":"SOUTH","quantity":3}' \
  http://localhost:5000/inventory/widget/transfers
```

Expected result: `HTTP/1.1 200 OK` with the response shown above. Repeating the
request with `"quantity":1` after the source reaches its floor answers `409` and
leaves the inventory unchanged.

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

If `ASPNETCORE_URLS` is omitted, Kestrel falls back to its environment defaults
(`HTTP_PORTS`/`HTTPS_PORTS`), so the port is not reproducible.

## Build and tests

From the `src/` directory:

```bash
dotnet build src/Inventory.Api/Inventory.Api.csproj
dotnet run --project tests/Inventory.Tests/Inventory.Tests.csproj
```
