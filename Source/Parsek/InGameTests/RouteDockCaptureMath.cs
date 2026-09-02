using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure, Unity-free decision + formatting core for the
    /// <c>RouteDockCapture</c> in-game category
    /// (<see cref="RouteDockCaptureInGameTest"/>). Everything here is
    /// headlessly unit-tested in <c>RouteDockCaptureMathTests</c>; the live
    /// cell file keeps only the KSP-touching orchestration.
    ///
    /// <para>The split exists for the same reason
    /// <c>InGameFixtureMath</c> exists: a FLIGHT cell that sizes itself
    /// against whatever vessel the batch flies has real arithmetic in it, and
    /// arithmetic that only ever runs inside KSP is arithmetic nobody can
    /// red locally.</para>
    /// </summary>
    internal static class RouteDockCaptureMath
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// How much of a resource can actually move in one direction:
        /// bounded by what the source holds, by the destination's free
        /// capacity, and by what the cell asked for. Negative / NaN inputs
        /// clamp to zero so a degenerate live tank can never produce a
        /// negative "transfer" that inflates one side's manifest.
        ///
        /// <para>The cells assert against the RETURNED amount rather than the
        /// requested one - the route window's manifests are corner
        /// differences of the real tanks, so a request the tanks could not
        /// satisfy must not become an expectation.</para>
        /// </summary>
        internal static double ResolveTransferAmount(
            double sourceAmount, double destFreeCapacity, double requested)
        {
            double moved = requested;
            if (double.IsNaN(moved) || moved <= 0.0)
                return 0.0;
            if (double.IsNaN(sourceAmount) || sourceAmount <= 0.0)
                return 0.0;
            if (double.IsNaN(destFreeCapacity) || destFreeCapacity <= 0.0)
                return 0.0;
            if (moved > sourceAmount)
                moved = sourceAmount;
            if (moved > destFreeCapacity)
                moved = destFreeCapacity;
            return moved > 0.0 ? moved : 0.0;
        }

        /// <summary>
        /// The EXACT predicate
        /// <c>FlightRecorder.CaptureStartRouteOriginProofIfDocked</c> uses to
        /// build its <see cref="OriginPartnerCandidate"/> list:
        /// <c>p.parent != null &amp;&amp; p.parent.vessel != null &amp;&amp;
        /// p.parent.vessel != v</c>. Mirrored here as three booleans so the
        /// probe cell's counting rule is provably the production rule and can
        /// be unit-tested without a live part hierarchy.
        ///
        /// <para>ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE (todo, SUSPECTED)
        /// is precisely the claim that this predicate is unsatisfiable after
        /// a settled couple, because <c>Part.Couple</c> ends in
        /// <c>SetVessel(tgtPart.vessel)</c> over the whole absorbed subtree.
        /// The probe measures it; nothing here asserts a verdict.</para>
        /// </summary>
        internal static bool IsExternallyParentedPart(
            bool hasParent, bool parentVesselResolves, bool parentVesselIsSelf)
        {
            return hasParent && parentVesselResolves && !parentVesselIsSelf;
        }

        /// <summary>
        /// Grep-stable instrument line for the origin-proof probe. The cell
        /// PASSES whenever it measured, so every fact the operator needs to
        /// decide whether roadmap item B4 is a flight or a bug has to live in
        /// this one line.
        /// </summary>
        internal static string FormatOriginProofProbeLine(
            int externalParentParts,
            bool proofCaptured,
            int activeVesselSituation,
            string producerOutcome,
            uint partnerPid)
        {
            return "OriginProofProbe: externalParentParts="
                + externalParentParts.ToString(IC)
                + " proofCaptured=" + (proofCaptured ? "True" : "False")
                + " situation=" + activeVesselSituation.ToString(IC)
                + " outcome=" + (string.IsNullOrEmpty(producerOutcome) ? "<none>" : producerOutcome)
                + " partnerPid=" + partnerPid.ToString(IC);
        }

        /// <summary>
        /// Grep-stable per-cell verdict line. ONE format across the whole
        /// category so the scenario spec pins one regex shape per cell,
        /// discriminated by <c>cell=</c>; the trailing <c>detail=</c> carries
        /// whatever that cell measured beyond the shared fields.
        /// </summary>
        internal static string FormatPassLine(
            string cell,
            string runId,
            int completeWindows,
            RouteConnectionKind kind,
            bool complete,
            int deliveryResources,
            int deliveryInventory,
            int pickupResources,
            int pickupInventory,
            string detail)
        {
            return "DockCapture PASS: cell=" + cell
                + " run=" + (string.IsNullOrEmpty(runId) ? "<none>" : runId)
                + " windows=" + completeWindows.ToString(IC)
                + " kind=" + kind
                + " complete=" + (complete ? "True" : "False")
                + " deliveryResources=" + deliveryResources.ToString(IC)
                + " deliveryInventory=" + deliveryInventory.ToString(IC)
                + " pickupResources=" + pickupResources.ToString(IC)
                + " pickupInventory=" + pickupInventory.ToString(IC)
                + " detail=" + (string.IsNullOrEmpty(detail) ? "<none>" : detail);
        }

        /// <summary>
        /// Grep-stable verdict line for the <c>RouteStartDockedOrigin</c> cells (Tier B
        /// item 4: the transport STARTS docked to a surface base, undocks, delivers
        /// elsewhere). ONE format across that category so its spec pins one regex shape
        /// per cell, discriminated by <c>cell=</c>.
        ///
        /// <para>The fields are the ones a reader needs to tell the fix from a
        /// coincidence: whether the producer captured at all, the origin vessel pid it
        /// resolved (the merged docked pair's, which <c>Part.Undock</c> leaves on the half
        /// that keeps the parent side of the seam), and the M1 endpoint descriptor's body
        /// + surface flag, which is what gives the origin its proximity rebuild. A
        /// <c>proofCaptured=False</c> line is a legitimate reading for the negative
        /// control and a red for the subject cell, which is why the value is IN the line
        /// rather than implied by which line was emitted.</para>
        /// </summary>
        internal static string FormatStartDockedOriginLine(
            string cell,
            string runId,
            bool proofCaptured,
            uint originVesselPid,
            string originBodyName,
            bool originIsSurface,
            int originSituation,
            int completeWindows,
            string detail)
        {
            return "StartDockedOrigin: cell=" + cell
                + " run=" + (string.IsNullOrEmpty(runId) ? "<none>" : runId)
                + " proofCaptured=" + (proofCaptured ? "True" : "False")
                + " originPid=" + originVesselPid.ToString(IC)
                + " body=" + (string.IsNullOrEmpty(originBodyName) ? "<none>" : originBodyName)
                + " surface=" + (originIsSurface ? "1" : "0")
                + " situation=" + originSituation.ToString(IC)
                + " windows=" + completeWindows.ToString(IC)
                + " detail=" + (string.IsNullOrEmpty(detail) ? "<none>" : detail);
        }

        /// <summary>
        /// The B6 (EVA-construction drift) observation, reduced to one token.
        /// The documented contract is: the drift warning fires, the window
        /// still completes, and the moved part appears in NO manifest. Any
        /// other combination is named rather than silently swallowed, so a
        /// first flight that measures something else says WHICH half moved.
        /// </summary>
        internal static string ClassifyDriftObservation(
            bool driftWarnSeen, bool windowComplete, bool movedPartInAnyManifest)
        {
            if (!windowComplete)
                return "window-did-not-complete";
            if (!driftWarnSeen)
                return "no-drift-warning";
            if (movedPartInAnyManifest)
                return "moved-part-in-manifest";
            return "drift-warned-window-complete-part-unmanifested";
        }
    }
}
