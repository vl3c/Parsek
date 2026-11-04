using System;
using System.Collections.Generic;
using UnityEngine;
using Vectrosity;

namespace Parsek.Display
{
    // Forward legs and arcs: the arc cache key, the forward-leg gate, chain-run member collection, the descent-transfer data-end clamp and forward-arc segment selection.
    internal static partial class GhostTrajectoryPolylineRenderer
    {
        internal static string ForwardArcDictKey(string recordingId, bool boundaryOverlapSecondary)
            => (boundaryOverlapSecondary && !string.IsNullOrEmpty(recordingId))
                ? recordingId + ForwardSecondaryCacheSuffix
                : recordingId;

        /// <summary>
        /// Forward-window overlap leg-gate (Step 3 B', forward-trajectory-render plan): should this
        /// non-orbital leg be drawn as part of the FORWARD chain ahead of the icon? True when the leg's
        /// recorded [<paramref name="legStartUT"/>, <paramref name="legEndUT"/>] span OVERLAPS the forward
        /// render window <c>(<paramref name="forwardWindowStartUT"/>, <paramref name="forwardStopUT"/>]</c>
        /// AND the leg is NOT the CURRENT leg (the one already drawn in full by the head-gated current-leg
        /// pass via <see cref="ShouldDrawLegAtHeadUT"/>). Excluding the current leg keeps the forward pass
        /// PURELY ADDITIVE: the current element renders exactly as today (and publishes ownership exactly as
        /// today), and the forward legs only extend the line ahead. An empty forward range
        /// (<c>forwardStopUT &lt;= forwardWindowStartUT</c>, e.g. the icon sits on a full-loop closed orbit)
        /// draws no forward leg. Pure so the contract is xUnit-testable without Unity.
        /// </summary>
        /// <param name="legStartUT">Leg's first recorded UT.</param>
        /// <param name="legEndUT">Leg's last recorded UT.</param>
        /// <param name="forwardWindowStartUT">The current element's startUT (window lower bound).</param>
        /// <param name="forwardStopUT">The forward stop UT (window upper bound, exclusive of the next-SOI /
        /// full-loop element).</param>
        /// <param name="headUT">The ghost's current playback UT, used to identify and EXCLUDE the current
        /// leg (drawn by the head-gated pass).</param>
        internal static bool ShouldDrawForwardLeg(
            double legStartUT, double legEndUT,
            double forwardWindowStartUT, double forwardStopUT, double headUT)
        {
            // Empty forward range -> nothing forward to draw.
            if (forwardStopUT <= forwardWindowStartUT) return false;
            // The current leg is already drawn in full by the head-gated pass; never double-draw it here.
            if (ShouldDrawLegAtHeadUT(legStartUT, legEndUT, headUT)) return false;
            // Overlap test against the half-open forward window (legStart < stop && legEnd > windowStart).
            return legStartUT < forwardStopUT && legEndUT > forwardWindowStartUT;
        }

