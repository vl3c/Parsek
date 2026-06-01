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
