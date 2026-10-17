using Inventory.Api.Data;

namespace Inventory.Api.Features.Inventory;

internal static class AdjustStock
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/inventory/{sku}/locations/{location}/adjustments",
            (string sku, string location, AdjustStockRequest request, InventoryState state) =>
            {
                var result = Execute(sku, location, request.Delta, request.Reason, state);
                return result.Outcome switch
                {
                    AdjustStockOutcome.Adjusted => Results.Ok(result.Item),
                    AdjustStockOutcome.Invalid => Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            [
                                result.InvalidField
                                ?? throw new InvalidOperationException(
                                    "Invalid adjustment outcome without a validated field."
                                )
                            ] = [
                                "SKU, location, and reason must not be blank and delta must not be zero.",
                            ],
                        }
                    ),
                    AdjustStockOutcome.UnknownSku => Results.NotFound(),
                    AdjustStockOutcome.UnknownLocation => Results.NotFound(),
                    AdjustStockOutcome.BelowSafetyStock => Results.Conflict(),
                    AdjustStockOutcome.OutOfRange => Results.Conflict(),
                    _ => throw new InvalidOperationException("Unknown adjustment outcome."),
                };
            }
        );
    }

    /// <summary>
    /// Corrects a location's recorded stock to a counted reality. Identifiers, delta, and
    /// reason are validated before the lock (400 shape first), entities are resolved inside
    /// one lock acquisition (404 identity next, never created), and the int-range and
    /// safety-stock floor checks (409) guard the write. A successful adjustment applies the
    /// delta, appends one audit record, and increments the version once in the same critical
    /// section; every refusal is mutation-free.
    /// </summary>
    internal static AdjustStockResult Execute(
        string sku,
        string location,
        int delta,
        string reason,
        InventoryState state
    )
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return RejectedAdjustment("sku");
        }
        if (string.IsNullOrWhiteSpace(location))
        {
            return RejectedAdjustment("location");
        }
        if (delta == 0)
        {
            return RejectedAdjustment("delta");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            return RejectedAdjustment("reason");
        }

        var normalizedSku = sku.Trim().ToUpperInvariant();
        var normalizedLocation = location.Trim().ToUpperInvariant();
        lock (state.SyncRoot)
        {
            if (!state.Stock.TryGetValue(normalizedSku, out var locations))
            {
                return new AdjustStockResult(AdjustStockOutcome.UnknownSku, null);
            }
            if (!locations.TryGetValue(normalizedLocation, out var current))
            {
                return new AdjustStockResult(AdjustStockOutcome.UnknownLocation, null);
            }

            // The counted quantity must stay inside int range and, exactly like the transfer
            // source-floor rule, must not land strictly below the location's safety stock.
            var counted = (long)current.Available + delta;
            if (counted > int.MaxValue || counted < int.MinValue)
            {
                return new AdjustStockResult(AdjustStockOutcome.OutOfRange, null);
            }

            var available = (int)counted;
            if (available < current.SafetyStock)
            {
                return new AdjustStockResult(AdjustStockOutcome.BelowSafetyStock, null);
            }

            var audit = new StockAdjustmentRecord(
                Guid.NewGuid(),
                normalizedSku,
                normalizedLocation,
                delta,
                available,
                reason.Trim(),
                DateTimeOffset.UtcNow
            );
            locations[normalizedLocation] = current with { Available = available };
            state.Adjustments.Add(audit.Id, audit);
            state.Version++;
            return new AdjustStockResult(
                AdjustStockOutcome.Adjusted,
                new AdjustStockResponse(
                    normalizedSku,
                    normalizedLocation,
                    delta,
                    available,
                    current.SafetyStock
                )
            );
        }
    }

    static AdjustStockResult RejectedAdjustment(string field) =>
        new(AdjustStockOutcome.Invalid, null, field);
}

internal sealed record AdjustStockRequest(int Delta, string Reason);

internal sealed record AdjustStockResult(
    AdjustStockOutcome Outcome,
    AdjustStockResponse? Item,
    string? InvalidField = null
);

internal sealed record AdjustStockResponse(
    string Sku,
    string Location,
    int Delta,
    int Available,
    int SafetyStock
);

internal enum AdjustStockOutcome
{
    Adjusted,
    Invalid,
    UnknownSku,
    UnknownLocation,
    BelowSafetyStock,
    OutOfRange,
}
