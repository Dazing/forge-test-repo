using Inventory.Api.Data;
using Inventory.Api.Features.Inventory;
using Inventory.Api.Features.Reservations;

GetInventory_NormalizesAndOrdersLocationAwareStock();
ReceiveLocationStock_ValidatesOverflowAndVersioning();
CreateReservation_AllocatesGreedilyWithoutCrossingSafetyStock();
CreateReservation_FailuresAreMutationFree();
GetReservation_ReturnsCompleteStableRepresentation();
ConfirmReservation_ConsumesStockAndRejectsFurtherTransitions();
CancelReservation_RestoresEveryAllocationExactlyOnce();
ReservationLifecycle_ConservesPhysicalStock();
SearchReservations_DefaultListingReturnsFirstPageInIdOrder();
SearchReservations_FiltersMatchSkuAndStatusTogether();
SearchReservations_PaginationSelectsExpectedPages();
SearchReservations_OrderingIsDeterministicAcrossPages();
SearchReservations_RejectsInvalidFiltersAndPagination();
SearchReservations_ReturnsMappedReservationsFromTheCurrentSnapshot();
Console.WriteLine("PASS: 14 location-aware inventory slice behavior checks");

static void GetInventory_NormalizesAndOrdersLocationAwareStock()
{
    var state = new InventoryState();

    var item = GetInventory.Execute(" widget ", state);

    Assert(
        item is { Sku: "WIDGET", Available: 15 },
        "lookup normalizes SKU and aggregates allocatable stock"
    );
    Assert(item!.Locations.Length == 2, "both seeded locations are returned");
    Assert(
        item.Locations[0] is { Location: "NORTH", Available: 10, SafetyStock: 2, Allocatable: 8 },
        "NORTH is first with safety stock excluded"
    );
    Assert(
        item.Locations[1] is { Location: "SOUTH", Available: 8, SafetyStock: 1, Allocatable: 7 },
        "SOUTH is second with safety stock excluded"
    );
    Assert(GetInventory.Execute("missing", state) is null, "unknown SKU is absent");
}

static void ReceiveLocationStock_ValidatesOverflowAndVersioning()
{
    var state = new InventoryState();

    var received = ReceiveLocationStock.Execute(" part ", " west ", 3, state);
    var rejected = ReceiveLocationStock.Execute("part", "west", 0, state);
    var blank = ReceiveLocationStock.Execute("part", " ", 1, state);
    var blankSku = ReceiveLocationStock.Execute(" ", "west", 1, state);
    var filled = ReceiveLocationStock.Execute("overflow", "only", int.MaxValue, state);
    var overflow = ReceiveLocationStock.Execute("overflow", "only", 1, state);

    Assert(received.Outcome == ReceiveLocationStockOutcome.Received, "positive receipt succeeds");
    Assert(
        received.Item
            is { Sku: "PART", Location: "WEST", Available: 3, SafetyStock: 0, Allocatable: 3 },
        "receipt creates normalized location stock"
    );
    Assert(
        rejected.Outcome == ReceiveLocationStockOutcome.Invalid,
        "non-positive receipt is rejected"
    );
    Assert(blank.Outcome == ReceiveLocationStockOutcome.Invalid, "blank location is rejected");
    Assert(blankSku.Outcome == ReceiveLocationStockOutcome.Invalid, "blank SKU is rejected");
    Assert(
        filled.Outcome == ReceiveLocationStockOutcome.Received,
        "maximum initial receipt succeeds"
    );
    Assert(
        overflow.Outcome == ReceiveLocationStockOutcome.Overflow,
        "overflowing receipt is rejected"
    );
    Assert(
        GetInventory.Execute("part", state)?.Available == 3,
        "invalid receipts do not mutate stock"
    );
    Assert(
        GetInventory.Execute("overflow", state)?.Available == int.MaxValue,
        "overflow does not mutate stock"
    );
    Assert(state.Version == 2, "only the two successful receipts increment version");
}