        /// <summary>
        /// Chain-aware run membership (playtest-4 chain-boundary fix): the recordings whose legs/arcs
        /// participate in ONE render run. A chain (shared <c>Recording.ChainId</c>) splits one logical
        /// flight across multiple recordings at handoff seams, so a run computed over a single member
        /// cannot span the seam: a launch segment that hands off before reaching orbit carries ZERO
        /// OrbitSegments (no window at all while the icon rides it, so the ascent leg draws alone), and
        /// after the handoff the next member's run only reaches its OWN legs (the ascent leg vanishes) -
        /// exactly the playtest-4 symptom. Returns every committed member of <paramref name="rec"/>'s
        /// chain ordered by StartUT (chain members partition one shared recorded-UT axis, so run-window
        /// boundaries computed over the concatenated effective segments are directly comparable), or just
        /// <paramref name="rec"/> itself for a standalone recording. Fills the caller's scratch list
        /// (clear-and-fill, per-frame hot path). Pure; xUnit-testable without Unity.
        /// </summary>
        internal static void CollectChainRunMembers(
            IReadOnlyList<Recording> committed, Recording rec, int recordingIndex,
            List<(int index, Recording rec)> members,
            GhostPlaybackLogic.LoopUnitSet loopUnits = null)
        {
            if (members == null) return;
            members.Clear();
            if (rec == null) return;
            if (!rec.IsChainRecording || committed == null)
            {
                // DESCENT TRIGGER: a standalone (chain=none) descent member must NOT be added to the run -
                // it is drawn solely by its own trigger-gated primary pass at the re-anchored head. The
                // chain-member exclusion loop below never runs for a non-chain rec, so the exclusion has to
                // be applied on this direct-add path too (the live leak: a chain=none descent member like
                // "Merger Kerman" drew its descent leg on the visible member's loop clock). Leave members
                // empty for it. A non-descent standalone recording is unaffected (guard false).
                if (!IsDescentTriggerMember(recordingIndex, loopUnits))
                    members.Add((recordingIndex, rec));
                return;
            }
            for (int i = 0; i < committed.Count; i++)
            {
                var m = committed[i];
                if (m == null || string.IsNullOrEmpty(m.RecordingId)) continue;
                if (!string.Equals(m.ChainId, rec.ChainId, StringComparison.Ordinal)) continue;
                // DESCENT TRIGGER: a descent-set member renders ONLY via its own trigger-gated primary pass
                // (hidden during the loiter, drawn at the re-anchored head during its rotation-aligned window).
                // Exclude it from the chain RUN entirely: the run draws sibling members' body-fixed legs against
                // the VISIBLE (transfer) member's LOOP-CLOCK head, so a descent member left in the run would draw
                // its descent line on the loop clock every pass over its recorded slot - desynced from the
                // trigger-controlled icon (the descent line painted on the loiter while the icon still waits).
                // Its legs AND arcs stay out of the run, matching the descent member's intended full hide.
                if (IsDescentTriggerMember(i, loopUnits))
                    continue;
                members.Add((i, m));
            }
            if (members.Count == 0)
            {
                // Defensive: rec itself was not in the committed list (caller passed a detached
                // recording) - fall back to the single-member run so the pass still works.
                // Mirror the exclusion: never re-add a descent-trigger member here either (a
                // chain=none descent member reaches this fallback because the chain-member loop
                // above matched none, and re-adding it would draw its descent leg on the run's
                // loop-clock head - exactly the leak the descent member's own trigger-gated
                // primary pass already covers). Leave members empty so its legs/arcs/bridges
                // stay out of the run.
                if (!IsDescentTriggerMember(recordingIndex, loopUnits))
                    members.Add((recordingIndex, rec));
                return;
            }
            members.Sort((a, b) => a.rec.StartUT.CompareTo(b.rec.StartUT));
        }

        /// <summary>
        /// True when committed index <paramref name="index"/> is a descent-set member of a re-aim looped
        /// arrival (its owning unit <see cref="GhostPlaybackLogic.LoopUnit.HasDescentTrigger"/> and the index
        /// is in its descent set). A descent member renders ONLY via its own trigger-gated primary pass (drawn
        /// at the re-anchored rotation-aligned head, hidden during the loiter); it must be kept out of EVERY
        /// forward-run leg/arc/bridge path, which all draw against the VISIBLE member's raw LOOP-CLOCK head and
        /// would otherwise paint the descent line on the loiter while the icon still waits. The single seam for
        /// the three forward-run exclusion sites (chain run membership, the head-leg bridge candidate, and the
        /// per-member run loop) so they cannot drift apart. <c>loopUnits == null</c> / non-member / no descent
        /// trigger -> false (byte-identical to the no-trigger path). Pure; xUnit-testable without Unity.
        /// </summary>
        internal static bool IsDescentTriggerMember(int index, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            return loopUnits != null
                && loopUnits.TryGetUnitForMember(index, out GhostPlaybackLogic.LoopUnit du)
                && du.HasDescentTrigger && du.IsDescentMember(index);
        }

