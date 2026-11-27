using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// THROWAWAY P9 Y1 timing probe of the FxReentryRig ghost leg (never merged). At each ghost
    /// mark, once that mark's shots are done: up to 7 copies of the probe's ghost are built from
    /// the probe's post-staging snapshot and kept beside the main ghost (2 m apart, so they
    /// overlap on screen), warmed up at 1x so their fire reaches its steady state, then the
    /// clock is slowed (Time.timeScale) so the ghost stays at the mark while frames are timed.
    /// For 1, 4 and 8 ghosts it alternates blocks of frames with the fire of that many ghosts on
    /// and with every ghost's fire isolated off, and logs Time.unscaledDeltaTime per condition:
    /// <c>[FxReentryRig] timing mark=&lt;m&gt; ghosts=&lt;k&gt; onMs= offMs= p95On= p95Off= ...</c>.
    /// Every ghost mesh (8) renders in every frame; only the fire of k ghosts changes, so on
    /// minus off is the fire cost of k ghosts. Parsek logging is suppressed while frames are
    /// sampled (both conditions), so per-frame log writes do not bias either side.
    /// Set PARSEK_FXR_TIMING=0 in the environment to skip it.
    /// This branch half (main's effect): a ghost's fire is isolated with the rig's fxOff
    /// mechanism (its fire particle renderer off and its shell mesh list emptied), held for the
    /// whole off block; the particles keep simulating, as in the fxOff photo frames.
    /// </summary>
    internal sealed partial class FxReentryRig
    {
        private const string TimingVariant = "before(main: particles + shell, fxOff = renderer off + shell list emptied)";
        private const int TimingCopies = 7;
        private const float TimingCopySpacingM = 2f;
        private const int TimingSamples = 120;
        private const int TimingBlockFrames = 12;
        private const int TimingDiscardFrames = 3;
        private const float TimingTimeScale = 0.02f;
        private const double TimingWarmGameSeconds = 0.4;
        private const int TimingWarmMinFrames = 12;
        private const double TimingMarginUt = 0.25;
        private static readonly int[] TimingGhostCounts = { 1, 4, 8 };

        private static ConfigNode timingCopySnapshot;
        private static bool timingSlowActive;
        private static float timingSavedTimeScale = 1f;
        private static bool timingSuppressActive;
        private static bool timingSuppressPrev;

        internal sealed class TimingGhost
        {
            public GhostPlaybackState State;
            public GhostCoOrbitalRig.LiveGhost Live;
            public bool IsMain;
            public int Index;
            public float OffsetM;
            public bool FireOn = true;
            // fxOff bookkeeping (main's effect)
            public ParticleSystemRenderer OffRenderer;
            public List<FireShellMesh> SavedShell;
        }

        private static bool TimingEnabled
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("PARSEK_FXR_TIMING");
                return !string.Equals(v, "0", StringComparison.Ordinal);
            }
        }

        private void CaptureTimingCopySnapshot(Vessel v)
        {
            ConfigNode snap = v != null ? FlightRecorder.TryBackupSnapshot(v) : null;
            if (snap != null) timingCopySnapshot = snap;
            ParsekLog.Info(Tag, $"timing copy snapshot after staging: {(snap != null ? "taken" : "failed, keeping the pre-staging one")} " +
                $"parts={(v != null && v.parts != null ? v.parts.Count : -1).ToString(Ic)}");
        }

        private static double Ut() => Planetarium.GetUniversalTime();

        private static void Stats(List<float> ms, out double mean, out double p95)
        {
            mean = 0; p95 = 0;
            if (ms == null || ms.Count == 0) { mean = double.NaN; p95 = double.NaN; return; }
            double sum = 0;
            for (int i = 0; i < ms.Count; i++) sum += ms[i];
            mean = sum / ms.Count;
            var sorted = new List<float>(ms);
            sorted.Sort();
            int k = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
            p95 = sorted[Mathf.Clamp(k, 0, sorted.Count - 1)];
        }

        private static string Ms(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0.000", Ic);

        private string BuildTimingCopies(GhostPlaybackState main, string mark, List<TimingGhost> ghosts)
        {
            if (timingCopySnapshot == null) return "no copy snapshot (real leg not run in this process)";
            int built = 0;
            var fails = new StringBuilder();
            for (int c = 0; c < TimingCopies; c++)
            {
                int idx = 9001 + c;
                string id = "fxr-timing-" + mark + "-" + c.ToString(Ic) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                if (!GhostCoOrbitalRig.TryBuildGhostFromSnapshot(timingCopySnapshot, id, "ParsekTest_fxr_timing_" + c.ToString(Ic),
                        null, main.vesselName, 0u, Ut(), out GhostCoOrbitalRig.LiveGhost lg, out string fail)
                    || lg?.State?.ghost == null)
                {
                    fails.Append($"[copy {c.ToString(Ic)}: {fail}] ");
                    continue;
                }
                GhostCoOrbitalRig.NeutralisePhysics(lg.State.ghost, out int _, out int _);
                GhostPlaybackState cs = lg.State;
                cs.reentryFxPendingBuild = false;
                cs.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(cs.ghost, cs.heatInfos, idx, cs.vesselName,
                    GhostPlaybackLogic.CollectGhostOwnedMaterials(cs));
                if (cs.reentryFxInfo != null && main.reentryFxInfo != null)
                    cs.reentryFxInfo.lastIntensity = main.reentryFxInfo.lastIntensity;
                cs.ghost.SetActive(true);
                // 2, -2, 4, -4, 6, -6, 8 metres along the root part's z (across the camera view).
                int step = c / 2 + 1;
                float offset = (c % 2 == 0 ? 1f : -1f) * step * TimingCopySpacingM;
                ghosts.Add(new TimingGhost { State = cs, Live = lg, Index = idx, OffsetM = offset });
                built++;
            }
            return $"copies={built.ToString(Ic)}/{TimingCopies.ToString(Ic)} {fails}";
        }

        private static void DestroyTimingCopies(List<TimingGhost> ghosts)
        {
            if (ghosts == null) return;
            foreach (TimingGhost g in ghosts)
                if (!g.IsMain && g.Live != null) GhostCoOrbitalRig.DestroyLiveGhost(g.Live);
        }

        /// <summary>Copies the main ghost's interpolated conditions onto each copy, applies the
        /// per-frame half of the isolation, and drives each copy's reentry FX through the engine
        /// (the same drive the main ghost gets in ParsekFlight.Update).</summary>
        private static void DriveTimingGhosts(GhostPlaybackEngine engine, List<TimingGhost> ghosts)
        {
            GhostPlaybackState main = ghosts[0].State;
            foreach (TimingGhost g in ghosts)
            {
                if (!g.FireOn) IsolateFrame(g);
                if (g.IsMain) continue;
                GhostPlaybackState cs = g.State;
                if (cs == null || cs.ghost == null) continue;
                cs.lastInterpolatedBodyName = main.lastInterpolatedBodyName;
                cs.lastInterpolatedAltitude = main.lastInterpolatedAltitude;
                cs.lastInterpolatedSurfaceVelocity = main.lastInterpolatedSurfaceVelocity;
                cs.lastInterpolatedSurfaceVelocitySource = main.lastInterpolatedSurfaceVelocitySource;
                engine.UpdateReentryFx(g.Index, cs, cs.vesselName, TimeWarp.CurrentRate);
            }
        }

        // ---- branch half: main's effect (particles + shell), isolated with the fxOff mechanism ----

        /// <summary>The per-frame half of the isolation: nothing on this branch (fxOff holds).</summary>
        private static void IsolateFrame(TimingGhost g) { }

        /// <summary>Fire on / off with the rig's fxOff mechanism, held until switched back.</summary>
        private static void SetTimingFire(TimingGhost g, bool on)
        {
            if (g.FireOn == on) return;
            g.FireOn = on;
            ReentryFxInfo info = g.State != null ? g.State.reentryFxInfo : null;
            if (!on)
            {
                if (info == null) return;
                if (info.fireParticles != null)
                {
                    var pr = info.fireParticles.GetComponent<ParticleSystemRenderer>();
                    if (pr != null && pr.enabled) { pr.enabled = false; g.OffRenderer = pr; }
                }
                g.SavedShell = info.fireShellMeshes;
                info.fireShellMeshes = new List<FireShellMesh>();
            }
            else
            {
                if (g.OffRenderer != null) g.OffRenderer.enabled = true;
                g.OffRenderer = null;
                if (info != null && g.SavedShell != null) info.fireShellMeshes = g.SavedShell;
                g.SavedShell = null;
            }
        }

        /// <summary>What the fire of the ghosts looks like at the end of an on block.</summary>
        private static string TimingFireState(List<TimingGhost> ghosts, int k)
        {
            int particles = 0, shells = 0, built = 0;
            float maxIntensity = 0f;
            for (int i = 0; i < ghosts.Count && i < k; i++)
            {
                ReentryFxInfo info = ghosts[i].State != null ? ghosts[i].State.reentryFxInfo : null;
                if (info == null) continue;
                built++;
                if (info.fireParticles != null) particles += info.fireParticles.particleCount;
                if (info.fireShellMeshes != null) shells += info.fireShellMeshes.Count;
                maxIntensity = Math.Max(maxIntensity, info.lastIntensity);
            }
            return $"fxBuilt={built.ToString(Ic)} intensity={maxIntensity.ToString("0.000", Ic)} fireParticles={particles.ToString(Ic)} shellMeshes={shells.ToString(Ic)}";
        }

        // ---- shared ----

        private void RestoreTimingProbe(string why)
        {
            if (timingSuppressActive) { ParsekLog.SuppressLogging = timingSuppressPrev; timingSuppressActive = false; }
            if (timingSlowActive)
            {
                Time.timeScale = timingSavedTimeScale;
                timingSlowActive = false;
                ParsekLog.Info(Tag, $"timing time scale restored ({why}): {timingSavedTimeScale.ToString("0.00", Ic)}");
            }
        }

        private IEnumerator TimingProbe(string mark, double nextShotUt)
        {
            if (!TimingEnabled) { ParsekLog.Info(Tag, $"timing mark={mark} skipped: PARSEK_FXR_TIMING=0"); yield break; }
            ParsekFlight f = ParsekFlight.Instance;
            GhostPlaybackEngine engine = f != null ? f.Engine : null;
            if (engine == null) { ParsekLog.Info(Tag, $"timing mark={mark} skipped: no flight / engine"); yield break; }
            if (!FindGhost(mainRecIds, rootPartPid, out int mainIdx, out GhostPlaybackState st, out Transform mainPart))
            {
                ParsekLog.Info(Tag, $"timing mark={mark} skipped: no main ghost ut={Ut().ToString("0.000", Ic)}");
                yield break;
            }
            if (TimeWarp.CurrentRateIndex != 0 || Math.Abs(Time.timeScale - 1f) > 1e-3f)
            {
                ParsekLog.Info(Tag, $"timing mark={mark} skipped: warp index={TimeWarp.CurrentRateIndex.ToString(Ic)} timeScale={Time.timeScale.ToString("0.000", Ic)}");
                yield break;
            }

            SetPose(mainPart, Vector3.right, new Vector3(0f, 0f, SideGhostOffset * 0.5f), MainDist);
            var ghosts = new List<TimingGhost> { new TimingGhost { State = st, IsMain = true, Index = mainIdx } };
            float buildStart = Time.realtimeSinceStartup;
            string buildLine = BuildTimingCopies(st, mark, ghosts);
            float buildMs = (Time.realtimeSinceStartup - buildStart) * 1000f;
            FxReentryRigCopyMover mover = gameObject.GetComponent<FxReentryRigCopyMover>() ?? gameObject.AddComponent<FxReentryRigCopyMover>();
            mover.Follow(st.ghost.transform, mainPart, ghosts);
            ParsekLog.Info(Tag, $"timing mark={mark} begin variant={TimingVariant} ut={Ut().ToString("0.000", Ic)} nextShotUt={nextShotUt.ToString("0.000", Ic)} " +
                $"mainIndex={mainIdx.ToString(Ic)} {buildLine}buildMs={buildMs.ToString("0.0", Ic)} meshGhosts={ghosts.Count.ToString(Ic)} " +
                $"samples={TimingSamples.ToString(Ic)} block={TimingBlockFrames.ToString(Ic)} discard={TimingDiscardFrames.ToString(Ic)} slowTimeScale={TimingTimeScale.ToString("0.000", Ic)}");

            try
            {
                // Warm-up at 1x: the copies' fire reaches its steady state (particle lifetime
                // 0.1 to 0.35 s on main's effect) before the clock slows.
                double ut0 = Ut();
                int wf = 0;
                while ((Ut() < ut0 + TimingWarmGameSeconds || wf < TimingWarmMinFrames) && wf < 600)
                {
                    yield return null;
                    if (st.ghost == null) break;
                    DriveTimingGhosts(engine, ghosts);
                    wf++;
                }
                if (st.ghost == null) { ParsekLog.Info(Tag, $"timing mark={mark} aborted: main ghost gone during warm-up"); yield break; }
                ParsekLog.Info(Tag, $"timing mark={mark} warm frames={wf.ToString(Ic)} ut={Ut().ToString("0.000", Ic)} {TimingFireState(ghosts, ghosts.Count)}");

                timingSavedTimeScale = Time.timeScale;
                Time.timeScale = TimingTimeScale;
                timingSlowActive = true;

                foreach (int want in TimingGhostCounts)
                {
                    if (Ut() > nextShotUt - TimingMarginUt)
                    {
                        ParsekLog.Info(Tag, $"timing mark={mark} ghosts={want.ToString(Ic)} truncated: ut={Ut().ToString("0.000", Ic)} next shot at {nextShotUt.ToString("0.000", Ic)}");
                        break;
                    }
                    int k = Math.Min(want, ghosts.Count);
                    var on = new List<float>(TimingSamples);
                    var off = new List<float>(TimingSamples);
                    string fireAtOn = "-";
                    double utStart = Ut();
                    float wallStart = Time.realtimeSinceStartup;
                    timingSuppressPrev = ParsekLog.SuppressLogging;
                    ParsekLog.SuppressLogging = true;
                    timingSuppressActive = true;
                    bool aborted = false;
                    string abortWhy = "-";
                    for (int b = 0; (on.Count < TimingSamples || off.Count < TimingSamples) && b < 400; b++)
                    {
                        bool blockOn = (b % 2) == 0;
                        for (int i = 0; i < ghosts.Count; i++) SetTimingFire(ghosts[i], blockOn && i < k);
                        for (int fi = 0; fi < TimingBlockFrames; fi++)
                        {
                            yield return null;
                            if (st.ghost == null) { aborted = true; abortWhy = "main ghost gone"; break; }
                            if (Ut() > nextShotUt - TimingMarginUt) { aborted = true; abortWhy = "next shot near"; break; }
                            DriveTimingGhosts(engine, ghosts);
                            if (fi < TimingDiscardFrames) continue;
                            List<float> into = blockOn ? on : off;
                            if (into.Count < TimingSamples) into.Add(Time.unscaledDeltaTime * 1000f);
                        }
                        if (aborted) break;
                        if (blockOn) fireAtOn = TimingFireState(ghosts, k);
                    }
                    for (int i = 0; i < ghosts.Count; i++) SetTimingFire(ghosts[i], true);
                    ParsekLog.SuppressLogging = timingSuppressPrev;
                    timingSuppressActive = false;
                    Stats(on, out double onMean, out double onP95);
                    Stats(off, out double offMean, out double offP95);
                    ParsekLog.Info(Tag, $"timing mark={mark} ghosts={k.ToString(Ic)} onMs={Ms(onMean)} offMs={Ms(offMean)} p95On={Ms(onP95)} p95Off={Ms(offP95)} " +
                        $"deltaMs={Ms(onMean - offMean)} nOn={on.Count.ToString(Ic)} nOff={off.Count.ToString(Ic)} requested={want.ToString(Ic)} meshGhosts={ghosts.Count.ToString(Ic)} " +
                        $"utSpan={(Ut() - utStart).ToString("0.000", Ic)} wallS={(Time.realtimeSinceStartup - wallStart).ToString("0.0", Ic)} " +
                        $"aborted={aborted} abortWhy={abortWhy} atOn[{fireAtOn}] variant={TimingVariant}");
                    if (aborted) break;
                }
            }
            finally
            {
                if (ghosts != null) for (int i = 0; i < ghosts.Count; i++) SetTimingFire(ghosts[i], true);
                RestoreTimingProbe("mark " + mark + " done");
                mover.Clear();
                DestroyTimingCopies(ghosts);
                ParsekLog.Info(Tag, $"timing mark={mark} end ut={Ut().ToString("0.000", Ic)}");
            }
        }
    }

    /// <summary>
    /// Keeps the timing probe's ghost copies beside the main ghost: runs after ParsekFlight's
    /// LateUpdate (the main ghost's final placement) and before the frame's FX submission.
    /// </summary>
    [DefaultExecutionOrder(5000)]
    internal sealed class FxReentryRigCopyMover : MonoBehaviour
    {
        private Transform root, frame;
        private List<FxReentryRig.TimingGhost> ghosts;

        internal void Follow(Transform mainRoot, Transform mainFrame, List<FxReentryRig.TimingGhost> list)
        {
            root = mainRoot; frame = mainFrame; ghosts = list;
            Place();
        }

        internal void Clear() { root = null; frame = null; ghosts = null; }

        private void LateUpdate() => Place();

        private void Place()
        {
            if (root == null || frame == null || ghosts == null) return;
            foreach (FxReentryRig.TimingGhost g in ghosts)
            {
                if (g.IsMain || g.State == null || g.State.ghost == null) continue;
                Transform t = g.State.ghost.transform;
                t.SetPositionAndRotation(root.position + frame.rotation * new Vector3(0f, 0f, g.OffsetM), root.rotation);
            }
        }
    }
}
