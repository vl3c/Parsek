using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Display;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 2 (design §6 / migration plan §4): builds a <see cref="PhaseChain"/>
    /// (<c>TrajectoryPhase[]</c>) from the SAME inputs <see cref="ChainAssembler.Build"/> consumes, and
    /// runs in SHADOW behind the <c>mapRenderTracing</c> setting. It does NOT drive any draw — a gated
    /// comparator (<see cref="GeometryParityComparator"/>) asserts byte-parity of the GEOMETRY fields
    /// against the assembler's <see cref="GhostRenderChain"/>, emitting the <c>factory-parity</c>
    /// Tier-C anomaly on a mismatch.
    ///
    /// <para><b>Geometry source.</b> The geometry fields (<c>Treatment</c>, <c>StartUt</c>, <c>EndUt</c>,
    /// <c>FrameBodyName</c>, conic payload, and the chain-level window + faithful-fallback) are produced
    /// by reusing the assembler's canonical geometry decision: the factory calls
    /// <see cref="ChainAssembler.Build"/> to obtain the ordered <see cref="RenderSegment"/>s, then wraps
    /// each one in a typed <see cref="TrajectoryPhase"/>. This guarantees geometry parity BY CONSTRUCTION
    /// (the factory never re-derives the orbit-vs-traced split — the one place the design wants that
    /// decision is the assembler). The NEW, independent work the factory adds is the CLASSIFICATION:
    /// which phase subclass / <see cref="PhaseKind"/> / <see cref="SegmentProvenance"/> /
    /// <see cref="AnchorFrame"/> each segment becomes. Those are NOT in the parity set (validated by the
    /// Phase-1 unit tests), so a wrong classification never silently corrupts geometry — and the
    /// comparator still gates the SHADOW hook because it proves the typed phases project (via
    /// <see cref="TrajectoryPhase.Emit"/>) losslessly back to the assembler's geometry. When Phase 3
    /// swaps the spine to consume phases, the factory becomes the geometry SOURCE and this comparator is
    /// the regression gate.</para>
    ///
    /// <para><b>Faithful vs re-aimed identity split (design §6 / §7).</b> The classification of leaf
    /// identity differs by member:
    /// <list type="bullet">
    ///   <item>FAITHFUL members: leaf phase identity comes from
    ///     <see cref="SegmentPhaseClassifier.EnvironmentToPhase"/> +
    ///     <see cref="RecordingOptimizer.SplitEnvironmentClass"/> (read off the recorded
    ///     <see cref="TrackSection"/>s), NOT the re-aim plan.</item>
    ///   <item>RE-AIMED members (a non-null <paramref name="orbitSegmentsOverride"/> that differs from the
    ///     recorded list, mirroring <c>ShadowRenderDriver.GetOrBuildChain</c>'s reference-inequality
    ///     signal): the heliocentric transfer leg is <see cref="SegmentProvenance.Synthesized"/> from the
    ///     <see cref="Parsek.Reaim.ReaimMissionPlan"/>; the heliocentric-park variant becomes a
    ///     synthesized <see cref="DepartureLoiterPhase"/>.</item>
    /// </list></para>
    ///
    /// <para><b>Edge cases the factory tolerates (design §11.3):</b> a BG-on-rails member emits no
    /// env-classified <see cref="TrackSection"/>s, so the faithful path falls through to an all-orbital
    /// chain (Loiter/Transfer conics + FlexibleSoi at body changes) with NO Descent/Surface phase, never
    /// asserting on absent <c>SegmentPhase</c> data; a single-recording empty-<c>Points</c> trajectory
    /// produces a conic-only chain; a null trajectory produces an empty chain.</para>
    /// </summary>
    internal static class PhaseFactory
    {
        /// <summary>
        /// Build the typed <see cref="PhaseChain"/> for one committed member + cycle instance from the
        /// same inputs as <see cref="ChainAssembler.Build"/>. <paramref name="orbitSegmentsOverride"/>
        /// non-null + reference-distinct from <c>traj.OrbitSegments</c> marks the member RE-AIMED (the
        /// heliocentric transfer leg is synthesized); null keeps the FAITHFUL classification.
        /// </summary>
        internal static PhaseChain BuildPhaseChain(
            IPlaybackTrajectory traj,
            int committedIndex,
            int instanceKey,
            double windowStartUT,
            double windowEndUT,
            bool faithfulFallback = false,
            GhostTrajectoryPolylineRenderer.BodySurfaceProvider surface = null,
            IReadOnlyList<OrbitSegment> orbitSegmentsOverride = null,
            string reaimAncestorBody = null)
        {
            if (traj == null)
            {
                return new PhaseChain(
                    null, committedIndex, instanceKey, Array.Empty<TrajectoryPhase>(),
                    windowStartUT, windowEndUT, faithfulFallback);
            }

            // Loud-assertion carry-forward (RenderSegment.cs:94-98 / AnchorFrame.cs): a parent-anchored
            // child is NEVER handed a re-aimed/generated segment list. Keep that failure loud rather than
            // silently body-framing it, so the §11.3 transfer-leg-debris fail-closed (Phase 7) degrades
            // loudly. The factory/PlaybackResolver already skips IsDebris (it never reaches here), but a
            // controlled-decoupled child (IsDebris=false, ParentAnchorRecordingId!=null) DOES render, so
            // guard it explicitly.
            bool isReaimedMember =
                orbitSegmentsOverride != null
                && !ReferenceEquals(orbitSegmentsOverride, traj.OrbitSegments);
            if (isReaimedMember && !string.IsNullOrEmpty(traj.ParentAnchorRecordingId))
            {
                ParsekLog.Warn("MapRender", string.Format(CultureInfo.InvariantCulture,
                    "PhaseFactory: parent-anchored child rec={0} handed a re-aimed segment override " +
                    "(parent={1}) — refusing to body-frame a generated arc (design §6 loud-assertion); " +
                    "building faithful chain instead",
                    traj.RecordingId ?? "?", traj.ParentAnchorRecordingId));
                isReaimedMember = false;
                orbitSegmentsOverride = null;
                reaimAncestorBody = null;
            }

            // GEOMETRY SOURCE: reuse the assembler's canonical orbit-vs-traced decision verbatim. This is
            // the one place the orbit/traced split lives; the factory never re-derives it (byte-parity by
            // construction). The classification below is the NEW, independent layer.
            GhostRenderChain geometry = ChainAssembler.Build(
                traj, committedIndex, instanceKey, windowStartUT, windowEndUT,
                faithfulFallback, surface, orbitSegmentsOverride, reaimAncestorBody);

            // Resolve the faithful env-class phase tokens once (per body run) so the leaf identity of a
            // FAITHFUL traced/conic segment comes from EnvironmentToPhase + SplitEnvironmentClass, not the
            // re-aim plan. BG-on-rails: TrackSections carry no env-class runs, so this resolves empty and
            // every traced/conic segment falls to its geometry-default kind WITHOUT asserting.
            var phases = new List<TrajectoryPhase>(geometry.SegmentCount);
            for (int i = 0; i < geometry.SegmentCount; i++)
            {
                RenderSegment seg = geometry.Segments[i];
                phases.Add(ClassifySegment(
                    seg, i, traj, instanceKey, isReaimedMember, reaimAncestorBody));
            }

            ParsekLog.Verbose("MapRender", string.Format(CultureInfo.InvariantCulture,
                "factory chain rec={0} idx={1} inst={2} phases={3} reaimed={4} window=[{5:F1},{6:F1}] faithfulFallback={7}",
                traj.RecordingId ?? "?", committedIndex, instanceKey, phases.Count, isReaimedMember,
                windowStartUT, windowEndUT, faithfulFallback));

            // Phase 7 (migration plan §9 / design §9.2 / §10): the FAIL-CLOSED decision site. Decide whether
            // this member is one of the three UNSUPPORTED synthetic producers (nested-SOI Jool tour /
            // moving-target station / cross-SOI whole-chain synthesis) and, if so, emit the Tier-A
            // fail-closed-to-faithful structural event naming the unsupported producer. DEFINE-ONLY +
            // GEOMETRY-NEUTRAL: the decision changes nothing about the geometry built above (the three
            // producers have no synthetic implementation in v1, so "fail-closed to faithful" is exactly
            // what the pipeline already does — the recorded trajectory renders verbatim, the cross-SOI kink
            // renders the current FlexibleSoi G0 behavior unchanged). The whole block is gated on
            // MapRenderTrace.IsEnabled, so flag-OFF / tracing-OFF normal play pays nothing AND never touches
            // the live Unity body-info resolver (keeping the headless factory tests pure). The body parent
            // chain comes from the live FlightGlobalsBodyInfo only here, behind the gate.
            EmitFailClosedDecisionTraceIfEnabled(traj, committedIndex, isReaimedMember);

            return new PhaseChain(
                traj.RecordingId, committedIndex, instanceKey, phases,
                geometry.WindowStartUT, geometry.WindowEndUT, geometry.IsFaithfulFallback);
        }

        /// <summary>
        /// Phase 7: the live fail-closed decision + Tier-A trace emit, gated ON TRACING (free in normal
        /// play). Builds the recorded body sequence (pure), resolves the parent chain from the live
        /// <see cref="FlightGlobalsBodyInfo"/> (a top-level <c>Parsek</c> type; a Unity read — only ever reached here
        /// under the gate, so the headless factory tests never invoke it), runs the pure
        /// <see cref="FailClosedClassifier.Classify"/>, and emits the once-per-event
        /// <c>fail-closed-to-faithful</c> structural event when the member is unsupported.
        ///
        /// <para>The v1 live path NEVER signals a <c>LiveVesselAnchor</c> arrival (the moving-target
        /// producer is deferred and no faithful trajectory resolves one), so the station case is reachable
        /// only through the explicit <see cref="FailClosedClassifier.Classify"/> seam the unit tests drive;
        /// the live path detects the nested-SOI (Jool) case from the recorded body sequence. The whole
        /// helper is geometry-neutral: it reads the already-built chain inputs and emits a log line, never
        /// mutating geometry.</para>
        /// </summary>
        private static void EmitFailClosedDecisionTraceIfEnabled(
            IPlaybackTrajectory traj, int committedIndex, bool isReaimedMember)
        {
            if (!MapRenderTrace.IsEnabled || traj == null)
                return;

            // A re-aimed member that solved successfully is, by definition, a SUPPORTED producer this
            // window (the heliocentric leg re-aimed) — fail-closed is the DECLINE outcome, so a re-aimed
            // member is not the fail-closed subject. (A declined window arrives here as faithful, which is
            // exactly where the nested-SOI / station fail-closed lives.)
            if (isReaimedMember)
                return;

            System.Collections.Generic.List<string> bodies = BuildOrderedRecordedBodies(traj);
            FailClosedClassifier.FailClosedDecision decision = FailClosedClassifier.Classify(
                bodies,
                hasLiveVesselArrivalAnchor: false,
                referenceBodyName: FlightGlobalsBodyInfo.Instance.ReferenceBodyName);

            if (!decision.IsFailClosed)
                return;

            FailClosedClassifier.EmitFailClosedToFaithful(
                traj.RecordingId, committedIndex, GhostMapPresence.CurrentUTNow(), decision);
        }

        /// <summary>
        /// PURE: the ordered recorded body sequence the fail-closed classifier reads, taken from the
        /// recorded <see cref="OrbitSegment"/>s in time order (the orbital body sequence is the SOI
        /// hierarchy the cross-SOI / nested-SOI decision walks). Adjacent duplicate bodies collapse to one
        /// run; the OrbitSegment list is the authoritative body sequence (a traced atmospheric run rides
        /// its body's conic). Tolerates a null / empty orbit list (returns an empty list — a no-orbit
        /// recording is never a multi-body tour). No Unity reads.
        /// </summary>
        internal static System.Collections.Generic.List<string> BuildOrderedRecordedBodies(
            IPlaybackTrajectory traj)
        {
            var bodies = new System.Collections.Generic.List<string>();
            var orbits = traj?.OrbitSegments;
            if (orbits == null)
                return bodies;

            // OrbitSegments are stored in time order; walk them and append each body, collapsing adjacent
            // duplicates so a multi-orbit stay at one body is a single run (the body CHANGES are the SOI
            // crossings the classifier counts).
            string last = null;
            for (int i = 0; i < orbits.Count; i++)
            {
                string b = orbits[i].bodyName;
                if (string.IsNullOrEmpty(b))
                    continue;
                if (string.Equals(b, last, StringComparison.Ordinal))
                    continue;
                bodies.Add(b);
                last = b;
            }
            return bodies;
        }

        /// <summary>
        /// Classify ONE geometry <see cref="RenderSegment"/> into a typed <see cref="TrajectoryPhase"/>.
        /// The geometry fields (treatment / UTs / body / conic) are carried through unchanged so the
        /// phase's <see cref="TrajectoryPhase.Emit"/> projects back to the same geometry. The NEW work is
        /// the kind / provenance / anchor choice (NOT in the parity set).
        ///
        /// <para>Pure given the segment + classification flags (no Unity / scene reads), so it is unit
        /// testable. The <paramref name="reaimAncestorBody"/> is the re-aim plan's common-ancestor (star)
        /// body used to mark the synthesized heliocentric transfer.</para>
        /// </summary>
        internal static TrajectoryPhase ClassifySegment(
            RenderSegment seg,
            int ordinal,
            IPlaybackTrajectory traj,
            int instanceKey,
            bool isReaimedMember,
            string reaimAncestorBody)
        {
            var id = new PhaseId(traj?.RecordingId, instanceKey, ordinal);
            var anchor = ResolveAnchorFrame(seg, traj);
            SegmentProvenance provenance = ResolveProvenance(seg, isReaimedMember, reaimAncestorBody);

            // NOTE: every phase below is built with default (null) leading/trailing PhaseSeams. Seams are
            // DELIBERATELY OUTSIDE the Phase-2 byte-parity field set (GeometryParityComparator checks
            // Treatment / UTs / FrameBodyName / conic payload, NOT seams); a null PhaseSeam projects to
            // SeamKind.None via Emit, which does not match the assembler's Rigid/FlexibleSoi seam stamping,
            // but that is intentional. Seam reproduction is a Phase-3 concern (the spine consumes phases and
            // re-derives seams there), not a Phase-2 regression.

            if (seg.Treatment == Treatment.TracedPath)
            {
                // Faithful leaf identity from env-class: a SURFACE run is a SurfacePhase, an ASCENT-shaped
                // atmospheric run is an AscentPhase, a DESCENT-shaped one is a DescentPhase. The geometry
                // carries no env token, so classify by the env-class phase of the segment's body run.
                //
                // Carry the assembler-stamped GEOMETRY body name (seg.FrameBodyName) onto the traced phase
                // so Emit reproduces it losslessly for ANY anchor. A ParentAnchoredChild anchor has no
                // BodyAnchor payload, so without this the traced leg's FrameBodyName would project as null
                // (ctx fallback) while the assembler stamped the real recorded body -> factory-parity
                // FALSE-fire on a correct factory. The body name is geometry, not the anchor.
                PhaseKind tracedKind = ClassifyTracedKind(seg, traj);
                switch (tracedKind)
                {
                    case PhaseKind.Surface:
                        return new SurfacePhase(id, provenance, anchor, seg.StartUT, seg.EndUT, seg.FrameBodyName);
                    case PhaseKind.Descent:
                        return new DescentPhase(id, provenance, anchor, seg.StartUT, seg.EndUT, seg.FrameBodyName);
                    default:
                        return new AscentPhase(id, provenance, anchor, seg.StartUT, seg.EndUT, seg.FrameBodyName);
                }
            }

            // StockConic: a generated transfer is the heliocentric transfer leg; a recorded conic is a
            // departure or arrival loiter (the departure-vs-arrival split is NEW, from the conic's role).
            OrbitSegment conic = seg.Payload.HasConic ? seg.Payload.Conic : default(OrbitSegment);
            if (seg.IsGenerated)
            {
                return new HeliocentricTransferPhase(
                    id, provenance, anchor, seg.StartUT, seg.EndUT, conic);
            }

            // Heliocentric-park variant: a re-aimed member whose conic is the common-ancestor (star) body
            // but is NOT the generated transfer is the recorded park copy (LAN-re-phased upstream), a
            // SYNTHESIZED DepartureLoiterPhase (design §6: s15).
            if (isReaimedMember
                && !string.IsNullOrEmpty(reaimAncestorBody)
                && string.Equals(seg.FrameBodyName, reaimAncestorBody, StringComparison.Ordinal))
            {
                return new DepartureLoiterPhase(
                    id, SegmentProvenance.Synthesized, anchor, seg.StartUT, seg.EndUT, conic);
            }

            // A recorded conic: departure loiter (before the transfer) vs arrival loiter (after). The
            // geometry-only split uses the segment's body relative to the transfer ancestor: a conic on the
            // re-aim ancestor frame is heliocentric; a conic on a non-launch body after a star-frame
            // crossing is arrival. Without per-leg role data the v1 default is DepartureLoiter (the legacy
            // SegmentKind.Loiter), matching the assembler's role-blind Loiter; arrival loiter is resolved
            // when the env-class / transfer ordinal says so.
            if (IsArrivalConic(seg, ordinal, traj))
            {
                return new ArrivalLoiterPhase(id, provenance, anchor, seg.StartUT, seg.EndUT, conic);
            }
            return new DepartureLoiterPhase(id, provenance, anchor, seg.StartUT, seg.EndUT, conic);
        }

        // Resolve the anchor frame for a segment. v1: a BodyAnchor on the frame body for ordinary
        // members; a ParentAnchoredChild for a controlled-decoupled child (IsDebris=false,
        // ParentAnchorRecordingId!=null). The re-aimed-override case never reaches here for a
        // parent-anchored child (BuildPhaseChain strips it loudly above).
        internal static AnchorFrame ResolveAnchorFrame(RenderSegment seg, IPlaybackTrajectory traj)
        {
            if (traj != null && !string.IsNullOrEmpty(traj.ParentAnchorRecordingId))
                return new AnchorFrame.ParentAnchoredChild(traj.ParentAnchorRecordingId);
            return new AnchorFrame.BodyAnchor(seg.FrameBodyName);
        }

        // Provenance: a generated transfer is Synthesized; a faithful-fallback chain's segments are
        // FaithfulFallback; an isPredicted conic tail is FinalizedPredicted; everything else Recorded.
        // (Provenance is NOT a parity field; it is validated by the Phase-1 unit tests.)
        internal static SegmentProvenance ResolveProvenance(
            RenderSegment seg, bool isReaimedMember, string reaimAncestorBody)
        {
            if (seg.IsGenerated)
                return SegmentProvenance.Synthesized;
            if (seg.Treatment == Treatment.StockConic && seg.Payload.HasConic && seg.Payload.Conic.isPredicted)
                return SegmentProvenance.FinalizedPredicted;
            return SegmentProvenance.Recorded;
        }

        // Classify a TracedPath segment's gameplay kind from the recorded env-class (design §6: faithful
        // leaf identity is EnvironmentToPhase + SplitEnvironmentClass, NOT the re-aim plan). Reads the
        // recorded TrackSections overlapping the segment window; a "surface" env => Surface, otherwise
        // ascent-vs-descent by the segment's position relative to the recording's first conic (a traced
        // run BEFORE the first orbit is ascent; one AFTER is descent). BG-on-rails has no env-class runs,
        // so this returns the geometry default (Ascent) WITHOUT asserting — but BG-on-rails has no traced
        // segments anyway (orbit-bridge-only), so that default is never observed there.
        internal static PhaseKind ClassifyTracedKind(RenderSegment seg, IPlaybackTrajectory traj)
        {
            string env = ResolveEnvPhaseForWindow(traj, seg.StartUT, seg.EndUT);
            if (string.Equals(env, "surface", StringComparison.Ordinal))
                return PhaseKind.Surface;

            // Ascent vs descent: a traced run that starts before the first above-surface conic is ascent;
            // one that starts at/after it is descent (the deorbit/landing leg). With no conic at all
            // (pure atmospheric recording) the v1 default is Ascent (matches the assembler's role-blind
            // Surface kind, which is geometry-irrelevant).
            double firstConicStart = FirstConicStartUT(traj);
            if (!double.IsNaN(firstConicStart) && seg.StartUT >= firstConicStart)
                return PhaseKind.Descent;
            return PhaseKind.Ascent;
        }

        // Read the recorded env-class phase token (atmo / surface / approach / exo) for the section
        // overlapping [startUT, endUT]. Tolerates a null/empty TrackSection list (BG-on-rails) by
        // returning null. Uses the LAST overlapping section's environment so a multi-section run resolves
        // to its terminal class (a descent run ending in atmo/surface reads surface).
        internal static string ResolveEnvPhaseForWindow(
            IPlaybackTrajectory traj, double startUT, double endUT)
        {
            var sections = traj?.TrackSections;
            if (sections == null || sections.Count == 0)
                return null;

            string phase = null;
            for (int i = 0; i < sections.Count; i++)
            {
                TrackSection s = sections[i];
                // A section overlaps the window if it intersects [startUT, endUT].
                if (s.endUT < startUT || s.startUT > endUT)
                    continue;
                phase = SegmentPhaseClassifier.EnvironmentToPhase(s.environment);
            }
            return phase;
        }

        // The UT of the recording's first above-surface conic (StockConic-eligible orbit), or NaN when
        // there is none. Used to split ascent (before) vs descent (after) on the traced legs.
        private static double FirstConicStartUT(IPlaybackTrajectory traj)
        {
            var orbits = traj?.OrbitSegments;
            if (orbits == null) return double.NaN;
            double best = double.NaN;
            for (int i = 0; i < orbits.Count; i++)
            {
                OrbitSegment o = orbits[i];
                if (o.endUT <= o.startUT) continue;
                if (double.IsNaN(best) || o.startUT < best) best = o.startUT;
            }
            return best;
        }

        // A recorded conic is an ARRIVAL loiter when its env-class is approach OR it is the LAST conic in
        // the chain on a body different from the recording's first conic body (the destination park). The
        // v1 default is DepartureLoiter (legacy role-blind Loiter), so this only promotes the clear
        // arrival case; the geometry is identical either way (kind is non-parity).
        internal static bool IsArrivalConic(RenderSegment seg, int ordinal, IPlaybackTrajectory traj)
        {
            string env = ResolveEnvPhaseForWindow(traj, seg.StartUT, seg.EndUT);
            if (string.Equals(env, "approach", StringComparison.Ordinal))
                return true;

            var orbits = traj?.OrbitSegments;
            if (orbits == null || orbits.Count == 0)
                return false;

            // The first conic's body is the departure body; a conic on a different body that is the last
            // conic in time is the arrival park.
            string firstBody = null;
            double firstStart = double.NaN;
            double lastStart = double.NaN;
            for (int i = 0; i < orbits.Count; i++)
            {
                OrbitSegment o = orbits[i];
                if (o.endUT <= o.startUT) continue;
                if (double.IsNaN(firstStart) || o.startUT < firstStart)
                {
                    firstStart = o.startUT;
                    firstBody = o.bodyName;
                }
                if (double.IsNaN(lastStart) || o.startUT > lastStart)
                    lastStart = o.startUT;
            }
            bool differentBody =
                !string.IsNullOrEmpty(firstBody)
                && !string.Equals(seg.FrameBodyName, firstBody, StringComparison.Ordinal);
            bool isLastInTime = !double.IsNaN(lastStart) && seg.StartUT >= lastStart;
            return differentBody && isLastInTime;
        }
    }
}
