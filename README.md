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
