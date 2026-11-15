using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Effective map orbit segments, orbit-renderer repair, per-frame line coverage accounting
    // and visible orbit bounds.
    internal static partial class GhostMapPresence
    {
        // Resolves the OrbitSegment list the map orbit line / icon should follow for a committed
        // recording this frame: for a re-aim loop unit member that carries a heliocentric leg, the
        // per-window list with that leg RE-AIMED; otherwise the recorded segments. Applies to ANY member
        // of a re-aim unit (the resolver's heliocentric pre-check leaves launch / arrival / debris
        // members on the faithful path, since only the inertial-fixed heliocentric leg needs re-aiming).
        // The window is mapped from the LIVE <paramref name="liveCurrentUT"/> via the shared resolver
        // (the SAME window the flight engine uses), and the returned list is in recorded-span time, so
        // the caller searches it at the recorded-span effUT exactly as it would the recorded list.
        // Recording overload (tracking-station path).
        internal static List<OrbitSegment> ResolveEffectiveMapOrbitSegments(
            int committedIndex, Recording rec, double liveCurrentUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits)
            => ResolveEffectiveMapOrbitSegments(
                committedIndex,
                rec != null ? rec.RecordingId : null,
                rec != null ? rec.OrbitSegments : null,
                liveCurrentUT,
                loopUnits);

        // Raw-pieces overload so the FLIGHT map path (ParsekPlaybackPolicy, which holds an
        // IPlaybackTrajectory, not a Recording) resolves the SAME re-aimed window list the tracking
        // station does. Both flight create + per-frame refresh and the tracking-station refresh must
        // draw the re-aimed transfer; the flight path previously read the recorded segments directly,
        // so its map orbit line pointed at the target's RECORDED position (wrong place in the target's
        // orbit). Returns the recorded list unchanged for every non-re-aim member / faithful window.
        internal static List<OrbitSegment> ResolveEffectiveMapOrbitSegments(
            int committedIndex, string recordingId, List<OrbitSegment> recorded,
            double liveCurrentUT, GhostPlaybackLogic.LoopUnitSet loopUnits)
            => ResolveEffectiveMapOrbitSegments(
                committedIndex, recordingId, recorded, liveCurrentUT, loopUnits, out long _);

        // Window-exposing overload (Phase 8d re-aim Director wiring): forwards the resolver's synodic
        // WINDOW INDEX so the MapRender ShadowRenderDriver can key its chain cache on it (a window advance
        // changes the re-aimed geometry without changing the RECORDED OrbitSegments.Count, so the window is
        // the load-bearing cache discriminator). Reference-equality contract preserved exactly: a non-re-aim
        // member / declined window / pre-first-window returns the RECORDED list UNCHANGED (Same reference)
        // with windowIndex = -1 (the caller's ReferenceEquals(effective, recorded) test at :6260 and the
        // ShadowRenderDriver's chainHasReaimedSegments flag both rely on this). A re-aimed window returns the
        // synthesized list with windowIndex set to the resolver's cycle index.
        internal static List<OrbitSegment> ResolveEffectiveMapOrbitSegments(
            int committedIndex, string recordingId, List<OrbitSegment> recorded,
            double liveCurrentUT, GhostPlaybackLogic.LoopUnitSet loopUnits, out long windowIndex)
        {
            windowIndex = -1;
            if (string.IsNullOrEmpty(recordingId) || loopUnits == null)
                return recorded;
            if (!loopUnits.TryGetUnitForMember(committedIndex, out GhostPlaybackLogic.LoopUnit unit))
                return recorded;
            if (!unit.IsReaim)
                return recorded;
            if (Parsek.Reaim.ReaimPlaybackResolver.Shared.TryResolveWindowSegments(
                    recordingId, recorded, unit.ReaimPlan.Value, unit.ReaimSchedule.Value,
                    unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                    liveCurrentUT, out List<OrbitSegment> reaimed, out long resolvedWindow))
            {
                windowIndex = resolvedWindow;
                LogMapEffectiveSegments(committedIndex, "RE-AIMED", reaimed, unit.ReaimPlan.Value.CommonAncestor);
                return reaimed;
            }
            LogMapEffectiveSegments(committedIndex, "RECORDED-resolver-miss", recorded, unit.ReaimPlan.Value.CommonAncestor);
            return recorded; // no heliocentric leg / window miss / pre-first-window -> faithful (windowIndex = -1)
        }

        // Re-aim covering-segment substitution for the FLIGHT create paths: when a Segment-source map
        // ghost is about to be created for a re-aim owner, swap the recorded covering segment for the
        // re-aimed one (transfer elements aimed at the target's CURRENT position, trimmed to the
        // interplanetary span). Returns FALSE (and the caller must NOT create the ghost) when the member
        // is a re-aim owner whose re-aimed list has NO covering segment at <paramref name="sampleUT"/> -
        // a TRIM GAP between a recorded body-relative leg (escape / capture) and the trimmed transfer, or
        // a window the resolver declined. Falling back to the recorded segment there (the old behavior)
        // re-created the ghost from the recorded sub-segments at random orbit positions while the
        // per-frame refresh kept removing it (the create/destroy icon flicker at the SOI boundaries).
        // Returns TRUE with the caller's recorded segment unchanged for a non-re-aim member / faithful
        // window (effective list reference-identical to the recorded one). <paramref name="liveCurrentUT"/>
        // maps the synodic window; <paramref name="sampleUT"/> is the recorded-span effUT searched at.
        internal static bool TryResolveReaimedCoveringSegment(
            int committedIndex, string recordingId, List<OrbitSegment> recorded,
            double liveCurrentUT, double sampleUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits, OrbitSegment recordedSegment,
            out OrbitSegment segment)
        {
            List<OrbitSegment> effective = ResolveEffectiveMapOrbitSegments(
                committedIndex, recordingId, recorded, liveCurrentUT, loopUnits);
            if (effective == null || ReferenceEquals(effective, recorded))
            {
                segment = recordedSegment; // non-re-aim / faithful window: keep the recorded segment
                return true;
            }
            OrbitSegment? coveringReaimed = TrajectoryMath.FindOrbitSegmentForMapDisplay(effective, sampleUT);
            if (coveringReaimed.HasValue)
            {
                segment = coveringReaimed.Value;
                return true;
            }
            segment = recordedSegment;
            return false; // re-aim owner in a trim gap -> caller keeps the ghost hidden (no create)
        }

        // Diagnostic: log whether the map got the RE-AIMED or RECORDED segments, plus the heliocentric
        // (common-ancestor) leg's sma + count, so a map line still on the recorded transfer (wrong place in
        // the target's orbit) is caught. The re-aimed list has ONE synthesized common-ancestor segment; the
        // recorded list has the original (typically several). Verbose, rate-limited per member.
        private static void LogMapEffectiveSegments(int idx, string kind, List<OrbitSegment> segs, string ancestor)
        {
            if (segs == null)
                return;
            int helioCount = 0;
            double firstHelioSma = double.NaN;
            for (int i = 0; i < segs.Count; i++)
            {
                if (segs[i].bodyName == ancestor)
                {
                    if (helioCount == 0) firstHelioSma = segs[i].semiMajorAxis;
                    helioCount++;
                }
            }
            ParsekLog.VerboseRateLimited("ReaimSeam", "map-eff-" + idx.ToString(ic),
                string.Format(ic, "map effective segments: {0} member={1} segs={2} helioSegs={3} firstHelioSma={4:F0}",
                    kind, idx, segs.Count, helioCount, firstHelioSma), 2.0);
        }

        // MAP/TS TRAJECTORY DIAGNOSTIC: the covering OrbitSegment the map icon + orbit line follow for this
        // member THIS frame, logged ON CHANGE (keyed per member+scene) so the reader sees discrete transition
        // events rather than per-frame spam. This is the chokepoint that decides "inside Kerbin SOI" (a
        // Kerbin-bodied segment) vs "heliocentric" (the Sun-bodied transfer). It catches: the SOI-exit blink
        // (body flip-flops Kerbin<->Sun back and forth => rapid alternating lines), the orbit line going dark
        // (covered -> GAP(no-segment)), and the Segment<->StateVector route flip. A reader greps tag MapTraj
        // for one member to see the exact sequence of what the map drew. <paramref name="scene"/> = "TS" or
        // "FLIGHT". Pairs with the gated MapRenderProbe (which subsumed the old icon-pos-delta stall
        // diagnostic) and the existing "SOI change" orbit-line log.
        internal static void LogMapCoveringSegmentChange(
            string scene, int idx, double effUT, OrbitSegment? coveringSegment,
            bool segmentCoversEffUT, bool isStateVector, int effectiveSegmentCount)
        {
            // With verbose off, the 3-4 string.Formats below are pure waste per reseeded index —
            // and the flight reseed now runs up to once per frame at extreme warp (warp-scaled
            // cadence). Skipping also skips the on-change registration: on enabling verbose
            // mid-scene the next call registers as a change and emits once, which is fine for a
            // transition diagnostic.
            if (!ParsekLog.IsVerboseEnabled)
                return;
            string coveringBody = coveringSegment.HasValue
                ? (coveringSegment.Value.bodyName ?? "(null)")
                : "GAP(no-segment)";
            string source = isStateVector ? "StateVector" : "Segment";
            string segSpan = coveringSegment.HasValue
                ? string.Format(ic, "[{0:F1},{1:F1}] sma={2:F0} ecc={3:F3}",
                    coveringSegment.Value.startUT, coveringSegment.Value.endUT,
                    coveringSegment.Value.semiMajorAxis, coveringSegment.Value.eccentricity)
                : "n/a";
            // Include the covering segment's start UT (coarse, whole-second) in the on-change key so a
            // same-body segment->segment advance (e.g. several consecutive Kerbin-frame segments, or a
            // same-body carry stepping to the next segment) registers as a CHANGE. Without it the key
            // body|covered|source stays constant across same-body advances and the transition - the exact
            // thing this diagnostic exists to trace - would never log. Segment startUT is a fixed per-segment
            // value, so F0 introduces no float-jitter churn.
            string segKey = coveringSegment.HasValue
                ? coveringSegment.Value.startUT.ToString("F0", ic)
                : "none";
            ParsekLog.VerboseOnChange("MapTraj",
                string.Format(ic, "covering-{0}-{1}", scene, idx),
                string.Format(ic, "{0}|{1}|{2}|{3}", coveringBody, segmentCoversEffUT, source, segKey),
                string.Format(ic,
                    "{0} covering-segment CHANGED: member={1} effUT={2:F1} body={3} covered={4} source={5} " +
                    "effSegs={6} seg={7}",
                    scene, idx, effUT, coveringBody, segmentCoversEffUT, source, effectiveSegmentCount, segSpan));
        }

        /// <summary>
        /// Ensure all tracked ghost vessels have mapObject and orbitRenderer.
        /// During SpaceTracking.Awake prefix, MapView.fetch may not be initialized yet
        /// (Unity doesn't guarantee Awake ordering), causing Vessel.AddOrbitRenderer()
        /// to silently skip creation. This method calls AddOrbitRenderer via Traverse
        /// on ghosts missing their renderer. Must be called after all Awake methods
        /// complete (e.g., from a buildVesselsList Prefix or from Start). (#195)
        ///
        /// <para>The repair is skipped while the application is quitting: Unity's quit
        /// teardown runs every vessel's <c>OnDestroy</c>, which fires stock
        /// <c>onVesselDestroy</c>, SpaceTracking rebuilds its list, and the buildVesselsList
        /// Prefix lands here for ghosts that are themselves being destroyed. Rebuilding their
        /// MapObject then throws inside stock (<c>MapObject.Awake</c> /
        /// <c>FlightGlobals.ActiveVessel</c>) with Parsek frames on the stack, and the
        /// repaired renderer would not outlive the teardown anyway. The quit latch is what
        /// covers that observed NRE. The repair is also skipped while
        /// <see cref="RemoveAllGhostVessels"/>'s Die loop is on the stack, which guards only
        /// synchronous re-entry during the loop: <c>Die()</c> defers the destroy, so the
        /// <c>onVesselDestroy</c> rebuilds it causes run after the loop, outside that scope
        /// (see <see cref="removeAllGhostVesselsDepth"/>).</para>
        /// </summary>
        internal static int EnsureGhostOrbitRenderers()
        {
            string skipReason = ResolveEnsureOrbitRenderersSkipReason(
                ParsekProcess.IsApplicationQuitting,
                IsRemoveAllGhostVesselsInProgress);
            if (skipReason != null)
            {
                int tracked = vesselsByChainPid.Count + vesselsByRecordingIndex.Count;
                ParsekLog.VerboseRateLimited(Tag,
                    "ensure-orbit-renderers-skip|" + skipReason,
                    string.Format(ic,
                        "EnsureGhostOrbitRenderers: skipped repair reason={0} trackedGhosts={1} " +
                        "(a ghost repaired now is destroyed by the same teardown)",
                        skipReason, tracked),
                    5.0);
                return 0;
            }

            return RepairGhostOrbitRenderersCore();
        }

        // Every live-Unity read of the repair lives here, behind NoInlining, so the skip guard
        // above stays callable headless (the runtime resolves Unity ECalls when it compiles the
        // method that names them).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RepairGhostOrbitRenderersCore()
        {
            if (MapView.fetch == null)
            {
                ParsekLog.Warn(Tag, "EnsureGhostOrbitRenderers: MapView.fetch is null — cannot create orbit renderers");
                return 0;
            }

            // Collect unique ghost vessels from both dictionaries
            var ghosts = new HashSet<Vessel>();
            foreach (var v in vesselsByChainPid.Values)
                if (v != null) ghosts.Add(v);
            foreach (var v in vesselsByRecordingIndex.Values)
                if (v != null) ghosts.Add(v);

            int fixedCount = 0;
            foreach (var v in ghosts)
            {
                bool needsMapObj = v.mapObject == null;
                bool needsRenderer = v.orbitRenderer == null;

                if (!needsMapObj && !needsRenderer)
                    continue;

                // Call the private AddOrbitRenderer — it creates both mapObject and
                // orbitRenderer if missing. Now that MapView.fetch is available, the
                // guard inside AddOrbitRenderer passes. The method is idempotent.
                Traverse.Create(v).Method("AddOrbitRenderer").GetValue();

                if (v.orbitRenderer == null && needsRenderer)
                {
                    ParsekLog.Warn(Tag, string.Format(ic,
                        "EnsureGhostOrbitRenderers: AddOrbitRenderer via Traverse had no effect on '{0}' pid={1} — " +
                        "method may have been renamed in a KSP update",
                        v.vesselName, v.persistentId));
                }

                // Configure rendering (same as post-Load block)
                if (v.orbitRenderer != null)
                {
                    v.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                    v.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                    if (!v.orbitRenderer.enabled)
                        v.orbitRenderer.enabled = true;
                }

                fixedCount++;
                ParsekLog.Info(Tag, string.Format(ic,
                    "EnsureGhostOrbitRenderers: fixed ghost '{0}' pid={1} (mapObj was null={2}, renderer was null={3}, " +
                    "now mapObj={4} renderer={5})",
                    v.vesselName, v.persistentId, needsMapObj, needsRenderer,
                    v.mapObject != null, v.orbitRenderer != null));
            }

            if (fixedCount > 0)
                ParsekLog.Info(Tag, string.Format(ic,
                    "EnsureGhostOrbitRenderers: fixed {0} of {1} ghost vessel(s)", fixedCount, ghosts.Count));
            else if (ghosts.Count > 0)
                ParsekLog.Verbose(Tag, string.Format(ic,
                    "EnsureGhostOrbitRenderers: all {0} ghost vessel(s) already have orbit renderers", ghosts.Count));

            return fixedCount;
        }

        /// <summary>
        /// PURE. Why <see cref="EnsureGhostOrbitRenderers"/> must not repair right now, or
        /// null when the repair may run. Quitting wins over a remove-all in progress because
        /// it is the broader condition (a remove-all can run inside the quit teardown).
        /// </summary>
        internal static string ResolveEnsureOrbitRenderersSkipReason(
            bool applicationQuitting,
            bool removeAllGhostVesselsInProgress)
        {
            if (applicationQuitting)
                return EnsureOrbitRenderersSkipApplicationQuitting;
            if (removeAllGhostVesselsInProgress)
                return EnsureOrbitRenderersSkipRemoveAllInProgress;
            return null;
        }

        // ---- Phase 8e S0 Instrument 1: coverage-closure accounting (PURELY ADDITIVE) ----

        /// <summary>
        /// Clears this frame's coverage-closure sets. Called by the polyline Driver at the TOP of every
        /// LateUpdate decide walk (the same lifecycle the ownership-publish sets clear on) so the sets
        /// reflect ONLY the recordings the walk drew this frame. Diagnostic-only; nothing in the live
        /// render path reads these.
        /// </summary>
        internal static void ClearFrameCoverageSets()
        {
            drawnRecordingIdsThisFrame.Clear();
            protoLessCoverageRecordingIdsThisFrame.Clear();
            paintedRecordingIdsThisFrame.Clear();
        }

        /// <summary>
        /// Records that the polyline Driver's decide walk enqueued SOME trajectory line for
        /// <paramref name="recordingId"/> this frame (current-element leg, forward run leg, or forward
        /// arc). Called from the Driver's decide walk, already gated there on
        /// <see cref="MapRenderTrace.IsEnabled"/>. Diagnostic-only; no render/draw effect. See
        /// <see cref="paintedRecordingIdsThisFrame"/> for why this is broader than the ownership set.
        /// </summary>
        internal static void NotePaintedRecordingLine(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;
            paintedRecordingIdsThisFrame.Add(recordingId);
        }

        /// <summary>
        /// Did the trajectory polyline actually paint ANY line for this ghost's recording this frame?
        /// The map-render probe's <c>line-blink</c> coverage input: a proto-orbit-line dark window is a
        /// render defect only if the map had NOTHING for the ghost across it, and the ghost's trajectory
        /// stays on screen through the inter-leg gaps that <see cref="IsPolylineOwningGhostPhase"/>
        /// reports false for. Same pid -&gt; RecordingId bridge and the same same-frame ordering as
        /// ownership (the Driver publishes at <c>[DefaultExecutionOrder(-50)]</c>, before the probe's
        /// LateUpdate). Always false when tracing is off, since the Driver only populates the set then -
        /// which is harmless because the probe only runs when tracing is on.
        /// </summary>
        internal static bool IsPolylinePaintingGhostTrajectory(uint pid)
        {
            return vesselPidToRecordingId.TryGetValue(pid, out string recId)
                && recId != null
                && paintedRecordingIdsThisFrame.Contains(recId);
        }

        /// <summary>Test-only seam: set/clear PAINTED-set membership without a live Driver walk.</summary>
        internal static void SetPaintedRecordingLineForTesting(string recordingId, bool painted)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;
            if (painted) paintedRecordingIdsThisFrame.Add(recordingId);
            else paintedRecordingIdsThisFrame.Remove(recordingId);
        }

        /// <summary>
        /// Records that the autonomous polyline walk decided to DRAW a non-orbital leg for
        /// <paramref name="recordingId"/> this frame (the will-draw == actual-draw signal). When
        /// <paramref name="ghostPid"/> is 0 the recording has NO ProtoVessel ghost (pid-0
        /// atmospheric/ascent), so it is ALSO added to the proto-less coverage set - the Director's
        /// genuine accounting of a recording its enumerated <see cref="ghostMapVesselPids"/> set cannot
        /// see. Called from the Driver's decide walk, already gated by the Driver on
        /// <see cref="MapRenderTrace.IsEnabled"/>. Diagnostic-only; no render/draw effect.
        /// </summary>
        internal static void NoteDrawnRecordingCoverage(string recordingId, uint ghostPid)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;
            drawnRecordingIdsThisFrame.Add(recordingId);
            // pid 0 => no proto vessel => the Director's enumerated set (ghostMapVesselPids) misses it;
            // the non-proto polyline path is the ONLY thing rendering it, so account for it here. A
            // proto-BEARING recording is intentionally NOT added (it is accounted via the pid bridge),
            // which keeps the assertion non-vacuous.
            if (ghostPid == 0)
                protoLessCoverageRecordingIdsThisFrame.Add(recordingId);
        }

        /// <summary>
        /// PURE accounting predicate (S0 Instrument 1): is a DRAWN recording ACCOUNTED FOR by the
        /// Director's coverage? Expressed entirely in the RecordingId domain (the draw domain). A drawn
        /// recording is accounted when EITHER it maps to a proto-bearing ghost pid (the Director's
        /// enumerated <see cref="ghostMapVesselPids"/> set, bridged back to RecordingIds via
        /// <see cref="vesselPidToRecordingId"/>) OR it is in the proto-less coverage set (the non-proto
        /// path's accounting). A drawn recording in NEITHER is the deletion-blocker: the autonomous walk
        /// drew it but the Director's accounted set does not cover it, so deleting the legacy ownership
        /// would silently drop it. Unit-testable (no Unity / no live state - all three inputs are
        /// passed in), so it can FIRE for a genuinely-unaccounted recording and PASS for an accounted one.
        /// </summary>
        internal static bool IsDrawnRecordingAccounted(
            string drawnRecordingId,
            ICollection<string> protoBearingRecordingIds,
            ICollection<string> protoLessCoverageRecordingIds)
        {
            if (string.IsNullOrEmpty(drawnRecordingId))
                return true; // a null/empty id is not a real drawn recording; never flag it
            if (protoBearingRecordingIds != null && protoBearingRecordingIds.Contains(drawnRecordingId))
                return true;
            if (protoLessCoverageRecordingIds != null
                && protoLessCoverageRecordingIds.Contains(drawnRecordingId))
                return true;
            return false;
        }

        /// <summary>
        /// Runs the S0 coverage-closure assertion over this frame's drawn-recording set, invoking
        /// <paramref name="onUnaccounted"/> once per drawn recording that is NEITHER proto-bearing NOR in
        /// the proto-less coverage set. Builds the proto-bearing RecordingId set ONCE (from the live
        /// <see cref="vesselPidToRecordingId"/> values - the Director's enumerated pid set bridged to the
        /// RecordingId domain) so the per-recording check is O(1). The callback receives
        /// <c>(recordingId, protoBearingCount, protoLessCoverageCount, drawnCount)</c> for the anomaly
        /// line. Diagnostic-only; the caller gates on <see cref="MapRenderTrace.IsEnabled"/>. No-ops when
        /// nothing drew this frame.
        /// </summary>
        internal static void AssertDrawnRecordingsAccounted(
            System.Action<string, int, int, int> onUnaccounted)
        {
            if (drawnRecordingIdsThisFrame.Count == 0)
                return;

            // Bridge the proto-bearing pids back into the RecordingId DOMAIN (the draw domain). Comparing
            // a pid-0 recording against a pid SET would always report "absent" - the task's explicit
            // false-positive trap. The values of vesselPidToRecordingId ARE the proto-bearing RecordingIds.
            protoBearingRecordingIdScratch.Clear();
            foreach (var kv in vesselPidToRecordingId)
                if (!string.IsNullOrEmpty(kv.Value))
                    protoBearingRecordingIdScratch.Add(kv.Value);

            foreach (string recId in drawnRecordingIdsThisFrame)
            {
                if (IsDrawnRecordingAccounted(
                        recId, protoBearingRecordingIdScratch, protoLessCoverageRecordingIdsThisFrame))
                    continue;
                onUnaccounted?.Invoke(
                    recId,
                    protoBearingRecordingIdScratch.Count,
                    protoLessCoverageRecordingIdsThisFrame.Count,
                    drawnRecordingIdsThisFrame.Count);
            }
        }

        /// <summary>Test-only seam: stamp this frame's drawn / proto-less coverage sets so
        /// <see cref="AssertDrawnRecordingsAccounted"/> and the pid-bridge can be exercised end-to-end
        /// from xUnit (the real producer is the Unity-coupled Driver walk). Mirrors the live
        /// <see cref="NoteDrawnRecordingCoverage"/> semantics.</summary>
        internal static void SetFrameCoverageForTesting(string recordingId, bool drawn, bool protoLess)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;
            if (drawn) drawnRecordingIdsThisFrame.Add(recordingId);
            else drawnRecordingIdsThisFrame.Remove(recordingId);
            if (protoLess) protoLessCoverageRecordingIdsThisFrame.Add(recordingId);
            else protoLessCoverageRecordingIdsThisFrame.Remove(recordingId);
        }

        /// <summary>Test-only seam: stamp the proto-bearing pid bridge (pid -> recordingId) so the
        /// assertion's RecordingId-domain bridge can be exercised from xUnit.</summary>
        internal static void SetProtoBearingPidForTesting(uint pid, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) vesselPidToRecordingId.Remove(pid);
            else vesselPidToRecordingId[pid] = recordingId;
        }

        /// <summary>Test-only: clear all coverage-closure state (the S0 frame sets + the scratch), so a
        /// test starts from a known-empty accounting.</summary>
        internal static void ResetCoverageSetsForTesting()
        {
            drawnRecordingIdsThisFrame.Clear();
            protoLessCoverageRecordingIdsThisFrame.Clear();
            protoBearingRecordingIdScratch.Clear();
            // DELIBERATELY NOT the PAINTED set. This helper has a LIVE in-game caller
            // (ExtendedRuntimeTests' AccountedSetCoversDrawnSet, which calls it at entry and again in
            // finally), and for the painted set the polarity is inverted relative to the others: empty
            // means "nothing painted", which is the RAISE direction of the line-blink guard, and
            // `line-blink` is a GATED harness token. Stranding it empty mid-frame could inject a
            // spurious PARSEK-FAIL into any in-game batch that happens to run with tracing on and a
            // ghost mid-dark-window. Unit tests that need it cleared call ClearFrameCoverageSets(),
            // the same per-frame clear the live Driver runs.
        }

        /// <summary>
        /// Resolve the visible orbit time window for a ghost vessel at the current UT.
        /// Recording-index ghosts use dynamic same-body gap carry; other segment-based
        /// ghosts fall back to their stored bounds.
        /// </summary>
        internal static bool TryGetVisibleOrbitBoundsForGhostVessel(
            uint vesselPid, double currentUT, out double startUT, out double endUT)
        {
            startUT = 0;
            endUT = 0;

            // Resolved once up front so both single-segment short-circuit branches below can pass it to
            // ExpandStoredBoundsAcrossEquivalentSegments. O(1) dictionary lookup, no side effects.
            int recordingIndex = FindRecordingIndexByVesselPid(vesselPid);

            // Loop-shifted ghost: the stored bounds are already in the live frame and match the
            // shifted orbit epoch, so use them directly. Re-deriving from the raw recorded
            // OrbitSegments at the live UT (below) would return raw recorded UTs that desync from
            // the shifted orbit when the live clock falls inside the member's recorded window.
            // Empty set off the loop path, so this never fires for non-loop ghosts.
            if (ghostOrbitLoopShiftedPids.Contains(vesselPid)
                && TryGetStoredOrbitBoundsForGhostVessel(
                    vesselPid,
                    currentUT,
                    "loop-shifted",
                    out startUT,
                    out endUT))
            {
                // The stored bounds are a SINGLE applied OrbitSegment, so the arc clip would draw only
                // that fragment of a multi-fragment same-orbit coast (the loop SOI-approach hyperbola
                // splits into adjacent equivalent fragments). Expand the arc-clip window across those
                // element-equivalent fragments (read-only; ghostOrbitBounds is untouched) so the line
                // draws the whole approach in one frame instead of one piece then the rest seconds later.
                ExpandStoredBoundsAcrossEquivalentSegments(
                    vesselPid, recordingIndex, ref startUT, ref endUT, "loop-shifted");
                return true;
            }

            if (IsEndpointTailRecordingGhost(vesselPid, recordingIndex)
                && TryGetStoredOrbitBoundsForGhostVessel(
                    vesselPid,
                    currentUT,
                    "endpoint-tail",
                    out startUT,
                    out endUT))
            {
                // Same single-segment clip structure as the loop-shifted branch. An EndpointTail
                // synthetic segment's startUT may not exist verbatim in the recorded OrbitSegments;
                // when no seed matches the expansion is a no-op (keeps stored bounds, behaviour-identical
                // to today). Otherwise it widens across the equivalent recorded fragments.
                ExpandStoredBoundsAcrossEquivalentSegments(
                    vesselPid, recordingIndex, ref startUT, ref endUT, "endpoint-tail");
                return true;
            }

            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndex >= 0
                && committed != null
                && recordingIndex < committed.Count
                && committed[recordingIndex].HasOrbitSegments
                && TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                    committed[recordingIndex].OrbitSegments, currentUT,
                    out OrbitSegment segment,
                    out startUT,
                    out endUT,
                    out int firstVisibleIndex,
                    out int lastVisibleIndex,
                    out bool carriedAcrossGap))
            {
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "visible-window-segment|{0}", vesselPid),
                    string.Format(ic,
                        "segment|rec={0}|segment={1:F3}-{2:F3}|gap={3}",
                        recordingIndex,
                        segment.startUT,
                        segment.endUT,
                        carriedAcrossGap ? "gap" : "segment"),
                    string.Format(ic,
                        "Map-visible orbit window pid={0} recIndex={1} ut={2:F2} body={3} " +
                        "segmentUT={4:F2}-{5:F2} windowUT={6:F2}-{7:F2} windowIndices={8}-{9} gapCarry={10}",
                        vesselPid,
                        recordingIndex,
                        currentUT,
                        segment.bodyName,
                        segment.startUT,
                        segment.endUT,
                        startUT,
                        endUT,
                        firstVisibleIndex,
                        lastVisibleIndex,
                        carriedAcrossGap));

                if (PlaybackOrbitDiagnostics.TryBuildMapPredictedTailLog(
                    recordingIndex,
                    vesselPid,
                    committed[recordingIndex],
                    segment,
                    currentUT,
                    startUT,
                    endUT,
                    carriedAcrossGap,
                    out string mapRenderKey,
                    out string mapRenderMessage))
                {
                    ParsekLog.VerboseRateLimited("MapRender", mapRenderKey, mapRenderMessage, 1.0);
                }

                return true;
            }

            if (recordingIndex >= 0
                && committed != null
                && recordingIndex < committed.Count
                && committed[recordingIndex].HasOrbitSegments)
            {
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "visible-window-none|{0}", vesselPid),
                    string.Format(ic, "none|rec={0}", recordingIndex),
                    string.Format(ic,
                        "Map-visible orbit window unavailable source=none reason=no-active-equivalent-segment " +
                        "pid={0} recIndex={1} ut={2:F2} — " +
                        "no active or equivalent same-orbit segment chain",
                        vesselPid, recordingIndex, currentUT));
            }

            if (TryGetStoredOrbitBoundsForGhostVessel(
                    vesselPid,
                    currentUT,
                    "fallback",
                    out startUT,
                    out endUT))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolve the body-frame time window for a ghost vessel at the current UT — the
        /// bounds covering all consecutive same-body OrbitSegments around the playback head.
        /// This is the wider window used by <see cref="Parsek.Patches.GhostOrbitLinePatch"/>
        /// to decide whether the rendered orbit line should stay on. Inside a body frame
        /// the line stays continuously visible across inter-segment burns / dropouts; only
        /// the SOI / body change at a frame boundary blanks it. Arc clipping and icon
        /// clamping still use <see cref="TryGetVisibleOrbitBoundsForGhostVessel"/> (segment
        /// bounds), so the line shape and icon position update per-segment as before.
        /// Reads the per-PID cache <see cref="ghostBodyFrameOrbitBounds"/>, which is populated
        /// by <see cref="UpdateGhostOrbitForRecording"/> at the same time the segment is applied
        /// (so the loop-epoch shift is baked in identically to <see cref="ghostOrbitBounds"/>).
        /// </summary>
        internal static bool TryGetBodyFrameOrbitBoundsForGhostVessel(
            uint vesselPid, double currentUT, out double startUT, out double endUT)
        {
            startUT = 0;
            endUT = 0;

            if (ghostBodyFrameOrbitBounds.TryGetValue(vesselPid, out var bf))
            {
                startUT = bf.startUT;
                endUT = bf.endUT;
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "body-frame-bounds|{0}", vesselPid),
                    string.Format(ic, "body-frame|cached|{0:F3}-{1:F3}", startUT, endUT),
                    string.Format(ic,
                        "Body-frame orbit bounds pid={0} source=cached ut={1:F2} bodyFrameUT={2:F2}-{3:F2}",
                        vesselPid, currentUT, startUT, endUT));
                return true;
            }

            // Fallback to the stored per-segment bounds: a ghost that was created with
            // a segment but never had body-frame bounds cached (e.g., a chain-pid ghost
            // taking a different code path) still gets gated by its existing arc-clip
            // bounds rather than rendering perpetually.
            return TryGetStoredOrbitBoundsForGhostVessel(
                vesselPid,
                currentUT,
                "fallback-body-frame",
                out startUT,
                out endUT);
        }

        private static bool TryGetStoredOrbitBoundsForGhostVessel(
            uint vesselPid,
            double currentUT,
            string reason,
            out double startUT,
            out double endUT)
        {
            startUT = 0;
            endUT = 0;

            if (!ghostOrbitBounds.TryGetValue(vesselPid, out var bounds))
                return false;

            startUT = bounds.startUT;
            endUT = bounds.endUT;
            string source;
            if (string.Equals(reason, "endpoint-tail", StringComparison.Ordinal))
                source = "stored-bounds-endpoint-tail";
            else if (string.Equals(reason, "loop-shifted", StringComparison.Ordinal))
                source = "stored-bounds-loop-shifted";
            else if (string.Equals(reason, "loop-shifted-body-frame", StringComparison.Ordinal))
                source = "stored-bounds-loop-shifted-body-frame";
            else if (string.Equals(reason, "endpoint-tail-body-frame", StringComparison.Ordinal))
                source = "stored-bounds-endpoint-tail-body-frame";
            else if (string.Equals(reason, "fallback-body-frame", StringComparison.Ordinal))
                source = "stored-bounds-fallback-body-frame";
            else
                source = "stored-bounds-fallback";
            ParsekLog.VerboseOnChange(Tag,
                string.Format(ic, "visible-window-stored-bounds|{0}|{1}", vesselPid, reason),
                string.Format(ic, "stored-bounds|{0}|{1:F3}-{2:F3}", reason, startUT, endUT),
                string.Format(ic,
                    "Map-visible orbit window pid={0} source={1} ut={2:F2} windowUT={3:F2}-{4:F2}",
                    vesselPid, source, currentUT, startUT, endUT));
            return true;
        }

        /// <summary>
        /// Expand a stored SINGLE-segment arc-clip window (loop-shifted / endpoint-tail branches) across
        /// element-equivalent adjacent recorded OrbitSegments so the orbit line draws one continuous
        /// same-orbit arc instead of a single fragment. Mirrors the non-loop merge
        /// (<see cref="TrajectoryMath.TryGetOrbitWindowForMapDisplay"/>) the loop path short-circuits past.
        ///
        /// <para>The stored bounds (<paramref name="startUT"/>/<paramref name="endUT"/>) are in the LIVE
        /// frame (the orbit epoch + bounds were shifted by <c>loopEpochShiftSeconds</c> when applied); the
        /// recorded OrbitSegments are RAW. To compare/merge in ONE consistent frame this un-shifts the
        /// stored bounds to raw via <see cref="MapLiveUTToEffUT"/>, expands on the RAW segments, then
        /// re-applies +shift to the merged result. Writes back to the ref params ONLY when more than one
        /// fragment coalesced; a no-seed-match or single-fragment result leaves them at the stored values
        /// (behaviour-identical to before). Reads <c>RecordingStore.CommittedRecordings</c> only (the same
        /// raw committed read the adjacent non-loop branch makes); NEVER mutates <c>ghostOrbitBounds</c>,
        /// so the icon-drive / SetOrbit epoch stay on the single applied segment and only the drawn arc
        /// sweep widens.</para>
        /// </summary>
        private static void ExpandStoredBoundsAcrossEquivalentSegments(
            uint vesselPid, int recordingIndex, ref double startUT, ref double endUT, string reason)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndex < 0
                || committed == null
                || recordingIndex >= committed.Count
                || committed[recordingIndex] == null
                || !committed[recordingIndex].HasOrbitSegments)
            {
                return;
            }

            double shift = GetGhostOrbitEpochShift(vesselPid);
            double rawStart = MapLiveUTToEffUT(startUT, shift);
            double rawEnd = MapLiveUTToEffUT(endUT, shift);

            if (!TrajectoryMath.TryExpandStoredSingleSegmentWindow(
                    committed[recordingIndex].OrbitSegments,
                    rawStart,
                    rawEnd,
                    out double rawExpandedStart,
                    out double rawExpandedEnd,
                    out int firstVisibleIndex,
                    out int lastVisibleIndex,
                    out int fragmentCount))
            {
                return;
            }

            if (fragmentCount <= 1)
                return;

            // The expansion is purely ADDITIVE: it widens one applied fragment across its element-
            // equivalent recorded neighbours so the same-orbit coast draws in one frame. It must NEVER
            // shrink the stored window. A re-aimed loop transfer stores the FULL synthesized
            // [departure, arrival] span, but the RAW recorded segments walked above were split mid-coast
            // (a recorded mid-course element change > the equivalence tolerance), so the walk stops at the
            // split and would otherwise truncate the drawn arc partway to the target. Union with the stored
            // window (startUT/endUT still hold the caller's stored bounds here) so the expansion can only
            // ever widen, never truncate.
            TrajectoryMath.UnionArcWindowWithStored(
                startUT, endUT,
                rawExpandedStart + shift, rawExpandedEnd + shift,
                out double widenedStartUT, out double widenedEndUT, out bool clampedToStored);
            startUT = widenedStartUT;
            endUT = widenedEndUT;

            // This resolves every render frame while a loop ghost approaches an SOI; emit only when the
            // coalesced window changes (VerboseOnChange) so the merge decision is captured without
            // per-frame spam, mirroring the stored-bounds VerboseOnChange site just above.
            ParsekLog.VerboseOnChange(Tag,
                string.Format(ic, "loop-arc-coalesce|{0}|{1}", vesselPid, reason),
                string.Format(ic, "coalesce|{0}|{1}-{2}|{3:F3}-{4:F3}|{5}",
                    fragmentCount, firstVisibleIndex, lastVisibleIndex, startUT, endUT, clampedToStored),
                string.Format(ic,
                    "Loop arc-window coalesced pid={0} recIndex={1} reason={2} fragments={3} " +
                    "segIndices={4}-{5} rawWindowUT={6:F2}-{7:F2} shiftedWindowUT={8:F2}-{9:F2} " +
                    "clampedToStored={10} loopShift={11:F2}",
                    vesselPid, recordingIndex, reason, fragmentCount,
                    firstVisibleIndex, lastVisibleIndex,
                    rawExpandedStart, rawExpandedEnd,
                    startUT, endUT, clampedToStored, shift));
        }
    }
}
