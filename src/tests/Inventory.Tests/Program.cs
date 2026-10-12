using Inventory.Api.Data;
using Inventory.Api.Features.Inventory;
using Inventory.Api.Features.Reservations;

GetInventory_NormalizesAndOrdersLocationAwareStock();
GetLowStockReport_ReportsQualifyingLocationsInStableOrder();
GetLowStockReport_ReturnsEmptyReportWhenNoLocationsQualify();
ReceiveLocationStock_ValidatesOverflowAndVersioning();
TransferStock_MovesStockAboveSafetyStockFloor();
TransferStock_RejectsInvalidAndMissingEntitiesWithoutMutation();
TransferStock_RejectsDestinationOverflowWithoutMutation();
TransferStock_ConcurrentTransfersCommitOnlyAffordableRequests();
CreateReservation_AllocatesGreedilyWithoutCrossingSafetyStock();
CreateReservation_FailuresAreMutationFree();
GetReservation_ReturnsCompleteStableRepresentation();
ConfirmReservation_ConsumesStockAndRejectsFurtherTransitions();
CancelReservation_RestoresEveryAllocationExactlyOnce();
ReservationLifecycle_ConservesPhysicalStock();
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

static void GetLowStockReport_ReportsQualifyingLocationsInStableOrder()
{
    var state = new InventoryState();
    SeedLocation(state, "gadget", "east", 1, 5);
    SeedLocation(state, "gadget", "west", 4, 4);
    SeedLocation(state, "gadget", "north", 9, 3);
    SeedLocation(state, "anvil", "south", 2, 7);

    var report = GetLowStockReport.Execute(state);

    Assert(report.Length == 3, "below-threshold and exactly-at-threshold locations are reported");
    Assert(
        report[0] is
        {
            Sku: "ANVIL",
            Location: "SOUTH",
            Available: 2,
            SafetyStock: 7,
            Shortfall: 5
        },
        "earlier SKU is reported first with its shortfall"
    );
    Assert(
        report[1] is
        {
            Sku: "GADGET",
            Location: "EAST",
            Available: 1,
            SafetyStock: 5,
            Shortfall: 4
        },
        "below-threshold shortfall is safety stock minus available quantity"
    );
    Assert(
        report[2] is
        {
            Sku: "GADGET",
            Location: "WEST",
            Available: 4,
            SafetyStock: 4,
            Shortfall: 0
        },
        "exactly-at-threshold locations are included with shortfall 0"
    );
    Assert(
        !report.Any(entry => entry.Location == "NORTH"),
        "above-threshold locations are excluded"
    );
    Assert(
        GetLowStockReport.Execute(state).SequenceEqual(report),
        "repeat reporting is stable and mutation-free"
    );
    Assert(state.Version == 0, "reporting does not increment the state version");
    Assert(state.Reservations.Count == 0, "reporting stores no reservations");
    Assert(PhysicalStock(state, "WIDGET") == 18, "reporting leaves physical stock unchanged");
    Assert(
        GetInventory.Execute("widget", state)?.Available == 15,
        "existing inventory lookup is intact after reporting"
    );
}

static void GetLowStockReport_ReturnsEmptyReportWhenNoLocationsQualify()
{
    var seeded = new InventoryState();

    var aboveThreshold = GetLowStockReport.Execute(seeded);

    Assert(
        aboveThreshold.Length == 0,
        "locations above their safety stock produce an empty report"
    );
    Assert(seeded.Version == 0, "empty reporting does not increment the state version");
    Assert(
        GetInventory.Execute("widget", seeded)?.Available == 15,
        "empty reporting leaves stock unchanged"
    );

    var empty = new InventoryState();
    lock (empty.SyncRoot)
    {
        empty.Stock.Clear();
    }

    var emptyReport = GetLowStockReport.Execute(empty);

    Assert(emptyReport.Length == 0, "empty inventory produces an empty report");
    Assert(empty.Version == 0, "reporting an empty inventory does not increment the version");
}