static void CreateReservation_AllocatesGreedilyWithoutCrossingSafetyStock()
{
    var state = new InventoryState();

    var result = CreateReservation.Execute("widget", 10, state);

    Assert(result.Outcome == CreateReservationOutcome.Created, "reservation is created");
    Assert(
        result.Reservation is { Sku: "WIDGET", Quantity: 10, Status: "Pending" },
        "pending reservation is returned"
    );
    Assert(result.Reservation!.Allocations.Length == 2, "reservation spans two locations");
    Assert(
        result.Reservation.Allocations[0] is { Location: "NORTH", Quantity: 8 },
        "NORTH allocatable stock is consumed first"
    );
    Assert(
        result.Reservation.Allocations[1] is { Location: "SOUTH", Quantity: 2 },
        "remaining quantity is allocated from SOUTH"
    );
    var inventory = GetInventory.Execute("widget", state)!;
    Assert(
        inventory.Available == 5,
        "aggregate allocatable stock decreases by reservation quantity"
    );
    Assert(
        inventory.Locations[0].Available == 2,
        "NORTH safety stock remains physical and unallocated"
    );
    Assert(
        inventory.Locations[1].Available == 6,
        "SOUTH physical stock decreases only by its allocation"
    );
    Assert(state.Version == 1, "creation increments version once");
}

static void CreateReservation_FailuresAreMutationFree()
{
    var state = new InventoryState();

    var invalid = CreateReservation.Execute("widget", 0, state);
    var blankSku = CreateReservation.Execute(" ", 1, state);
    var missing = CreateReservation.Execute("missing", 1, state);
    var insufficient = CreateReservation.Execute("widget", 16, state);

    Assert(
        invalid.Outcome == CreateReservationOutcome.Invalid,
        "non-positive quantity is rejected"
    );
    Assert(
        blankSku.Outcome == CreateReservationOutcome.Invalid,
        "blank reservation SKU is rejected"
    );
    Assert(missing.Outcome == CreateReservationOutcome.UnknownSku, "unknown SKU is rejected");
    Assert(
        insufficient.Outcome == CreateReservationOutcome.InsufficientStock,
        "insufficient stock is rejected"
    );
    Assert(
        GetInventory.Execute("widget", state)?.Available == 15,
        "failed creation leaves stock unchanged"
    );
    Assert(state.Reservations.Count == 0, "failed creation stores no reservation");
    Assert(state.Version == 0, "failed creation does not increment version");
}

static void GetReservation_ReturnsCompleteStableRepresentation()
{
    var state = new InventoryState();
    var created = CreateReservation.Execute("widget", 9, state).Reservation!;

    var found = GetReservation.Execute(created.Id, state);

    Assert(
        found is { Sku: "WIDGET", Quantity: 9, Status: "Pending" },
        "reservation lookup returns stable fields"
    );
    Assert(found!.Allocations.Length == 2, "reservation lookup returns every allocation");
    Assert(
        found.Allocations[0] is { Location: "NORTH", Quantity: 8 },
        "lookup preserves allocation order"
    );
    Assert(
        found.Allocations[1] is { Location: "SOUTH", Quantity: 1 },
        "lookup preserves remaining allocation"
    );
    Assert(GetReservation.Execute(Guid.NewGuid(), state) is null, "unknown reservation is absent");
}

static void ConfirmReservation_ConsumesStockAndRejectsFurtherTransitions()
{
    var state = new InventoryState();
    var created = CreateReservation.Execute("widget", 3, state).Reservation!;

    var confirmed = ConfirmReservation.Execute(created.Id, state);
    var repeated = ConfirmReservation.Execute(created.Id, state);
    var conflicting = CancelReservation.Execute(created.Id, state);
    var missing = ConfirmReservation.Execute(Guid.NewGuid(), state);

    Assert(confirmed == ConfirmReservationOutcome.Succeeded, "pending reservation confirms");
    Assert(repeated == ConfirmReservationOutcome.NotPending, "repeated confirmation is rejected");
    Assert(
        conflicting == CancelReservationOutcome.NotPending,
        "conflicting cancellation is rejected"
    );
    Assert(missing == ConfirmReservationOutcome.NotFound, "unknown confirmation is rejected");
    Assert(
        GetInventory.Execute("widget", state)?.Available == 12,
        "confirmation keeps stock consumed"
    );
    Assert(state.Version == 2, "creation and confirmation each increment version once");
}

static void CancelReservation_RestoresEveryAllocationExactlyOnce()
{
    var state = new InventoryState();
    var created = CreateReservation.Execute("widget", 10, state).Reservation!;

    var cancelled = CancelReservation.Execute(created.Id, state);
    var repeated = CancelReservation.Execute(created.Id, state);
    var missing = CancelReservation.Execute(Guid.NewGuid(), state);

    Assert(cancelled == CancelReservationOutcome.Succeeded, "pending reservation cancels");
    Assert(repeated == CancelReservationOutcome.NotPending, "repeated cancellation is rejected");
    Assert(missing == CancelReservationOutcome.NotFound, "unknown cancellation is rejected");
    Assert(
        GetInventory.Execute("widget", state)?.Available == 15,
        "cancellation restores both locations once"
    );
    Assert(
        GetReservation.Execute(created.Id, state)?.Status == "Cancelled",
        "cancelled representation is stable"
    );
    Assert(state.Version == 2, "creation and cancellation each increment version once");
}

