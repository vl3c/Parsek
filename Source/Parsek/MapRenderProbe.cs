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
        // Frame of the last line.active toggle per pid (for the blink predicate).
        private readonly Dictionary<uint, int> lastLineToggleFrame = new Dictionary<uint, int>();
        // Soft rate-limit timestamps for the per-pid jump anomaly.
        private readonly Dictionary<uint, double> lastJumpEmitRealtime = new Dictionary<uint, double>();
        // Soft rate-limit timestamps for the per-pid icon-off-orbit anomaly.
        private readonly Dictionary<uint, double> lastOffOrbitEmitRealtime = new Dictionary<uint, double>();
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
            lastLineToggleFrame.Clear();
            lastJumpEmitRealtime.Clear();
            lastOffOrbitEmitRealtime.Clear();
            firstPositionEmittedPids.Clear();
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
                    MapRenderTrace.FormatDouble(sma, "F0"), MapRenderTrace.FormatDouble(ecc, "F4")));

            EmitTruthOnChange(lastRendererEnabled, pid, rendererEnabled,
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT,
                "renderer.enabled",
                string.Format(ic,
                    "renderer.enabled={0} lineActive={1} drawMode={2} body={3}",
                    rendererEnabled, lineActive, drawMode, bodyName));

            EmitTruthOnChange(lastDrawIcons, pid, drawIcons,
                MapRenderTrace.RenderSurface.ProtoIcon, pidKey, currentUT,
                "drawIcons",
                string.Format(ic,
                    "drawIcons={0} lineActive={1} renderer.enabled={2} body={3}",
                    drawIcons, lineActive, rendererEnabled, bodyName));

            string bodyOrbitKey = bodyName + "|"
                + MapRenderTrace.FormatDouble(sma, "F0") + "|"
                + MapRenderTrace.FormatDouble(ecc, "F4");
            EmitTruthOnChange(lastBodyOrbit, pid, bodyOrbitKey,
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT,
                "body-orbit",
                string.Format(ic,
                    "body={0} sma={1} ecc={2} lineActive={3} renderer.enabled={4}",
                    bodyName, MapRenderTrace.FormatDouble(sma, "F0"),
                    MapRenderTrace.FormatDouble(ecc, "F4"), lineActive, rendererEnabled));

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
                            mismatch, lineIntent.Reason, frame - lineIntent.Frame));
            }

            // --- Tier-C polyline-orbit-overlap anomaly ---
            // The proto orbit line + icon must not draw while the trajectory polyline owns this
            // recording's non-orbital leg (the double-draw seam). A higher-level invariant check
            // independent of the patch's intent. The drawIcons facet is live now; the line facet
            // lights up with the OrbitLine-reflection fix.
            bool polylineOwns = GhostMapPresence.IsPolylineOwningGhostPhase(pid);
            string overlap = MapRenderTrace.ReconcilePolylineOverlap(
                polylineOwns, lineActive, drawIcons);
            if (!string.IsNullOrEmpty(overlap))
                MapRenderTrace.EmitAnomaly(
                    MapRenderTrace.RenderSurface.Polyline, pidKey, currentUT, currentUT,
                    "polyline-orbit-overlap", overlap);

            // --- Tier-C new-pipeline reconcile: intent (shadow) vs the OLD path's truth ---
            // If the new render pipeline recorded a GhostRenderIntent for this pid THIS frame
            // (decision-only shadow producer, wired in Phase 4), compare it against what the old
            // scattered coordination actually drew. A divergence is the bug class the rewrite exists
            // to surface (the new single-owner Director would have rendered something different).
            // No fresh intent → no-op, so this is dormant until the Phase 4 shadow scene calls
            // GhostRenderReconciler.NoteIntent. Reuses the same old-path truth read as above.
            Parsek.MapRender.GhostRenderReconciler.CheckIntentAgainstOldTruth(
                pid, pidKey, frame, currentUT, currentUT, lineActive, drawIcons, polylineOwns);

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
                        worldPos, bodyName, sma, ecc, "first-truth-read"));
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
                        && PassesJumpRateLimit(pid, realtime))
                    {
                        // dPosWorld is the raw world-frame delta, logged only as
                        // context so a re-test shows how much the reference-body
                        // frame motion was inflating the old metric.
                        Vector3d prevWorld;
                        double dPosWorld = prevWorldPos.TryGetValue(pid, out prevWorld)
                            ? (worldPos - prevWorld).magnitude
                            : double.NaN;
                        MapRenderTrace.EmitAnomaly(
                            MapRenderTrace.RenderSurface.ProtoIcon, pidKey, currentUT, currentUT,
                            "icon-jump",
                            string.Format(ic,
                                "dPos={0}m expectedDP={1}m dPosWorld={2}m warpRate={3} dt={4} body={5} lineActive={6} renderer.enabled={7} sma={8} ecc={9} bodyRelPos={10}",
                                MapRenderTrace.FormatDouble(dPos, "F0"),
                                MapRenderTrace.FormatDouble(expectedMotion, "F0"),
                                MapRenderTrace.FormatDouble(dPosWorld, "F0"),
                                UnityWarpRate().ToString("F0", ic),
                                UnityUnscaledDeltaTime().ToString("F4", ic),
                                bodyName, lineActive, rendererEnabled,
                                MapRenderTrace.FormatDouble(sma, "F0"),
                                MapRenderTrace.FormatDouble(ecc, "F4"),
                                MapRenderTrace.FormatVector3d(bodyRelPos)));
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
                    // Director-drive (mapRenderDirectorDrive) bakes the loop shift into the orbit EPOCH
                    // and resolves the icon at the LIVE clock, so the orbit's own LIVE-clock position IS
                    // the recorded phase the icon should sit on - compare against effUT = currentUT
                    // (shift 0). The legacy raw-epoch path drives the icon at effUT = currentUT - shift,
                    // so it keeps the recorded-clock comparison. The active check is keyed on the SEED
                    // (refreshed by the shadow in Update every render frame), the SAME predicate the
                    // icon-drive + arc-clip use, so all three agree even on no-FixedUpdate frames - no
                    // stale-stamp double-shift artifact. angleIconVsOrbitEff ~= 0 means the icon rides its
                    // OWN line; recorded-phase correctness is proven by the in-game epoch-bake test.
                    double offShift =
                        Parsek.MapRender.ShadowRenderDriver.IsDirectorDriveActive(pid, Time.frameCount)
                        ? 0.0
                        : GhostMapPresence.GetGhostOrbitEpochShift(pid);
                    double offEffUT = currentUT - offShift;
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
                                bodyName));
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
            // current truth every frame so the window captures continuous motion,
            // not just the on-change transitions. No-op outside a window. The
            // window check guards the string build so closed-window frames pay
            // nothing.
            if (MapRenderTrace.IsDetailedWindowOpen(pidKey, currentUT))
            {
                MapRenderTrace.EmitWindowSnapshot(
                    MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, currentUT,
                    string.Format(ic,
                        "lineActive={0} renderer.enabled={1} drawMode={2} drawIcons={3} body={4} sma={5} ecc={6} worldPos={7}",
                        lineActive, rendererEnabled, drawMode, drawIcons, bodyName,
                        MapRenderTrace.FormatDouble(sma, "F0"), MapRenderTrace.FormatDouble(ecc, "F4"),
                        MapRenderTrace.FormatVector3d(worldPos)));
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
            string details)
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
                    fieldPhase, surface, pidKey, currentUT, currentUT, details);
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

        private bool PassesOffOrbitRateLimit(uint pid, double realtime)
        {
            double last;
            if (lastOffOrbitEmitRealtime.TryGetValue(pid, out last)
                && realtime - last < OffOrbitAnomalyMinIntervalSeconds)
                return false;
            lastOffOrbitEmitRealtime[pid] = realtime;
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
