namespace Inventory.Api.Features.Health;

internal static class GetHealth
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    }
}
