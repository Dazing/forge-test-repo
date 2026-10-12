using Inventory.Api.Data;

namespace Inventory.Api.Features.Inventory;

internal static class GetLowStockReport
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/inventory/low-stock", (InventoryState state) => Results.Ok(Execute(state)));
    }

    /// <summary>
    /// Reports every SKU/location pair whose available quantity is at or below its
    /// safety stock, ordered by SKU then location. Read-only: the snapshot is taken
    /// under the state lock and no stock, reservation, or version is mutated.
    /// Returns an empty collection when no location qualifies.
    /// </summary>
    internal static LowStockEntryResponse[] Execute(InventoryState state)
    {
        lock (state.SyncRoot)
        {
            return state.Stock
                .Values.SelectMany(locations => locations.Values)
                .Where(stock => stock.Available <= stock.SafetyStock)
                .OrderBy(stock => stock.Sku, StringComparer.Ordinal)
                .ThenBy(stock => stock.Location, StringComparer.Ordinal)
                .Select(stock => new LowStockEntryResponse(
                    stock.Sku.ToUpperInvariant(),
                    stock.Location.ToUpperInvariant(),
                    stock.Available,
                    stock.SafetyStock,
                    Math.Max(0, stock.SafetyStock - stock.Available)
                ))
                .ToArray();
        }
    }
}

internal sealed record LowStockEntryResponse(
    string Sku,
    string Location,
    int Available,
    int SafetyStock,
    int Shortfall
);
