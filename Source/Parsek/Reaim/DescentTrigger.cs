using System;
using System.Collections.Generic;

namespace Parsek.Reaim
{
    /// <summary>
    /// PURE helpers for the looped re-aim LANDING descent trigger (docs/dev/plans/reaim-descent-trigger.md).
    ///
    /// <para>A re-aimed landing's transfer is shorter than recorded, so the conic/icon arrives at the
    /// destination early and the icon then circles the (periodic) parking ellipse for the whole
    /// ~|captureShift| gap before the body-fixed deorbit->reentry->landing polyline (pinned to its recorded
    /// loop-clock slot) is reached. The DESCENT TRIGGER detaches the descent from the recorded loop clock:
    /// the icon circles the loiter freely until the FIRST live moment the destination body's rotation phase
    /// equals the recorded deorbit rotation phase (so the body-fixed descent connects to the parking orbit
    /// AND lands on the EXACT recorded site), then the recorded descent clip plays forward from there.</para>
    ///
    /// <para>The destination body's rotation angle is a deterministic function of UT with period
    /// <c>T_rot = CelestialBody.rotationPeriod</c> (sidereal): <c>angle(UT) = angle(UT + N*T_rot)</c> for any
    /// integer N. So the live rotation equals the recorded deorbit rotation iff
    /// <c>currentUT ≡ recordedDeorbitUT (mod T_rot)</c>. That is the alignment condition - no recorded
    /// rotation angle is stored or needed; the recorded quantity is the deorbit UT.</para>
    ///
    /// <para>These helpers are pure (no Unity) so the trigger math is xUnit-testable. The live wiring
    /// (resolving <c>entryUT</c> from the loop phase and remapping the descent member's head in the shared
    /// loop-clock resolver) lives in <see cref="GhostPlaybackLogic"/>.</para>
    /// </summary>
    internal static class DescentTrigger
    {
        /// <summary>
        /// The first live UT at or after <paramref name="entryUT"/> (loiter entry) whose destination-body
        /// rotation phase equals the recorded deorbit rotation phase, i.e. the smallest
        /// <c>t &gt;= entryUT</c> with <c>t ≡ recordedDeorbitUT (mod rotationPeriod)</c>. The result lies in
        /// <c>[entryUT, entryUT + rotationPeriod)</c> - the loiter waits at most one destination day,
        /// however many parking revolutions that spans. NaN (no trigger) for NaN / non-positive
        /// <paramref name="rotationPeriod"/> or NaN inputs.
        /// </summary>
        internal static double ComputeRotationAlignedTriggerUT(
            double entryUT, double recordedDeorbitUT, double rotationPeriod)
        {
            if (double.IsNaN(entryUT) || double.IsNaN(recordedDeorbitUT) || double.IsNaN(rotationPeriod)
                || double.IsInfinity(entryUT) || double.IsInfinity(recordedDeorbitUT)
                || double.IsInfinity(rotationPeriod) || rotationPeriod <= 0.0)
                return double.NaN;

            // Seconds from entryUT forward to the next UT congruent to recordedDeorbitUT (mod rotationPeriod).
            // C#'s % can be negative; normalize into [0, rotationPeriod).
            double phase = (recordedDeorbitUT - entryUT) % rotationPeriod;
            if (phase < 0.0) phase += rotationPeriod;
            return entryUT + phase;
        }