static void ReservationLifecycle_ConservesPhysicalStock()
{
    var state = new InventoryState();
    var before = PhysicalStock(state, "WIDGET");
    var first = CreateReservation.Execute("widget", 11, state).Reservation!;
    var second = CreateReservation.Execute("widget", 4, state).Reservation!;

    Assert(
        GetInventory.Execute("widget", state)?.Available == 0,
        "all allocatable stock can be reserved safely"
    );
    Assert(
        ConfirmReservation.Execute(first.Id, state) == ConfirmReservationOutcome.Succeeded,
        "first reservation confirms"
    );
    Assert(
        CancelReservation.Execute(second.Id, state) == CancelReservationOutcome.Succeeded,
        "second reservation cancels"
    );
    Assert(
        GetInventory.Execute("widget", state)?.Available == 4,
        "only cancelled allocation is restored"
    );
    Assert(
        PhysicalStock(state, "WIDGET") + first.Quantity == before,
        "confirmed consumption plus physical stock is conserved"
    );
}

static void SearchReservations_DefaultListingReturnsFirstPageInIdOrder()
{
    var state = new InventoryState();
    SeedReservations(state, 25);

    var result = SearchReservations.Execute(null, null, null, null, state);

    Assert(result.Outcome == SearchReservationsOutcome.Success, "unfiltered search succeeds");
    var page = result.Response!;
    Assert(page.Total == 25, "total counts every reservation before pagination");
    Assert(page.Items.Length == 20, "default limit returns the first 20 reservations");
    Assert(page.Items[0].Id == ReservationId(1), "first page starts at the lowest reservation ID");
    Assert(page.Items[19].Id == ReservationId(20), "first page ends at the 20th reservation ID");
    Assert(state.Version == 0, "search does not increment the state version");
    Assert(state.Reservations.Count == 25, "search does not add or remove reservations");
    Assert(
        GetInventory.Execute("widget", state)?.Available == 15,
        "search leaves stock unchanged"
    );
}

static void SearchReservations_FiltersMatchSkuAndStatusTogether()
{
    var state = new InventoryState();
    SeedReservation(state, 1, "WIDGET", ReservationStatus.Pending);
    SeedReservation(state, 2, "WIDGET", ReservationStatus.Confirmed);
    SeedReservation(state, 3, "GADGET", ReservationStatus.Pending);
    SeedReservation(state, 4, "WIDGET", ReservationStatus.Cancelled);

    var bySku = SearchReservations.Execute("widget", null, null, null, state).Response!;
    Assert(bySku.Total == 3, "SKU filter counts only matching reservations before pagination");
    Assert(
        bySku.Items.Select(item => item.Id)
            .SequenceEqual([ReservationId(1), ReservationId(2), ReservationId(4)]),
        "SKU filter is case-insensitive and excludes other SKUs"
    );

    var pending = SearchReservations.Execute(null, "Pending", null, null, state).Response!;
    Assert(
        pending.Items.Select(item => item.Id).SequenceEqual([ReservationId(1), ReservationId(3)]),
        "Pending filter returns every pending reservation"
    );
    var confirmed = SearchReservations.Execute(null, "Confirmed", null, null, state).Response!;
    Assert(
        confirmed.Items.Select(item => item.Id).SequenceEqual([ReservationId(2)]),
        "Confirmed filter returns only the confirmed reservation"
    );
    var cancelled = SearchReservations.Execute(null, "Cancelled", null, null, state).Response!;
    Assert(
        cancelled.Items.Select(item => item.Id).SequenceEqual([ReservationId(4)]),
        "Cancelled filter returns only the cancelled reservation"
    );

    var combined = SearchReservations.Execute("WIDGET", "Confirmed", null, null, state).Response!;
    Assert(combined.Total == 1, "combined filters count only reservations matching both");
    Assert(
        combined.Items.Single().Id == ReservationId(2),
        "combined SKU and status filters return the intersection"
    );

    Assert(state.Version == 0, "filtered search does not increment the state version");
    Assert(state.Reservations.Count == 4, "filtered search does not add or remove reservations");
    Assert(
        GetInventory.Execute("widget", state)?.Available == 15,
        "filtered search leaves stock unchanged"
    );
}

