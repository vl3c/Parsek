using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// End-of-frame truth probe for the map / tracking-station render path,
    /// folded in from the <c>GhostRenderStateProbe</c> prototype and gated on the
    /// <c>mapRenderTracing</c> setting (via <see cref="MapRenderTrace.IsEnabled"/>).
    /// Off by default; when disabled every <see cref="LateUpdate"/> is a
    /// null-check plus a bool check and returns with no reflection, sampling, or
    /// allocation.
    ///
    /// <para>Reads the ACTUALLY RENDERED state per tracked ghost
    /// (<see cref="OrbitRendererBase"/> enabled / drawMode / drawIcons, the
    /// <c>VectorLine.active</c> truth read through the public
    /// <see cref="OrbitRendererBase.OrbitLine"/> property,
    /// <see cref="OrbitDriver"/> orbit body / sma / ecc,
    /// and <see cref="Vessel.GetWorldPos3D"/>) and emits:
    /// <list type="bullet">
    /// <item>Tier-A <c>FirstPosition</c>: the probe-derived MVP variant, emitted
    /// once on the first end-of-frame truth read for a pid (world position + orbit
    /// body/sma/ecc, no decision hook, no recordingId).</item>
    /// <item>Tier-B change-based truth: one <c>MapRenderTrace</c> line per field
    /// only when that field changes for a pid (so a 1-frame toggle out and back is
    /// two lines, steady state is one line then quiet).</item>
    /// <item>Tier-C anomalies: <c>icon-jump</c> (world-position delta exceeds the
    /// orbit-derived expected motion, floored by the prototype's fixed threshold)
    /// and <c>line-blink</c> (<c>line.active</c> toggled within N frames). Decided
    /// by the pure predicates <see cref="MapRenderTrace.IsIconJump"/> /
    /// <see cref="MapRenderTrace.IsLineBlink"/>.</item>
    /// </list></para>
    ///
    /// <para>Read-only: never mutates renderer / orbit / line state. Placed at
    /// <see cref="DefaultExecutionOrderAttribute"/> 10000 so the LateUpdate runs
    /// AFTER <c>OrbitRendererBase.LateUpdate</c> (order 0, where
    /// <c>GhostOrbitLinePatch.Postfix</c> runs) and after the polyline Driver
    /// (order -50). Iterates <see cref="GhostMapPresence.ghostMapVesselPids"/>
    /// directly and resolves each pid via <see cref="FlightGlobals.FindVessel"/>,
    /// so the enabled-path cost is O(ghosts). Per-pid state is cleared on scene
    /// transition so a stale previous world position does not fire a spurious
    /// jump on a tracking-station &lt;-&gt; flight switch.</para>
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [KSPAddon(KSPAddon.Startup.Instantly, true /* once */)]
    internal sealed class MapRenderProbe : MonoBehaviour
    {
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;
        private static MapRenderProbe instance;

        // Soft rate-limit on the jump anomaly so a runaway hyperbola fling cannot
        // flood the log; we still get a discrete event per distinct teleport
        // every ~0.5 s real time at worst.
        private const double JumpAnomalyMinIntervalSeconds = 0.5;

        // Soft rate-limit on the icon-off-orbit anomaly. Unlike a jump (a discrete
        // event), an off-orbit icon is a PERSISTENT condition - it would fire every
        // frame - so this throttles to one line per pid per second while the icon
        // sits off its line, enough to read the angle without flooding.
        private const double OffOrbitAnomalyMinIntervalSeconds = 1.0;

        // Soft rate-limit on the in-window per-frame full snapshot (phase=Snapshot). A persistent
        // anomaly keeps the detailed window open continuously, so an un-throttled snapshot floods
        // the log per-frame for the whole stretch. Sample at ~2 Hz per pid - enough to capture the
        // motion through a window while collapsing a sustained anomaly from per-frame to ~2/s; the
        // on-change truth lines still record every transition exactly.
        private const double SnapshotMinIntervalSeconds = 0.5;

        // Per-pid truth state, all cleared on scene change so a stale entry never
        // fires a spurious anomaly across a TS <-> flight transition. The probe
        // tracks last-value strings locally (rather than leaning on ParsekLog's
        // VerboseOnChange dict) so the scene-change clear is a single Clear() and
        // the just-reset suppression is exact.
        private readonly Dictionary<uint, Vector3d> prevWorldPos = new Dictionary<uint, Vector3d>();
        // Body-relative (orbit-frame) position per pid = GetWorldPos3D - referenceBody.position.
        // This is the icon-jump DECISION quantity. prevWorldPos above is retained only to log
        // the raw-world delta as context (so a re-test shows the frame contamination magnitude).
        // Measuring the jump in the orbit's own reference-body frame cancels the floating-origin
        // and reference-body world motion that otherwise dominates a distant ghost's raw world
        // delta at high warp (KSP builds an on-rails vessel's world position as
        // referenceBody.position + orbitRelative, so the body-relative delta IS the orbital arc).
        private readonly Dictionary<uint, Vector3d> prevBodyRelPos = new Dictionary<uint, Vector3d>();
        // Reference-body name backing the body-relative position per pid, so an SOI crossing
        // (e.g. Kerbin -> Sun) is detected and the cross-frame body-relative delta suppressed.
        private readonly Dictionary<uint, string> lastIconJumpBody = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> lastLineActive = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> lastRendererEnabled = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> lastDrawIcons = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> lastBodyOrbit = new Dictionary<uint, string>();
        // Last-sampled proto-icon suppression truth per pid (ghostsWithSuppressedIcon membership), so the
        // suppressed<->restored flip emits ONE on-change EVENT (surface=ProtoIcon) carrying recId + the
        // before->after. The probe already iterates every ghost each frame and reads IsIconSuppressed as an
        // anomaly guard, so this is a single extra dict read; no new observer. Cleared on scene switch.
        private readonly Dictionary<uint, string> lastIconSuppressed = new Dictionary<uint, string>();
        // Frame of the last line.active toggle per pid (for the blink predicate).
        private readonly Dictionary<uint, int> lastLineToggleFrame = new Dictionary<uint, int>();
        // Soft rate-limit timestamps for the per-pid jump anomaly.
        private readonly Dictionary<uint, double> lastJumpEmitRealtime = new Dictionary<uint, double>();
        // Soft rate-limit timestamps for the per-pid in-window per-frame snapshot.
        private readonly Dictionary<uint, double> lastSnapshotEmitRealtime = new Dictionary<uint, double>();
        // Soft rate-limit timestamps for the per-pid icon-off-orbit anomaly.
        private readonly Dictionary<uint, double> lastOffOrbitEmitRealtime = new Dictionary<uint, double>();
        // Soft rate-limit timestamps for the per-pid polyline-orbit-overlap anomaly (a PERSISTENT
        // condition through a whole burn, so it must not fire every frame).
        private readonly Dictionary<uint, double> lastPolylineOverlapEmitRealtime = new Dictionary<uint, double>();
        // Phase 8e S0: soft rate-limit timestamps for the per-RECORDING unaccounted-drawn-recording
        // anomaly (Instrument 1). Keyed by RecordingId, not pid, because the drawn set is RecordingId-
        // keyed and a drawn recording may be proto-LESS (pid 0). A PERSISTENT condition (would fire every
        // frame), so throttled to one line per recording per second; cleared on scene change.
        private const double UnaccountedAnomalyMinIntervalSeconds = 1.0;
        private readonly Dictionary<string, double> lastUnaccountedEmitRealtime =
            new Dictionary<string, double>(System.StringComparer.Ordinal);
        // Pids that have already had their Tier-A FirstPosition event emitted on
        // the first end-of-frame truth read for that pid. Cleared on scene change
        // alongside the other per-pid state, so re-entering a scene re-emits a
        // fresh FirstPosition for the rebuilt ghost (matching the prevWorldPos
        // reset semantics).
        private readonly HashSet<uint> firstPositionEmittedPids = new HashSet<uint>();

        void Awake()
        {
            if (instance != null) { Destroy(gameObject); return; }
            instance = this;
            DontDestroyOnLoad(gameObject);
            GameEvents.onGameSceneSwitchRequested.Add(OnGameSceneSwitchRequested);
        }

        void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
                GameEvents.onGameSceneSwitchRequested.Remove(OnGameSceneSwitchRequested);
            }
        }

        // Clear per-pid truth state on scene transition so a stale prevWorldPos
        // carried across a TS <-> flight switch does not fire a spurious
        // icon-jump on re-entry (design-review fix (a)). The first sample after
        // this clear is the per-pid just-reset frame.
        private void OnGameSceneSwitchRequested(
            GameEvents.FromToAction<GameScenes, GameScenes> action)
        {
            ClearAllPerPidState();
            // Also flush MapRenderTrace's pid-keyed stores (detailed windows + line/render intents) so they
            // do not grow unbounded across the AppDomain lifetime; mirrors this probe's own per-pid reset.
            MapRenderTrace.Reset();
            // Drop the reconciler's per-pid intent-reconcile rate-limit timestamps too, so a stale entry
            // cannot suppress the first gap-vs-retire / decision-vs-old-truth divergence after re-entry.
            Parsek.MapRender.GhostRenderReconciler.ClearRateLimitState();
            // Phase 8e S0: drop any S0 coverage state straddling the scene switch (per-frame-cleared by its
            // producer, so this is belt-and-suspenders against a switch landing mid-frame). Diagnostic-only.
            GhostMapPresence.ClearFrameCoverageSets();
            ParsekLog.Verbose(MapRenderTrace.Tag,
                string.Format(ic,
                    "probe per-pid state cleared on scene switch from={0} to={1}",
                    action.from, action.to));
        }

        private void ClearAllPerPidState()
        {
            prevWorldPos.Clear();
            prevBodyRelPos.Clear();
            lastIconJumpBody.Clear();
            lastLineActive.Clear();
            lastRendererEnabled.Clear();
            lastDrawIcons.Clear();
            lastBodyOrbit.Clear();
            lastIconSuppressed.Clear();
            lastLineToggleFrame.Clear();
            lastJumpEmitRealtime.Clear();
            lastSnapshotEmitRealtime.Clear();
            lastOffOrbitEmitRealtime.Clear();
            lastPolylineOverlapEmitRealtime.Clear();
            firstPositionEmittedPids.Clear();
            lastUnaccountedEmitRealtime.Clear();
        }

        void LateUpdate()
        {
            // Off by default: never do per-frame per-ghost reflection in normal
            // play. When disabled this is a single bool check and returns.
            if (!MapRenderTrace.IsEnabled)
                return;

            // Only sample in map-capable scenes.
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.FLIGHT && scene != GameScenes.TRACKSTATION)
                return;

            var pids = GhostMapPresence.ghostMapVesselPids;
            if (pids == null || pids.Count == 0)
                return;

            int frame = Time.frameCount;
            double currentUT = CurrentUT();
            // Routed through the MapRenderTrace seam so the floating-origin
            // suppression frame honours FloatingOriginFrameOverrideForTesting.
            int foShiftFrame = MapRenderTrace.LastFloatingOriginShiftFrame();
            double realtime = UnityRealtimeSinceStartup();

            // Iterate the ghost-pid set directly and resolve each pid to its
            // Vessel via the PersistentVesselIds dict lookup, so the enabled-path
            // cost is O(ghosts), not O(all vessels) (design-review fix (b)).
            int sampled = 0, resolveMisses = 0;
            foreach (uint pid in pids)
            {
                Vessel v;
                if (!FlightGlobals.FindVessel(pid, out v) || v == null)
                {
                    resolveMisses++;
                    continue;
                }
                Sample(v, pid, frame, currentUT, foShiftFrame, realtime);
                sampled++;
            }

            ParsekLog.VerboseRateLimited(MapRenderTrace.Tag, "probe-frame-summary",
                string.Format(ic,
                    "probe frame summary frame={0} ghosts={1} sampled={2} resolveMisses={3}",
                    frame, pids.Count, sampled, resolveMisses),
                5.0);

            // --- Phase 8e S0 Instrument 1: accounted-vs-drawn coverage assertion ---
            // Once per frame (NOT inside the per-pid loop above - a drawn recording may be PROTO-LESS,
            // i.e. pid-0, and therefore absent from ghostMapVesselPids). For EVERY recording the
            // autonomous polyline walk drew this frame, confirm it is ACCOUNTED: its RecordingId maps to
            // a proto-bearing pid (the Director's enumerated set, bridged into the RecordingId domain) OR
            // it is in the proto-less coverage set the walk populated. A drawn recording in NEITHER is a
            // deletion-blocker - the legacy ownership cannot be deleted until this is silent. The whole
            // LateUpdate is already IsEnabled-gated; the per-recId soft rate-limit below keeps a
            // persistent unaccounted recording from flooding the log (the condition holds every frame).
            AssertDrawnRecordingsAccounted(currentUT, realtime);

            // --- Descent render-window per-frame FULL map-object snapshot ---
            // When the polyline Driver (exec-order -50, earlier this frame) published a descent render window
            // (Loiter = the loiter orbit, Descent = the descent-to-landing), dump EVERY rendered map object this
            // frame: all active scene orbit lines (the full FlightGlobals.Vessels walk, not just tracked ghosts)
            // and every live Parsek polyline leg / forward arc (which are NOT in ghostMapVesselPids, so the
            // per-pid loop above never sees them). Deliberately UN-rate-limited per-frame inside the window so a
            // stray / extra trajectory line that renders for only a few frames is caught; the window itself
            // bounds the volume to the loiter + descent phases. No-op every other frame.
            if (MapRenderTrace.TryGetDescentRenderWindow(
                    frame, out string descentPhase, out string descentRecId))
                EmitMapSceneSnapshot(frame, currentUT, descentPhase, descentRecId);
        }

        // Per-frame full snapshot of every RENDERED map object, gated to the descent render window by the
        // caller. Two parts: (1) all scene orbit lines whose VectorLine is active (catches an extra orbit
        // ellipse on any vessel, ghost or not); (2) all live Parsek polyline legs / arcs (the "rendered but not
        // logged" trajectory lines). One Verbose line each, phase=SceneSnapshot, so a log grep on
        // "phase=SceneSnapshot" isolates the whole per-frame map state.
        private void EmitMapSceneSnapshot(int frame, double currentUT, string phase, string recId)
        {
            string headerPid = string.IsNullOrEmpty(recId) ? "(none)" : recId;
            MapRenderTrace.EmitRaw(false, "SceneSnapshot", MapRenderTrace.RenderSurface.Unknown,
                headerPid, currentUT, currentUT,
                string.Format(ic,
                    "descentWindow phase={0} frame={1} -- per-frame full map-object dump (loiter/descent)",
                    phase, frame));

            // (1) Every scene orbit line that is actually drawn (active). Full FlightGlobals.Vessels walk so a
            // non-ghost / unexpected orbit ellipse shows up too.
            int activeOrbitLines = 0, vesselsScanned = 0;
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                for (int vi = 0; vi < vessels.Count; vi++)
                {
                    Vessel v = vessels[vi];
                    if (v == null) continue;
                    vesselsScanned++;
                    OrbitRendererBase rb = v.orbitRenderer;
                    string lineActive = ReadLineActive(rb);
                    if (lineActive != "True") continue; // only RENDERED orbit lines
                    activeOrbitLines++;

                    OrbitDriver dr = v.orbitDriver;
                    string bodyName = "(none)";
                    double sma = double.NaN, ecc = double.NaN;
                    if (dr != null && dr.orbit != null)
                    {
                        CelestialBody b = dr.orbit.referenceBody;
                        if (b != null) bodyName = b.bodyName;
                        sma = dr.orbit.semiMajorAxis;
                        ecc = dr.orbit.eccentricity;
                    }
                    bool isGhost = GhostMapPresence.ghostMapVesselPids != null
                        && GhostMapPresence.ghostMapVesselPids.Contains(v.persistentId);
                    MapRenderTrace.EmitRaw(false, "SceneSnapshot",
                        MapRenderTrace.RenderSurface.ProtoOrbitLine, v.persistentId.ToString(ic),
                        currentUT, currentUT,
                        string.Format(ic,
                            "orbit-line vessel=\"{0}\" pid={1} isGhost={2} lineActive=True renderer.enabled={3} "
                            + "drawMode={4} drawIcons={5} body={6} sma={7} ecc={8}",
                            v.GetName(), v.persistentId, isGhost, rb != null && rb.enabled,
                            rb != null ? rb.drawMode.ToString() : "(no-renderer)",
                            rb != null ? rb.drawIcons.ToString() : "(no-renderer)", bodyName,
                            MapRenderTrace.FormatDouble(sma, "F0"), MapRenderTrace.FormatDouble(ecc, "F4")));
                }
            }

            // (2) Every live Parsek polyline leg / forward arc this frame.
            int polylineLines = 0;
            Parsek.Display.GhostTrajectoryPolylineRenderer.EmitRenderedPolylineSnapshot(
                frame,
                line =>
                {
                    polylineLines++;
                    MapRenderTrace.EmitRaw(false, "SceneSnapshot",
                        MapRenderTrace.RenderSurface.Polyline, headerPid, currentUT, currentUT, line);
                });

            MapRenderTrace.EmitRaw(false, "SceneSnapshot", MapRenderTrace.RenderSurface.Unknown,
                headerPid, currentUT, currentUT,
                string.Format(ic,
                    "scene-snapshot end: vesselsScanned={0} activeOrbitLines={1} polylineLines={2}",
                    vesselsScanned, activeOrbitLines, polylineLines));
        }

        private void AssertDrawnRecordingsAccounted(double currentUT, double realtime)
        {
            GhostMapPresence.AssertDrawnRecordingsAccounted(
                (recId, protoBearingCount, protoLessCoverageCount, drawnCount) =>
                {
                    if (!PassesUnaccountedRateLimit(recId, realtime))
                        return;
                    // Tier-C anomaly. Keyed (surface=Polyline) by the unaccounted RecordingId (carried in
                    // the prefix pid= slot, the marker-surface convention). A CLEAN run (zero of these) is
                    // the deletion-readiness signal: the Director's accounted set is a superset of the
                    // autonomous walk's drawn set.
                    MapRenderTrace.EmitAnomaly(
                        MapRenderTrace.RenderSurface.Polyline, recId, currentUT, currentUT,
                        "unaccounted-drawn-recording",
                        string.Format(ic,
                            "recId={0} drawnByAutonomousWalk=true protoBearing=false inProtoLessCoverage=false "
                            + "| protoBearingRecs={1} protoLessCoverageRecs={2} drawnRecs={3}",
                            recId, protoBearingCount, protoLessCoverageCount, drawnCount),
                        recId);
                });
        }

        private bool PassesUnaccountedRateLimit(string recId, double realtime)
        {
            if (string.IsNullOrEmpty(recId))
                return false;
            double last;
            if (lastUnaccountedEmitRealtime.TryGetValue(recId, out last)
                && realtime - last < UnaccountedAnomalyMinIntervalSeconds)
                return false;
            lastUnaccountedEmitRealtime[recId] = realtime;
            return true;
        }

        private void Sample(
            Vessel v, uint pid, int frame, double currentUT, int foShiftFrame, double realtime)
        {
            // --- Renderer-level fields (cheap; no reflection) ---
            OrbitRendererBase rendererBase = v.orbitRenderer;
            OrbitDriver driver = v.orbitDriver;

            bool rendererEnabledBool = rendererBase != null && rendererBase.enabled;
            string rendererEnabled = rendererEnabledBool.ToString();
            string drawMode = rendererBase != null ? rendererBase.drawMode.ToString() : "(no-renderer)";
            string drawIcons = rendererBase != null ? rendererBase.drawIcons.ToString() : "(no-renderer)";

            string bodyName = "(none)";
            double sma = double.NaN, ecc = double.NaN;
            CelestialBody body = null;
            if (driver != null && driver.orbit != null)
            {
                body = driver.orbit.referenceBody;
                if (body != null) bodyName = body.bodyName;
                sma = driver.orbit.semiMajorAxis;
                ecc = driver.orbit.eccentricity;
            }

            string lineActive = ReadLineActive(rendererBase);

            string pidKey = pid.ToString(ic);
            // Resolve the recordingId for this ghost once per sample (the reverse map covers chain ghosts)
            // so every truth / anomaly / suppression / first-position line carries recId= and one grep
            // reconstructs a recording's whole render history. Null -> recId=<none> (e.g. a transient
            // pre-registration frame).
            string recId = GhostMapPresence.FindRecordingIdByVesselPid(pid);

            // --- Tier-B: proto-icon suppression flip (suppressed<->restored) ---
            // ghostsWithSuppressedIcon membership decides whether the proto icon is the visible indicator or
            // the non-proto marker is; its flip is the "did the icon hand off to / from the trajectory
            // marker" event. Emitted on-change (surface=ProtoIcon) carrying recId, BEFORE the field truth
            // below so the flip reads first in the log.
            EmitTruthOnChange(lastIconSuppressed, pid,
                MapRenderTrace.Bool(GhostMapPresence.IsIconSuppressed(pid)),
                MapRenderTrace.RenderSurface.ProtoIcon, pidKey, currentUT,
                "icon-suppressed",
                string.Format(ic,
                    "iconSuppressed={0} drawIcons={1} lineActive={2} body={3}",
                    MapRenderTrace.Bool(GhostMapPresence.IsIconSuppressed(pid)),
                    drawIcons, lineActive, bodyName),
                recId);

            // --- Tier-B change-based truth (one line per field on change) ---
            // First sample for this pid (including the first after a scene-change
            // clear, or the first resolvable-body frame after a body=null gap):
            // no trustworthy previous body-relative position, so the jump check
            // is suppressed via justReset.
            bool justReset = !prevBodyRelPos.ContainsKey(pid);

            // Capture the previously-sampled line.active BEFORE EmitTruthOnChange
            // overwrites lastLineActive, so the blink predicate below sees the
            // prior frame's value.
            string prevLineActive;
            bool hadPrevLine = lastLineActive.TryGetValue(pid, out prevLineActive);

            EmitTruthOnChange(lastLineActive, pid, lineActive,
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT,
                "line.active",
                string.Format(ic,
                    "lineActive={0} renderer.enabled={1} drawMode={2} drawIcons={3} body={4} sma={5} ecc={6}",
                    lineActive, rendererEnabled, drawMode, drawIcons, bodyName,
                    MapRenderTrace.FormatDouble(sma, "F0"), MapRenderTrace.FormatDouble(ecc, "F4")),
                recId);

            EmitTruthOnChange(lastRendererEnabled, pid, rendererEnabled,
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT,
                "renderer.enabled",
                string.Format(ic,
                    "renderer.enabled={0} lineActive={1} drawMode={2} body={3}",
                    rendererEnabled, lineActive, drawMode, bodyName),
                recId);

            EmitTruthOnChange(lastDrawIcons, pid, drawIcons,
                MapRenderTrace.RenderSurface.ProtoIcon, pidKey, currentUT,
                "drawIcons",
                string.Format(ic,
                    "drawIcons={0} lineActive={1} renderer.enabled={2} body={3}",
                    drawIcons, lineActive, rendererEnabled, bodyName),
                recId);

            string bodyOrbitKey = bodyName + "|"
                + MapRenderTrace.FormatDouble(sma, "F0") + "|"
                + MapRenderTrace.FormatDouble(ecc, "F4");
            // Capture the orbit the icon was on LAST frame BEFORE EmitTruthOnChange overwrites it, so a
            // teleport line below can name BOTH orbits it jumped between (e.g. loiter -> synthesized
            // burn arc). "(first)" on the very first sample for this pid.
            string fromOrbit = lastBodyOrbit.TryGetValue(pid, out string priorOrbit) ? priorOrbit : "(first)";
            // body-orbit on-change IS the proto "moves/rebinds" event: a body / sma / ecc change is the
            // visible re-anchor of the line + icon onto a new orbit. Carry recId + the loop epoch shift so a
            // looped re-aim rebind reads in one line (the decision-side SegmentApplied with source/segment
            // index stays a documented second cut).
            double loopShift = GhostMapPresence.GetGhostOrbitEpochShift(pid);
            EmitTruthOnChange(lastBodyOrbit, pid, bodyOrbitKey,
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT,
                "body-orbit",
                string.Format(ic,
                    "body={0} sma={1} ecc={2} loopShift={3} from=[{4}] lineActive={5} renderer.enabled={6}",
                    bodyName, MapRenderTrace.FormatDouble(sma, "F0"),
                    MapRenderTrace.FormatDouble(ecc, "F4"), MapRenderTrace.FormatDouble(loopShift, "F1"),
                    fromOrbit, lineActive, rendererEnabled),
                recId);

            // --- Tier-C decision-vs-truth reconciliation (proto orbit-line / icon) ---
            // If GhostOrbitLinePatch recorded an authoritative line/icon decision on THIS frame,
            // compare it against the actual end-of-frame read. A same-frame mismatch means KSP or
            // another patch toggled line.active / drawIcons AFTER our Postfix decided it (the blink /
            // post-decision-mutation case). Stale intent (a frame on which the Postfix did not run,
            // e.g. KSP skipped the renderer's LateUpdate) is dropped by the freshness check, not
            // flagged. The line.active half stays dormant while the read is "(field-missing)"; the
            // drawIcons half is live now.
            if (MapRenderTrace.TryGetFreshLineIntent(pidKey, frame, out var lineIntent))
            {
                string mismatch = MapRenderTrace.ReconcileLineState(lineIntent, lineActive, drawIcons);
                if (!string.IsNullOrEmpty(mismatch))
                    MapRenderTrace.EmitAnomaly(
                        MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, currentUT,
                        "decision-vs-truth",
                        string.Format(ic, "{0} intentReason={1} sinceFrames={2}",
                            mismatch, lineIntent.Reason, frame - lineIntent.Frame),
                        recId);
            }

            // --- Tier-C polyline-orbit-overlap anomaly ---
            // The proto orbit line + icon must not draw while the trajectory polyline owns this
            // recording's non-orbital leg (the double-draw seam). A higher-level invariant check
            // independent of the patch's intent. The drawIcons facet is live now; the line facet
            // lights up with the OrbitLine-reflection fix.
            bool polylineOwns = GhostMapPresence.IsPolylineOwningGhostPhase(pid);
            string overlap = MapRenderTrace.ReconcilePolylineOverlap(
                polylineOwns, lineActive, drawIcons);
            if (!string.IsNullOrEmpty(overlap) && PassesPolylineOverlapRateLimit(pid, realtime))
                // Name the orbit the icon is wrongly shown on (sma/ecc): during a polyline-owned burn the
                // icon must NOT ride a per-frame synthesized orbit. Rate-limited per pid (the condition is
                // persistent through the burn, so without this it would fire every frame).
                MapRenderTrace.EmitAnomaly(
                    MapRenderTrace.RenderSurface.Polyline, pidKey, currentUT, currentUT,
                    "polyline-orbit-overlap",
                    string.Format(ic, "{0} icon-on-orbit sma={1} ecc={2} body={3} drawIcons={4} lineActive={5}",
                        overlap, MapRenderTrace.FormatDouble(sma, "F0"),
                        MapRenderTrace.FormatDouble(ecc, "F4"), bodyName, drawIcons, lineActive),
                    recId);

            // --- Tier-C new-pipeline reconcile: intent (shadow) vs the OLD path's truth ---
            // If the new render pipeline recorded a GhostRenderIntent for this pid THIS frame
            // (decision-only shadow producer, wired in Phase 4), compare it against what the old
            // scattered coordination actually drew. A divergence is the bug class the rewrite exists
            // to surface (the new single-owner Director would have rendered something different).
            // No fresh intent → no-op, so this is dormant until the Phase 4 shadow scene calls
            // GhostRenderReconciler.NoteIntent. Reuses the same old-path truth read as above.
            Parsek.MapRender.GhostRenderReconciler.CheckIntentAgainstOldTruth(
                pid, pidKey, frame, currentUT, currentUT, lineActive, drawIcons, polylineOwns, realtime);

            // --- Tier-C line-blink anomaly (line.active toggled within N frames) ---
            // A toggle is line.active != the previous sample's value. The blink
            // predicate fires only when the PREVIOUS toggle was within the window,
            // i.e. the line just flickered out and back.
            if (hadPrevLine && prevLineActive != lineActive)
            {
                int lastToggle;
                bool hasLastToggle = lastLineToggleFrame.TryGetValue(pid, out lastToggle);
                if (MapRenderTrace.IsLineBlink(
                        toggled: true,
                        hasLastToggleFrame: hasLastToggle,
                        lastToggleFrame: lastToggle,
                        currentFrame: frame))
                {
                    MapRenderTrace.EmitAnomaly(
                        MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, currentUT,
                        "line-blink",
                        string.Format(ic,
                            "lineActive={0} prevActive={1} lastToggleFrame={2} sinceFrames={3} body={4}",
                            lineActive, prevLineActive, lastToggle, frame - lastToggle, bodyName));
                }
                lastLineToggleFrame[pid] = frame;
            }

            // --- Tier-A FirstPosition (probe-derived MVP variant) ---
            // The first end-of-frame truth read for a pid: emit the ghost's first
            // world position on its orbit/trajectory plus orbit body/sma/ecc. No
            // decision hook and no recordingId (those are second-cut). Fires once
            // per pid per scene (the set is cleared on scene change), keyed by the
            // map world's native persistentId.
            Vector3d worldPos = v.GetWorldPos3D();
            if (!firstPositionEmittedPids.Contains(pid))
            {
                MapRenderTrace.EmitStructural(
                    "FirstPosition",
                    MapRenderTrace.RenderSurface.ProtoOrbitLine,
                    pidKey,
                    currentUT,
                    currentUT,
                    MapRenderTrace.InitialWindowSeconds,
                    MapRenderTrace.BuildFirstPositionDetails(
                        worldPos, bodyName, sma, ecc, "first-truth-read"),
                    recId);
                firstPositionEmittedPids.Add(pid);
            }

            // --- Tier-C icon-jump anomaly (per-frame, orbit-relative threshold) ---
            // Measure the jump in the orbit's OWN reference-body frame
            // (bodyRelPos = worldPos - body.position), NOT the raw GetWorldPos3D
            // world-frame delta. KSP builds an on-rails vessel's world position
            // as referenceBody.position + orbitRelative, so the body-relative
            // delta is the actual orbital arc that ComputeExpectedMotionMeters
            // predicts. The raw world-frame delta of a ghost far from the
            // floating origin is dominated by the reference-body's own world
            // motion under warp (both worldPos and body.position are read at the
            // SAME live UT, so the loop shift is NOT a factor - it only sets
            // where on its orbit the ghost is), which scales with geometry and is
            // unrelated to the ghost's orbital speed - that frame contamination
            // is exactly what produced false-positive icon-jumps on smooth
            // heliocentric coasts at high warp.
            if (body != null)
            {
                Vector3d bodyRelPos = worldPos - body.position;

                // Reference-body change (e.g. SOI crossing Kerbin -> Sun): the
                // previous body-relative position was measured in the OLD body's
                // frame, so its delta against this frame is a frame mismatch.
                string lastJumpBody;
                bool bodyChanged = lastIconJumpBody.TryGetValue(pid, out lastJumpBody)
                    && lastJumpBody != bodyName;

                Vector3d prevBodyRel;
                if (prevBodyRelPos.TryGetValue(pid, out prevBodyRel))
                {
                    double dPos = (bodyRelPos - prevBodyRel).magnitude;
                    double expectedMotion = ComputeExpectedMotionMeters(driver, body);
                    if (MapRenderTrace.IsIconJump(
                            dPos: dPos,
                            expectedMotionMeters: expectedMotion,
                            currentFrame: frame,
                            floatingOriginShiftFrame: foShiftFrame,
                            justReset: justReset,
                            bodyChanged: bodyChanged)
                        // Skip a SUPPRESSED icon: its proto position may still jump (stock propagates a
                        // phantom orbit) but it is not DRAWN, so it is not a visible teleport - only flag
                        // teleports the user can actually see (matches the icon-off-orbit guard).
                        && !GhostMapPresence.IsIconSuppressed(pid)
                        && PassesJumpRateLimit(pid, realtime))
                    {
                        // dPosWorld is the raw world-frame delta, logged only as
                        // context so a re-test shows how much the reference-body
                        // frame motion was inflating the old metric.
                        Vector3d prevWorld;
                        double dPosWorld = prevWorldPos.TryGetValue(pid, out prevWorld)
                            ? (worldPos - prevWorld).magnitude
                            : double.NaN;
                        // Emphasize the teleport: lead with the jump magnitude as a MULTIPLE of the
                        // expected orbital motion (the headline red flag), and name BOTH orbits it jumped
                        // between (fromOrbit -> toOrbit) so a reseed onto a synthesized burn arc reads in
                        // one line. ratio = dPos / expected (NaN-safe; "inf" when expected is ~0).
                        double jumpRatio = expectedMotion > 1.0 ? dPos / expectedMotion : double.NaN;
                        string ratioStr = double.IsNaN(jumpRatio)
                            ? (dPos > 1.0 ? "inf" : "0")
                            : jumpRatio.ToString("F0", ic);
                        MapRenderTrace.EmitAnomaly(
                            MapRenderTrace.RenderSurface.ProtoIcon, pidKey, currentUT, currentUT,
                            "icon-teleport",
                            string.Format(ic,
                                "TELEPORT dPos={0}m = {1}x expected({2}m) | fromOrbit=[{3}] toOrbit=[sma={4} ecc={5}] body={6} | lineActive={7} drawIcons={8} dPosWorld={9}m warpRate={10} dt={11} bodyRelPos={12}",
                                MapRenderTrace.FormatDouble(dPos, "F0"),
                                ratioStr,
                                MapRenderTrace.FormatDouble(expectedMotion, "F0"),
                                fromOrbit,
                                MapRenderTrace.FormatDouble(sma, "F0"),
                                MapRenderTrace.FormatDouble(ecc, "F4"),
                                bodyName, lineActive, drawIcons,
                                MapRenderTrace.FormatDouble(dPosWorld, "F0"),
                                UnityWarpRate().ToString("F0", ic),
                                UnityUnscaledDeltaTime().ToString("F4", ic),
                                MapRenderTrace.FormatVector3d(bodyRelPos)),
                            recId);
                    }
                }
                // --- Tier-C icon-off-orbit anomaly (is the icon ON its own orbit line?) ---
                // The orbit LINE and the vessel ICON both derive from OrbitDriver.orbit, so a
                // correctly placed icon sits on the line. For a loop-shifted ghost,
                // GhostOrbitIconDrivePatch positions the icon at effUT = liveUT - loopShift; compare
                // the icon's body-relative position against the orbit's OWN predicted body-relative
                // position at that same effUT. ~0 deg => icon on its line; a large angle => the icon
                // is off its own orbit (the looped / re-aimed rotation bug), which icon-jump (no
                // per-frame delta on a static offset) and line-blink (line stays active) cannot see.
                // Reads are non-mutating and run at exec-order 10000 (after the renderer). Rate-
                // limited per pid because the offset is persistent (it would fire every frame).
                Orbit offOrbit = driver != null ? driver.orbit : null;
                // Skip when the icon is SUPPRESSED / clamped: in those states
                // (GhostOrbitIconDrivePatch past-window / before-window / off-arc)
                // the OrbitDriver is parked at a clamped endpoint UT, NOT at effUT,
                // so comparing against the orbit's effUT position would be a
                // guaranteed false positive - and the icon is correctly OFF the
                // visible arc by design there. ghostsWithSuppressedIcon is set/cleared
                // this frame by the drive Prefix + the line Postfix, both before this
                // order-10000 probe, so IsIconSuppressed is current. Only the actively
                // driven (on-arc, line-shown) icon is checked - the genuine bug case.
                if (offOrbit != null && !GhostMapPresence.IsIconSuppressed(pid))
                {
                    // Compare against the UT the icon-drive ACTUALLY propagated the icon to this frame
                    // (recorded by GhostOrbitIconDrivePatch at the SetPosition site), not a clock this
                    // probe re-derives. The drive (OrbitDriver LateUpdate, exec-order 0) and this probe
                    // (exec-order 10000) used to evaluate IsDirectorDriveActive independently; the
                    // shadow's StockConic seed can flip to "fresh" between them within one frame, so the
                    // drive placed the icon at the legacy shifted phase while the probe assumed the
                    // director unshifted phase, producing a spurious icon-off-orbit angle on the frame
                    // after a ghost is created / reseeded. Reading the drive's recorded propagateUT makes
                    // the reference conic match where the icon physically is, by construction, while still
                    // flagging a REAL off-orbit (icon NOT at its driven phase). A stale / absent record (a
                    // frame the icon-drive did not run, e.g. stock re-took the drive at a stale-segment
                    // transition) falls back to the legacy derivation: the director epoch-bake resolves
                    // the icon at the live clock (shift 0), the legacy raw-epoch path at effUT = currentUT
                    // - shift. recorded-phase correctness is proven by the in-game epoch-bake test.
                    bool hasDrivenUT = GhostMapPresence.TryGetFreshIconDrivePropagateUT(
                        pid, Time.frameCount,
                        Parsek.MapRender.ShadowRenderDriver.SeedFreshnessFrames, out double drivenUT);
                    double offEffUT = MapRenderTrace.ResolveIconReferenceUT(
                        hasDrivenUT, drivenUT, currentUT,
                        Parsek.MapRender.ShadowRenderDriver.IsDirectorDriveActive(pid, Time.frameCount),
                        GhostMapPresence.GetGhostOrbitEpochShift(pid));
                    double offShift = currentUT - offEffUT;
                    Vector3d orbitRelEff = OrbitRelativePositionYup(offOrbit, offEffUT);
                    // Degenerate / unresolved orbit: a zero predicted position makes
                    // Vector3d.Angle return 90 deg (acos of a zero dot) - a phantom
                    // rotation. Real ghost orbits have a nonzero radius, so a near-zero
                    // magnitude means "not resolved yet"; feed NaN so the predicate
                    // (NaN-guarded) skips it rather than reporting a spurious 90 deg.
                    double angleIconVsOrbitEff = orbitRelEff.sqrMagnitude > 1.0
                        ? UnityAngleDeg(bodyRelPos, orbitRelEff)
                        : double.NaN;
                    if (MapRenderTrace.IsIconOffOrbit(
                            angleIconVsOrbitEff, MapRenderTrace.IconOffOrbitMinAngleDeg)
                        && PassesOffOrbitRateLimit(pid, realtime))
                    {
                        Vector3d orbitRelLive = OrbitRelativePositionYup(offOrbit, currentUT);
                        MapRenderTrace.EmitAnomaly(
                            MapRenderTrace.RenderSurface.ProtoIcon, pidKey, currentUT, offEffUT,
                            "icon-off-orbit",
                            string.Format(ic,
                                "angleIconVsOrbitEff={0} angleEffVsLive={1} loopShift={2} effUT={3} | "
                                + "lonIcon={4} lonOrbitEff={5} lonOrbitLive={6} | iconR={7} orbitEffR={8} | "
                                + "lineActive={9} inc={10} LAN={11} argPe={12} sma={13} ecc={14} body={15}",
                                MapRenderTrace.FormatDouble(angleIconVsOrbitEff, "F2"),
                                MapRenderTrace.FormatDouble(UnityAngleDeg(orbitRelEff, orbitRelLive), "F2"),
                                MapRenderTrace.FormatDouble(offShift, "F1"),
                                MapRenderTrace.FormatDouble(offEffUT, "F1"),
                                MapRenderTrace.FormatDouble(LongitudeDeg(bodyRelPos), "F2"),
                                MapRenderTrace.FormatDouble(LongitudeDeg(orbitRelEff), "F2"),
                                MapRenderTrace.FormatDouble(LongitudeDeg(orbitRelLive), "F2"),
                                MapRenderTrace.FormatDouble(bodyRelPos.magnitude, "F0"),
                                MapRenderTrace.FormatDouble(orbitRelEff.magnitude, "F0"),
                                lineActive,
                                MapRenderTrace.FormatDouble(offOrbit.inclination, "F3"),
                                MapRenderTrace.FormatDouble(offOrbit.LAN, "F3"),
                                MapRenderTrace.FormatDouble(offOrbit.argumentOfPeriapsis, "F3"),
                                MapRenderTrace.FormatDouble(offOrbit.semiMajorAxis, "F0"),
                                MapRenderTrace.FormatDouble(offOrbit.eccentricity, "F4"),
                                bodyName),
                            recId);
                    }
                }

                // Record this frame's body-relative position + body so the next
                // frame's jump check is live (next sample has justReset == false).
                prevBodyRelPos[pid] = bodyRelPos;
                lastIconJumpBody[pid] = bodyName;
            }
            else
            {
                // No reference body this frame: cannot form a body-relative
                // position. Drop any stale prev so the next resolvable-body frame
                // is treated as just-reset rather than compared across the gap.
                prevBodyRelPos.Remove(pid);
                lastIconJumpBody.Remove(pid);
            }
            // Retain the raw world position for the dPosWorld context log above.
            prevWorldPos[pid] = worldPos;

            // --- In-window full per-frame snapshot (Tier-B detail) ---
            // While a detailed window is open for this pid (opened by a structural
            // event - GhostCreated / FirstPosition - or an anomaly), dump the full
            // current truth so the window captures continuous motion, not just the
            // on-change transitions. No-op outside a window.
            //
            // Wall-clock rate-limit (SnapshotMinIntervalSeconds): a PERSISTENT anomaly
            // (gap-vs-retire / icon-off-orbit / polyline-orbit-overlap) refreshes the
            // detailed window every divergent frame, so an un-throttled per-frame snapshot
            // floods the log for the whole anomalous stretch (a real looped-mission capture
            // had ~1900 of these, 80% of the trace volume, from two sustained-anomaly
            // ghosts). Sampling the snapshot at ~2 Hz per pid still captures the motion
            // through the window while collapsing a sustained anomaly from per-frame to ~2/s.
            // The on-change truth lines (line.active / drawIcons / body-orbit) still record
            // every transition exactly, so no TRANSITION detail is lost. What IS coarsened to
            // ~2 Hz is the only per-frame-exclusive content of the snapshot - the continuous
            // worldPos / drawMode trace - but neither is a transition carrier: a discrete icon
            // jump stays on the unthrottled icon-teleport anomaly line, and drawMode changes
            // rarely. The rate-limit also gates the string build, so a throttled frame pays
            // nothing. Warp-stable (wall-clock key, the #1063 rule).
            if (MapRenderTrace.IsDetailedWindowOpen(pidKey, currentUT)
                && PassesSnapshotRateLimit(pid, realtime))
            {
                MapRenderTrace.EmitWindowSnapshot(
                    MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, currentUT,
                    string.Format(ic,
                        "lineActive={0} renderer.enabled={1} drawMode={2} drawIcons={3} body={4} sma={5} ecc={6} worldPos={7}",
                        lineActive, rendererEnabled, drawMode, drawIcons, bodyName,
                        MapRenderTrace.FormatDouble(sma, "F0"), MapRenderTrace.FormatDouble(ecc, "F4"),
                        MapRenderTrace.FormatVector3d(worldPos)),
                    recId);
            }
        }

        private void EmitTruthOnChange(
            Dictionary<uint, string> store,
            uint pid,
            string currentValue,
            MapRenderTrace.RenderSurface surface,
            string pidKey,
            double currentUT,
            string fieldPhase,
            string details,
            string recId = null)
        {
            string last;
            bool had = store.TryGetValue(pid, out last);
            bool changed = !had || last != currentValue;
            if (changed)
            {
                // This probe-local dict (cleared on scene switch) is the SINGLE
                // on-change gate; EmitOnChange routes straight to Verbose and does
                // not re-gate, so the first post-scene-switch transition is not
                // swallowed by stale state.
                MapRenderTrace.EmitOnChange(
                    fieldPhase, surface, pidKey, currentUT, currentUT, details, recId);
                store[pid] = currentValue;
            }
        }

        // Orbit-derived expected per-frame motion: orbital speed * dt * warpRate.
        // dt is the visual-frame Time.unscaledDeltaTime; warpRate is
        // TimeWarp.CurrentRate. Returns 0 for a degenerate / unresolved orbit so
        // the jump predicate falls back to the fixed floor.
        private double ComputeExpectedMotionMeters(OrbitDriver driver, CelestialBody body)
        {
            if (driver == null || driver.orbit == null || body == null)
                return 0.0;
            double orbitalSpeed = driver.orbit.orbitalSpeed;
            if (double.IsNaN(orbitalSpeed) || double.IsInfinity(orbitalSpeed))
                return 0.0;
            orbitalSpeed = System.Math.Abs(orbitalSpeed);
            double dt = UnityUnscaledDeltaTime();
            double warpRate = UnityWarpRate();
            return orbitalSpeed * dt * warpRate;
        }

        private bool PassesJumpRateLimit(uint pid, double realtime)
        {
            double last;
            if (lastJumpEmitRealtime.TryGetValue(pid, out last)
                && realtime - last < JumpAnomalyMinIntervalSeconds)
                return false;
            lastJumpEmitRealtime[pid] = realtime;
            return true;
        }

        private bool PassesSnapshotRateLimit(uint pid, double realtime)
        {
            double last;
            if (lastSnapshotEmitRealtime.TryGetValue(pid, out last)
                && realtime - last < SnapshotMinIntervalSeconds)
                return false;
            lastSnapshotEmitRealtime[pid] = realtime;
            return true;
        }

        private bool PassesOffOrbitRateLimit(uint pid, double realtime)
        {
            double last;
            if (lastOffOrbitEmitRealtime.TryGetValue(pid, out last)
                && realtime - last < OffOrbitAnomalyMinIntervalSeconds)
                return false;
            lastOffOrbitEmitRealtime[pid] = realtime;
            return true;
        }

        private bool PassesPolylineOverlapRateLimit(uint pid, double realtime)
        {
            double last;
            if (lastPolylineOverlapEmitRealtime.TryGetValue(pid, out last)
                && realtime - last < OffOrbitAnomalyMinIntervalSeconds)
                return false;
            lastPolylineOverlapEmitRealtime[pid] = realtime;
            return true;
        }

        // Body-relative orbit position in Y-up WORLD axes (the same frame the icon's
        // bodyRelPos = GetWorldPos3D - body.position lives in). orbit.getRelativePositionAtUT
        // returns Z-up KSP-internal axes; Swizzle() converts to Y-up world. Isolated here so
        // the pure MapRenderTrace.IsIconOffOrbit predicate stays Unity-ECall-free.
        private static Vector3d OrbitRelativePositionYup(Orbit orbit, double ut)
        {
            Vector3d p = orbit.getRelativePositionAtUT(ut);
            p.Swizzle();
            return p;
        }

        private static double UnityAngleDeg(Vector3d a, Vector3d b)
        {
            return Vector3d.Angle(a, b);
        }

        // Inertial longitude proxy (degrees) of a Y-up world body-relative vector:
        // atan2(z, x). Logged so the icon-vs-orbit gap reads as a longitude delta
        // (directly comparable to the documented 96.8 deg measurement), not only a 3D angle.
        private static double LongitudeDeg(Vector3d bodyRelYup)
        {
            return System.Math.Atan2(bodyRelYup.z, bodyRelYup.x) * (180.0 / System.Math.PI);
        }

        // Read the orbit line's visibility truth through the PUBLIC
        // OrbitRendererBase.OrbitLine property (the Vectrosity VectorLine the
        // renderer actually draws) and read VectorLine.active directly - the same
        // access GhostOrbitLinePatch uses to toggle the ghost orbit line. The
        // retired GhostRenderStateProbe prototype reflected a NonPublic instance
        // field named "line", but the field is named "orbitLine" and is exposed
        // via the OrbitLine property, so the GetField("line", ...) reflection
        // always returned null ("(field-missing)") and the line.active truth
        // never had real data. No reflection here: VectorLine.active is a
        // compile-time member. internal static so the in-game test can drive it
        // against a live ghost's renderer. The null / error fallbacks are kept.
        internal static string ReadLineActive(OrbitRendererBase rendererBase)
        {
            if (rendererBase == null) return "(no-renderer)";
            try
            {
                var line = rendererBase.OrbitLine;
                if (line == null) return "(line-null)";
                return line.active.ToString();
            }
            catch (System.Exception ex)
            {
                return "(read-err:" + ex.GetType().Name + ")";
            }
        }

        // --- Unity-ECall isolation: every Unity-native read lives in its own
        // tiny method so the JIT verifier never walks an ECall on an
        // unreachable branch when the pure predicates are unit-tested. ---

        private static double CurrentUT()
        {
            return Planetarium.GetUniversalTime();
        }

        private static double UnityUnscaledDeltaTime()
        {
            return Time.unscaledDeltaTime;
        }

        private static double UnityWarpRate()
        {
            return TimeWarp.CurrentRate;
        }

        private static double UnityRealtimeSinceStartup()
        {
            return Time.realtimeSinceStartup;
        }
    }
}