        /// <summary>
        /// The descent member's RE-ANCHORED playback head at <paramref name="currentUT"/>: once the trigger
        /// has fired (<c>currentUT &gt;= triggerUT</c>), the recorded descent clip plays forward verbatim from
        /// its recorded deorbit UT at the live rate, <c>recordedDeorbitUT + (currentUT - triggerUT)</c>.
        /// STRICTLY FORWARD / monotone in <paramref name="currentUT"/> (never &lt; recordedDeorbitUT), so it
        /// can never feed an earlier UT to the insert-only arrival hold (freeze-free by construction). Returns
        /// NaN before the trigger (the descent is still hidden; the icon circles the loiter). NaN inputs ->
        /// NaN.
        /// </summary>
        internal static double ComputeDescentEffectiveHeadUT(
            double currentUT, double triggerUT, double recordedDeorbitUT)
        {
            if (double.IsNaN(currentUT) || double.IsNaN(triggerUT) || double.IsNaN(recordedDeorbitUT))
                return double.NaN;
            if (currentUT < triggerUT)
                return double.NaN; // not yet triggered - descent stays hidden, icon loiters
            return recordedDeorbitUT + (currentUT - triggerUT);
        }

        /// <summary>The phase of the descent-capable member's head this frame (see
        /// <see cref="ComputeDescentMemberHead"/>).</summary>
        internal enum DescentHeadPhase
        {
            /// <summary>The descent trigger is not applicable this frame (degenerate inputs, or the icon has
            /// not yet reached the parking-orbit deorbit point) - the caller keeps the NORMAL loop-clock head
            /// (launch / transfer / parking-conic ride). <c>head</c> is NaN.</summary>
            Inert,
            /// <summary>Waiting for the descent alignment: the icon CIRCLES the (shifted) parking conic.
            /// <c>head</c> is the circling head on the conic's last revolution.</summary>
            Loiter,
            /// <summary>Triggered: the recorded deorbit-&gt;reentry-&gt;landing clip plays forward verbatim.
            /// <c>head</c> is the re-anchored descent head.</summary>
            Descent,
            /// <summary>The descent clip has finished THIS cycle (landed) - the caller HIDES the member until
            /// the next loop. <c>head</c> is NaN.</summary>
            Done,
        }