        /// <summary>
        /// Clamp B (re-aim descent trigger): cap a forward-run window's <paramref name="chainDataEndUT"/> at the
        /// unit's SHIFTED parking-conic end (<c>RecordedDeorbitUT + CaptureShiftSeconds</c>, captureShift NEGATIVE)
        /// when <paramref name="recordingIndex"/> is the DESTINATION transfer member
        /// (<see cref="GhostPlaybackLogic.LoopUnit.TransferMemberIndex"/>) of a descent-trigger
        /// unit. PR #1177 shifts that member's destination parking conics ~|captureShift| EARLIER to meet the
        /// early-arriving re-aimed transfer, so the rendered conics end at <c>deorbit+captureShift</c>. Leaving the
        /// forward-run window at the UNSHIFTED <c>rec.EndUT</c> (~the seam, ~|captureShift| later) lets the
        /// <see cref="ForwardRenderWindow"/> past-end branch (<c>currentUT &lt;= dataEndUT + 1</c>) fire once the
        /// loiter icon passes the shifted conic end and paint the member's OWN unshifted recorded approach tail
        /// plus a ~|captureShift|-gap bridge — the spurious "second path" the icon cannot follow. Capping at the
        /// shifted conic end makes that guard FAIL the moment the icon leaves the shifted conic, killing the leak;
        /// the shifted parking conic the icon rides is untouched. Byte-identical (returns
        /// <paramref name="chainDataEndUT"/> unchanged) when: <paramref name="loopUnits"/> is null, the index is
        /// not a unit member, the unit has no descent trigger, the index is NOT the destination transfer member
        /// (the owner, every ride-along in a different/unshifted frame whose forward window must not be clipped at
        /// the transfer member's geometry, or a descent-set member whose line comes only from its own trigger-gated
        /// pass), RecordedDeorbitUT/CaptureShiftSeconds is NaN, captureShift is not negative, or the window already
        /// ends at/before the shifted conic end. Pure; xUnit-testable without Unity.
        /// </summary>
        internal static double ClampChainDataEndForDescentTransfer(
            double chainDataEndUT, int recordingIndex, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            if (loopUnits != null
                && loopUnits.TryGetUnitForMember(recordingIndex, out GhostPlaybackLogic.LoopUnit unit)
                && unit.HasDescentTrigger && recordingIndex == unit.TransferMemberIndex
                && !double.IsNaN(unit.RecordedDeorbitUT) && !double.IsNaN(unit.CaptureShiftSeconds))
            {
                double shiftedConicEndUT = unit.RecordedDeorbitUT + unit.CaptureShiftSeconds;
                if (shiftedConicEndUT < unit.RecordedDeorbitUT && chainDataEndUT > shiftedConicEndUT)
                    return shiftedConicEndUT;
            }
            return chainDataEndUT;
        }

        /// <summary>
        /// Run-arc cache key (Step 3 Cache bullet): the run-arc VectorLine set is re-sampled only when the
        /// SELECTED segment set (the indices <see cref="SelectForwardArcSegmentIndices"/> returned) or the
        /// re-aim synodic WINDOW changes. Keying on the actual selected set (not currentElementIndex) is
        /// required by the revised run rule: with PAST arcs included, the same currentElementIndex maps to
        /// different selections as the icon crosses element boundaries within a run (the excluded current arc
        /// changes), so an index-only key would serve a stale arc set. The recorded segment geometry is
        /// static, but a re-aim window rollover swaps the EFFECTIVE geometry without changing the recorded
        /// segment count, so <paramref name="reaimWindowIndex"/> (the <c>ResolveEffectiveMapOrbitSegments</c>
        /// out window index, also the <c>ShadowRenderDriver.BuildChainSignature</c> discriminator) MUST be
        /// part of the key or a stale wrong-aimed arc would survive the rollover. The selected indices are
        /// emitted in ascending order (the selector scans ascending), so the key is order-stable. Pure;
        /// xUnit-testable.
        /// </summary>
        internal static string BuildForwardArcKey(
            List<int> selectedSegmentIndices, long reaimWindowIndex)
        {
            string indices = selectedSegmentIndices == null || selectedSegmentIndices.Count == 0
                ? "-"
                : string.Join(",", selectedSegmentIndices);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1}", indices, reaimWindowIndex);
        }