static void SearchReservations_PaginationSelectsExpectedPages()
{
    var state = new InventoryState();
    SeedReservations(state, 5);

    var firstPage = SearchReservations.Execute(null, null, "0", "1", state).Response!;
    Assert(
        firstPage.Items.Length == 1 && firstPage.Items[0].Id == ReservationId(1),
        "explicit offset 0 with limit 1 selects the first reservation"
    );
    Assert(firstPage.Total == 5, "explicit page keeps the total matching count");

    var middlePage = SearchReservations.Execute(null, null, "2", "2", state).Response!;
    Assert(
        middlePage.Items.Select(item => item.Id)
            .SequenceEqual([ReservationId(3), ReservationId(4)]),
        "offset 2 with limit 2 selects the middle page"
    );

    var partialFinalPage = SearchReservations.Execute(null, null, "4", "2", state).Response!;
    Assert(
        partialFinalPage.Items.Select(item => item.Id).SequenceEqual([ReservationId(5)]),
        "offset 4 returns the partial final page"
    );
    Assert(partialFinalPage.Total == 5, "partial final page retains the total matching count");

    var atMatchingCount = SearchReservations.Execute(null, null, "5", "20", state).Response!;
    Assert(
        atMatchingCount.Items.Length == 0 && atMatchingCount.Total == 5,
        "offset at the matching count returns an empty page with the total"
    );

    var beyondMatchingCount = SearchReservations.Execute(null, null, "6", "20", state).Response!;
    Assert(
        beyondMatchingCount.Items.Length == 0 && beyondMatchingCount.Total == 5,
        "offset beyond the matching count returns an empty page with the total"
    );

    var noMatches = SearchReservations.Execute("GADGET", null, null, null, state).Response!;
    Assert(
        noMatches.Items.Length == 0 && noMatches.Total == 0,
        "unmatched SKU returns an empty page with total 0"
    );

    var largeState = new InventoryState();
    SeedReservations(largeState, 105);
    var largestPage = SearchReservations.Execute(null, null, "0", "100", largeState).Response!;
    Assert(
        largestPage.Items.Length == 100 && largestPage.Total == 105,
        "limit 100 returns exactly 100 reservations from a larger match set"
    );
    var tailPage = SearchReservations.Execute(null, null, "100", "100", largeState).Response!;
    Assert(
        tailPage.Items.Length == 5
            && tailPage.Items[0].Id == ReservationId(101)
            && tailPage.Total == 105,
        "offset 100 returns the remaining partial page"
    );

    Assert(state.Version == 0, "pagination does not increment the state version");
    Assert(
        GetInventory.Execute("widget", state)?.Available == 15,
        "pagination leaves stock unchanged"
    );
}

static void SearchReservations_OrderingIsDeterministicAcrossPages()
{
    var state = new InventoryState();
    foreach (var ordinal in new[] { 7, 3, 11, 1, 9, 5, 2, 10, 4, 6, 8 })
    {
        SeedReservation(state, ordinal, "WIDGET", ReservationStatus.Pending);
    }

    var firstPage = SearchReservations.Execute(null, null, "0", "3", state).Response!;
    Assert(
        firstPage.Items.Select(item => item.Id)
            .SequenceEqual([ReservationId(1), ReservationId(2), ReservationId(3)]),
        "first page is ordered by reservation ID, not insertion order"
    );

    var seen = new List<Guid>();
    for (var offset = 0; offset <= 12; offset += 3)
    {
        var page = SearchReservations.Execute(null, null, offset.ToString(), "3", state).Response!;
        Assert(page.Total == 11, "total stays the matching count on every page");
        foreach (var item in page.Items)
        {
            seen.Add(item.Id);
        }
    }

    Assert(
        seen.SequenceEqual(Enumerable.Range(1, 11).Select(ordinal => ReservationId(ordinal))),
        "successive pages cover every reservation in ID order without duplicates or omissions"
    );
    Assert(state.Version == 0, "paging does not increment the state version");
}