        /// <summary>
        /// The looped re-aim LANDING member's HEAD this frame, detaching the deorbit-&gt;reentry-&gt;landing clip
        /// from the recorded loop clock and re-anchoring it to the first rotation-aligned moment after the icon
        /// reaches the parking-orbit deorbit point (docs/dev/plans/reaim-descent-trigger.md). One head drives
        /// BOTH the icon (rides the OrbitSegment containing the head) and the body-fixed descent polyline (the
        /// per-leg <c>ShouldDrawLegAtHeadUT</c> gate), so this one remap moves both coherently.
        ///
        /// <para>Geometry of the gap (the bug): PR #1177 shifts the target-body parking CONIC EARLIER by
        /// <paramref name="captureShiftSeconds"/> (negative) so it meets the early-arriving re-aimed transfer.
        /// <see cref="ReaimSegmentAssembler.ShiftInTime"/> shifts startUT/endUT/epoch together (phase preserved),
        /// so the shifted conic covers recorded <c>[arrival, deorbit] + captureShift</c> and its END (UT
        /// <c>deorbit + captureShift</c>) is at the SAME orbital phase as the recorded deorbit point. The
        /// body-fixed descent is NOT shifted (pinned at recorded <c>deorbit</c>), so the icon rides the conic,
        /// reaches the deorbit point at <c>deorbitLive_N + captureShift</c>, then there is a ~|captureShift| gap
        /// before the recorded descent UT - the gap the icon used to sit frozen across while the descent drew at
        /// the wrong rotation.</para>
        ///
        /// <para>The remap, per cycle N (stateless - everything derives from the loop clock):
        /// <list type="bullet">
        /// <item><b>entryUT</b> = <c>deorbitLive_N + captureShiftSeconds</c> where
        ///   <c>deorbitLive_N = phaseAnchorUT + N*cadence + (recordedDeorbitUT - spanStartUT)</c>: the live UT the
        ///   icon reaches the deorbit point on the shifted conic.</item>
        /// <item><b>triggerUT</b> = <see cref="ComputeRotationAlignedTriggerUT"/>(entryUT, recordedDeorbitUT,
        ///   rotationPeriod): the first live UT at/after entry whose body rotation equals the recorded deorbit
        ///   rotation (landing at the EXACT recorded site).</item>
        /// <item><c>currentUT &lt; entryUT</c> -&gt; <see cref="DescentHeadPhase.Inert"/> (normal loop clock).</item>
        /// <item><c>entryUT &lt;= currentUT &lt; triggerUT</c> -&gt; <see cref="DescentHeadPhase.Loiter"/>: head
        ///   circles the shifted conic's last rev, ANCHORED to triggerUT so the icon reaches the deorbit point
        ///   (conicEnd) EXACTLY at the handoff: <c>conicEnd - ((triggerUT - currentUT) mod parkingPeriod)</c>,
        ///   <c>conicEnd = recordedDeorbitUT + captureShiftSeconds</c>. The icon orbits as many revs as needed;
        ///   only the final partial rev retimes (imperceptibly), so the transition is SMOOTH (icon at the deorbit
        ///   point) with the EXACT recorded site - no T_park/T_rot beat-search, no fallback hop.</item>
        /// <item><c>currentUT &gt;= triggerUT</c> -&gt; the descent head
        ///   <c>recordedDeorbitUT + (currentUT - triggerUT)</c>: <see cref="DescentHeadPhase.Descent"/> while
        ///   <c>&lt;= descentEndUT</c>, else <see cref="DescentHeadPhase.Done"/> (landed; hide until next loop).</item>
        /// </list></para>
        ///
        /// <para>Returns <see cref="DescentHeadPhase.Inert"/> (head NaN) for any degenerate input
        /// (NaN, non-positive <paramref name="rotationPeriod"/> / <paramref name="parkingPeriod"/>,
        /// <paramref name="descentEndUT"/> &lt; <paramref name="recordedDeorbitUT"/>), so the caller stays
        /// byte-identical to the no-trigger path. Pure; no Unity (xUnit-testable).</para>
        /// </summary>
        internal static DescentHeadPhase ComputeDescentMemberHead(
            double currentUT,
            long cycleIndex,
            double phaseAnchorUT,
            double cadenceSeconds,
            double spanStartUT,
            double recordedDeorbitUT,
            double descentEndUT,
            double rotationPeriod,
            double parkingPeriod,
            double captureShiftSeconds,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> loiterCuts,
            out double head)
        {
            head = double.NaN;

            // Degenerate guard: any bad input -> Inert (caller keeps the normal loop-clock head).
            if (double.IsNaN(currentUT) || double.IsNaN(phaseAnchorUT) || double.IsNaN(cadenceSeconds)
                || double.IsNaN(spanStartUT) || double.IsNaN(recordedDeorbitUT) || double.IsNaN(descentEndUT)
                || double.IsNaN(captureShiftSeconds)
                || double.IsNaN(rotationPeriod) || double.IsInfinity(rotationPeriod) || rotationPeriod <= 0.0
                || double.IsNaN(parkingPeriod) || double.IsInfinity(parkingPeriod) || parkingPeriod <= 0.0
                || cadenceSeconds <= 0.0 || cycleIndex < 0
                || descentEndUT < recordedDeorbitUT)
                return DescentHeadPhase.Inert;

            // conicEnd: the deorbit point on the SHIFTED parking conic (recorded UT recordedDeorbitUT +
            // captureShift). Because the shift is large and negative, conicEnd lands in the recorded TRANSFER
            // region (before arrival), so only LAUNCH-side loiter cuts precede it - CompressSpanUT subtracts
            // exactly those, mapping conicEnd to the live offset from launch (the destination loiter cut, which
            // sits after arrival, is after conicEnd and correctly contributes 0). Null cuts -> identity, so the
            // no-compression path is byte-identical.
            double conicEnd = recordedDeorbitUT + captureShiftSeconds;
            double entryOffset = GhostPlaybackLogic.CompressSpanUT(conicEnd, loiterCuts) - spanStartUT;
            double entryUT = phaseAnchorUT + cycleIndex * cadenceSeconds + entryOffset;

            if (currentUT < entryUT)
                return DescentHeadPhase.Inert; // icon still launching / transferring / riding the parking conic

            double triggerUT = ComputeRotationAlignedTriggerUT(entryUT, recordedDeorbitUT, rotationPeriod);
            if (double.IsNaN(triggerUT))
                return DescentHeadPhase.Inert; // defensive: degenerate trigger -> no remap

            if (currentUT < triggerUT)
            {
                // WAIT: circle the shifted conic's last revolution. conicEnd (UT recordedDeorbitUT +
                // captureShift) is the deorbit point on the conic - ShiftInTime preserved the orbital phase, so
                // it is the SAME position as the recorded deorbit point the descent starts from.
                //
                // ANCHOR THE CIRCLING TO triggerUT (not entryUT) so the icon reaches conicEnd (the deorbit
                // point) EXACTLY at the handoff: head = conicEnd - ((triggerUT - currentUT) mod parkingPeriod).
                // The earlier revs are uniform; only the final partial rev retimes (imperceptibly - timing is
                // irrelevant, only the geometry matters), giving a SMOOTH transition with the EXACT recorded
                // site and no T_park/T_rot beat-search, no tolerance, no fallback hop. At triggerUT- the head
                // -> conicEnd (deorbit point); the Descent branch then continues from recordedDeorbitUT (same
                // orbital position), so the icon is position-continuous across the seam.
                double toTrigger = (triggerUT - currentUT) % parkingPeriod;
                if (toTrigger < 0.0) toTrigger += parkingPeriod; // defensive (triggerUT > currentUT here, so > 0)
                head = conicEnd - toTrigger;
                return DescentHeadPhase.Loiter;
            }

            // TRIGGERED: play the recorded descent clip forward at the live rate.
            double descentHead = recordedDeorbitUT + (currentUT - triggerUT);
            if (descentHead > descentEndUT)
                return DescentHeadPhase.Done; // landed - hide the member until the next loop

            head = descentHead;
            return DescentHeadPhase.Descent;
        }