SeedLocation(
    InventoryState state,
    string sku,
    string location,
    int available,
    int safetyStock
)
{
    lock (state.SyncRoot)
    {
        if (!state.Stock.TryGetValue(sku, out var locations))
        {
            locations = new Dictionary<string, LocationStockRecord>(StringComparer.OrdinalIgnoreCase);
            state.Stock[sku] = locations;
        }
        locations[location] = new LocationStockRecord(sku, location, available, safetyStock);
    }
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

static void TransferStock_MovesStockAboveSafetyStockFloor()
{
    var state = new InventoryState();

    var transferred = TransferStock.Execute(" widget ", " north ", " south ", 3, state);

    Assert(
        transferred.Outcome == TransferStockOutcome.Transferred,
        "mixed-case transfer above the safety floor succeeds"
    );
    Assert(
        transferred.Item
            is
        {
            Sku: "WIDGET",
            SourceLocation: "NORTH",
            DestinationLocation: "SOUTH",
            Quantity: 3,
            SourceAvailable: 7,
            DestinationAvailable: 11
        },
        "response uses uppercase identifiers and updated stock for both locations"
    );
    Assert(state.Version == 1, "successful transfer increments version once");

    var toFloor = TransferStock.Execute("WIDGET", "NORTH", "SOUTH", 5, state);
    Assert(
        toFloor.Outcome == TransferStockOutcome.Transferred,
        "source can transfer exactly its stock above the safety floor"
    );
    Assert(
        state.Stock["WIDGET"]["NORTH"].Available == 2,
        "source is left exactly at its safety floor"
    );
    Assert(
        state.Stock["WIDGET"]["SOUTH"].Available == 16,
        "destination gains every transferred unit"
    );
    Assert(
        PhysicalStock(state, "WIDGET") == 18,
        "successful transfers conserve total physical stock"
    );
    Assert(state.Version == 2, "each successful transfer increments version once");

    var beyondFloor = TransferStock.Execute("widget", "north", "south", 1, state);
    Assert(
        beyondFloor.Outcome == TransferStockOutcome.InsufficientStock,
        "transfer beyond the safety floor is rejected"
    );
    Assert(
        state.Stock["WIDGET"]["NORTH"].Available == 2,
        "rejected transfer leaves source stock at the floor"
    );
    Assert(
        state.Stock["WIDGET"]["SOUTH"].Available == 16,
        "rejected transfer leaves destination stock unchanged"
    );
    Assert(
        state.Stock["WIDGET"]["NORTH"].SafetyStock == 2
            && state.Stock["WIDGET"]["SOUTH"].SafetyStock == 1,
        "rejected transfer preserves both safety-stock values"
    );
    Assert(state.Version == 2, "rejected transfer does not increment version");

    var belowFloorState = new InventoryState();
    belowFloorState.Stock["GADGET"] = new Dictionary<string, LocationStockRecord>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["EAST"] = new("GADGET", "EAST", 1, 3),
        ["WEST"] = new("GADGET", "WEST", 5, 0),
    };
    var belowFloor = TransferStock.Execute("gadget", "east", "west", 1, belowFloorState);
    Assert(
        belowFloor.Outcome == TransferStockOutcome.InsufficientStock,
        "source below its safety floor cannot transfer"
    );
    Assert(
        belowFloorState.Stock["GADGET"]["EAST"].Available == 1
            && belowFloorState.Stock["GADGET"]["WEST"].Available == 5,
        "below-floor failure is mutation-free"
    );
    Assert(belowFloorState.Version == 0, "below-floor failure does not increment version");
}

static void TransferStock_RejectsInvalidAndMissingEntitiesWithoutMutation()
{
    var state = new InventoryState();

    var zero = TransferStock.Execute("widget", "north", "south", 0, state);
    var negative = TransferStock.Execute("widget", "north", "south", -5, state);
    var blankSource = TransferStock.Execute("widget", " ", "south", 1, state);
    var blankDestination = TransferStock.Execute("widget", "north", " ", 1, state);
    var sameLocation = TransferStock.Execute("widget", "north", "NORTH", 1, state);
    var trimmedSameLocation = TransferStock.Execute("widget", " north ", "NORTH", 1, state);
    var missingSku = TransferStock.Execute("missing", "north", "south", 1, state);
    var missingSource = TransferStock.Execute("widget", "east", "south", 1, state);
    var missingDestination = TransferStock.Execute("widget", "north", "east", 1, state);

    Assert(zero.Outcome == TransferStockOutcome.Invalid, "zero quantity is rejected");
    Assert(zero.InvalidField == "quantity", "zero quantity names the quantity field");
    Assert(negative.Outcome == TransferStockOutcome.Invalid, "negative quantity is rejected");
    Assert(
        blankSource.Outcome == TransferStockOutcome.Invalid,
        "blank source location is rejected"
    );
    Assert(blankSource.InvalidField == "sourceLocation", "blank source names its field");
    Assert(
        blankDestination.Outcome == TransferStockOutcome.Invalid,
        "blank destination location is rejected"
    );
    Assert(
        sameLocation.Outcome == TransferStockOutcome.Invalid,
        "locations that differ only in case are the same location and are rejected"
    );
    Assert(
        trimmedSameLocation.Outcome == TransferStockOutcome.Invalid,
        "locations that match after trimming are rejected"
    );
    Assert(
        missingSku.Outcome == TransferStockOutcome.UnknownSku,
        "unknown SKU is rejected"
    );
    Assert(
        missingSource.Outcome == TransferStockOutcome.UnknownSourceLocation,
        "unknown source location is rejected"
    );
    Assert(
        missingDestination.Outcome == TransferStockOutcome.UnknownDestinationLocation,
        "unknown destination location is rejected"
    );

    var inventory = GetInventory.Execute("widget", state)!;
    Assert(inventory.Locations.Length == 2, "missing locations are not created implicitly");
    Assert(
        inventory.Locations[0] is { Location: "NORTH", Available: 10, SafetyStock: 2 },
        "failed transfers leave source stock and safety stock unchanged"
    );
    Assert(
        inventory.Locations[1] is { Location: "SOUTH", Available: 8, SafetyStock: 1 },
        "failed transfers leave destination stock and safety stock unchanged"
    );
    Assert(
        GetInventory.Execute("missing", state) is null,
        "unknown SKU is not created implicitly"
    );
    Assert(state.Version == 0, "failed transfers do not increment version");
}

