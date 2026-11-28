using System;
using System.Collections;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// THROWAWAY P9 Y1 invisible-fire diagnostics of the FxReentryRig ghost leg (never merged).
    /// At marks m6 and m375, after the mark's shots and before its timing probe, the main ghost
    /// is posed as in that mark's mainA shot and the renderer's diagnostic capture is requested:
    /// Parsek's FX camera rendered alone (replacement shader, then the plain sanity material) and
    /// the ghost's velocity depth map, plus the renderer's report with its camera-state dump.
    /// Then one frame each is grabbed in SanityUnlit and NoSceneDepth mode. Every image is written
    /// as Screenshots/fxr-diag-&lt;mark&gt;-{fx,plain,depth,sanity,nodepth}.png beside the other
    /// fxr shots. The diagnostic mode is set back to Normal on every exit, including the rig's
    /// OnDestroy when the coroutine is stopped mid-way.
    /// </summary>
    internal sealed partial class FxReentryRig
    {
        private static readonly string[] AeroDiagMarks = { "m6", "m375" };
        // Game seconds left before the next shot when the diagnostics stop waiting, so the
        // timing probe still has its warm-up and no shot of the next mark goes late.
        private const double AeroDiagReserveUt = 1.0;
        private const int AeroDiagMaxCaptureFrames = 310;
        private static bool aeroDiagModeTouched;

        private void RestoreAeroDiagnosticMode(string why)
        {
            if (!aeroDiagModeTouched) return;
            aeroDiagModeTouched = false;
            bool ok = GhostAeroFxRenderer.SetDiagnosticModeForTesting(GhostAeroFxDiagnosticMode.Normal);
            ParsekLog.Info(Tag, $"diag mode restored to Normal ({why}) ok={ok}");
        }

        private static bool AeroDiagOverBudget(double nextShotUt)
            => Planetarium.GetUniversalTime() > nextShotUt - AeroDiagReserveUt;

        private IEnumerator AeroDiagnostics(string mark, double nextShotUt)
        {
            if (Array.IndexOf(AeroDiagMarks, mark) < 0) yield break;
            Shot a = shots.Find(x => x.Mark == mark && x.Kind == "mainA" && x.OnGhost);
            if (a == null || !FindGhost(mainRecIds, rootPartPid, out int _, out GhostPlaybackState st, out Transform frame))
            {
                ParsekLog.Info(Tag, $"diag {mark}: skipped (mainA shot or main ghost missing)");
                yield break;
            }
            if (AeroDiagOverBudget(nextShotUt))
            {
                ParsekLog.Info(Tag, $"diag {mark}: skipped (ut={Planetarium.GetUniversalTime().ToString("0.000", Ic)} within {AeroDiagReserveUt.ToString("0.0", Ic)}s of next shot {nextShotUt.ToString("0.000", Ic)})");
                yield break;
            }
            int written = 0;
            string why = "done";
            try
            {
                SetPose(frame, a.DirLocal, a.FocusLocal, a.Dist);
                yield return null;
                ReentryFxInfo r = st.reentryFxInfo;
                string sheath = r != null && r.lastSheathDecision.HasValue ? r.lastSheathDecision.Value.ToString() : "none";
                ParsekLog.Info(Tag, $"diag {mark}: begin sheath={sheath} intensity={(r != null ? r.lastIntensity : -1f).ToString("0.000", Ic)} " +
                    $"ut={Planetarium.GetUniversalTime().ToString("0.000", Ic)} nextShotUt={nextShotUt.ToString("0.000", Ic)} pose=mainA");

                GhostAeroFxDiagnosticCapture cap = null;
                int waited = 0;
                if (GhostAeroFxRenderer.RequestDiagnosticCaptureForTesting("fxr-diag-" + mark, c => cap = c))
                {
                    for (; cap == null && waited < AeroDiagMaxCaptureFrames; waited++)
                    {
                        if (AeroDiagOverBudget(nextShotUt)) { why = "capture wait over budget"; break; }
                        if (FindGhost(mainRecIds, rootPartPid, out int _, out GhostPlaybackState _, out Transform t))
                            SetPose(t, a.DirLocal, a.FocusLocal, a.Dist);
                        yield return null;
                    }
                }
                if (cap != null)
                {
                    written += PngCounted("fxr-diag-" + mark + "-fx", cap.FxPixels, cap.Width, cap.Height);
                    written += PngCounted("fxr-diag-" + mark + "-plain", cap.PlainPixels, cap.Width, cap.Height);
                    written += PngCounted("fxr-diag-" + mark + "-depth", cap.DepthPixels, cap.DepthSize, cap.DepthSize);
                    ParsekLog.Info(Tag, $"diag {mark} capture after {waited.ToString(Ic)} frames: fx={(cap.FxPixels != null)} plain={(cap.PlainPixels != null)} " +
                        $"depth={(cap.DepthPixels != null)} {cap.Width.ToString(Ic)}x{cap.Height.ToString(Ic)} depthSize={cap.DepthSize.ToString(Ic)} report: {cap.Report}");
                }
                else ParsekLog.Info(Tag, $"diag {mark}: no capture after {waited.ToString(Ic)} frames ({why}; renderer missing or no drawing frame)");

                var modes = new[] { GhostAeroFxDiagnosticMode.SanityUnlit, GhostAeroFxDiagnosticMode.NoSceneDepth };
                foreach (GhostAeroFxDiagnosticMode mode in modes)
                {
                    if (AeroDiagOverBudget(nextShotUt)) { why = "grabs over budget"; ParsekLog.Info(Tag, $"diag {mark} {mode} skipped: over budget"); continue; }
                    aeroDiagModeTouched = true;
                    GhostAeroFxRenderer.SetDiagnosticModeForTesting(mode);
                    if (FindGhost(mainRecIds, rootPartPid, out int _, out GhostPlaybackState _, out Transform t))
                        SetPose(t, a.DirLocal, a.FocusLocal, a.Dist);
                    yield return null;
                    Color32[] px = null; int w = 0, h = 0;
                    yield return Grab((p, ww, hh) => { px = p; w = ww; h = hh; });
                    string suffix = mode == GhostAeroFxDiagnosticMode.SanityUnlit ? "sanity" : "nodepth";
                    written += PngCounted("fxr-diag-" + mark + "-" + suffix, px, w, h);
                    ParsekLog.Info(Tag, $"diag {mark} {mode} grabbed ut={Planetarium.GetUniversalTime().ToString("0.000", Ic)} {w.ToString(Ic)}x{h.ToString(Ic)}");
                }
            }
            finally
            {
                aeroDiagModeTouched = true;
                RestoreAeroDiagnosticMode("diag " + mark + " end");
                ParsekLog.Info(Tag, $"diag {mark} end: fxr-diag files written={written.ToString(Ic)} ({why}) ut={Planetarium.GetUniversalTime().ToString("0.000", Ic)}");
            }
        }

        private int PngCounted(string name, Color32[] px, int w, int h)
        {
            int before = files;
            Png(name, px, w, h);
            return files - before;
        }
    }
}