        /// <summary>
        /// One candidate member's identity for descent-set selection (see
        /// <see cref="SelectDescentMemberIndices"/>).
        /// </summary>
        internal readonly struct MemberArrivalInfo
        {
            internal MemberArrivalInfo(int committedIndex, double startUT, string body)
            {
                CommittedIndex = committedIndex;
                StartUT = startUT;
                Body = body;
            }

            /// <summary>The member's committed-recording-list index.</summary>
            internal int CommittedIndex { get; }

            /// <summary>The member's recorded window start UT.</summary>
            internal double StartUT { get; }

            /// <summary>The destination/body the member's trajectory is on (its first recorded body), or null.</summary>
            internal string Body { get; }
        }

        /// <summary>
        /// PURE selection of the "descent" member SET for a re-aim looped arrival: the post-parking
        /// body-fixed clip members (deorbit-&gt;reentry-&gt;landing for a surface mission, OR the
        /// rendezvous/docking approach for an orbital one). In a multi-recording chain the through-line
        /// continues PAST the destination arrival as one or more separate committed recordings, each a
        /// member; these are the members the descent trigger must re-anchor (the transfer member only
        /// carries the in-orbit capture/parking conics up to <paramref name="seamUT"/>).
        ///
        /// <para>A member is selected iff its window starts at/after <paramref name="seamUT"/> (the transfer
        /// member's last target-body conic end == the destination-arrival boundary, within
        /// <paramref name="epsSeconds"/>) AND its body is <paramref name="targetBody"/>. The transfer member
        /// (starts at the launch, far before the seam) and the launch / heliocentric members are all excluded
        /// by the seam gate; the body gate rejects any post-seam member that is not on the destination. Returns
        /// the selected committed indices in ascending committed-index order (a stable, contiguous set for the
        /// common single-chain case). Empty (=&gt; trigger OFF, byte-identical) when nothing qualifies or
        /// <paramref name="seamUT"/> / <paramref name="targetBody"/> is degenerate. Pure; no Unity.</para>
        /// </summary>
        internal static int[] SelectDescentMemberIndices(
            IReadOnlyList<MemberArrivalInfo> members, double seamUT, string targetBody, double epsSeconds)
        {
            if (members == null || members.Count == 0
                || double.IsNaN(seamUT) || double.IsInfinity(seamUT) || string.IsNullOrEmpty(targetBody))
                return System.Array.Empty<int>();

            var selected = new List<int>();
            for (int m = 0; m < members.Count; m++)
            {
                MemberArrivalInfo info = members[m];
                if (double.IsNaN(info.StartUT))
                    continue;
                if (info.StartUT >= seamUT - epsSeconds
                    && string.Equals(info.Body, targetBody, System.StringComparison.Ordinal))
                    selected.Add(info.CommittedIndex);
            }
            selected.Sort();
            return selected.ToArray();
        }