        /// <summary>
        /// PURE forward-arc segment selector (Step 3 C). Given the EFFECTIVE (re-aim-resolved) orbit
        /// segments, the computed forward window <c>(<paramref name="forwardWindowStartUT"/>,
        /// <paramref name="forwardStopUT"/>]</c>, and the ghost's current <paramref name="headUT"/>, returns
        /// the INDICES of the segments to draw as FORWARD arcs ahead of the icon. A segment is selected when
        /// it:
        ///   - overlaps the forward window (<c>seg.startUT &lt; forwardStopUT &amp;&amp;
        ///     seg.endUT &gt; forwardWindowStartUT</c>),
        ///   - is NOT the segment the icon currently sits on (the CURRENT arc is drawn in full by the stock
        ///     <c>OrbitRenderer</c> + <c>GhostOrbitArcPatch</c>, so the forward pass must never double-draw
        ///     it - this keeps "the current element renders exactly as today"),
        ///   - is ABOVE the surface (<see cref="IsOrbitSegmentBelowSurface"/> false): a descent that dips
        ///     below the surface is drawn as a forward LEG (B'), not an arc (Step 3 / FIX #27),
        ///   - has a positive span (<c>endUT &gt; startUT</c>).
        /// An empty forward range (<c>forwardStopUT &lt;= forwardWindowStartUT</c>) selects nothing. Pure
        /// (the only KSP coupling, the below-surface test, is injected via <paramref name="surface"/>); the
        /// live Driver then samples each selected segment's Kepler conic. xUnit-testable.
        /// </summary>
        internal static List<int> SelectForwardArcSegmentIndices(
            List<OrbitSegment> effectiveSegments,
            double forwardWindowStartUT, double forwardStopUT, double headUT,
            BodySurfaceProvider surface)
        {
            var indices = new List<int>();
            SelectForwardArcSegmentIndices(
                effectiveSegments, forwardWindowStartUT, forwardStopUT, headUT, surface, indices);
            return indices;
        }

        /// <summary>
        /// Hot-path overload (forward-render review finding): fills the caller-provided
        /// <paramref name="indices"/> buffer (clear-and-fill) instead of allocating a fresh
        /// <c>List&lt;int&gt;</c> each call. The Driver's forward decide pass runs this once per
        /// leg-bearing committed recording per frame on the multi-ghost map path, so it reuses a single
        /// per-Driver scratch list to avoid sustained Gen-0 churn proportional to the ghost count. The
        /// allocating overload above wraps this for tests / one-shot callers. Pure selection logic is
        /// unchanged: same current-arc exclusion, below-surface skip, and half-open window overlap test.
        /// </summary>
        internal static void SelectForwardArcSegmentIndices(
            List<OrbitSegment> effectiveSegments,
            double forwardWindowStartUT, double forwardStopUT, double headUT,
            BodySurfaceProvider surface, List<int> indices)
        {
            if (indices == null) return;
            indices.Clear();
            if (effectiveSegments == null) return;
            if (forwardStopUT <= forwardWindowStartUT) return;
            for (int i = 0; i < effectiveSegments.Count; i++)
            {
                var seg = effectiveSegments[i];
                if (seg.endUT <= seg.startUT) continue;
                // The CURRENT arc (icon's element) is drawn by stock; never forward-draw it.
                if (headUT >= seg.startUT && headUT < seg.endUT) continue;
                // Below-surface descent conic -> drawn as a forward leg, not an arc.
                if (IsSegmentExcludedFromOrbitOwnership(seg, surface)) continue;
                // Overlap the half-open forward window.
                if (seg.startUT < forwardStopUT && seg.endUT > forwardWindowStartUT)
                    indices.Add(i);
            }
        }

        /// <summary>
        /// Constructs a fresh forward-arc <c>LineType.Continuous</c> VectorLine sized to hold exactly the
        /// arc's sample count (Step 3 C). One line PER forward segment (same Draw3D drawStart/drawEnd
        /// constraint as legs). Identical orbit-line style to the legs via
        /// <see cref="ApplyStockOrbitLineStyle"/> so the forward arc reads as one continuous line with the
        /// current stock arc and the legs.
        /// </summary>
        internal static VectorLine BuildForwardArcVectorLine(
            string recordingId, int arcIndex, int pointCount)
        {
            return BuildContinuousMapLine(
                "ParsekGhostForwardArc-" + recordingId + "-arc" + arcIndex, pointCount);
        }
    }
}
