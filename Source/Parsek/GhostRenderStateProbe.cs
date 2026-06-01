using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Diagnostic per-frame probe of the ACTUALLY RENDERED state of every ghost map
    /// vessel. Catches the failure modes the existing logging cannot see:
    /// <list type="bullet">
    /// <item><see cref="Parsek.Patches.GhostOrbitLinePatch.LogOrbitLineDecision"/> is
    /// <see cref="ParsekLog.VerboseOnChange"/>, so it only logs when OUR DECISION
    /// changes: if KSP / another patch toggles <c>orbitRenderer.line.active</c> or
    /// <c>orbitRenderer.enabled</c> BETWEEN our LateUpdate Postfix invocations, the
    /// decision log shows a steady "visible-body-frame, line.active=True" while the
    /// rendered line actually blinks. The probe samples those fields at end-of-frame
    /// (execution order 10000) and logs every discrete transition via VerboseOnChange,
    /// so a 1-frame toggle out and back becomes two log lines.</item>
    /// <item>The existing <c>icon-pos-delta</c> log is <see cref="ParsekLog.VerboseRateLimited"/>
    /// at 5 s/pid, so it samples the icon position only every 5 s and misses an
    /// intra-window teleport-and-snap-back. The probe runs a per-frame jump detector
    /// against <see cref="Vessel.GetWorldPos3D"/> with a 1000 km/frame threshold; a
    /// genuine orbital coast moves the icon by thousands of m/frame even at high warp
    /// (logs show ~2860 m/frame on the heliocentric coast), so any frame above the
    /// threshold is a real teleport worth a discrete log line.</item>
    /// <item><c>GhostOrbitLinePatch.Postfix</c> runs only when KSP invokes
    /// <c>OrbitRendererBase.LateUpdate</c> for that renderer; if KSP skips a frame
    /// (e.g. the renderer is disabled mid-frame, or the unloaded ghost's MonoBehaviour
    /// is not scheduled), our decision log goes silent and we cannot tell whether the
    /// blink is from our patch's decision flipping or from the patch never running.
    /// The probe's frame-count delta lets us see whether the renderer state actually
    /// updated this frame.</item>
    /// </list>
    ///
    /// Read-only: this never mutates renderer / orbit / line state (every site reads
    /// and logs only). Placed at <see cref="DefaultExecutionOrderAttribute"/> 10000 so
    /// the LateUpdate runs AFTER OrbitRendererBase.LateUpdate (order 0, where
    /// GhostOrbitLinePatch.Postfix runs) and after the polyline Driver (order -50).
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [KSPAddon(KSPAddon.Startup.Instantly, true /* once */)]
    internal sealed class GhostRenderStateProbe : MonoBehaviour
    {
        private const string Tag = "GhostRenderProbe";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;
        private static GhostRenderStateProbe instance;

        /// <summary>
        /// DISABLED BY DEFAULT. This is an opt-in forensic diagnostic (it found the
        /// SOI-transition orbit-line blink, fixed in <see cref="Parsek.Patches.GhostOrbitDominantBodyPatch"/>).
        /// It does per-frame, per-ghost reflection (<see cref="System.Reflection.FieldInfo.GetValue"/>)
        /// plus position sampling, which must NOT run in normal play, so the per-frame
        /// body of <see cref="LateUpdate"/> early-returns unless this is flipped true.
        /// Re-arm it from a debugger / temporary edit when investigating a future
        /// rendering issue. (The file is retained rather than deleted only because file
        /// deletion was unavailable in the authoring environment; it is safe to delete.)
        /// </summary>
        internal static bool Enabled = false;

        /// <summary>Single-frame icon-position delta threshold for the JUMP detector
        /// (metres). The heliocentric coast under high warp moves the icon by a few km
        /// per frame; an SOI-exit teleport in the log was tens of millions of metres,
        /// so 1000 km/frame cleanly separates "real teleport" from "fast warp".</summary>
        private const double IconJumpThresholdM = 1_000_000.0;

        // Cached reflection on the protected OrbitRendererBase.line field. KSP marks
        // the Vectrosity line protected; the Harmony patch can see it via __instance,
        // but a regular MonoBehaviour cannot. Reflection is resolved once at first use
        // and reused for every sample (FieldInfo.GetValue is cheap).
        private FieldInfo orbitRendererLineField;
        private PropertyInfo vectorLineActiveProperty;

        private readonly Dictionary<uint, Vector3d> prevWorldPos = new Dictionary<uint, Vector3d>();

        void Awake()
        {
            if (instance != null) { Destroy(gameObject); return; }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void LateUpdate()
        {
            // Opt-in only (see Enabled): never do per-frame per-ghost reflection in
            // normal play. Disabled by default, so this is a one-bool no-op in release.
            if (!Enabled)
                return;

            // Only sample in map-capable scenes; this is cheap, but no point firing
            // it on the main menu / KSC / editor.
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.FLIGHT && scene != GameScenes.TRACKSTATION)
                return;

            if (GhostMapPresence.ghostMapVesselPids.Count == 0)
                return;

            var vessels = FlightGlobals.Vessels;
            if (vessels == null) return;
            int frame = Time.frameCount;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null) continue;
                uint pid = v.persistentId;
                if (!GhostMapPresence.ghostMapVesselPids.Contains(pid)) continue;
                Sample(v, pid, frame);
            }
        }

        private void Sample(Vessel v, uint pid, int frame)
        {
            // --- Renderer-level fields (cheap; no reflection) ---
            OrbitRendererBase rendererBase = v.orbitRenderer;
            OrbitDriver driver = v.orbitDriver;

            bool rendererEnabled = rendererBase != null && rendererBase.enabled;
            string drawMode = rendererBase != null ? rendererBase.drawMode.ToString() : "(no-renderer)";
            string drawIcons = rendererBase != null ? rendererBase.drawIcons.ToString() : "(no-renderer)";

            string bodyName = "(none)";
            double sma = double.NaN, ecc = double.NaN;
            if (driver != null && driver.orbit != null)
            {
                if (driver.orbit.referenceBody != null) bodyName = driver.orbit.referenceBody.bodyName;
                sma = driver.orbit.semiMajorAxis;
                ecc = driver.orbit.eccentricity;
            }

            // --- VectorLine.active via cached reflection (Harmony Postfix can access
            // OrbitRendererBase.line directly via __instance, but we cannot here) ---
            string lineActive = ReadLineActive(rendererBase);

            // Discrete transitions per field. VerboseOnChange ensures we emit one log
            // line at the moment of every change (and only then) so a 1-frame toggle
            // out-and-back is two log lines, not silence.
            string pidStr = pid.ToString(ic);
            ParsekLog.VerboseOnChange(Tag,
                "probe-line-" + pidStr,
                lineActive,
                string.Format(ic, "render-state line.active CHANGED pid={0} now={1} renderer.enabled={2} drawMode={3} drawIcons={4} body={5} sma={6:F0} ecc={7:F4} frame={8}",
                    pid, lineActive, rendererEnabled, drawMode, drawIcons, bodyName, sma, ecc, frame));
            ParsekLog.VerboseOnChange(Tag,
                "probe-renderer-" + pidStr,
                rendererEnabled.ToString(),
                string.Format(ic, "render-state renderer.enabled CHANGED pid={0} now={1} line.active={2} drawMode={3} body={4} frame={5}",
                    pid, rendererEnabled, lineActive, drawMode, bodyName, frame));
            ParsekLog.VerboseOnChange(Tag,
                "probe-icons-" + pidStr,
                drawIcons,
                string.Format(ic, "render-state drawIcons CHANGED pid={0} now={1} line.active={2} renderer.enabled={3} body={4} frame={5}",
                    pid, drawIcons, lineActive, rendererEnabled, bodyName, frame));
            ParsekLog.VerboseOnChange(Tag,
                "probe-body-" + pidStr,
                bodyName + "|" + sma.ToString("F0", ic) + "|" + ecc.ToString("F4", ic),
                string.Format(ic, "render-state body/orbit CHANGED pid={0} body={1} sma={2:F0} ecc={3:F4} line.active={4} renderer.enabled={5} frame={6}",
                    pid, bodyName, sma, ecc, lineActive, rendererEnabled, frame));

            // --- Icon JUMP detector (per-frame, not rate-limited) ---
            Vector3d worldPos = v.GetWorldPos3D();
            if (prevWorldPos.TryGetValue(pid, out Vector3d prev))
            {
                double dPos = (worldPos - prev).magnitude;
                if (dPos > IconJumpThresholdM
                    && !double.IsNaN(dPos) && !double.IsInfinity(dPos))
                {
                    // Soft rate-limit to avoid flooding on a runaway hyperbola fling
                    // (we still get a discrete event for every distinct teleport,
                    // every ~0.5 s real time at worst).
                    ParsekLog.VerboseRateLimited(Tag, "probe-jump-" + pidStr,
                        string.Format(ic, "render-state icon JUMP pid={0} dPos={1:F0}m frame={2} body={3} line.active={4} renderer.enabled={5} sma={6:F0} ecc={7:F4} worldPos=({8:F0},{9:F0},{10:F0})",
                            pid, dPos, frame, bodyName, lineActive, rendererEnabled, sma, ecc,
                            worldPos.x, worldPos.y, worldPos.z),
                        0.5);
                }
            }
            prevWorldPos[pid] = worldPos;
        }

        private string ReadLineActive(OrbitRendererBase rendererBase)
        {
            if (rendererBase == null) return "(no-renderer)";
            try
            {
                if (orbitRendererLineField == null)
                {
                    orbitRendererLineField = typeof(OrbitRendererBase).GetField(
                        "line",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (orbitRendererLineField == null)
                        return "(field-missing)";
                }
                object line = orbitRendererLineField.GetValue(rendererBase);
                if (line == null) return "(line-null)";
                if (vectorLineActiveProperty == null)
                {
                    vectorLineActiveProperty = line.GetType().GetProperty("active");
                    if (vectorLineActiveProperty == null)
                        return "(prop-missing)";
                }
                object val = vectorLineActiveProperty.GetValue(line);
                return val != null ? val.ToString() : "(prop-null)";
            }
            catch (System.Exception ex)
            {
                return "(reflect-err:" + ex.GetType().Name + ")";
            }
        }
    }
}