        /// <summary>
        /// PURE per-member resolution for a descent-set member under the multi-member shared trigger: computes
        /// the shared descent head via <see cref="ComputeDescentMemberHead"/> (member-agnostic) and dispatches
        /// it to THIS member by window. A descent member renders ONLY during the <see cref="DescentHeadPhase.Descent"/>
        /// phase AND only the slice of the (single, monotone) descent clip whose head falls inside its own
        /// <c>[memberStartUT, memberEndUT]</c> window — so the chain members #N, #N+1, … each carry their own
        /// contiguous portion of the clip, exactly as they play un-looped, with no tearing at the seams.
        ///
        /// <para>In every other phase (Inert before the icon reaches the deorbit point, Loiter while the icon
        /// circles the transfer member's conic, Done after the clip ends) the descent member is HIDDEN — it
        /// NEVER renders on the raw loop clock (that is the wrong-rotation bug). Returns true (with
        /// <paramref name="head"/> set) when this member should render at the re-anchored head; false (head NaN)
        /// to hide it this frame. Pure; no Unity.</para>
        /// </summary>
        internal static bool TryResolveDescentMemberHead(
            double currentUT,
            long cycleIndex,
            double phaseAnchorUT,
            double cadenceSeconds,
            double spanStartUT,
            double recordedDeorbitUT,
            double descentEndUT,
            double rotationPeriod,
            double parkingPeriod,
            double captureShiftSeconds,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> loiterCuts,
            double memberStartUT,
            double memberEndUT,
            out double head,
            out DescentHeadPhase phase)
        {
            phase = ComputeDescentMemberHead(
                currentUT, cycleIndex, phaseAnchorUT, cadenceSeconds, spanStartUT,
                recordedDeorbitUT, descentEndUT, rotationPeriod, parkingPeriod, captureShiftSeconds,
                loiterCuts, out double sharedHead);

            // Doubly-inclusive epsilon window, matching the normal-path IsLoopUTInMemberWindow contract. At an
            // exact-abutting or slightly-overlapping chain seam (real recordings can overlap ~0.02 s) the head can
            // momentarily satisfy two adjacent members at once, rendering both for one frame. That is accepted:
            // the members are a continuous chain so both draw the SAME world position (same head UT, abutting
            // recordings), so it reads as one ghost, not two; a clean per-member tiling would need each member to
            // know its neighbours' windows, which this per-member seam deliberately does not. The builder declines
            // to engage on a REAL gap (review B1), so the only residual is this harmless same-position seam frame.
            if (phase == DescentHeadPhase.Descent
                && sharedHead >= memberStartUT - LoopTiming.BoundaryEpsilon
                && sharedHead <= memberEndUT + LoopTiming.BoundaryEpsilon)
            {
                head = sharedHead;
                return true;
            }

            head = double.NaN;
            return false;
        }
    }
}