static void TransferStock_RejectsDestinationOverflowWithoutMutation()
{
    var state = new InventoryState();
    state.Stock["OVERFLOW"] = new Dictionary<string, LocationStockRecord>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["SOURCE"] = new("OVERFLOW", "SOURCE", 10, 0),
        ["DEST"] = new("OVERFLOW", "DEST", int.MaxValue - 3, 0),
    };

    var exact = TransferStock.Execute("overflow", "source", "dest", 3, state);
    var exceeding = TransferStock.Execute("overflow", "source", "dest", 1, state);

    Assert(
        exact.Outcome == TransferStockOutcome.Transferred,
        "destination can reach the maximum integer value exactly"
    );
    Assert(
        state.Stock["OVERFLOW"]["DEST"].Available == int.MaxValue,
        "destination reaches the maximum integer value"
    );
    Assert(
        state.Stock["OVERFLOW"]["SOURCE"].Available == 7,
        "source is debited by the successful transfer"
    );
    Assert(
        exceeding.Outcome == TransferStockOutcome.DestinationOverflow,
        "transfer that would overflow the destination is rejected"
    );
    Assert(
        state.Stock["OVERFLOW"]["SOURCE"].Available == 7,
        "overflow rejects without debiting the source"
    );
    Assert(
        state.Stock["OVERFLOW"]["DEST"].Available == int.MaxValue,
        "overflow leaves the destination unchanged"
    );
    Assert(
        state.Stock["OVERFLOW"]["SOURCE"].SafetyStock == 0
            && state.Stock["OVERFLOW"]["DEST"].SafetyStock == 0,
        "overflow preserves safety-stock values"
    );
    Assert(state.Version == 1, "only the successful transfer increments version");
}

static void TransferStock_ConcurrentTransfersCommitOnlyAffordableRequests()
{
    var state = new InventoryState();
    var quantities = new[] { 3, 3, 3, 3 };
    var outcomes = new TransferStockOutcome[quantities.Length];
    var start = new ManualResetEventSlim(false);
    var threads = quantities
        .Select((quantity, index) => new Thread(() =>
        {
            start.Wait();
            outcomes[index] = TransferStock.Execute("widget", "north", "south", quantity, state)
                .Outcome;
        }))
        .ToArray();

    foreach (var thread in threads)
    {
        thread.Start();
    }
    start.Set();
    foreach (var thread in threads)
    {
        thread.Join();
    }

    var succeeded = outcomes.Count(outcome => outcome == TransferStockOutcome.Transferred);
    var conflicts = outcomes.Count(outcome => outcome == TransferStockOutcome.InsufficientStock);

    Assert(
        succeeded == 2,
        "only transfers affordable at their atomic commit succeed"
    );
    Assert(
        conflicts == 2,
        "combined demand beyond the transferable stock is rejected as a conflict"
    );
    var inventory = GetInventory.Execute("widget", state)!;
    Assert(inventory.Locations[0].Available == 4, "source never crosses its safety floor");
    Assert(inventory.Locations[0].SafetyStock == 2, "source safety stock is preserved");
    Assert(inventory.Locations[1].Available == 14, "destination gains only committed stock");
    Assert(inventory.Locations[1].SafetyStock == 1, "destination safety stock is preserved");
    Assert(PhysicalStock(state, "WIDGET") == 18, "competing transfers conserve total stock");
    Assert(state.Version == 2, "version increments once per successful transfer");
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
