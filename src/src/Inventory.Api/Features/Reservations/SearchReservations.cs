using Inventory.Api.Data;

namespace Inventory.Api.Features.Reservations;

internal static class SearchReservations
{
    private static readonly Dictionary<string, ReservationStatus> SupportedStatuses =
        Enum.GetValues<ReservationStatus>().ToDictionary(
            status => status.ToString(),
            StringComparer.OrdinalIgnoreCase
        );

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/reservations",
            (string? sku, string? status, string? offset, string? limit, InventoryState state) =>
            {
                var result = Execute(sku, status, offset, limit, state);
                return result.Outcome switch
                {
                    SearchReservationsOutcome.Success => Results.Ok(result.Response),
                    SearchReservationsOutcome.InvalidSku => Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            ["sku"] = ["SKU filter must not be blank."],
                        }
                    ),
                    SearchReservationsOutcome.InvalidStatus => Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            ["status"] = ["status must be Pending, Confirmed, or Cancelled."],
                        }
                    ),
                    SearchReservationsOutcome.InvalidOffset => Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            ["offset"] = ["offset must be a non-negative integer."],
                        }
                    ),
                    SearchReservationsOutcome.InvalidLimit => Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            ["limit"] = ["limit must be an integer from 1 to 100."],
                        }
                    ),
                    _ => throw new InvalidOperationException("Unknown reservation search outcome."),
                };
            }
        );
    }

    internal static SearchReservationsResult Execute(
        string? sku,
        string? status,
        string? offset,
        string? limit,
        InventoryState state
    )
    {
        var (outcome, query) = ParseQuery(sku, status, offset, limit);
        if (query is null)
        {
            return new SearchReservationsResult(outcome, null);
        }

        lock (state.SyncRoot)
        {
            var matching = state.Reservations.Values
                .Where(reservation =>
                    (query.Sku is null
                        || string.Equals(
                            reservation.Sku,
                            query.Sku,
                            StringComparison.OrdinalIgnoreCase
                        ))
                    && (query.Status is null || reservation.Status == query.Status.Value))
                .OrderBy(reservation => reservation.Id)
                .ToArray();
            var items = matching
                .Skip(query.Offset)
                .Take(query.Limit)
                .Select(reservation => new SearchReservationsItemResponse(
                    reservation.Id,
                    reservation.Sku,
                    reservation.Quantity,
                    reservation.Status.ToString(),
                    reservation
                        .Allocations.Select(allocation => new SearchReservationsAllocationResponse(
                            allocation.Location,
                            allocation.Quantity
                        ))
                        .ToArray()
                ))
                .ToArray();
            return new SearchReservationsResult(
                SearchReservationsOutcome.Success,
                new SearchReservationsResponse(items, matching.Length)
            );
        }
    }

    private static (SearchReservationsOutcome Outcome, SearchReservationsQuery? Query) ParseQuery(
        string? sku,
        string? status,
        string? offset,
        string? limit
    )
    {
        string? normalizedSku = null;
        if (sku is not null)
        {
            if (string.IsNullOrWhiteSpace(sku))
            {
                return (SearchReservationsOutcome.InvalidSku, null);
            }

            normalizedSku = sku.Trim().ToUpperInvariant();
        }

        ReservationStatus? parsedStatus = null;
        if (status is not null)
        {
            if (!SupportedStatuses.TryGetValue(status.Trim(), out var matched))
            {
                return (SearchReservationsOutcome.InvalidStatus, null);
            }

            parsedStatus = matched;
        }

        var parsedOffset = 0;
        if (offset is not null)
        {
            if (!int.TryParse(offset, out parsedOffset) || parsedOffset < 0)
            {
                return (SearchReservationsOutcome.InvalidOffset, null);
            }
        }

        var parsedLimit = 20;
        if (limit is not null)
        {
            if (!int.TryParse(limit, out parsedLimit) || parsedLimit is < 1 or > 100)
            {
                return (SearchReservationsOutcome.InvalidLimit, null);
            }
        }

        return (
            SearchReservationsOutcome.Success,
            new SearchReservationsQuery(normalizedSku, parsedStatus, parsedOffset, parsedLimit)
        );
    }
}

internal sealed record SearchReservationsQuery(
    string? Sku,
    ReservationStatus? Status,
    int Offset,
    int Limit
);

internal sealed record SearchReservationsResult(
    SearchReservationsOutcome Outcome,
    SearchReservationsResponse? Response
);

internal enum SearchReservationsOutcome
{
    Success,
    InvalidSku,
    InvalidStatus,
    InvalidOffset,
    InvalidLimit,
}

internal sealed record SearchReservationsResponse(
    SearchReservationsItemResponse[] Items,
    int Total
);

internal sealed record SearchReservationsItemResponse(
    Guid Id,
    string Sku,
    int Quantity,
    string Status,
    SearchReservationsAllocationResponse[] Allocations
);

internal sealed record SearchReservationsAllocationResponse(string Location, int Quantity);
