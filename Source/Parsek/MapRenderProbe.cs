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

        // Soft rate-limit on the Phase-0 recorded-vs-rendered parity-drift anomaly. Like the off-orbit
        // anomaly it is a PERSISTENT condition (a faithful member drawing the wrong geometry diverges every
        // frame), so throttle to one line per pid per second; the detailed window EmitAnomaly opens still
        // refreshes every divergent frame.
        private const double ParityDriftAnomalyMinIntervalSeconds = 1.0;

        // Number of UTs the rendered orbit (and the matching recorded reference orbit) is sampled at across
        // its visible span for the FAITHFUL parity diff. Mirrored from
        // RenderGeometrySampler.DefaultOrbitSampleCount so the probe + the pure helper agree.
        private const int ParityOrbitSampleCount =
            Parsek.MapRender.RenderGeometrySampler.DefaultOrbitSampleCount;

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
        // Reference-body name at the last line.active toggle per pid, so the blink predicate can tell a
        // legitimate cross-segment / SOI-seam toggle pair (line off on one orbit, on on the next body's
        // orbit) from a true same-geometry flicker. Without this, a Kerbin-escape -> Sun-heliocentric
        // handoff under high warp compresses two correct toggles below the frame window and reads as a blink.
        private readonly Dictionary<uint, string> lastLineToggleBody = new Dictionary<uint, string>();
        // Universal time at the previous sample per pid. The icon-jump expected-motion model uses the ACTUAL
        // per-frame UT advance (currentUT - prevSampleUT) instead of Time.unscaledDeltaTime *
        // TimeWarp.CurrentRate, which under-counts the real on-rails warp step by ~20x at high warp and
        // produced false icon-teleports on short-period / eccentric orbits. Cleared on scene change.
        private readonly Dictionary<uint, double> prevSampleUT = new Dictionary<uint, double>();
        // Pids whose proto icon was SUPPRESSED on the PREVIOUS sample. While suppressed the proto is parked
        // at a clamped endpoint UT and HIDDEN; the first visible frame after suppression lifts re-propagates
        // it to its live phase, a position delta the user never saw. The teleport + off-orbit checks are
        // skipped on that one lift frame (mirrors justReset / bodyChanged). Cleared on scene change.
        private readonly HashSet<uint> suppressedLastSample = new HashSet<uint>();
        // Soft rate-limit timestamps for the per-pid jump anomaly.
        private readonly Dictionary<uint, double> lastJumpEmitRealtime = new Dictionary<uint, double>();
        // Soft rate-limit timestamps for the per-pid in-window per-frame snapshot.
        private readonly Dictionary<uint, double> lastSnapshotEmitRealtime = new Dictionary<uint, double>();
        // Soft rate-limit timestamps for the per-pid icon-off-orbit anomaly.
        private readonly Dictionary<uint, double> lastOffOrbitEmitRealtime = new Dictionary<uint, double>();
        // Soft rate-limit timestamps for the per-pid Phase-0 recorded-vs-rendered parity-drift anomaly (a
        // PERSISTENT condition, like icon-off-orbit, so it must not fire every frame).
        private readonly Dictionary<uint, double> lastParityDriftEmitRealtime = new Dictionary<uint, double>();

        // Per-PASS faithful-parity accounting (stack-review S2): reset at the top of every LateUpdate
        // pass, accumulated by TrySampleAndEmitFaithfulOrbitParity, emitted as ONE rate-limited summary
        // after the per-pid loop (house batch-counting convention). Makes a silent oracle stand-down
        // (every ghost skipping) distinguishable from a clean measurement in a sign-off log.
        private readonly Dictionary<string, int> faithfulParitySkipCounts =
            new Dictionary<string, int>(System.StringComparer.Ordinal);
        private int faithfulParitySampledCount;
        private int faithfulParityOverCount;
        // A1 cutover-instrument: soft rate-limit timestamps for the per-pid SYNTHESIZED-mode (rendered conic
        // vs the Director's intended re-aimed seed) parity-drift anomaly. SEPARATE from the faithful dict so
        // a faithful drift and a synthesized drift on the same pid do not suppress each other. PERSISTENT
        // condition (a re-aim draw bug diverges every frame), so throttled like the faithful one.
        private readonly Dictionary<uint, double> lastSynthParityDriftEmitRealtime =
            new Dictionary<uint, double>();
        // A1 cutover-instrument: soft rate-limit timestamps for the per-RECORDING polyline-leg parity-drift
        // anomaly (rendered VectorLine points vs the recorded leg surface track). Keyed by RecordingId (legs
        // are per-recording, NOT per-pid - they are not in ghostMapVesselPids). PERSISTENT (a mis-anchored
        // leg diverges every frame it draws), so throttled to one line per recording per second.
        private const double PolylineParityDriftAnomalyMinIntervalSeconds = 1.0;
        private readonly Dictionary<string, double> lastPolylineParityDriftEmitRealtime =
            new Dictionary<string, double>(System.StringComparer.Ordinal);
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
            // Drop the reconciler's per-pid intent-reconcile rate-limit timestamps too. NOTE (Phase 8): the
            // intent-vs-old-truth comparator (CheckIntentAgainstOldTruth) was UNWIRED from this probe, so in
            // production these dicts are never populated and this Clear is a harmless no-op; it is kept
            // defensive (and the method stays referenced by GhostRenderReconcilerTests, which DO populate it).
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
            lastLineToggleBody.Clear();
            prevSampleUT.Clear();
            suppressedLastSample.Clear();
            lastJumpEmitRealtime.Clear();
            lastSnapshotEmitRealtime.Clear();
            lastOffOrbitEmitRealtime.Clear();
            lastParityDriftEmitRealtime.Clear();
            lastSynthParityDriftEmitRealtime.Clear();
            lastPolylineParityDriftEmitRealtime.Clear();
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

            // NOTE: no early return on an empty ghost-pid set. The per-RECORDING captures below (the
            // drawn-recording accounting, the descent snapshot, and the polyline leg parity lens) cover
            // PROTO-LESS recordings - a save whose only recording is atmospheric has polyline legs but
            // zero entries in ghostMapVesselPids, and the old early return silently blinded exactly the
            // lens meant to watch it (stack-review S3). The per-pid loop over an empty set is a no-op.
            var pids = GhostMapPresence.ghostMapVesselPids;
            int ghostCount = pids != null ? pids.Count : 0;

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
            faithfulParitySampledCount = 0;
            faithfulParityOverCount = 0;
            faithfulParitySkipCounts.Clear();
            if (pids != null)
            {
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
            }

            ParsekLog.VerboseRateLimited(MapRenderTrace.Tag, "probe-frame-summary",
                string.Format(ic,
                    "probe frame summary frame={0} ghosts={1} sampled={2} resolveMisses={3}",
                    frame, ghostCount, sampled, resolveMisses),
                5.0);

            // Faithful-parity pass accounting (stack-review S2): one rate-limited summary per the house
            // batch-counting convention, so a sign-off log distinguishes "the oracle measured N ghosts and
            // read clean" from "the oracle skipped everything" (the loop-ghost lookup blindness survived
            // three sign-off runs because every skip was silent). Emitted only when the pass touched the
            // lens at all.
            if (faithfulParitySampledCount > 0 || faithfulParitySkipCounts.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("faithful-parity summary sampled=").Append(faithfulParitySampledCount)
                  .Append(" overTolerance=").Append(faithfulParityOverCount);
                foreach (var kv in faithfulParitySkipCounts)
                    sb.Append(" skip.").Append(kv.Key).Append('=').Append(kv.Value);
                ParsekLog.VerboseRateLimited(MapRenderTrace.Tag, "faithful-parity-summary",
                    sb.ToString(), 5.0);
            }

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

            // --- A1 cutover-instrument / design §14: SYNTHESIZED-mode POLYLINE leg parity ---
            // Once per frame (NOT inside the per-pid loop above: the TracedPath polyline legs are per-
            // RECORDING and are NOT in ghostMapVesselPids, so the loop never sees them). For every leg the
            // polyline renderer actually drew this frame, diff the RENDERED VectorLine geometry (its live
            // points3, scaled->world body-relative) against the RECORDED leg surface track (its own
            // lats/lons/alts, body-relative) via the pure RenderParityOracle. A leg rotated onto a wrong
            // conic seam, mis-anchored, or otherwise drawn off its recorded path shows drift here - the
            // TracedPath-polyline regression class the faithful orbit oracle (orbit-only) cannot see. The
            // whole LateUpdate is IsEnabled-gated; the per-recording rate-limit below keeps a persistent
            // mis-drawn leg from flooding the log.
            CapturePolylineLegParity(frame, currentUT, realtime);
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

            // --- Tier-C new-pipeline reconcile: UNWIRED (migration plan §10, Phase 8) ---
            // The end-of-frame GhostRenderReconciler.CheckIntentAgainstOldTruth call (intent-vs-OLD-truth)
            // was removed here. Through Phases 0-7 it compared the new spine's GhostRenderIntent against the
            // OLD scattered coordination's rendered truth, to surface the swap/ownership divergence the
            // rewrite existed to find. With the spine now driving (Phases 3-7) the OLD truth this probe reads
            // (lineActive / drawIcons / polylineOwns) IS produced by that same spine, so the comparison
            // became CIRCULAR (intent vs the intent's own consequence) and self-confirming - it can no longer
            // detect a real divergence. The §0/§14 recorded-vs-rendered RenderParityOracle (TrySampleAndEmit-
            // FaithfulOrbitParity above) is the DISTINCT axis that has coexisted since Phase 0 and is now the
            // SOLE acceptance oracle. This is an UNWIRING, not a rename/promote of the old comparator; the
            // GhostRenderReconciler type + its pure predicates stay (exercised by GhostRenderReconcilerTests)
            // but have no LIVE production call site (a scripts/grep-audit-render-reconciler-unwired.ps1 gate
            // enforces zero CheckIntentAgainstOldTruth call sites under Source/Parsek/). The shadow
            // PRODUCER side (ShadowRenderDriver -> GhostRenderReconciler.NoteIntent) still writes the intent
            // into the tracer store, but that store now has NO production reader (its only consumer was this
            // retired comparator); the write is kept only to avoid touching the driver in an
            // observability-only phase - a removal candidate at the 5a/5b deletes. This whole probe is
            // MapRenderTrace.IsEnabled-gated (tracing OFF by default), so removing the call is
            // OBSERVABILITY-ONLY: flag-OFF / tracing-OFF normal play is byte-identical (the probe never ran).

            // --- Tier-C line-blink anomaly (line.active toggled within N frames) ---
            // A toggle is line.active != the previous sample's value. The blink
            // predicate fires only when the PREVIOUS toggle was within the window,
            // i.e. the line just flickered out and back.
            if (hadPrevLine && prevLineActive != lineActive)
            {
                int lastToggle;
                bool hasLastToggle = lastLineToggleFrame.TryGetValue(pid, out lastToggle);
                // The body at the PREVIOUS toggle: a toggle pair that straddles a reference-body /
                // segment change (line off on one orbit, on on the next body's orbit) is two legitimate
                // transitions at an SOI seam, not a same-geometry flicker. lastLineToggleBody holds the
                // prior toggle's body; differs from the current body => cross-seam => not a blink.
                bool toggleCrossedBody = lastLineToggleBody.TryGetValue(pid, out string priorToggleBody)
                    && priorToggleBody != bodyName;
                if (MapRenderTrace.IsLineBlink(
                        toggled: true,
                        hasLastToggleFrame: hasLastToggle,
                        lastToggleFrame: lastToggle,
                        currentFrame: frame,
                        bodyChanged: toggleCrossedBody))
                {
                    MapRenderTrace.EmitAnomaly(
                        MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, currentUT,
                        "line-blink",
                        string.Format(ic,
                            "lineActive={0} prevActive={1} lastToggleFrame={2} sinceFrames={3} body={4}",
                            lineActive, prevLineActive, lastToggle, frame - lastToggle, bodyName));
                }
                lastLineToggleFrame[pid] = frame;
                lastLineToggleBody[pid] = bodyName;
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

                // Suppression-lift edge: while suppressed the proto is parked at a clamped endpoint UT and
                // HIDDEN, so the first VISIBLE frame after suppression lifts re-propagates it to its live
                // phase - a position delta (teleport) and an icon-vs-orbit angle (off-orbit) the user never
                // saw. Skip both checks on that single lift frame (mirrors justReset / bodyChanged).
                bool iconSuppressedNow = GhostMapPresence.IsIconSuppressed(pid);
                bool suppressionLifted = suppressedLastSample.Contains(pid) && !iconSuppressedNow;

                Vector3d prevBodyRel;
                if (prevBodyRelPos.TryGetValue(pid, out prevBodyRel))
                {
                    double dPos = (bodyRelPos - prevBodyRel).magnitude;
                    // Real per-frame UT advance for the expected-motion model: the actual on-rails warp
                    // step, NOT Time.unscaledDeltaTime * CurrentRate (which under-counts it ~20x at high
                    // warp and false-fired on short-period / eccentric orbits). prevSampleUT always has an
                    // entry here (the first sample is guarded by justReset), so the lookup hits; 0.0 only
                    // on the impossible miss, which yields expected=0 -> floor (matching the old degenerate).
                    double prevUT;
                    double deltaUT = prevSampleUT.TryGetValue(pid, out prevUT) ? currentUT - prevUT : 0.0;
                    double expectedMotion = ComputeExpectedMotionMeters(driver, body, deltaUT);
                    if (MapRenderTrace.IsIconJump(
                            dPos: dPos,
                            expectedMotionMeters: expectedMotion,
                            currentFrame: frame,
                            floatingOriginShiftFrame: foShiftFrame,
                            justReset: justReset,
                            bodyChanged: bodyChanged,
                            suppressionLifted: suppressionLifted)
                        // Skip a SUPPRESSED icon: its proto position may still jump (stock propagates a
                        // phantom orbit) but it is not DRAWN, so it is not a visible teleport - only flag
                        // teleports the user can actually see (matches the icon-off-orbit guard).
                        && !iconSuppressedNow
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
                // suppressionLifted additionally skips the FIRST visible frame after the
                // icon un-suppresses: the per-frame drive has not re-propagated the icon
                // onto the (often just-rebound) orbit yet, so the icon sits at its stale
                // pre-suppression position for one frame - a transient the user never saw.
                if (offOrbit != null && !iconSuppressedNow && !suppressionLifted)
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

                    // --- Phase 0 / design §14: recorded-vs-rendered FAITHFUL parity-drift ---
                    // The DISTINCT new acceptance axis (RenderParityOracle), beside the icon-off-orbit
                    // angle above. Where icon-off-orbit asks "is the ICON on its own DRIVEN orbit?", this
                    // asks "did the orbit geometry the pipeline actually RENDERED (the live OrbitDriver.orbit
                    // that drives both the icon AND the orbit line) match the RECORDED source geometry it was
                    // supposed to draw?" - a faithful (non-re-aimed) member must draw what it recorded.
                    //
                    // FAITHFUL ONLY this slice: the reference is the RECORDED OrbitSegment covering the
                    // ghost's RECORDED clock (currentUT - loopShift; rec.OrbitSegments live on the recorded
                    // timeline, and under the unconditional 8e S4 director drive the icon-drive clock is
                    // currentUT, which for a loop ghost lies loopShift beyond the recorded span - the old
                    // drive-clock lookup silently skipped every loop ghost, the stack-review BLOCKER).
                    // Re-aimed / synthesized members (whose intended arc is NOT the recorded segment) still
                    // skip cleanly (no covering segment / body mismatch), preserving the division of labor
                    // with the synthesized lens. PHASE-MATCH: the reference is built with its epoch baked
                    // + loopShift and sampled at the LIVE clock (currentUT); the RENDERED orbit is sampled
                    // at the clock its OWN epoch convention implies (currentUT when director-baked,
                    // currentUT - loopShift when raw; an unexplained epoch skips as a transition frame) -
                    // see ComputeFaithfulOrbitParity for the full clock contract. Body-relative frame (the
                    // same bodyRelPos frame the off-orbit check uses) so the diff is body-world-motion-free.
                    // loopShift is the SAME value the live orbit was baked with (GetGhostOrbitEpochShift).
                    double parityLoopShift = GhostMapPresence.GetGhostOrbitEpochShift(pid);
                    TrySampleAndEmitFaithfulOrbitParity(
                        offOrbit, body, bodyRelPos, offEffUT, offShift, parityLoopShift, currentUT,
                        pid, pidKey, recId, lineActive, bodyName, realtime);

                    // --- A1 cutover-instrument / design §14: SYNTHESIZED-mode conic parity ---
                    // Where the FAITHFUL path above asks "did the rendered orbit match the RECORDED segment?"
                    // (and SKIPS re-aimed / synthesized members because no recorded segment covers their
                    // drive clock), this asks the COMPLEMENTARY question for exactly those members: "did the
                    // rendered StockConic match the PRODUCER'S INTENDED arc - the re-aimed OrbitSegment the
                    // spine actually fed the drive this frame?" The intended arc is the Director's fresh
                    // StockConic seed (ShadowRenderDriver.TryGetFreshStockConicSeed = intent.Payload.Conic).
                    // A re-aim DRAW regression (rendered conic != the seed it was driven from) shows drift;
                    // a faithful StockConic member reads ~0 (rendered IS the seed) and never emits. Validates
                    // DRAW-fidelity (rendered == intended), NOT solve-correctness (the re-aim solver owns
                    // whether the intended arc is physically right). Same body-relative Y-up frame.
                    //
                    // PHASE-MATCH: the intended reference is built with the loop-shift epoch bake
                    // (BuildPhaseMatchedReferenceOrbit) and sampled at the LIVE clock (currentUT), and the
                    // RENDERED conic is sampled at the icon-drive clock passed as effUT. The synth lens runs ONLY
                    // under the director epoch-bake path - it requires ShadowRenderDriver.TryGetFreshStockConicSeed,
                    // which (in normal play, body always resolves) implies IsDirectorDriveActive, the branch where
                    // StockConicTreatment.SeedAndDriveLive bakes epoch += loopShift and propagates the conic at
                    // the LIVE clock. So the rendered orbit's correct icon clock here is currentUT, NOT offEffUT:
                    // offEffUT (the icon-drive's recorded propagateUT) can be up to SeedFreshnessFrames stale at a
                    // RESEED boundary (~56s at warp), which made the rendered + reference arcs cover non-
                    // overlapping spans and read a ~50km FALSE drift even though the orbit elements were IDENTICAL
                    // (the live synth 50km FP). currentUT is always fresh and is where the epoch-baked icon
                    // actually sits, so a faithful draw reads ~0. (The effUT PARAMETER is retained because the
                    // in-game synth fixture drives a RAW-epoch ghost directly - bypassing the production
                    // epoch-bake gate - so it passes its own icon clock currentUT - loopShift to phase-match;
                    // production, only ever epoch-baked here, passes currentUT.) parityLoopShift is the value the
                    // reference epoch is baked with (GetGhostOrbitEpochShift(pid), computed above).
                    TrySampleAndEmitSynthesizedConicParity(
                        offOrbit, body, bodyRelPos, currentUT, currentUT, parityLoopShift, pid, pidKey, recId,
                        lineActive, bodyName, realtime);
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

            // Record this frame's icon-suppression truth + UT for the NEXT sample's suppression-lift guard
            // and real-deltaUT expected-motion model. Runs unconditionally (even on a body == null gap) so
            // the suppression edge and UT cadence stay tracked across every frame.
            if (GhostMapPresence.IsIconSuppressed(pid))
                suppressedLastSample.Add(pid);
            else
                suppressedLastSample.Remove(pid);
            prevSampleUT[pid] = currentUT;

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

        // Orbit-derived UPPER BOUND on the proto's per-frame motion = the orbit's maximum
        // (periapsis) speed * the ACTUAL per-frame UT advance (deltaUT, passed by the caller as
        // currentUT - prevSampleUT). The earlier estimate used the INSTANTANEOUS orbital speed *
        // Time.unscaledDeltaTime * TimeWarp.CurrentRate, which (a) under-counted the real on-rails warp
        // UT step by ~20x at high warp and (b) used the instantaneous speed, which under-counts an
        // eccentric orbit's faster periapsis arc - together producing false icon-teleports on
        // short-period / eccentric orbits. The per-frame chord (the measured dPos) can never exceed
        // maxSpeed * deltaUT, so the jump predicate (this * the fixed multiplier, floored) never
        // false-fires on real orbital motion at any warp, while a genuine reseed / teleport off the
        // orbit still exceeds it. Returns 0 for a degenerate / unresolved orbit or a non-positive
        // deltaUT, so the jump predicate falls back to the fixed floor (matching the old behavior).
        private double ComputeExpectedMotionMeters(OrbitDriver driver, CelestialBody body, double deltaUT)
        {
            if (driver == null || driver.orbit == null || body == null)
                return 0.0;
            if (double.IsNaN(deltaUT) || double.IsInfinity(deltaUT) || deltaUT <= 0.0)
                return 0.0;
            double maxSpeed = MapRenderTrace.ComputeMaxOrbitalSpeedMeters(
                driver.orbit.semiMajorAxis, driver.orbit.eccentricity, body.gravParameter,
                driver.orbit.orbitalSpeed);
            if (double.IsNaN(maxSpeed) || double.IsInfinity(maxSpeed))
                return 0.0;
            return maxSpeed * deltaUT;
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

        private bool PassesParityDriftRateLimit(uint pid, double realtime)
        {
            double last;
            if (lastParityDriftEmitRealtime.TryGetValue(pid, out last)
                && realtime - last < ParityDriftAnomalyMinIntervalSeconds)
                return false;
            lastParityDriftEmitRealtime[pid] = realtime;
            return true;
        }

        private bool PassesSynthParityDriftRateLimit(uint pid, double realtime)
        {
            double last;
            if (lastSynthParityDriftEmitRealtime.TryGetValue(pid, out last)
                && realtime - last < ParityDriftAnomalyMinIntervalSeconds)
                return false;
            lastSynthParityDriftEmitRealtime[pid] = realtime;
            return true;
        }

        private bool PassesPolylineParityDriftRateLimit(string recId, double realtime)
        {
            if (string.IsNullOrEmpty(recId))
                return false;
            double last;
            if (lastPolylineParityDriftEmitRealtime.TryGetValue(recId, out last)
                && realtime - last < PolylineParityDriftAnomalyMinIntervalSeconds)
                return false;
            lastPolylineParityDriftEmitRealtime[recId] = realtime;
            return true;
        }

        /// <summary>
        /// Outcome of one FAITHFUL recorded-vs-rendered orbit parity sample: the oracle's
        /// <see cref="Parsek.MapRender.RenderParityOracle.ParityResult"/> plus the contextual values the
        /// emit line carries. <see cref="Sampled"/> is false (and <see cref="SkipReason"/> set) when the
        /// sampler cleanly bailed before a diff (wrong body / no covering recorded segment / degenerate
        /// elements / re-aimed member): the caller MUST treat a non-sampled outcome as "no anomaly", never
        /// as a pass or a drift. internal so the in-game baseline test exercises the REAL orchestration
        /// (the buggy loop-shift bake path included) instead of an inlined copy.
        /// </summary>
        internal readonly struct FaithfulParitySample
        {
            internal bool Sampled { get; }
            internal string SkipReason { get; }
            internal Parsek.MapRender.RenderParityOracle.ParityResult Result { get; }
            internal double Scale { get; }
            internal double RecordedSegmentSma { get; }
            internal double RecordedSegmentEcc { get; }
            internal string RecordedSegmentBody { get; }

            internal FaithfulParitySample(
                bool sampled, string skipReason,
                Parsek.MapRender.RenderParityOracle.ParityResult result, double scale,
                double recordedSegmentSma, double recordedSegmentEcc, string recordedSegmentBody)
            {
                Sampled = sampled;
                SkipReason = skipReason;
                Result = result;
                Scale = scale;
                RecordedSegmentSma = recordedSegmentSma;
                RecordedSegmentEcc = recordedSegmentEcc;
                RecordedSegmentBody = recordedSegmentBody;
            }

            internal static FaithfulParitySample Skip(string reason)
            {
                return new FaithfulParitySample(
                    false, reason,
                    default(Parsek.MapRender.RenderParityOracle.ParityResult),
                    0.0, 0.0, 0.0, null);
            }
        }

        // --- Phase 0 / design §14: recorded-vs-rendered FAITHFUL parity sampler (Unity capture) ---
        //
        // Samples the RENDERED orbit geometry (the live OrbitDriver.orbit that drives both the icon and the
        // orbit line) and the FAITHFUL RECORDED reference (the recorded OrbitSegment covering the ghost's
        // drive clock), both in the SAME Y-up body-relative metres frame (OrbitRelativePositionYup, the same
        // frame the icon's bodyRelPos = GetWorldPos3D - body.position lives in), hands them to the pure
        // RenderParityOracle, and emits a parity-drift anomaly on OverTolerance. FAITHFUL ONLY: a re-aimed /
        // synthesized member (no covering recorded segment at offEffUT, or a recorded segment on a different
        // body than the rendered orbit) is skipped cleanly with no anomaly. Additive observability: no draw
        // change. Gated by the caller's IsEnabled check (the whole probe LateUpdate is IsEnabled-gated).
        //
        // Returns the FaithfulParitySample (the oracle result + context) so the in-game baseline test can
        // assert on the REAL orchestration (not an inlined copy). The probe ignores the return value; only
        // the rate-limited emit-on-OverTolerance below has a production side effect.
        private Parsek.MapRender.RenderParityOracle.ParityResult TrySampleAndEmitFaithfulOrbitParity(
            Orbit renderedOrbit, CelestialBody renderedBody, Vector3d iconBodyRel,
            double offEffUT, double offShift, double loopShift, double currentUT,
            uint pid, string pidKey, string recId, string lineActive, string bodyName, double realtime)
        {
            FaithfulParitySample sample = ComputeFaithfulOrbitParity(
                renderedOrbit, renderedBody, loopShift, currentUT, recId);

            // Batch skip/sample accounting (house convention: count in the loop, one summary after it -
            // see the LateUpdate summary emit). Without this, "zero parity lines" in a sign-off log was
            // indistinguishable from "the oracle never measured anything", which is exactly how the
            // loop-ghost lookup blindness survived three sign-off runs.
            if (!sample.Sampled)
            {
                string reason = sample.SkipReason ?? "unknown";
                faithfulParitySkipCounts.TryGetValue(reason, out int c);
                faithfulParitySkipCounts[reason] = c + 1;
                return sample.Result;
            }
            faithfulParitySampledCount++;
            Parsek.MapRender.RenderParityOracle.ParityResult result = sample.Result;
            if (!result.HasMeasurement || !result.OverTolerance)
                return result;
            faithfulParityOverCount++;
            if (!PassesParityDriftRateLimit(pid, realtime))
                return result;

            MapRenderTrace.EmitAnomaly(
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, offEffUT,
                MapRenderTrace.AnomalyParityDrift,
                string.Format(ic,
                    "mode=faithful maxDev={0}m tol={1}m scale={2}m refCount={3} rendCount={4} "
                    + "countMismatch={5} offShift={6} loopShift={7} effUT={8} | recSeg=[sma={9} ecc={10} body={11}] "
                    + "rendOrbit=[sma={12} ecc={13} body={14}] iconR={15} lineActive={16}",
                    MapRenderTrace.FormatDouble(result.MaxDeviationMeters, "F0"),
                    MapRenderTrace.FormatDouble(result.ToleranceMeters, "F0"),
                    MapRenderTrace.FormatDouble(sample.Scale, "F0"),
                    result.ReferenceCount, result.RenderedCount, result.CountMismatch,
                    MapRenderTrace.FormatDouble(offShift, "F1"),
                    MapRenderTrace.FormatDouble(loopShift, "F1"),
                    MapRenderTrace.FormatDouble(offEffUT, "F1"),
                    MapRenderTrace.FormatDouble(sample.RecordedSegmentSma, "F0"),
                    MapRenderTrace.FormatDouble(sample.RecordedSegmentEcc, "F4"),
                    sample.RecordedSegmentBody ?? "(null)",
                    MapRenderTrace.FormatDouble(renderedOrbit.semiMajorAxis, "F0"),
                    MapRenderTrace.FormatDouble(renderedOrbit.eccentricity, "F4"),
                    bodyName,
                    MapRenderTrace.FormatDouble(iconBodyRel.magnitude, "F0"),
                    lineActive),
                recId);
            return result;
        }

        // The pure-orchestration CORE of the faithful parity sample: resolve the recorded reference, build
        // the PHASE-MATCHED reference orbit, sample both, and run the oracle diff. No emit, no rate-limit,
        // no per-pid state - so the in-game baseline test drives the EXACT production geometry path (the
        // recorded-segment lookup, the loop-shift epoch bake, the OrbitRelativePositionYup sampling, the
        // scale-derived tolerance, the oracle diff) end-to-end, including the loop-shifted bake that the
        // BLOCKER fix corrects. internal for that test.
        internal static FaithfulParitySample ComputeFaithfulOrbitParity(
            Orbit renderedOrbit, CelestialBody renderedBody,
            double loopShift, double currentUT, string recId)
        {
            if (renderedOrbit == null || renderedBody == null)
                return FaithfulParitySample.Skip("no-rendered-orbit");
            if (string.IsNullOrEmpty(recId))
                return FaithfulParitySample.Skip("no-recId");

            // Resolve the FAITHFUL recorded reference: the recorded OrbitSegment covering the ghost's
            // RECORDED clock, on the SAME body as the rendered orbit. THE LOOKUP CLOCK IS THE RECORDED
            // CLOCK (currentUT - loopShift), NOT the icon-drive clock: rec.OrbitSegments live on the
            // recorded timeline, and under the (unconditional since 8e S4) director epoch-bake drive the
            // icon-drive clock equals currentUT, which for any loop ghost past its first iteration lies
            // loopShift BEYOND the recorded span - looking the segment up there silently skipped every
            // loop-shifted ghost in production ("no-covering-segment"), leaving the top regression class
            // unguarded while the sign-off read "zero drift" (the stack-review BLOCKER). The recorded
            // clock is path-independent: on the legacy raw-epoch path it equals the icon-drive effUT, on
            // the director path it lands inside the recorded span. A re-aim / synthesized member, a window
            // with no covering segment, or a different-body segment -> skip (no false anomaly): the
            // deliberate division of labor with the synthesized lens is unchanged.
            if (!Parsek.GhostMapPresence.TryGetCommittedRecordingById(
                    recId, out int _, out Recording rec)
                || rec == null || rec.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                return FaithfulParitySample.Skip("no-recording-or-segments");

            double lookupUT = ResolveFaithfulLookupUT(currentUT, loopShift);
            OrbitSegment? coveringMaybe = TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, lookupUT);
            if (!coveringMaybe.HasValue)
                return FaithfulParitySample.Skip("no-covering-segment"); // re-aim / trim gap -> skip
            OrbitSegment covering = coveringMaybe.Value;
            if (!TrajectoryMath.HasUsableOrbitSegmentElements(covering))
                return FaithfulParitySample.Skip("degenerate-elements");
            // Faithful-only frame guard: the rendered and recorded body-relative inertial positions are
            // comparable ONLY about the SAME body. A different recorded body is a re-aim / SOI-leg case -> skip.
            if (!string.Equals(covering.bodyName, renderedBody.bodyName, System.StringComparison.Ordinal))
                return FaithfulParitySample.Skip("body-mismatch");
            CelestialBody recordedBody = ResolveBodyByName(covering.bodyName);
            if (recordedBody == null)
                return FaithfulParitySample.Skip("recorded-body-unresolved");

            // RENDERED sampling clock: derived from the live orbit's OWN epoch convention (the same
            // state-inspection discipline as the synthesized lens's epoch gate, warp-immune - never a
            // drive-truth frame stamp, which can be up to SeedFreshnessFrames stale at a reseed boundary
            // and read orbitalSpeed x lag as false drift at warp):
            //   - epoch == covering.epoch + loopShift (director bake)  -> sample at currentUT
            //   - epoch == covering.epoch (legacy raw)                 -> sample at currentUT - loopShift
            //   - neither (creation / rebind / segment-transition)     -> skip, measured next frame
            // Either way the rendered arc lands on the SAME recorded phase as the reference below, so a
            // faithful loop ghost reads ~0 in BOTH conventions while a wrong-elements / off-orbit draw
            // still drifts.
            double renderedClockUT;
            if (IsSynthEpochConventionBaked(
                    renderedOrbit.epoch, covering.epoch, loopShift, out bool isRawConvention))
                renderedClockUT = currentUT;
            else if (isRawConvention)
                renderedClockUT = lookupUT;
            else
                return FaithfulParitySample.Skip("epoch-transition");

            // Build the rendered Orbit's own visible-span half-window from its period (a full conic) or a
            // fixed window (degenerate / hyperbolic). The RECORDED reference is sampled at the LIVE clock
            // (currentUT) on the +loopShift-baked reference orbit (BuildPhaseMatchedReferenceOrbit), so it
            // traces the recorded mean-anomaly arc the ghost SHOULD draw; the RENDERED orbit is sampled at
            // renderedClockUT (its own epoch-convention clock, above). Keeping the reference phase
            // controlled solely by loopShift keeps the phase-match load-bearing: a wrong loopShift still
            // drifts.
            double halfSpan = ResolveOrbitSampleHalfSpanSeconds(renderedOrbit);
            double[] referenceUTs = Parsek.MapRender.RenderGeometrySampler.BuildSampleUTs(
                currentUT, halfSpan, ParityOrbitSampleCount);
            double[] renderedUTs = Parsek.MapRender.RenderGeometrySampler.BuildSampleUTs(
                renderedClockUT, halfSpan, ParityOrbitSampleCount);
            if (referenceUTs.Length == 0 || renderedUTs.Length == 0)
                return FaithfulParitySample.Skip("no-sample-uts");

            // Rendered samples in the Y-up body-relative frame at the rendered orbit's own clock. NOTE:
            // no icon vertex is appended to the rendered set - the oracle's deviation is one-sided (max
            // over REFERENCE points of the nearest distance to the RENDERED polyline), so an extra
            // rendered vertex can only LOWER distances: it cannot catch an off-arc icon and can only mask
            // real drift. Icon-off-its-own-line detection lives solely in the icon-off-orbit angle check.
            var renderedPts = new Vector3d[renderedUTs.Length];
            for (int i = 0; i < renderedUTs.Length; i++)
                renderedPts[i] = OrbitRelativePositionYup(renderedOrbit, renderedUTs[i]);

            // The recorded reference orbit, built ONCE from the covering segment's Kepler elements with its
            // epoch PHASE-MATCHED to the live rendered orbit (epoch = seg.epoch + loopShift; loopShift == 0 ->
            // BuildOrbitFromSegment verbatim). Wrapped in a try/catch: an unexpected getRelativePositionAtUT
            // throw on degenerate reconstructed elements skips this parity sample (no measurement) rather
            // than aborting the rest of the tracing-on probe pass. (BuildSampleUTs includes the exact centre
            // sample, so no separate currentUT anchor point is appended - it was a duplicate.)
            Orbit recordedOrbit = BuildPhaseMatchedReferenceOrbit(covering, recordedBody, loopShift);
            if (recordedOrbit == null)
                return FaithfulParitySample.Skip("reference-orbit-build-failed");
            var referencePts = new Vector3d[referenceUTs.Length];
            try
            {
                for (int i = 0; i < referenceUTs.Length; i++)
                    referencePts[i] = OrbitRelativePositionYup(recordedOrbit, referenceUTs[i]);
            }
            catch (System.Exception)
            {
                return FaithfulParitySample.Skip("reference-sample-threw");
            }

            double[] referenceFlat = Parsek.MapRender.RenderGeometrySampler.Flatten(
                referencePts, referencePts.Length);
            double[] renderedFlat = Parsek.MapRender.RenderGeometrySampler.Flatten(
                renderedPts, renderedPts.Length);

            // Per-scenario tolerance derived from the recorded reference's OWN scale (its bounding-box
            // diagonal) - NOT a blanket metre constant (a LKO arc and a heliocentric transfer want wildly
            // different absolute tolerances). 0.1% of the arc extent separates a faithful draw from a
            // looped-rotation / off-orbit / wrong-element draw with margin.
            double scale = Parsek.MapRender.RenderParityOracle.EstimateScaleFromPoints(referenceFlat);
            double tol = Parsek.MapRender.RenderParityOracle.ToleranceForScale(scale);
            Parsek.MapRender.RenderParityOracle.ParityResult result =
                Parsek.MapRender.RenderParityOracle.ComputeDrift(
                    Parsek.MapRender.RenderParityOracle.ParityMode.Faithful,
                    referenceFlat, renderedFlat, tol);

            return new FaithfulParitySample(
                true, null, result, scale,
                covering.semiMajorAxis, covering.eccentricity, covering.bodyName);
        }

        // --- A1 cutover-instrument / design §14: SYNTHESIZED-mode conic parity (Unity capture) ---
        //
        // Samples the RENDERED orbit geometry (the live OrbitDriver.orbit) and the PRODUCER'S INTENDED arc
        // (the Director's fresh StockConic seed = intent.Payload.Conic, the re-aimed OrbitSegment the spine
        // fed the drive THIS frame), both in the SAME Y-up body-relative metres frame, hands them to the
        // pure RenderParityOracle in ParityMode.Synthesized, and emits a parity-drift anomaly on
        // OverTolerance. Validates DRAW-fidelity (rendered == intended), NOT solve-correctness. The intended
        // reference is built PHASE-MATCHED to the live rendered orbit (its epoch baked with the SAME loop
        // shift StockConicTreatment.SeedAndDriveLive drove the rendered conic with) and BOTH orbits are
        // sampled at the SAME live-clock UTs, so a LOOPED re-aim member lands on the same arc as its rendered
        // orbit and reads ~0 when the draw is faithful to the seed (the false-drift BLOCKER fix - the raw-
        // epoch reference traced a different mean-anomaly half-arc and false-fired on every looped member).
        // When there is no fresh seed (a TracedPath / faithful-fallback / hidden member) it skips cleanly. A
        // faithful StockConic member's rendered orbit IS the seed (loop or not), so the diff reads ~0 and
        // never emits - the synthesized lens only LIGHTS UP on a re-aim DRAW regression (rendered conic
        // diverging from the re-aimed seed it was driven from), which the faithful lens (orbit-vs-recorded-
        // segment) skips for exactly those re-aimed members. Additive observability: no draw change. Gated by
        // the caller's IsEnabled check.
        private void TrySampleAndEmitSynthesizedConicParity(
            Orbit renderedOrbit, CelestialBody renderedBody, Vector3d iconBodyRel,
            double currentUT, double effUT, double loopShift, uint pid, string pidKey, string recId,
            string lineActive, string bodyName, double realtime)
        {
            int frame = Time.frameCount;
            // The intended arc: the Director's fresh StockConic seed for this pid. No fresh seed (TracedPath /
            // faithful-fallback / hidden / no shadow run) -> nothing to diff against, skip (no anomaly).
            if (!Parsek.MapRender.ShadowRenderDriver.TryGetFreshStockConicSeed(
                    pid, frame, out OrbitSegment intendedSeg, out string seedBody))
                return;

            // CREATION / REBIND TRANSIENT GATE: only measure when the live orbit ACTUALLY carries the
            // director epoch-bake convention (epoch == seg.epoch + loopShift) the currentUT sampling below
            // assumes. A fresh seed does NOT guarantee the orbit was driven from it yet: on the ghost's
            // CREATION frame (and an ApplyOrbitToVessel rebind window) the orbit still carries the RAW
            // seg.epoch until the next icon-drive FixedUpdate bakes it, so sampling it at currentUT against
            // the +loopShift-baked reference reads a phase offset of n*loopShift - a huge FALSE drift on
            // identical elements (the live 777km/2709km creation-frame emits; a loop member's shift can be
            // years). Direct STATE inspection (the orbit's own epoch) is warp-immune, unlike a drive-truth
            // frame-freshness window. Raw-epoch frames skip quietly (known 1-frame transient, measured next
            // frame once baked); an epoch matching NEITHER convention is logged distinctly (unexplained -
            // worth eyes, but not a geometry measurement). Production-only: the in-game synth fixtures drive
            // RAW-epoch ghosts directly and pass their own icon clock, so they bypass this gate by design.
            if (!IsSynthEpochConventionBaked(
                    renderedOrbit.epoch, intendedSeg.epoch, loopShift, out bool isRawConvention))
            {
                ParsekLog.VerboseRateLimited("MapRender", "synth-parity-epoch-gate",
                    string.Format(ic,
                        "Synth parity skipped: rendered orbit epoch={0:F3} is not the baked convention "
                        + "(seg.epoch={1:F3} + loopShift={2:F1}) - {3} pid={4} recId={5} frame={6}",
                        renderedOrbit.epoch, intendedSeg.epoch, loopShift,
                        isRawConvention
                            ? "RAW epoch (creation/rebind transient, measured next baked frame)"
                            : "UNEXPLAINED epoch convention",
                        pid, recId ?? "<none>", frame),
                    5.0);
                return;
            }

            SynthesizedConicParitySample sample = ComputeSynthesizedConicParity(
                renderedOrbit, renderedBody, intendedSeg, seedBody, loopShift, currentUT, effUT);
            if (!sample.Sampled)
                return;
            Parsek.MapRender.RenderParityOracle.ParityResult result = sample.Result;
            if (!result.HasMeasurement || !result.OverTolerance)
                return;
            if (!PassesSynthParityDriftRateLimit(pid, realtime))
                return;

            MapRenderTrace.EmitAnomaly(
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, currentUT,
                MapRenderTrace.AnomalyParityDrift,
                string.Format(ic,
                    "mode=synthesized maxDev={0}m tol={1}m scale={2}m refCount={3} rendCount={4} "
                    + "countMismatch={5} | intendedSeg=[sma={6} ecc={7} body={8}] "
                    + "rendOrbit=[sma={9} ecc={10} body={11}] iconR={12} lineActive={13}",
                    MapRenderTrace.FormatDouble(result.MaxDeviationMeters, "F0"),
                    MapRenderTrace.FormatDouble(result.ToleranceMeters, "F0"),
                    MapRenderTrace.FormatDouble(sample.Scale, "F0"),
                    result.ReferenceCount, result.RenderedCount, result.CountMismatch,
                    MapRenderTrace.FormatDouble(intendedSeg.semiMajorAxis, "F0"),
                    MapRenderTrace.FormatDouble(intendedSeg.eccentricity, "F4"),
                    intendedSeg.bodyName ?? "(null)",
                    MapRenderTrace.FormatDouble(renderedOrbit.semiMajorAxis, "F0"),
                    MapRenderTrace.FormatDouble(renderedOrbit.eccentricity, "F4"),
                    bodyName,
                    MapRenderTrace.FormatDouble(iconBodyRel.magnitude, "F0"),
                    lineActive),
                recId);
        }

        /// <summary>
        /// Outcome of one SYNTHESIZED-mode rendered-conic-vs-intended-arc parity sample. <see cref="Sampled"/>
        /// is false (with <see cref="SkipReason"/>) when the sampler cleanly bailed before a diff (no rendered
        /// orbit / different intended body / degenerate intended elements / reference build or sample fault):
        /// the caller MUST treat a non-sampled outcome as "no anomaly". internal so an in-game baseline test
        /// can exercise the REAL orchestration.
        /// </summary>
        internal readonly struct SynthesizedConicParitySample
        {
            internal bool Sampled { get; }
            internal string SkipReason { get; }
            internal Parsek.MapRender.RenderParityOracle.ParityResult Result { get; }
            internal double Scale { get; }

            internal SynthesizedConicParitySample(
                bool sampled, string skipReason,
                Parsek.MapRender.RenderParityOracle.ParityResult result, double scale)
            {
                Sampled = sampled;
                SkipReason = skipReason;
                Result = result;
                Scale = scale;
            }

            internal static SynthesizedConicParitySample Skip(string reason)
            {
                return new SynthesizedConicParitySample(
                    false, reason,
                    default(Parsek.MapRender.RenderParityOracle.ParityResult), 0.0);
            }
        }

        // The pure-orchestration CORE of the synthesized conic parity sample: build the intended re-aimed
        // reference orbit from the Director's seed segment PHASE-MATCHED to the live rendered orbit (the SAME
        // BuildPhaseMatchedReferenceOrbit loop-shift epoch bake the faithful path uses, NOT the raw-epoch
        // BuildOrbitFromSegment - that was the false-drift BLOCKER on LOOPED members), sample BOTH the
        // rendered orbit and the intended reference at the SAME live-clock UTs in the SAME Y-up body-relative
        // frame, and run the oracle diff in ParityMode.Synthesized. No emit, no rate-limit, no per-pid state.
        // internal for an in-game baseline test.
        internal static SynthesizedConicParitySample ComputeSynthesizedConicParity(
            Orbit renderedOrbit, CelestialBody renderedBody,
            OrbitSegment intendedSeg, string seedBody, double loopShift, double currentUT, double effUT)
        {
            if (renderedOrbit == null || renderedBody == null)
                return SynthesizedConicParitySample.Skip("no-rendered-orbit");
            // The seed's frame body must match the rendered orbit's body: the body-relative inertial
            // positions are comparable ONLY about the same body. A mismatch is a stale-seed / SOI-leg race
            // (the icon is on the next body's orbit while the seed still names the prior body) -> skip.
            string intendedBodyName = !string.IsNullOrEmpty(seedBody) ? seedBody : intendedSeg.bodyName;
            if (!string.Equals(intendedBodyName, renderedBody.bodyName, System.StringComparison.Ordinal))
                return SynthesizedConicParitySample.Skip("body-mismatch");
            if (!TrajectoryMath.HasUsableOrbitSegmentElements(intendedSeg))
                return SynthesizedConicParitySample.Skip("degenerate-elements");

            // The intended reference orbit, built from the seed's Kepler elements PHASE-MATCHED to the live
            // rendered orbit via the SAME BuildPhaseMatchedReferenceOrbit helper the faithful path uses. The
            // producer DRIVES the rendered conic at epoch = seg.epoch + loopShift (StockConicTreatment
            // .SeedAndDriveLive, GhostOrbitLinePatch.cs:410 with shift = GetGhostOrbitEpochShift(pid)) and
            // propagates it at the LIVE clock, so the reference MUST use the SAME loop-shift epoch bake and be
            // sampled at the SAME live-clock UTs - otherwise a LOOPED member samples a different mean-anomaly
            // half-arc and reads a FALSE drift on a CORRECT draw (the BLOCKER this fix corrects). When the
            // draw is faithful to the seed, both orbits land on the same phase at the same UT and read ~0.
            // A non-loop member's loopShift is ~0, so this is BuildOrbitFromSegment verbatim.
            Orbit intendedOrbit = BuildPhaseMatchedReferenceOrbit(intendedSeg, renderedBody, loopShift);
            if (intendedOrbit == null)
                return SynthesizedConicParitySample.Skip("intended-orbit-build-failed");

            double halfSpan = ResolveOrbitSampleHalfSpanSeconds(renderedOrbit);
            // Sample the INTENDED reference at the LIVE clock (currentUT) with its epoch baked + loopShift, and
            // the RENDERED orbit at the icon-drive clock the CALLER passes as effUT (the clock the rendered orbit
            // actually sits on). In PRODUCTION the synth lens runs only under the director epoch-bake path (it
            // requires TryGetFreshStockConicSeed, which implies IsDirectorDriveActive: the conic is propagated at
            // the live clock), so the caller passes effUT == currentUT and both arcs cover the same span - a
            // faithful draw reads ~0. The in-game synth fixture instead drives a RAW-epoch ghost directly
            // (bypassing the production epoch-bake gate), so it passes effUT == currentUT - loopShift to land the
            // rendered raw orbit on the same recorded phase as the +loopShift-baked reference. A wrong loopShift
            // moves only the reference, so the phase-match stays load-bearing in both cases.
            double[] referenceUTs = Parsek.MapRender.RenderGeometrySampler.BuildSampleUTs(
                currentUT, halfSpan, ParityOrbitSampleCount);
            double[] renderedUTs = Parsek.MapRender.RenderGeometrySampler.BuildSampleUTs(
                effUT, halfSpan, ParityOrbitSampleCount);
            if (referenceUTs.Length == 0 || renderedUTs.Length == 0)
                return SynthesizedConicParitySample.Skip("no-sample-uts");

            // NOTE: no icon vertex is appended to the rendered set - the oracle's deviation is one-sided
            // (max over REFERENCE points of nearest distance to the RENDERED polyline), so an extra
            // rendered vertex can only LOWER distances: it could never catch an icon parked off the
            // intended arc and could only mask real drift, while permanently printing countMismatch=True.
            // Icon-off-its-own-line detection lives solely in the icon-off-orbit angle check.
            var renderedPts = new Vector3d[renderedUTs.Length];
            var referencePts = new Vector3d[referenceUTs.Length];
            try
            {
                for (int i = 0; i < referenceUTs.Length; i++)
                    referencePts[i] = OrbitRelativePositionYup(intendedOrbit, referenceUTs[i]);
                for (int i = 0; i < renderedUTs.Length; i++)
                    renderedPts[i] = OrbitRelativePositionYup(renderedOrbit, renderedUTs[i]);
            }
            catch (System.Exception)
            {
                return SynthesizedConicParitySample.Skip("sample-threw");
            }

            double[] referenceFlat = Parsek.MapRender.RenderGeometrySampler.Flatten(
                referencePts, referencePts.Length);
            double[] renderedFlat = Parsek.MapRender.RenderGeometrySampler.Flatten(
                renderedPts, renderedPts.Length);

            double scale = Parsek.MapRender.RenderParityOracle.EstimateScaleFromPoints(referenceFlat);
            double tol = Parsek.MapRender.RenderParityOracle.ToleranceForScale(scale);
            Parsek.MapRender.RenderParityOracle.ParityResult result =
                Parsek.MapRender.RenderParityOracle.ComputeDrift(
                    Parsek.MapRender.RenderParityOracle.ParityMode.Synthesized,
                    referenceFlat, renderedFlat, tol);

            return new SynthesizedConicParitySample(true, null, result, scale);
        }

        // --- A1 cutover-instrument / design §14: SYNTHESIZED-mode POLYLINE leg parity (Unity capture) ---
        //
        // Once per frame: for every NON-ANCHORED body-fixed TracedPath polyline leg the renderer drew this
        // frame, the renderer hands back the rendered VectorLine geometry and the recorded leg surface track
        // BOTH in body-relative world metres (CaptureRenderedVsRecordedLegGeometry isolates all the Unity
        // reads AND skips conic-anchored legs), and this method runs the pure RenderParityOracle diff + emits
        // a parity-drift anomaly on OverTolerance. CONIC-ANCHORED vacuum-maneuver legs are NOT covered (the
        // draw intentionally rotates them ~96 deg off the raw recorded surface track, so a rendered-vs-raw-
        // recorded diff would false-fire on a CORRECT anchor; the anchor's per-point Slerp is not cheaply
        // recoverable to build the reference in - a stated limitation). The lens therefore validates the
        // descent / atmospheric / surface re-stitch the audit cares about, where rendered == recorded. Per-
        // RECORDING rate-limited (legs are per-recording, not per-pid). The geometry math (the oracle) is pure
        // / unit-tested; only the Unity sampling (points3 / ScaledSpace / GetWorldSurfacePosition) lives in
        // the renderer accessor, in-game-only.
        private void CapturePolylineLegParity(int frame, double currentUT, double realtime)
        {
            Parsek.Display.GhostTrajectoryPolylineRenderer.CaptureRenderedVsRecordedLegGeometry(
                frame,
                (recId, legIndex, legCount, legBody, renderedFlat, recordedFlat, noiseFloorMeters) =>
                {
                    // Reference = the DRAW-FRAME snapshot of the recorded leg track; rendered = what the
                    // VectorLine actually drew (both from the same draw - see the capture's frame-coherence
                    // contract). noiseFloorMeters is the drawn vertices' float-quantization metrology floor:
                    // the tolerance is clamped up to it so sub-float-noise deviations never fire while a
                    // real mis-draw above the floor still does.
                    Parsek.MapRender.RenderParityOracle.ParityResult result =
                        Parsek.MapRender.RenderParityOracle.ComputeDriftScaleDerived(
                            Parsek.MapRender.RenderParityOracle.ParityMode.Synthesized,
                            recordedFlat, renderedFlat,
                            minToleranceMeters: noiseFloorMeters);
                    if (!result.HasMeasurement || !result.OverTolerance)
                        return;
                    if (!PassesPolylineParityDriftRateLimit(recId, realtime))
                        return;

                    double scale = Parsek.MapRender.RenderParityOracle.EstimateScaleFromPoints(recordedFlat);
                    // recId carried in the marker-surface pid= slot (the same convention the unaccounted-
                    // drawn-recording anomaly uses for proto-less, recording-keyed events).
                    MapRenderTrace.EmitAnomaly(
                        MapRenderTrace.RenderSurface.Polyline, recId, currentUT, currentUT,
                        MapRenderTrace.AnomalyParityDrift,
                        string.Format(ic,
                            "mode=polyline maxDev={0}m tol={1}m scale={2}m noiseFloor={3}m refCount={4} "
                            + "rendCount={5} countMismatch={6} leg={7}/{8} body={9} recId={10}",
                            MapRenderTrace.FormatDouble(result.MaxDeviationMeters, "F0"),
                            MapRenderTrace.FormatDouble(result.ToleranceMeters, "F0"),
                            MapRenderTrace.FormatDouble(scale, "F0"),
                            MapRenderTrace.FormatDouble(noiseFloorMeters, "F0"),
                            result.ReferenceCount, result.RenderedCount, result.CountMismatch,
                            legIndex, legCount, legBody, recId),
                        recId);
                });
        }

        // Half-window (seconds) the parity samples span on either side of the drive clock. A full closed
        // conic samples a quarter-period each side (half the orbit total - enough of the arc to distinguish
        // two same-shaped orbits at different elements / phase while keeping the few getPositionAtUT calls
        // cheap); a degenerate / non-finite period (hyperbolic / open) falls back to a fixed 1-hour window.
        private static double ResolveOrbitSampleHalfSpanSeconds(Orbit orbit)
        {
            const double FallbackHalfSpanSeconds = 3600.0;
            if (orbit == null)
                return FallbackHalfSpanSeconds;
            double period = orbit.period;
            if (double.IsNaN(period) || double.IsInfinity(period) || period <= 0.0)
                return FallbackHalfSpanSeconds;
            return period * 0.25;
        }

        // Build a KSP Orbit from a recorded OrbitSegment's Kepler elements (the SAME construction
        // TrajectoryMath.EvaluateOrbitSegmentAtUT / GhostMapPresence.TryResolveOrbitWorldPosition use). Unity
        // construction isolated here so the pure RenderGeometrySampler stays Unity-ECall-free. Returns null
        // on any throw (degenerate elements). internal so the in-game capture-harness fixture builds the
        // reference orbit through the EXACT production construction.
        internal static Orbit BuildOrbitFromSegment(OrbitSegment seg, CelestialBody body)
        {
            try
            {
                return new Orbit(
                    seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                    seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                    seg.meanAnomalyAtEpoch, seg.epoch, body);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        // Build the FAITHFUL reference orbit with its epoch baked + loopShift: identical Kepler elements to
        // BuildOrbitFromSegment but epoch = seg.epoch + loopShift, so evaluating it at the LIVE clock
        // (currentUT) yields the recorded mean anomaly M = MAE + n*(currentUT - seg.epoch - loopShift) - the
        // recorded phase a loop-shifted ghost SHOULD be drawing. The shift moves PHASE only
        // (LAN/inc/argPe/sma/ecc untouched), so the orbit SHAPE is unchanged. The CALLER cancels the loop phase
        // by sampling this reference at currentUT and the live RENDERED orbit at its own drive clock offEffUT
        // (== currentUT - loopShift on the dominant legacy raw-epoch path), so both land on the same recorded
        // phase for a faithful draw. When loopShift == 0 (a non-loop faithful ghost) this is BuildOrbitFromSegment
        // verbatim. A non-finite loopShift falls back to 0 (raw epoch) rather than poisoning the epoch with NaN.
        // internal so the in-game baseline test builds the reference through the EXACT production path.
        /// <summary>
        /// Pure: the FAITHFUL lens's covering-segment lookup clock - the RECORDED timeline UT
        /// (<c>currentUT - loopShift</c>, NaN/Inf shift treated as 0). rec.OrbitSegments live on the
        /// recorded clock, so the lookup must too: under the unconditional director epoch-bake drive the
        /// icon-drive clock equals currentUT, which for any loop ghost past its first iteration lies
        /// loopShift beyond the recorded span - looking up there silently skipped every loop ghost (the
        /// stack-review BLOCKER). Path-independent: equals the legacy icon-drive effUT on the raw-epoch
        /// path and lands inside the recorded span on the director path. internal + pure for xUnit.
        /// </summary>
        internal static double ResolveFaithfulLookupUT(double currentUT, double loopShift)
        {
            double shift = (double.IsNaN(loopShift) || double.IsInfinity(loopShift)) ? 0.0 : loopShift;
            return currentUT - shift;
        }

        /// <summary>
        /// Pure: does <paramref name="renderedEpoch"/> (the live OrbitDriver.orbit's epoch) carry the
        /// director EPOCH-BAKE convention (<c>seg.epoch + loopShift</c>) that the production synthesized
        /// parity sampling assumes? StockConicTreatment.SeedAndDriveLive copies the seed's epoch + shift
        /// verbatim, so a driven orbit matches EXACTLY; a generous 0.5s slack absorbs double round-trips.
        /// When false, <paramref name="isRawConvention"/> distinguishes the KNOWN transient (the orbit
        /// still carries the RAW <c>seg.epoch</c> from ApplyOrbitToVessel - a creation / rebind frame the
        /// icon-drive has not baked yet) from an UNEXPLAINED epoch state. A non-finite
        /// <paramref name="loopShift"/> is treated as 0 (mirrors BuildPhaseMatchedReferenceOrbit's NaN
        /// fallback, so gate and reference always agree on the baked epoch). Note when loopShift == 0 the
        /// raw and baked conventions coincide: the gate passes and reports non-raw (measurable either way).
        /// internal + pure for direct xUnit coverage.
        /// </summary>
        internal static bool IsSynthEpochConventionBaked(
            double renderedEpoch, double segEpoch, double loopShift, out bool isRawConvention)
        {
            const double EpochSlackSeconds = 0.5;
            double shift = (double.IsNaN(loopShift) || double.IsInfinity(loopShift)) ? 0.0 : loopShift;
            double bakedEpoch = segEpoch + shift;
            if (double.IsNaN(renderedEpoch) || double.IsInfinity(renderedEpoch))
            {
                isRawConvention = false;
                return false;
            }
            if (System.Math.Abs(renderedEpoch - bakedEpoch) <= EpochSlackSeconds)
            {
                isRawConvention = false; // baked (or shift==0, where the two conventions coincide)
                return true;
            }
            isRawConvention = System.Math.Abs(renderedEpoch - segEpoch) <= EpochSlackSeconds;
            return false;
        }

        internal static Orbit BuildPhaseMatchedReferenceOrbit(
            OrbitSegment seg, CelestialBody body, double loopShift)
        {
            double shift = (double.IsNaN(loopShift) || double.IsInfinity(loopShift)) ? 0.0 : loopShift;
            try
            {
                return new Orbit(
                    seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                    seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                    seg.meanAnomalyAtEpoch, seg.epoch + shift, body);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        internal static CelestialBody ResolveBodyByName(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return null;
            var bodies = FlightGlobals.Bodies;
            if (bodies == null)
                return null;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody b = bodies[i];
                if (b != null && string.Equals(b.bodyName, bodyName, System.StringComparison.Ordinal))
                    return b;
            }
            return null;
        }

        // Body-relative orbit position in Y-up WORLD axes (the same frame the icon's
        // bodyRelPos = GetWorldPos3D - body.position lives in). orbit.getRelativePositionAtUT
        // returns Z-up KSP-internal axes; Swizzle() converts to Y-up world. Isolated here so
        // the pure MapRenderTrace.IsIconOffOrbit predicate stays Unity-ECall-free. internal so
        // the in-game parity capture-harness fixture exercises the REAL capture function (the
        // "green but blind" guard: it proves the Unity sampler itself is correct, separate from
        // the oracle's pure diff-math unit tests).
        internal static Vector3d OrbitRelativePositionYup(Orbit orbit, double ut)
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
