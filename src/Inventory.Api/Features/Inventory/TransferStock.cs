using Inventory.Api.Data;

namespace Inventory.Api.Features.Inventory;

internal static class TransferStock
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/inventory/{sku}/transfers",
            (string sku, TransferStockRequest request, InventoryState state) =>
            {
                var result = Execute(
                    sku,
                    request.SourceLocation,
                    request.DestinationLocation,
                    request.Quantity,
                    state
                );
                return result.Outcome switch
                {
                    TransferStockOutcome.Transferred => Results.Ok(result.Item),
                    TransferStockOutcome.Invalid => Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            [
                                result.InvalidField
                                ?? throw new InvalidOperationException(
                                    "Invalid transfer outcome without a validated field."
                                )
                            ] = [
                                "Source and destination must be distinct non-blank locations and quantity must be a positive integer.",
                            ],
                        }
                    ),
                    TransferStockOutcome.UnknownSku => Results.NotFound(),
                    TransferStockOutcome.UnknownSourceLocation => Results.NotFound(),
                    TransferStockOutcome.UnknownDestinationLocation => Results.NotFound(),
                    TransferStockOutcome.InsufficientStock => Results.Conflict(),
                    TransferStockOutcome.DestinationOverflow => Results.Conflict(),
                    _ => throw new InvalidOperationException("Unknown transfer outcome."),
                };
            }
        );
    }

    internal static TransferStockResult Execute(
        string sku,
        string sourceLocation,
        string destinationLocation,
        int quantity,
        InventoryState state
    )
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return RejectedTransfer("sku");
        }
        if (string.IsNullOrWhiteSpace(sourceLocation))
        {
            return RejectedTransfer("sourceLocation");
        }
        if (string.IsNullOrWhiteSpace(destinationLocation))
        {
            return RejectedTransfer("destinationLocation");
        }
        if (quantity <= 0)
        {
            return RejectedTransfer("quantity");
        }

        var normalizedSku = sku.Trim().ToUpperInvariant();
        var normalizedSource = sourceLocation.Trim().ToUpperInvariant();
        var normalizedDestination = destinationLocation.Trim().ToUpperInvariant();
        if (normalizedSource == normalizedDestination)
        {
            return RejectedTransfer("destinationLocation");
        }

        lock (state.SyncRoot)
        {
            if (!state.Stock.TryGetValue(normalizedSku, out var locations))
            {
                return new TransferStockResult(TransferStockOutcome.UnknownSku, null);
            }
            if (!locations.TryGetValue(normalizedSource, out var source))
            {
                return new TransferStockResult(TransferStockOutcome.UnknownSourceLocation, null);
            }
            if (!locations.TryGetValue(normalizedDestination, out var destination))
            {
                return new TransferStockResult(
                    TransferStockOutcome.UnknownDestinationLocation,
                    null
                );
            }

            var transferable = Math.Max(0, source.Available - source.SafetyStock);
            if (quantity > transferable)
            {
                return new TransferStockResult(TransferStockOutcome.InsufficientStock, null);
            }
            if (destination.Available > int.MaxValue - quantity)
            {
                return new TransferStockResult(TransferStockOutcome.DestinationOverflow, null);
            }

            var sourceAvailable = source.Available - quantity;
            var destinationAvailable = destination.Available + quantity;
            locations[normalizedSource] = source with { Available = sourceAvailable };
            locations[normalizedDestination] = destination with
            {
                Available = destinationAvailable
            };
            state.Version++;
            return new TransferStockResult(
                TransferStockOutcome.Transferred,
                new TransferStockResponse(
                    normalizedSku,
                    normalizedSource,
                    normalizedDestination,
                    quantity,
                    sourceAvailable,
                    destinationAvailable
                )
            );
        }
    }

    static TransferStockResult RejectedTransfer(string field) =>
        new(TransferStockOutcome.Invalid, null, field);
}

internal sealed record TransferStockRequest(
    string SourceLocation,
    string DestinationLocation,
    int Quantity
);

internal sealed record TransferStockResult(
    TransferStockOutcome Outcome,
    TransferStockResponse? Item,
    string? InvalidField = null
);

internal sealed record TransferStockResponse(
    string Sku,
    string SourceLocation,
    string DestinationLocation,
    int Quantity,
    int SourceAvailable,
    int DestinationAvailable
);

internal enum TransferStockOutcome
{
    Transferred,
    Invalid,
    UnknownSku,
    UnknownSourceLocation,
    UnknownDestinationLocation,
    InsufficientStock,
    DestinationOverflow,
}