static void SearchReservations_RejectsInvalidFiltersAndPagination()
{
    var state = new InventoryState();
    SeedReservations(state, 5);

    Assert(
        SearchReservations.Execute(null, "Shipping", null, null, state).Outcome
            == SearchReservationsOutcome.InvalidStatus,
        "unsupported status is rejected"
    );
    Assert(
        SearchReservations.Execute(null, "", null, null, state).Outcome
            == SearchReservationsOutcome.InvalidStatus,
        "blank status filter is rejected"
    );
    Assert(
        SearchReservations.Execute(null, null, "abc", null, state).Outcome
            == SearchReservationsOutcome.InvalidOffset,
        "malformed offset is rejected"
    );
    Assert(
        SearchReservations.Execute(null, null, "1.5", null, state).Outcome
            == SearchReservationsOutcome.InvalidOffset,
        "non-integer offset is rejected"
    );
    Assert(
        SearchReservations.Execute(null, null, "-1", null, state).Outcome
            == SearchReservationsOutcome.InvalidOffset,
        "negative offset is rejected"
    );
    Assert(
        SearchReservations.Execute(null, null, null, "0", state).Outcome
            == SearchReservationsOutcome.InvalidLimit,
        "zero limit is rejected"
    );
    Assert(
        SearchReservations.Execute(null, null, null, "-5", state).Outcome
            == SearchReservationsOutcome.InvalidLimit,
        "negative limit is rejected"
    );
    Assert(
        SearchReservations.Execute(null, null, null, "101", state).Outcome
            == SearchReservationsOutcome.InvalidLimit,
        "limit above 100 is rejected"
    );
    Assert(
        SearchReservations.Execute(null, null, null, "x", state).Outcome
            == SearchReservationsOutcome.InvalidLimit,
        "malformed limit is rejected"
    );
    Assert(
        SearchReservations.Execute(" ", null, null, null, state).Outcome
            == SearchReservationsOutcome.InvalidSku,
        "blank SKU filter is rejected"
    );

    Assert(
        state.Version == 0 && state.Reservations.Count == 5,
        "rejected searches leave reservations and version unchanged"
    );
    Assert(
        GetInventory.Execute("widget", state)?.Available == 15,
        "rejected searches leave stock unchanged"
    );
}

static void SearchReservations_ReturnsMappedReservationsFromTheCurrentSnapshot()
{
    var state = new InventoryState();
    var created = CreateReservation.Execute("widget", 9, state).Reservation!;
    SeedReservation(state, 1, "WIDGET", ReservationStatus.Confirmed);

    var page = SearchReservations.Execute("WIDGET", null, null, null, state).Response!;

    Assert(page.Total == 2, "total counts every reservation in the snapshot used for the page");
    Assert(
        page.Items[0].Id == ReservationId(1)
            && page.Items[0].Status == "Confirmed"
            && page.Items[0].Quantity == 1,
        "mapped item exposes public status and quantity"
    );
    var createdItem = page.Items[1];
    Assert(
        createdItem.Id == created.Id && createdItem.Status == "Pending",
        "mapped item exposes the live reservation status"
    );
    Assert(createdItem.Allocations.Length == 2, "mapped item exposes every allocation");
    Assert(
        createdItem.Allocations[0] is { Location: "NORTH", Quantity: 8 },
        "mapped allocation order is preserved"
    );
    Assert(
        createdItem.Allocations[1] is { Location: "SOUTH", Quantity: 1 },
        "mapped allocation quantity is preserved"
    );

    ConfirmReservation.Execute(created.Id, state);
    var afterConfirm = SearchReservations.Execute("WIDGET", null, null, null, state).Response!;
    Assert(
        afterConfirm.Items[1].Status == "Confirmed" && afterConfirm.Total == 2,
        "a later search reflects the committed state"
    );
    Assert(state.Version == 2, "search leaves the version written by mutations intact");
}

static Guid ReservationId(int ordinal)
{
    return Guid.Parse($"{ordinal:x8}-0000-0000-0000-000000000000");
}

static void SeedReservations(InventoryState state, int count)
{
    for (var ordinal = count; ordinal >= 1; ordinal--)
    {
        SeedReservation(state, ordinal, "WIDGET", ReservationStatus.Pending);
    }
}

static void SeedReservation(
    InventoryState state,
    int ordinal,
    string sku,
    ReservationStatus status
)
{
    var id = ReservationId(ordinal);
    state.Reservations.Add(id, new ReservationRecord(id, sku, 1, status, []));
}

static int PhysicalStock(InventoryState state, string sku)
{
    lock (state.SyncRoot)
    {
        return state.Stock[sku].Values.Sum(location => location.Available);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"FAIL: {message}");
    }
}
