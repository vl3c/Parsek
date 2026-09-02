// [ERS-exempt] The DeleteRecording seam verb reads RecordingStore.CommittedRecordings
// RAW: the production entry points it reproduces (RecordingsTableUI.DeleteGhostOnlyRecording
// -> ParsekFlight.DeleteGhostOnlyRecording / ParsekFlight.DeleteRecording /
// RecordingStore.DeleteRecordingFull) take a COMMITTED-LIST index, and the flight and KSC
// ghost hosts key their per-index state on that same list, so an ERS-filtered list would
// re-index the target under the store. Same list-alignment rationale as EnterWatchMode's
// exemption. Test-command surface: unreachable without PARSEK_TEST_COMMANDS=1. File
// allowlisted in scripts/ers-els-audit-allowlist.txt.
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the ADDITIVE <c>DeleteRecording index=&lt;n&gt;</c>
    /// verb: the Recordings table's per-row delete, driven by committed-list index.
    ///
    /// <para>
    /// WHY THIS VERB EXISTS. AUTOMATION-GAP-KSC-TABLE-DELETE: the Space Center delete
    /// path (<c>RecordingStore.DeleteRecordingFull</c>, whose Removing / Removed
    /// notifications the <c>ParsekKSC</c> host shifts its index-keyed ghost state from)
    /// had no driven caller, so the subscriber that stopped a KSC delete from leaving
    /// every ghost above the row playing its neighbour's trajectory was pinned headlessly
    /// and never exercised in the scene it was written for.
    /// </para>
    ///
    /// <para>
    /// ROUTING mirrors <c>RecordingsTableUI.DeleteGhostOnlyRecording</c>'s scene branch
    /// and adds the table's other delete: in FLIGHT with a flight host present, a
    /// ghost-only row goes through <c>ParsekFlight.DeleteGhostOnlyRecording</c> and any
    /// other row through <c>ParsekFlight.DeleteRecording</c> (whose
    /// <c>CanDeleteRecording</c> guard is checked HERE first and typed REJECTED, because
    /// the production call refuses silently); everywhere else the row goes through
    /// <c>RecordingStore.DeleteRecordingFull</c>, the KSC branch. The verb is deliberately
    /// WIDER than the table's "X" button on one axis: it takes any committed row, not only
    /// ghost-only ones, because the button's gate is an OFFERING policy over rows only the
    /// Gloops recorder produces, and the removal the lane exists to drive is a MID-LIST
    /// one under living ghosts, which an appended ghost-only row can never be.
    /// </para>
    ///
    /// <para>
    /// SINGLE-PHASE, VERIFIED BY READ-BACK. Every production call above is void and
    /// swallows a refusal with a Warn, so the verdict is not the call returning: the row
    /// must be GONE from the committed list afterwards (by reference, never by index,
    /// which has just shifted), else the verb answers <c>ERROR delete-not-applied</c>
    /// rather than a false OK.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void DeleteRecordingImpl(ParsedCommand cmd)
        {
            string indexArg = ArgOrNull(cmd, "index");
            if (!TestCommandDeleteRecording.TryParseIndexArg(indexArg, out int index))
            {
                ParsekLog.Warn(Tag,
                    $"deleterecording rejected reason=index-arg-invalid index={indexArg ?? string.Empty}");
                SetExecResult("REJECTED", null, "index-arg-invalid");
                return;
            }

            // [ERS-exempt] - see the file-header note (committed-LIST indices).
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            int before = committed != null ? committed.Count : 0;
            if (index >= before || committed[index] == null)
            {
                ParsekLog.Warn(Tag,
                    $"deleterecording rejected reason=index-out-of-range index={index} committed={before}");
                SetExecResult("REJECTED", null, "index-out-of-range");
                return;
            }

            Recording rec = committed[index];
            string recId = rec.RecordingId ?? string.Empty;
            string vesselName = rec.VesselName ?? string.Empty;
            bool ghostOnly = rec.IsGhostOnly;
            ParsekFlight flight = ParsekFlight.Instance;
            bool flightHostPresent = HighLogic.LoadedScene == GameScenes.FLIGHT && flight != null;
            DeleteRecordingRoute route = TestCommandDeleteRecording.DecideRoute(flightHostPresent, ghostOnly);
            string routeToken = TestCommandDeleteRecording.RouteToken(route);

            if (route == DeleteRecordingRoute.FlightFull && !flight.CanDeleteRecording)
            {
                // The production call would Warn and return; typed here so a spec reads
                // WHY (a live recorder or a tracked chain continuation) instead of a
                // false OK or an opaque read-back error.
                ParsekLog.Warn(Tag,
                    $"deleterecording rejected reason=flight-delete-blocked index={index} recId={recId} " +
                    $"isRecording={Bool(flight.IsRecording)}");
                SetExecResult("REJECTED", null, "flight-delete-blocked");
                return;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "deleterecording start index={0} recId={1} vessel='{2}' ghostOnly={3} route={4} committed={5} scene={6}",
                index, recId, vesselName, Bool(ghostOnly), routeToken, before, HighLogic.LoadedScene));

            switch (route)
            {
                case DeleteRecordingRoute.FlightGhostOnly:
                    flight.DeleteGhostOnlyRecording(index);
                    break;
                case DeleteRecordingRoute.FlightFull:
                    flight.DeleteRecording(index);
                    break;
                default:
                    RecordingStore.DeleteRecordingFull(index);
                    break;
            }

            IReadOnlyList<Recording> after = RecordingStore.CommittedRecordings;
            int afterCount = after != null ? after.Count : 0;
            if (TestCommandDeleteRecording.IsStillPresent(after, rec))
            {
                ParsekLog.Error(Tag,
                    $"deleterecording error reason=delete-not-applied index={index} recId={recId} route={routeToken} " +
                    $"committedBefore={before} committedAfter={afterCount} (the production call refused; see its Warn line)");
                SetExecResult("ERROR", null, "delete-not-applied");
                return;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "deleterecording complete index={0} recId={1} route={2} committedBefore={3} committedAfter={4}",
                index, recId, routeToken, before, afterCount));
            SetExecResult("OK",
                TestCommandDeleteRecording.BuildCompletePayload(
                    index, recId, vesselName, ghostOnly, routeToken, before, afterCount),
                null);
        }
    }

    /// <summary>Which production delete a DeleteRecording call is routed to.</summary>
    internal enum DeleteRecordingRoute
    {
        /// <summary>Not in FLIGHT (or no flight host): <c>RecordingStore.DeleteRecordingFull</c>,
        /// the Recordings table's KSC branch.</summary>
        Store,

        /// <summary>FLIGHT, ghost-only row: <c>ParsekFlight.DeleteGhostOnlyRecording</c>, the
        /// table's "X" button.</summary>
        FlightGhostOnly,

        /// <summary>FLIGHT, any other row: <c>ParsekFlight.DeleteRecording</c> behind its
        /// <c>CanDeleteRecording</c> guard.</summary>
        FlightFull,
    }

    /// <summary>
    /// Pure decision half of the DeleteRecording verb (headlessly testable; the applier
    /// above owns the Unity / store calls only).
    /// </summary>
    internal static class TestCommandDeleteRecording
    {
        /// <summary>
        /// REQUIRED non-negative committed index. Unlike EnterWatchMode's optional arg
        /// there is no auto-select sentinel: a delete with no target is a spec error, not
        /// a "delete something". A present value must be a non-negative InvariantCulture
        /// integer; a negative, fractional, locale-comma or unparseable value is the
        /// REJECTED path.
        /// </summary>
        internal static bool TryParseIndexArg(string raw, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(raw))
                return false;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                return false;
            if (parsed < 0)
                return false;
            index = parsed;
            return true;
        }

        /// <summary>The table's scene branch plus its second delete: FLIGHT with a host
        /// routes ghost-only rows to the "X" button's call and every other row to the
        /// full flight delete; anything else is the store (KSC) branch.</summary>
        internal static DeleteRecordingRoute DecideRoute(bool flightHostPresent, bool ghostOnly)
        {
            if (!flightHostPresent)
                return DeleteRecordingRoute.Store;
            return ghostOnly ? DeleteRecordingRoute.FlightGhostOnly : DeleteRecordingRoute.FlightFull;
        }

        /// <summary>The grep-stable token a spec pins for the route taken.</summary>
        internal static string RouteToken(DeleteRecordingRoute route)
        {
            switch (route)
            {
                case DeleteRecordingRoute.FlightGhostOnly: return "flight-ghost-only";
                case DeleteRecordingRoute.FlightFull: return "flight-full";
                default: return "store";
            }
        }

        /// <summary>
        /// Read-back by REFERENCE: true when <paramref name="target"/> is still in the
        /// committed list after the delete. Never by index (the list has just shifted, so
        /// the old index now names the row's former neighbour) and never by id (a null or
        /// duplicated id would read a survivor as gone).
        /// </summary>
        internal static bool IsStillPresent(IReadOnlyList<Recording> after, Recording target)
        {
            if (after == null || target == null)
                return false;
            for (int i = 0; i < after.Count; i++)
                if (object.ReferenceEquals(after[i], target))
                    return true;
            return false;
        }

        /// <summary>Terminal payload: the deleted index, its recording id (the stable
        /// handle a spec pins), what it was, which production call removed it, and the
        /// committed count before and after (so a reader sees the removal even though
        /// RecordingState exposes no committed count).</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            int index, string recId, string vesselName, bool ghostOnly, string routeToken,
            int committedBefore, int committedAfter)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("index", index.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("recId", recId ?? string.Empty),
                new KeyValuePair<string, string>("vessel", vesselName ?? string.Empty),
                new KeyValuePair<string, string>("ghostOnly", ghostOnly ? "true" : "false"),
                new KeyValuePair<string, string>("route", routeToken ?? string.Empty),
                new KeyValuePair<string, string>("committedBefore", committedBefore.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("committedAfter", committedAfter.ToString(CultureInfo.InvariantCulture)),
            };
    }
}
