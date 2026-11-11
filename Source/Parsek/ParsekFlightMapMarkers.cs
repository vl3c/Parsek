using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The flight scene's custom ghost map markers (map view and flight view), drawn from
    /// ParsekFlight.OnGUI outside the UI-scale host scope: markers are projected from world
    /// positions in physical pixels and stay unscaled (UI-scale ruling 2026-11-04). Owned by
    /// ParsekFlight, one instance per flight scene; the Tracking Station draws its own
    /// markers (ParsekTrackingStation). Icon, label, hover and click handling live in
    /// MapMarkerRenderer, shared with the Tracking Station.
    /// </summary>
    internal class ParsekFlightMapMarkers
    {
        private readonly ParsekFlight flight;

        // Reusable per-frame buffers (used by DrawMapMarkers for chain dedup)
        private static readonly Dictionary<string, int> chainTipIndexBuffer = new Dictionary<string, int>();

        // Review MINOR-1 (PR #1105): chains that drew a LABELED marker via the pid-keyed walk this
        // frame, so the ghost-less polyline fallback never adds a second marker for the same chain
        // during a warp handoff. A chain whose tip showed only its stock vessel icon (nativeIcon skip)
        // is NOT in this set - the fallback may still mark the playing head there (two genuine
        // objects, two icons). Cleared with chainTipIndexBuffer each walk.
        private static readonly HashSet<string> drawnChainMarkerBuffer =
            new HashSet<string>(StringComparer.Ordinal);

        // Slice (iii): reusable per-recording head-UT buffer for the per-instance overlap marker
        // branch. Cleared (not reallocated) per overlap recording inside TryGetLiveOverlapHeadUTs so
        // drawing N markers on the one shared polyline allocates nothing per frame.
        private static readonly List<(long cycle, double headUT)> overlapHeadUtBuffer =
            new List<(long, double)>();

        // The labeled marker uses GhostMapPresence.IsPolylineRecentlyOwningGhostPhase to decide
        // when to prefer trajPos over the stale OrbitDriver mesh transform during the brief
        // post-polyline-release window (the seg-drive dispatcher runs on a ~0.5s cadence so the
        // mesh transform is stale for ~12 frames at 60Hz right after polyline release). The
        // GhostOrbitLinePatch reads from the same source to keep the stock icon suppressed
        // through the same window, so both the labeled marker and the stock icon agree.

        // Cached waypoint indices and body lookup for trajectory-derived map marker positions (Bug A fix)
        private readonly Dictionary<int, int> mapMarkerCachedIndices = new Dictionary<int, int>();
        private readonly Dictionary<string, CelestialBody> bodyCache = new Dictionary<string, CelestialBody>();

        internal ParsekFlightMapMarkers(ParsekFlight flight)
        {
            this.flight = flight;
        }

        internal enum MapMarkerPositionFailureReason
        {
            None,
            BadRecordingIndex,
            NoTrajectoryPoints,
            OutsideTimeRange,
            MissingBody,

            /// <summary>
            /// The UT falls in a RELATIVE-frame TrackSection with no body-fixed shadow, so no
            /// body-fixed (lat, lon, alt) triple exists for it. Refusing is mandatory: in a
            /// RELATIVE section those three fields are anchor-local METRE OFFSETS, and feeding
            /// them to <c>CelestialBody.GetWorldSurfacePosition</c> puts the marker deep inside
            /// the planet (the CLAUDE.md "Rotation / world frame" footgun).
            /// </summary>
            RelativeFrameWithoutBodyFixed
        }

        /// <summary>
        /// Which sample list answers a body-fixed (lat, lon, alt) read at one UT, per the
        /// section's <see cref="ReferenceFrame"/>. Mirrors the polyline builder's
        /// <c>ResolveSourceListForSection</c> policy exactly: Absolute walks the recording's own
        /// points, a Relative section walks its body-fixed shadow, and a Relative section WITHOUT
        /// one refuses rather than reading metre offsets as geographic coordinates.
        /// </summary>
        internal enum MapMarkerFrameSource
        {
            FlatPoints,
            SectionBodyFixedFrames,
            RelativeUnresolved
        }

        /// <summary>
        /// PURE frame dispatch for the map-marker position read. Returns which list to sample and,
        /// for <see cref="MapMarkerFrameSource.SectionBodyFixedFrames"/>, that list. A recording
        /// with no TrackSections, or a UT outside every section, keeps the flat-points path
        /// (byte-identical to the pre-2026-09-08 behaviour, which is what every Absolute-only
        /// recording is). xUnit-testable: no Unity or KSP call.
        /// </summary>
        internal static MapMarkerFrameSource ResolveMapMarkerFrameSource(
            List<TrackSection> sections, double ut, out List<TrajectoryPoint> source)
        {
            source = null;
            if (sections == null || sections.Count == 0)
                return MapMarkerFrameSource.FlatPoints;

            int idx = TrajectoryMath.FindTrackSectionForUT(sections, ut);
            if (idx < 0)
                return MapMarkerFrameSource.FlatPoints;

            TrackSection section = sections[idx];
            if (section.referenceFrame != ReferenceFrame.Relative)
                return MapMarkerFrameSource.FlatPoints;

            if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
            {
                source = section.bodyFixedFrames;
                return MapMarkerFrameSource.SectionBodyFixedFrames;
            }

            return MapMarkerFrameSource.RelativeUnresolved;
        }

        internal struct MapMarkerSummary
        {
            public bool IsMapView;
            public int Candidates;
            public int PreviewDrawn;
            public int Drawn;
            public int CameraUnavailable;
            public int HiddenInFlight;
            public int NativeIcon;
            public int Debris;
            public int ChainNonTip;
            public int PositionFailure;
            public int MissingBody;
            // A looped-mission member whose shared span-clock has it OUTSIDE its render window this
            // frame (inter-cycle tail / not yet / already past): the engine hides the mesh too, so
            // the custom marker is intentionally skipped (distinct from a position failure).
            public int LoopHidden;
            // GUI-P1: recordings the player hid with the per-recording playback tick box.
            // It is part of HasSignal on purpose: with EVERY recording unticked the pass has
            // nothing else to count, and a summary suppressed for "no signal" cannot say
            // that it drew nothing BECAUSE the player turned them off - which is the one
            // reading the GUI-9 lane exists to gate. Without this bucket `drawn=0` is
            // unreachable by construction.
            public int PlaybackDisabled;

            internal bool HasSignal =>
                Candidates > 0
                || PreviewDrawn > 0
                || Drawn > 0
                || CameraUnavailable > 0
                || HiddenInFlight > 0
                || NativeIcon > 0
                || Debris > 0
                || ChainNonTip > 0
                || PositionFailure > 0
                || MissingBody > 0
                || LoopHidden > 0
                || PlaybackDisabled > 0;
        }

        internal static string FormatMapMarkerSummary(MapMarkerSummary summary)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Map marker summary: view={0} candidates={1} previewDrawn={2} drawn={3} cameraUnavailable={4} hiddenInFlight={5} nativeIcon={6} debris={7} chainNonTip={8} positionFailure={9} missingBody={10} loopHidden={11} playbackDisabled={12}",
                summary.IsMapView ? "map" : "flight",
                summary.Candidates,
                summary.PreviewDrawn,
                summary.Drawn,
                summary.CameraUnavailable,
                summary.HiddenInFlight,
                summary.NativeIcon,
                summary.Debris,
                summary.ChainNonTip,
                summary.PositionFailure,
                summary.MissingBody,
                summary.LoopHidden,
                summary.PlaybackDisabled);
        }

        private static void LogMapMarkerSummary(MapMarkerSummary summary)
        {
            if (!summary.HasSignal)
                return;

            ParsekLog.VerboseRateLimited("UI",
                "map-marker-summary",
                FormatMapMarkerSummary(summary),
                2.0);
        }

        public void DrawMapMarkers()
        {
            bool isMapView = MapView.MapIsEnabled;
            var summary = new MapMarkerSummary { IsMapView = isMapView };

            // Resolve camera for current view; bail if unavailable
            if (isMapView)
            {
                if (PlanetariumCamera.Camera == null)
                {
                    summary.CameraUnavailable++;
                    LogMapMarkerSummary(summary);
                    return;
                }
            }
            else
            {
                if (FlightCamera.fetch == null || FlightCamera.fetch.mainCamera == null)
                {
                    summary.CameraUnavailable++;
                    LogMapMarkerSummary(summary);
                    return;
                }
            }

            // Manual preview ghost. Uses a fixed marker key so hover/sticky state is
            // consistent across frames, and a "Preview" display label.
            if (flight.IsPlaying && flight.PreviewGhost != null)
            {
                DrawMapMarkerAt(flight.PreviewGhost.transform.position, "preview", "Preview",
                    new Color(0.2f, 1f, 0.4f, 0.9f));
                summary.PreviewDrawn++;
            }

            // Timeline ghosts — skip if a ghost map ProtoVessel exists for this index
            // (the native KSP vessel icon replaces this marker and tracks the correct orbital position).
            // Deduplicate per chain: during warp, multiple chain segments can be active
            // simultaneously. Only draw the marker for the highest-index (latest) ghost per chain.
            // [ERS-exempt] reason: ghostStates dict is keyed by CommittedRecordings
            // index; markers must be looked up by the same index space. Converting
            // would mis-align the index -> recording mapping.
            // TODO(phase 6+): migrate ghostStates to recording-id keyed storage.
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
            {
                LogMapMarkerSummary(summary);
                return;
            }

            // First pass: find the highest active index per chain
            chainTipIndexBuffer.Clear();
            drawnChainMarkerBuffer.Clear();
            foreach (var kvp in flight.Engine.ghostStates)
            {
                if (kvp.Value == null) continue;
                if (kvp.Key >= committed.Count) continue;
                string chainId = committed[kvp.Key].ChainId;
                if (string.IsNullOrEmpty(chainId)) continue;
                int existing;
                if (!chainTipIndexBuffer.TryGetValue(chainId, out existing) || kvp.Key > existing)
                    chainTipIndexBuffer[chainId] = kvp.Key;
            }

            // Second pass: draw markers, skipping non-tip chain members and debris.
            // In flight view, skip hidden ghosts (#245/#247): their transform.position is stale
            // after FloatingOrigin shifts and projects to wrong map locations.
            // In map view, draw ALL active ghosts — use trajectory-derived positions when the
            // mesh is hidden by zone distance so every ghost is visible on the map.
            double currentUT = isMapView ? Planetarium.GetUniversalTime() : 0;

            // Marker-decision tracer timestamp: currentUT is 0 in flight view (only map view needs
            // it for positioning), so use the live UT for the trace line so it is meaningful in
            // both views. Only read when tracing is enabled.
            double traceUT = MapRenderTrace.IsEnabled ? Planetarium.GetUniversalTime() : 0.0;

            // C1 (flight descent icon ride): the descent-set members spawn in WINDOW order
            // (entry -> touchdown -> landing), which is NOT committed-index order, so the in-window descent
            // member is usually not the chain INDEX-tip and would be dropped by the chain-tip skip below - the
            // icon vanishes off the entry/descent legs and only the final landing member (highest index, widest
            // window) survives. Resolve the ONE descent-set member that is actually rendering its descent slice
            // this frame (highest-index in-window member, so a shared surface window draws a single icon) and
            // exempt it from the chain-tip skip so the icon rides the entry->surface curve. -1 (no exemption,
            // byte-identical) for non-descent / non-re-aim units or between cycles.
            int descentIconCarrier = GhostPlaybackLogic.ResolveFlightDescentIconCarrier(
                committed, currentUT, flight.Engine.CurrentLoopUnits);

            foreach (var kvp in flight.Engine.ghostStates)
            {
                var state = kvp.Value;
                if (state == null) continue;
                summary.Candidates++;
                bool meshActive = state.ghost != null && state.ghost.activeSelf;

                // ---- Marker-decision tracer state (per-pid, change-based; gated, off in normal play) ----
                // Carries the four ResolveMarkerDrawDecision disjuncts (resolved only at the proto-gate
                // branch below; false elsewhere because no proto vessel exists / the gate was not
                // consulted), the resolved shouldDrawNonProto bool, the terminal outcome, and (for a
                // drawn non-proto marker) the polyline ride reason + the fallback position source. A
                // single change-based line per ghost is emitted via EmitMarkerDecision() at each exit.
                bool decDirectorTraced = false, decPolylineOwning = false,
                    decIconSuppressed = false, decShouldDraw = false;
                var decOutcome = MapRenderTrace.MarkerOutcome.Unknown;
                var decRideReason = MapRenderTrace.MarkerRideReason.NotAttempted;
                int decRideLeg = -1;
                string decPosSource = "?";
                string traceRecId = kvp.Key < committed.Count ? committed[kvp.Key].RecordingId : null;
                string traceVessel = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                void EmitMarkerDecision()
                {
                    if (!MapRenderTrace.IsEnabled || string.IsNullOrEmpty(traceRecId))
                        return;
                    MapRenderTrace.EmitMarkerDecisionOnChange(
                        MapRenderTrace.RenderSurface.ImguiLabeledMarker, traceRecId, traceUT,
                        MapRenderTrace.BuildMarkerDecisionSignature(
                            kvp.Key, traceVessel, decDirectorTraced, decPolylineOwning,
                            decIconSuppressed, decShouldDraw, decOutcome, decRideReason, decRideLeg,
                            decPosSource));
                }

                // In flight view, skip hidden ghosts (stale positions cause wrong markers #245/#247)
                if (!meshActive && !isMapView)
                {
                    summary.HiddenInFlight++;
                    decOutcome = MapRenderTrace.MarkerOutcome.SkippedNotOnMap;
                    EmitMarkerDecision();
                    continue;
                }

                // ---- Slice (iii): per-instance overlap markers riding the ONE shared polyline ----
                // For an OVERLAPPING looped mission rendered via the trajectory polyline (the
                // maintainer's sub-2km hop with ZERO orbit), draw N markers - one per live overlap
                // instance - each at its own ComputeOverlapCyclePlaybackUT head along the single shared
                // polyline, so the map matches the N flight ghosts. The polyline GEOMETRY is untouched
                // (it draws ONCE keyed by RecordingId); this only adds N markers riding it. Gated behind
                // ShouldDriveOverlapPerInstance (inside TryGetLiveOverlapHeadUTs): non-overlap / gate-off
                // returns false and falls through to the UNCHANGED 8c proto gate + single-marker tail
                // below, byte-identically.
                //
                // HOISTED above the 8c proto gate (which consults ONLY the newest instance's pid) and
                // the chain-non-tip gate so a MIXED overlap recording - newest cycle mid-orbit (visible
                // proto icon) while OLDER cycles are simultaneously in a non-orbital reentry/descent
                // phase - does NOT get pre-empted by the newest-only `continue` and silently drop the
                // older instances' polyline markers. Safe to hoist because the PER-CYCLE no-double rule
                // inside DrawOneOverlapInstanceMarker (TryGetOverlapInstancePidForCycle +
                // ShouldDrawNonProtoMarkerForGhost(instancePid)) makes the correct per-cycle
                // orbital-vs-polyline decision: a cycle whose proto icon is visible is skipped (the proto
                // draws it), a cycle that is suborbital/suppressed draws its polyline marker. So moving
                // the whole branch up cannot double-draw and closes the dropped-marker gap. Debris is
                // still skipped here (overlap recordings are top-level mission members, but guard anyway
                // to preserve the IsDebris filter the 8c block below applied).
                //
                // Map-view only: the per-instance heads ride the polyline scratchScaledSpace (filled at
                // onPreCull) and resolve world positions via the map's scaled-space anchoring; in flight
                // view currentUT is 0 and there is no shared map polyline to ride, so overlap markers
                // stay on the single-instance (newest) path below.
                if (isMapView && kvp.Key < committed.Count
                    && !committed[kvp.Key].IsDebris
                    && GhostMapPresence.TryGetLiveOverlapHeadUTs(
                        committed[kvp.Key], kvp.Key, committed,
                        flight.Engine.CurrentLoopUnits, currentUT, overlapHeadUtBuffer))
                {
                    int instancesDrawn = 0;
                    for (int hi = 0; hi < overlapHeadUtBuffer.Count; hi++)
                    {
                        var (cycle, headUT) = overlapHeadUtBuffer[hi];
                        if (DrawOneOverlapInstanceMarker(kvp.Key, committed, headUT, cycle))
                            instancesDrawn++;
                    }
                    summary.Drawn += instancesDrawn;

                    decOutcome = MapRenderTrace.MarkerOutcome.DrawnNonProto;
                    EmitMarkerDecision();

                    // A single rate-limited summary per overlap recording (per the batch-counting
                    // convention): how many of the N live cycles actually drew a polyline marker this
                    // frame (an instance skips when its head is out-of-window / between legs / off-line,
                    // or when its cycle is currently drawn by a live non-suppressed proto icon).
                    int recIdxForOverlapLog = kvp.Key;
                    int liveCyclesForLog = overlapHeadUtBuffer.Count;
                    int drawnForLog = instancesDrawn;
                    string overlapNameForLog = committed[kvp.Key].VesselName;
                    ParsekLog.VerboseRateLimited("GhostMap",
                        "overlap-instance-markers-"
                            + recIdxForOverlapLog.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Overlap per-instance markers: rec={0} vessel={1} liveCycles={2} drawn={3}",
                            recIdxForOverlapLog, overlapNameForLog ?? "Ghost", liveCyclesForLog, drawnForLog),
                        2.0);
                    continue; // per-instance markers drew (or all skipped) — skip the 8c gate + tail
                }

                // ---- Boundary-overlap secondary marker (launch->escape seam render) ----
                // The early-launching instance N+1's in-SOI ASCENT icon, riding the shared polyline,
                // during the borrow window of a zero-slack re-aim launch loop. The PRIMARY (instance N,
                // far downstream near the destination) draws its OWN marker via the proto gate / tail
                // below; this ADDS the secondary's ascent marker at a DISJOINT position (the pad, months
                // apart in span phase). The secondary's map-presence proto vessel (icon + escape conic)
                // cannot exist during atmospheric ascent — no OrbitSegment to seed it — so the
                // map-presence sweep defers the create until the recorded escape Segment (the pre-Segment
                // gap). Without this the ascent POLYLINE draws (the second-head pass) with NO icon riding
                // it for those minutes. DrawOneOverlapInstanceMarker self-gates per cycle: once the escape
                // Segment materializes the proto icon owns the cycle and this returns false (no double).
                // NO continue: the primary marker still draws below. Inert for every non-launch-hold
                // member / aligned loop (no live secondary head) and outside map view.
                if (GhostMapPresence.TryResolveBoundaryOverlapSecondaryMarker(
                        isMapView, kvp.Key < committed.Count ? committed[kvp.Key] : null, kvp.Key,
                        currentUT, flight.Engine.CurrentLoopUnits,
                        out double boundarySecondaryUT, out long boundarySecondaryCycle)
                    && DrawOneOverlapInstanceMarker(
                        kvp.Key, committed, boundarySecondaryUT, boundarySecondaryCycle))
                {
                    summary.Drawn++;
                }

                // Skip if native KSP icon is active (ProtoVessel exists and icon not suppressed).
                // When the Harmony patch suppresses the icon (below atmosphere), we draw
                // our custom marker at the ghost mesh position instead.
                if (GhostMapPresence.HasGhostVesselForRecording(kvp.Key))
                {
                    uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(kvp.Key);
                    // Skip our marker only when the native proto icon is actually
                    // visible. The marker-draw decision (proto hidden -> draw our
                    // marker) is owned by GhostMapPresence.ShouldDrawNonProtoMarkerForGhost
                    // (Phase 8c): gate ON the Director's TracedPath decision +
                    // polyline-owns (8b.2 actual-draw) are authoritative, with the legacy
                    // icon-suppressed flag (below-atmosphere clamp / off-arc / Director
                    // no-bounds transient) kept as the fallback; gate OFF it is the
                    // legacy IsIconSuppressed || IsPolylineOwningGhostPhase predicate,
                    // byte-identical. In the polyline / suppressed case our marker is the
                    // sole position indicator, so it must draw - otherwise an airless
                    // descent (e.g. the Mun) shows the polyline with no ghost icon.
                    //
                    // Tracer: resolve the decision through the diagnostics overload to surface the
                    // four disjuncts (behavior-identical to the parameterless overload). ghostPid==0
                    // means no resolvable proto pid -> proto icon assumed active, decShouldDraw stays
                    // false.
                    bool decision;
                    if (ghostPid == 0)
                    {
                        decision = false;
                    }
                    else
                    {
                        decision = GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(
                            ghostPid, out decDirectorTraced,
                            out decPolylineOwning, out decIconSuppressed);
                    }
                    decShouldDraw = decision;
                    if (ghostPid == 0 || !decision)
                    {
                        summary.NativeIcon++;
                        decOutcome = MapRenderTrace.MarkerOutcome.DrawnProtoIcon;
                        EmitMarkerDecision();
                        continue; // native icon is active — skip our marker
                    }
                }
                if (kvp.Key < committed.Count)
                {
                    if (committed[kvp.Key].IsDebris)
                    {
                        summary.Debris++;
                        decOutcome = MapRenderTrace.MarkerOutcome.SkippedDebris;
                        EmitMarkerDecision();
                        continue;
                    }
                    string chainId = committed[kvp.Key].ChainId;
                    if (!string.IsNullOrEmpty(chainId) && chainTipIndexBuffer.Count > 0
                        && chainTipIndexBuffer.TryGetValue(chainId, out int tip) && kvp.Key != tip
                        // C1: do NOT skip the active descent-icon carrier (the in-window descent-set member) -
                        // its descent leg is the one drawn this frame, so its icon must ride it even though it is
                        // not the chain index-tip. -1 for non-descent units, so this is byte-identical there.
                        && kvp.Key != descentIconCarrier)
                    {
                        summary.ChainNonTip++;
                        decOutcome = MapRenderTrace.MarkerOutcome.SkippedChainNonTip;
                        EmitMarkerDecision();
                        continue; // not the tip — skip duplicate
                    }
                }

                // Resolve marker world position: use ghost mesh when it is actually POSITIONED, otherwise
                // compute from trajectory data (map view only — hidden ghosts need icons too).
                // A meshActive ghost whose transform sits at the world origin is stale / unpositioned: in map
                // view the engine drives a far ghost by its orbit/trajectory and leaves the hidden mesh
                // transform at (0,0,0) (the FloatingOrigin #245/#247 artifact), so reading it would pin the
                // labelled marker to the map centre (the "static yellow-label icon in the wrong location"
                // seam the playtest showed via Marker pos markerPos=(0,0,0)). Treat a near-origin transform
                // as unpositioned and fall through to the trajectory-derived position below; a genuinely
                // positioned mesh is never at the floating-origin centre (it is a different vessel).
                // During a non-orbital phase the trajectory polyline owns the rendering and the stock
                // proto orbit icon is hidden (GhostOrbitLinePatch drawIcons=NONE). The ghost mesh
                // transform stops being driven per-frame in that phase because the orbit-segment drive
                // path is not active and any state-vector reseed only refreshes the orbit at the orbit-
                // resolve cadence (~5s), so the mesh transform freezes at the last seeded position
                // between reseeds even as the recording's effUT advances. The labelled non-proto marker
                // would then ride that stale mesh and read as "frozen at the polyline's start" - the
                // exact playtest seam. During polyline ownership the recorded trajectory points are the
                // source of truth (the polyline draws them), so fall through to TryComputeGhostWorldPosition
                // at the loop-mapped effUT below. The diagnostic "Marker pos: ... meshVsTraj" makes this
                // explicit by reporting both sources.
                //
                // PolylineReleaseGraceSec extends the "polyline phase" through a short grace window
                // after the polyline releases ownership: the seg-drive dispatcher runs on a ~0.5s
                // cadence, so the next orbital segment is not applied on the same frame as release.
                // For ~12 frames in between, the mesh transform is still stale at the pre-polyline
                // position. Without the grace, the marker reads that stale mesh and snaps backward by
                // hundreds of km (the parking-orbit endpoint vs the loiter), which is what the playtest
                // reported as "icon teleported far away to the wrong position on the loiter". The grace
                // suppresses that stale read until the seg-drive catches up; trajPos / TryComputeGhost
                // WorldPosition is preferred when available, and the marker simply does not draw for
                // those frames when the recording has no points covering the post-cut effUT (gracefully
                // invisible for ~200ms rather than teleporting). End-of-grace lands on the freshly seg-
                // driven mesh transform and the marker resumes normally.
                uint ghostPidForPhase = GhostMapPresence.HasGhostVesselForRecording(kvp.Key)
                    ? GhostMapPresence.GetGhostVesselPidForRecording(kvp.Key)
                    : 0u;
                // Polyline ownership including the post-release grace - shared with GhostOrbitLinePatch.
                // The stamping happens in the orbit-line patch every frame the polyline owns; here we
                // just read. The grace duration matches Patches.GhostOrbitLinePatch.PolylineReleaseGraceSeconds.
                bool polylinePhase;
                if (ghostPidForPhase != 0)
                {
                    polylinePhase = GhostMapPresence.IsPolylineRecentlyOwningGhostPhase(
                        ghostPidForPhase,
                        Parsek.Patches.GhostOrbitLinePatch.PolylineReleaseGraceSeconds);
                }
                else
                {
                    // PID-less recording (a chain descent / atmospheric-only recording with NO proto ghost
                    // vessel and no OrbitSegments, e.g. the Duna re-aim descent member): there is no ghost PID
                    // to stamp polyline ownership against, so resolve ownership directly by RecordingId from
                    // the polyline Driver's per-frame publish. Without this the labeled marker for such a
                    // recording never rode its own drawn body-fixed line (polylinePhase stayed false) and the
                    // icon fell back to the body-fixed head, off the line. No post-release grace applies (there
                    // is no PID stamp), and TryAnchorMarkerToPolyline below still self-gates on the leg being
                    // drawn THIS frame, so the marker rides only while the line is actually drawn.
                    polylinePhase = kvp.Key < committed.Count
                        && committed[kvp.Key] != null
                        && Parsek.Display.GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(
                            committed[kvp.Key].RecordingId);
                }
                Vector3 markerPos;
                bool markerRidesPolyline = false;
                bool meshPositioned = meshActive && state.ghost.transform.position.sqrMagnitude > 1f
                    && !polylinePhase;
                if (meshPositioned)
                {
                    markerPos = state.ghost.transform.position;
                    decPosSource = "mesh";
                }
                else
                {
                    decPosSource = "traj";
                    // A looped-mission member's recorded trajectory points sit at the ORIGINAL
                    // (past) recorded UTs, so sampling at the LIVE UT is always OutsideTimeRange and
                    // the custom icon never draws (the engine positions the mesh at the shared
                    // span-clock effUT instead, but the mesh is hidden here by zone distance). Map
                    // the live UT to that same span-clock effUT - identity for a non-loop ghost or an
                    // Empty unit set, so non-loop behavior is unchanged - and skip the frame when the
                    // shared clock has this member outside its render window (renderHidden), matching
                    // the engine / proto-vessel paths.
                    double sampleUT = currentUT;
                    if (kvp.Key < committed.Count)
                    {
                        Recording recForUT = committed[kvp.Key];
                        double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                            kvp.Key, recForUT.StartUT, recForUT.EndUT, currentUT,
                            flight.Engine.CurrentLoopUnits, out bool loopHidden);
                        if (loopHidden)
                        {
                            summary.LoopHidden++;
                            decOutcome = MapRenderTrace.MarkerOutcome.SkippedLoopHidden;
                            EmitMarkerDecision();
                            continue;
                        }
                        sampleUT = effUT;
                    }

                    // M4b: derotate body-fixed marker sampling by the member's per-launch shift
                    // (0 for non-members / knob-less schedules).
                    double markerBodyFixedShift =
                        GhostPlaybackLogic.ComputeUnitMemberBodyFixedShiftSeconds(
                            kvp.Key, currentUT, sampleUT, flight.Engine.CurrentLoopUnits);

                    if (!TryComputeGhostWorldPosition(
                            kvp.Key,
                            committed,
                            sampleUT,
                            out markerPos,
                            out MapMarkerPositionFailureReason failureReason,
                            markerBodyFixedShift))
                    {
                        summary.PositionFailure++;
                        if (failureReason == MapMarkerPositionFailureReason.MissingBody)
                            summary.MissingBody++;
                        decOutcome = MapRenderTrace.MarkerOutcome.SkippedPositionFail;
                        EmitMarkerDecision();
                        continue;
                    }

                    // Ride the conic-anchored polyline: while the trajectory polyline owns this phase, the
                    // labeled marker (icon + label) sits ON the drawn burn line instead of the body-fixed
                    // head, which is ~96 deg off the loiter/hyperbola conics under the loop shift. Samples
                    // the same per-frame drawn points the line uses, so it matches exactly; a no-op outside
                    // an anchored leg or when the leg was not drawn this frame.
                    bool rideAttempted = polylinePhase && kvp.Key < committed.Count;
                    if (rideAttempted
                        && Parsek.Display.GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline(
                            committed[kvp.Key].RecordingId, sampleUT, out Vector3 onLineMarkerPos,
                            out decRideReason, out decRideLeg))
                    {
                        markerPos = onLineMarkerPos;
                        markerRidesPolyline = true;
                        decPosSource = "polyline";
                        if (ghostPidForPhase == 0)
                            ParsekLog.VerboseRateLimited(
                                "GhostMap",
                                "pidless-polyline-ride." + committed[kvp.Key].RecordingId,
                                "PID-less marker rides its own polyline: rec=" + kvp.Key
                                    + " recId=" + committed[kvp.Key].RecordingId);
                    }
                }

                string ghostName = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                // Use RecordingId as marker key so hover/sticky state follows the recording,
                // not the array index (which may shuffle when recordings are added/removed).
                string markerKey = kvp.Key < committed.Count ? committed[kvp.Key].RecordingId : null;
                VesselType vtype = kvp.Key < committed.Count
                    ? GhostMapPresence.ResolveVesselType(committed[kvp.Key].VesselSnapshot)
                    : VesselType.Ship;
                Color markerColor = GetGhostMarkerColorForType(vtype);
                DrawMapMarkerAt(markerPos, markerKey, ghostName, markerColor, vtype);
                summary.Drawn++;
                // Review MINOR-1 dedup input: remember which CHAINS drew a labeled marker this frame
                // so the ghost-less fallback below never adds a second one for the same chain.
                if (kvp.Key < committed.Count
                    && !string.IsNullOrEmpty(committed[kvp.Key].ChainId))
                    drawnChainMarkerBuffer.Add(committed[kvp.Key].ChainId);

                // Tracer: the non-proto labeled marker drew. Emit the per-pid change-based decision
                // line carrying WHY (the disjuncts), the ride reason + leg, and the position source.
                decOutcome = MapRenderTrace.MarkerOutcome.DrawnNonProto;
                EmitMarkerDecision();

                // Comprehensive per-marker DRAW log (always available, rate-limited, NOT tracing-gated):
                // EVERY non-proto labelled marker logs WHERE it drew + its position SOURCE - mesh-ridden
                // or trajectory-derived (polyline phase). A "yellow label in the wrong place" - e.g. riding
                // the body-fixed escape-burn polyline during a burn - reads in one line: source=traj
                // polylinePhase=True with the drawn world position. Built via the lazy Func overload so the
                // string.Format + markerPos.ToString are paid only when the 2s rate-limit actually emits,
                // not every frame for every ghost. (The detailed mesh-vs-recorded-path gap is tracing-gated below.)
                int recIdxForLog = kvp.Key;
                bool meshPositionedForLog = meshPositioned;
                bool polylinePhaseForLog = polylinePhase;
                bool meshActiveForLog = meshActive;
                bool ridesPolylineForLog = markerRidesPolyline;
                VesselType vtypeForLog = vtype;
                Vector3 markerPosForLog = markerPos;
                string ghostNameForLog = ghostName;
                ParsekLog.VerboseRateLimited("GhostMap",
                    "marker-draw-" + recIdxForLog.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Marker DRAWN: rec={0} vessel={1} source={2} polylinePhase={3} meshActive={4} " +
                        "meshPositioned={5} ridesPolyline={6} vtype={7} drawnPos={8}",
                        recIdxForLog, ghostNameForLog, meshPositionedForLog ? "mesh" : "traj", polylinePhaseForLog,
                        meshActiveForLog, meshPositionedForLog, ridesPolylineForLog, vtypeForLog,
                        markerPosForLog.ToString("F0")),
                    2.0);

                // MapRenderTrace IMGUI surface coverage (ImguiLabeledMarker). Decision-only: the
                // labeled marker draws here in OnGUI, so the projected world position IS the truth
                // (no end-of-frame reconciliation). Gated + rate-limited inside EmitMarker.
                if (MapRenderTrace.IsEnabled)
                    MapRenderTrace.EmitMarker(
                        MapRenderTrace.RenderSurface.ImguiLabeledMarker, markerKey, currentUT,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "vessel={0} markerPos={1} mapView={2} meshActive={3}",
                            ghostName, MapRenderTrace.FormatVector3(markerPos), isMapView, meshActive));

                // Diagnostic (loiter-seam icon debugging): when the marker rides the LIVE ghost mesh
                // (meshActive), compare the drawn mesh position against the trajectory-interpolated position
                // at the same span-clock head UT. Both are in the current world frame, so their gap
                // (meshVsTraj) is frame-independent: a large gap means the engine's mesh sits off the
                // recorded path (the "non-proto icon in the wrong location / not moving correctly on the
                // polyline" seam), while a ~0 gap means the icon is on the path and any apparent freeze is
                // just the sub-pixel parking circle at map scale. Rate-limited per recording.
                //
                // Gated behind the map-render tracer (off in normal play, so the per-frame
                // TryComputeGhostWorldPosition probe is skipped too) and emitted only when a
                // trajectory position exists to compare against: a NaN meshVsTraj (no trajPos)
                // carries no signal, so those frames are skipped rather than logged (they were
                // ~93% of this line's volume).
                if (MapRenderTrace.IsEnabled && meshActive && isMapView && kvp.Key < committed.Count)
                {
                    var recDiag = committed[kvp.Key];
                    double headUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                        kvp.Key, recDiag.StartUT, recDiag.EndUT, currentUT,
                        flight.Engine.CurrentLoopUnits, out bool _);
                    if (TryComputeGhostWorldPosition(
                            kvp.Key, committed, headUT, out Vector3 trajPos, out _))
                    {
                        Vector3 meshXform = state.ghost.transform.position;
                        double meshVsTraj = (meshXform - trajPos).magnitude;
                        ParsekLog.VerboseRateLimited("GhostMap",
                            "marker-pos-" + kvp.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Marker pos: rec={0} meshPositioned={1} headUT={2:F1} drawnPos={3} meshXform={4} trajPos={5} meshVsTraj={6:F0}",
                                kvp.Key, meshPositioned, headUT, markerPos.ToString("F0"),
                                meshXform.ToString("F0"), trajPos.ToString("F0"), meshVsTraj),
                            2.0);
                    }
                }
            }

            // Playtest-13 (map view): a chain ghost destroyed MID-PHASE - the engine retires it at the
            // below-surface Duna descent boundary ('chain-loop unit member outside its window') - leaves
            // the recording with NO ghostStates entry, so the pid-keyed walk above never considers it and
            // the icon vanished on the landing chord while the polyline kept drawing it. Recording-keyed
            // fallback (the TS atmospheric-marker equivalent): any committed recording that the walk
            // above did NOT cover and whose polyline OWNS the current phase (exactly one per chain - the
            // head-gated active member) draws its labeled marker riding the drawn line. The ride
            // self-gates on the leg having drawn this frame (or the short HeldLastGood pan-hold of a
            // position that was on the drawn line moments earlier), so an undrawn phase can never
            // paint a marker.
            if (isMapView)
            {
                // Render-EVENT decision emit for the GHOSTLESS fallback marker (no live engine ghost state;
                // the marker rides the drawn polyline leg). Without this the ghostless marker was invisible to
                // the MapRenderTrace marker-decision tracer (only a GhostMap VerboseRateLimited narrative
                // line), so its appear (DrawnNonProto) / in-candidate disappear (loop-hidden / ride-failed)
                // were silent at the decision tier - the flight-map gap vs the clean TS path. Keyed by
                // rec.RecordingId (the pid-less recording-scoped identity); mutually exclusive with the main
                // pid-keyed walk's key (this loop only handles recordings with NO ghostState), so no thrash.
                // The recording-level leg appear/disappear itself is the polyline ownership flip, already an
                // EVENT via GhostTrajectoryPolylineRenderer's PolylineLegChange. Gated; read-only.
                void EmitGhostlessDecision(
                    Recording r, int idx, MapRenderTrace.MarkerOutcome outcome,
                    MapRenderTrace.MarkerRideReason ride, string posSource, double decisionEffUT)
                {
                    if (!MapRenderTrace.IsEnabled || r == null || string.IsNullOrEmpty(r.RecordingId))
                        return;
                    MapRenderTrace.EmitMarkerDecisionOnChange(
                        MapRenderTrace.RenderSurface.ImguiLabeledMarker, r.RecordingId, traceUT,
                        MapRenderTrace.BuildMarkerDecisionSignature(
                            idx, r.VesselName ?? "Ghost",
                            directorTracedPathActive: false, polylineOwning: true,
                            iconSuppressed: false, shouldDrawNonProto: true,
                            outcome, ride, legIndex: -1, posSource),
                        // The loop-shifted sample UT the ghostless head was resolved at (a looped re-aim
                        // member's real ~2.5e9 region UT), so the line no longer mislabels effUT==currentUT.
                        effUT: decisionEffUT);
                }

                for (int ri = 0; ri < committed.Count; ri++)
                {
                    var rec = committed[ri];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                    // GUI-P1: the per-recording playback tick box is OFF - no marker anywhere.
                    // Transitively covered already (this loop only draws for a recording whose
                    // polyline OWNS the phase, and the gated Driver walk never publishes
                    // ownership for a hidden recording), but stated here so the flight-map
                    // non-proto marker path carries the gate explicitly rather than inheriting
                    // it from another file's draw decision.
                    if (GhostMapPresence.IsMapPresenceHiddenByPlaybackToggle(rec))
                    {
                        summary.PlaybackDisabled++;
                        continue;
                    }
                    if (flight.Engine.ghostStates.TryGetValue(ri, out var coveredState)
                        && coveredState != null)
                        continue; // covered (drawn or intentionally skipped) by the pid-keyed walk
                    if (!Parsek.Display.GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(
                            rec.RecordingId))
                        // Not a ghostless candidate (the polyline does not own this phase). No decision emit:
                        // the leg's disappear is the polyline ownership flip, already a PolylineLegChange
                        // EVENT; emitting a Skipped here would burst over every inactive recording.
                        continue;
                    // Review MINOR-1: if this chain already drew a LABELED marker via the pid-keyed
                    // walk (warp handoff: a sibling member still holds a live ghost state), skip -
                    // never two labeled markers for one chain. A chain whose live sibling showed only
                    // its stock vessel icon still gets the playing head marked here.
                    if (!string.IsNullOrEmpty(rec.ChainId)
                        && drawnChainMarkerBuffer.Contains(rec.ChainId))
                        continue;
                    double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                        ri, rec.StartUT, rec.EndUT, currentUT,
                        flight.Engine.CurrentLoopUnits, out bool ghostlessHidden);
                    if (ghostlessHidden)
                    {
                        EmitGhostlessDecision(rec, ri, MapRenderTrace.MarkerOutcome.SkippedLoopHidden,
                            MapRenderTrace.MarkerRideReason.NotAttempted, "polyline", effUT);
                        continue;
                    }
                    if (!Parsek.Display.GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline(
                            rec.RecordingId, effUT, out Vector3 onLinePos))
                    {
                        EmitGhostlessDecision(rec, ri, MapRenderTrace.MarkerOutcome.SkippedPositionFail,
                            MapRenderTrace.MarkerRideReason.FallbackHeadOutsideLegs, "polyline", effUT);
                        continue;
                    }
                    VesselType ghostlessType = GhostMapPresence.ResolveVesselType(rec.VesselSnapshot);
                    DrawMapMarkerAt(
                        onLinePos, rec.RecordingId, rec.VesselName ?? "Ghost",
                        GetGhostMarkerColorForType(ghostlessType), ghostlessType);
                    summary.Drawn++;
                    EmitGhostlessDecision(rec, ri, MapRenderTrace.MarkerOutcome.DrawnNonProto,
                        MapRenderTrace.MarkerRideReason.RodeLeg, "polyline", effUT);
                    ParsekLog.VerboseRateLimited("GhostMap",
                        "ghostless-polyline-marker." + rec.RecordingId,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Ghost-less polyline marker: rec={0} recId={1} effUT={2:F1} " +
                            "(engine ghost absent; marker rides the drawn leg)",
                            ri, rec.RecordingId, effUT),
                        5.0);
                }
            }

            LogMapMarkerSummary(summary);
        }

        /// <summary>
        /// Slice (iii): draw ONE per-instance overlap marker for a single live overlap cycle at its
        /// own playback head UT (<paramref name="headUT"/> = <c>ComputeOverlapCyclePlaybackUT(cycle)</c>),
        /// riding the SINGLE shared polyline keyed by RecordingId. Returns true when a marker drew, false
        /// when the instance was skipped (head out-of-window / between legs / off-line, or the cycle is
        /// currently represented by a live non-suppressed proto icon). Map-view only (the caller gates
        /// on isMapView).
        ///
        /// Orbital no-double-marker rule: for an ORBITAL overlap recording slice (i) creates N
        /// ProtoVessels with orbit icons. If this cycle has a live instance proto AND its icon is NOT
        /// suppressed, the proto icon already draws the cycle - skip the polyline marker. Otherwise (no
        /// proto for the cycle: pure-suborbital or pre-materialize, OR the icon is suppressed for a
        /// non-orbital phase) the polyline marker is the sole indicator - draw it. For the maintainer's
        /// pure-suborbital case overlapInstanceVessels is empty so every cycle takes the draw branch.
        ///
        /// Position contract mirrors the single-instance polyline tail in DrawMapMarkers verbatim:
        /// resolve the body-fixed head via TryComputeGhostWorldPosition (skip on failure), then RIDE the
        /// shared polyline via TryAnchorMarkerToPolyline - use the on-line position when it rides, else
        /// skip this instance's marker (never draw off-line). The per-instance marker key is
        /// <c>recId + "#" + cycle</c> so hover/sticky state is independent across instances; the visible
        /// label is the shared mission name for all N (they ARE the same mission).
        /// </summary>
        private bool DrawOneOverlapInstanceMarker(
            int recordingIndex, IReadOnlyList<Recording> committed, double headUT, long cycle)
        {
            if (committed == null || recordingIndex < 0 || recordingIndex >= committed.Count)
                return false;
            Recording rec = committed[recordingIndex];
            if (rec == null)
                return false;

            // Orbital no-double-marker join: if a live, non-suppressed proto icon already draws this
            // cycle, skip the polyline marker. A pid of 0 means no proto for the cycle (pure-suborbital
            // or not yet materialized) -> draw the polyline marker.
            uint instancePid = GhostMapPresence.TryGetOverlapInstancePidForCycle(recordingIndex, cycle);
            if (instancePid != 0
                && !GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(instancePid))
            {
                // The proto icon owns this cycle this frame — no polyline marker (no double).
                return false;
            }

            // Body-fixed head for the instance; skip on failure (out-of-window / between legs).
            if (!TryComputeGhostWorldPosition(
                    recordingIndex, committed, headUT, out Vector3 markerPos, out _))
                return false;

            // Ride the SINGLE shared polyline at this instance's head; never draw off-line.
            if (!Parsek.Display.GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline(
                    rec.RecordingId, headUT, out Vector3 onLinePos))
                return false;
            markerPos = onLinePos;

            // Per-instance key (hover-collision fix): distinct keys, identical labels are fine
            // (MapMarkerRenderer keys hover/sticky on markerKey, not the label).
            string markerKey = string.IsNullOrEmpty(rec.RecordingId)
                ? null
                : rec.RecordingId + "#" + cycle.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string label = rec.VesselName ?? "Ghost";
            VesselType vtype = GhostMapPresence.ResolveVesselType(rec.VesselSnapshot);
            Color markerColor = GetGhostMarkerColorForType(vtype);
            DrawMapMarkerAt(markerPos, markerKey, label, markerColor, vtype);

            // Throttle key is per-RECORDING (rec.RecordingId), NOT the per-cycle markerKey: at high
            // time warp the overlap cycle index advances every frame, so a per-cycle key yields a fresh
            // key each frame and defeats VerboseRateLimited (a per-marker flood). The cycle stays in the
            // detail; the per-recording `overlap-instance-markers` summary carries the drawn count.
            if (MapRenderTrace.IsEnabled)
                MapRenderTrace.EmitMarker(
                    MapRenderTrace.RenderSurface.ImguiLabeledMarker, rec.RecordingId, headUT,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "vessel={0} cycle={1} markerPos={2} overlapInstance=True",
                        label, cycle, MapRenderTrace.FormatVector3(markerPos)));

            return true;
        }

        /// <summary>
        /// Computes a ghost's world position from trajectory data when the ghost mesh is hidden.
        /// Uses InterpolatePoints for smooth movement between recorded trajectory points.
        /// Returns false if the recording is out of UT range or has no trajectory data.
        /// </summary>
        private bool TryComputeGhostWorldPosition(
            int recordingIndex,
            IReadOnlyList<Recording> committed,
            double ut,
            out Vector3 worldPos,
            out MapMarkerPositionFailureReason failureReason,
            double bodyFixedShiftSeconds = 0.0)
        {
            worldPos = Vector3.zero;
            failureReason = MapMarkerPositionFailureReason.None;
            if (committed == null || recordingIndex < 0 || recordingIndex >= committed.Count)
            {
                failureReason = MapMarkerPositionFailureReason.BadRecordingIndex;
                return false;
            }

            var rec = committed[recordingIndex];
            if (rec.Points == null || rec.Points.Count == 0)
            {
                failureReason = MapMarkerPositionFailureReason.NoTrajectoryPoints;
                return false;
            }
            if (ut < rec.StartUT || ut > rec.EndUT)
            {
                failureReason = MapMarkerPositionFailureReason.OutsideTimeRange;
                return false;
            }

            // RELATIVE-frame dispatch: rec.Points in a Relative section carries anchor-local
            // METRE OFFSETS in latitude/longitude/altitude, so reading them geographically puts
            // the marker inside the planet. Use the section's body-fixed shadow when it has one,
            // and refuse otherwise - the marker is a fallback affordance, so no marker beats a
            // marker at a fabricated position (the same "retire, never clamp" rule the
            // parent-anchored playback path follows).
            MapMarkerFrameSource frameSource = ResolveMapMarkerFrameSource(
                rec.TrackSections, ut, out List<TrajectoryPoint> sectionSource);
            if (frameSource == MapMarkerFrameSource.RelativeUnresolved)
            {
                failureReason = MapMarkerPositionFailureReason.RelativeFrameWithoutBodyFixed;
                return false;
            }
            List<TrajectoryPoint> samples =
                frameSource == MapMarkerFrameSource.SectionBodyFixedFrames
                    ? sectionSource
                    : rec.Points;

            int cachedIdx;
            if (!mapMarkerCachedIndices.TryGetValue(recordingIndex, out cachedIdx))
                cachedIdx = -1;
            // The cached index indexes rec.Points; a per-section list is a different array, so
            // start cold for it and do not write the section-local index back.
            bool useCachedIndex = frameSource == MapMarkerFrameSource.FlatPoints;
            int sampleIdx = useCachedIndex ? cachedIdx : -1;

            TrajectoryPoint before, after;
            float t;
            bool found = TrajectoryMath.InterpolatePoints(samples, ref sampleIdx, ut,
                out before, out after, out t);
            if (useCachedIndex)
                mapMarkerCachedIndices[recordingIndex] = sampleIdx;

            double lat, lon, alt;
            if (found)
            {
                lat = before.latitude + (after.latitude - before.latitude) * t;
                lon = before.longitude + (after.longitude - before.longitude) * t;
                alt = TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t);
            }
            else
            {
                lat = before.latitude;
                lon = before.longitude;
                alt = before.altitude;
            }

            CelestialBody body;
            if (!bodyCache.TryGetValue(before.bodyName, out body))
            {
                body = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
                if (body != null)
                    bodyCache[before.bodyName] = body;
            }
            if (body == null)
            {
                failureReason = MapMarkerPositionFailureReason.MissingBody;
                return false;
            }

            // M4b phasing-knob body-fixed derotation (identity at shift 0): a knob-shifted
            // launch's vacuum point must land at its recorded INERTIAL spot, not ride the
            // planet's extra rotation (the map-icon teleports near the station).
            lon = TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(
                lon, bodyFixedShiftSeconds, body);
            worldPos = (Vector3)body.GetWorldSurfacePosition(lat, lon, alt);
            return true;
        }

        /// <summary>
        /// Clears cached waypoint indices used for trajectory-derived map markers.
        /// Must be called when recordings are reindexed (deletion, optimization pass).
        /// </summary>
        internal void ClearMapMarkerCache()
        {
            mapMarkerCachedIndices.Clear();
            bodyCache.Clear();
        }

        /// <summary>
        /// Returns the orbit color for a ghost marker by vessel type.
        /// Delegates to MapMarkerRenderer.GetColorForType for consistency
        /// between flight map view and tracking station.
        /// </summary>
        private static Color GetGhostMarkerColorForType(VesselType vtype)
        {
            return MapMarkerRenderer.GetColorForType(vtype);
        }

        /// <summary>
        /// Flight-scene marker draw. Picks FlightCamera or PlanetariumCamera based on
        /// current view, then delegates to <see cref="MapMarkerRenderer.DrawMarkerAtScreen"/>
        /// so icon lookup, label hover/sticky, and click handling all stay in one place.
        /// </summary>
        private void DrawMapMarkerAt(Vector3 worldPos, string markerKey, string label, Color color,
            VesselType vtype = VesselType.Ship)
        {
            Vector3 screenPos;
            if (MapView.MapIsEnabled)
            {
                Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
                if (PlanetariumCamera.Camera == null) return;
                screenPos = PlanetariumCamera.Camera.WorldToScreenPoint(scaledPos);
            }
            else
            {
                if (FlightCamera.fetch == null || FlightCamera.fetch.mainCamera == null) return;
                screenPos = FlightCamera.fetch.mainCamera.WorldToScreenPoint(worldPos);
            }

            // Behind camera
            if (screenPos.z < 0) return;

            MapMarkerRenderer.DrawMarkerAtScreen(
                new Vector2(screenPos.x, screenPos.y), markerKey, label, color, vtype);
        }
    }
}
