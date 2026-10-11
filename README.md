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

## Reservation search endpoint

| Method | Route           | Response                                        |
| ------ | --------------- | ----------------------------------------------- |
| `GET`  | `/reservations` | `200` with JSON `{"items":[...],"total":N}`     |

The route is defined in `src/src/Inventory.Api/Features/Reservations/SearchReservations.cs`
and registered from `Program.cs`. It is read-only: results and the total come from one
snapshot taken under the `InventoryState` lock, and the response exposes mapped reservation
models, never persistence records.

All query parameters are optional:

| Parameter | Default        | Accepted values                                                    |
| --------- | -------------- | ------------------------------------------------------------------ |
| `sku`     | no filter      | SKU matched case-insensitively; a blank value is rejected           |
| `status`  | no filter      | `Pending`, `Confirmed`, `Cancelled` (case-insensitive)             |
| `offset`  | `0`            | non-negative integer                                                |
| `limit`   | `20`           | integer from 1 through 100                                          |

Supplied SKU and status filters are applied together, so a reservation is returned only when
it matches both. `total` counts the reservations matching the filters *before* pagination, so
an empty page still reports the full matching count. Reservations are ordered by reservation ID
before pagination, which makes ordering deterministic regardless of insertion order and keeps
successive pages over unchanged state free of duplicates and omissions.

Invalid filters and pagination values return `400` with a validation problem naming the
offending parameter: unsupported status, blank SKU, malformed numeric `offset`/`limit`,
negative `offset`, `limit` of 0 or negative, and `limit` greater than 100.

Verify from a second terminal, also from `src/`:

```bash
curl -i 'http://localhost:5000/reservations?sku=widget&status=Pending&offset=0&limit=1'
curl -i 'http://localhost:5000/reservations?limit=0'
```

Expected results: `HTTP/1.1 200 OK` with `{"items":[...],"total":N}` for the first request,
and `HTTP/1.1 400 Bad Request` with `errors.limit` for the second.

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
