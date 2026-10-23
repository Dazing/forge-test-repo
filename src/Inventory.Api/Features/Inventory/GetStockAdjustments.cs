using Inventory.Api.Data;

namespace Inventory.Api.Features.Inventory;

internal static class GetStockAdjustments
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/inventory/{sku}/adjustments",
            (string sku, InventoryState state) =>
            {
                var history = Execute(sku, state);
                return history is null ? Results.NotFound() : Results.Ok(history);
            }
        );
    }

    /// <summary>
    /// Reads one SKU's stock-adjustment audit trail so a recorded stock move can be
    /// explained. The SKU is trimmed and upper-cased before the case-insensitive lookup, so
    /// a blank or unknown SKU returns null (404) while a known SKU with no adjustments
    /// returns an empty array. Read-only: the projection is taken in one lock acquisition
    /// and no stock, audit record, or version is mutated.
    /// </summary>
    internal static StockAdjustmentResponse[]? Execute(string sku, InventoryState state)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return null;
        }

        var normalizedSku = sku.Trim().ToUpperInvariant();
        lock (state.SyncRoot)
        {
            if (!state.Stock.ContainsKey(normalizedSku))
            {
                return null;
            }

            // `Adjustments` enumerates in insertion order and LINQ's OrderBy is a stable
            // sort, so adjustments recorded within the same `DateTimeOffset.UtcNow` tick
            // keep the order in which the writes committed them.
            return state.Adjustments
                .Values.Where(
                    adjustment =>
                        string.Equals(
                            adjustment.Sku,
                            normalizedSku,
                            StringComparison.OrdinalIgnoreCase
                        )
                )
                .OrderBy(adjustment => adjustment.OccurredUtc)
                .Select(
                    adjustment => new StockAdjustmentResponse(
                        adjustment.Id,
                        adjustment.Sku,
                        adjustment.Location,
                        adjustment.Delta,
                        adjustment.ResultingAvailable,
                        adjustment.Reason,
                        adjustment.OccurredUtc
                    )
                )
                .ToArray();
        }
    }
}

internal sealed record StockAdjustmentResponse(
    Guid Id,
    string Sku,
    string Location,
    int Delta,
    int ResultingAvailable,
    string Reason,
    DateTimeOffset OccurredUtc
);
