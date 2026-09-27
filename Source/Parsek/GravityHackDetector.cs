using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using KSP.UI.Screens.DebugToolbar.Screens.Cheats;

namespace Parsek
{
    /// <summary>
    /// Detects the stock Alt+F12 "Hack Gravity" cheat (owner ruling 2026-09-27: one Warn,
    /// no behavior change). Stock keeps the cheat's state on the debug-screen widget
    /// <c>KSP.UI.Screens.DebugToolbar.Screens.Cheats.HackGravity</c> (decompiled KSP 1.12.5):
    /// <c>SetGravityFactor(f)</c> writes <c>GeeASL = original * f</c> on every body and stores
    /// <c>f</c> in the public <c>gravityFactor</c>; turning the toggle off (or Reset) calls
    /// <c>SetGravityFactor(1.0)</c>. So <c>gravityFactor != 1</c> is exactly "some body's gravity
    /// is hacked right now". The state is session-only (never saved).
    ///
    /// <para>Why it matters: an <c>OrbitSegment</c> stores elements, not mu, so an orbit
    /// recorded under factor k replays with the right shape but its mean motion off by
    /// sqrt(k) once the hack is off. Nothing is corrected; the Warn makes such a recording
    /// explainable from KSP.log.</para>
    /// </summary>
    internal static class GravityHackDetector
    {
        internal const string Tag = "Recorder";

        /// <summary>Factors within this of 1.0 read as "not hacked" (slider float round-trip).</summary>
        internal const double FactorEpsilon = 1e-6;

        /// <summary>Test seam: replaces the live widget scan. Return NaN for "not hacked".</summary>
        internal static Func<double> LiveFactorOverrideForTesting;

        internal static bool IsHackedFactor(double factor)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor))
                return false;
            return Math.Abs(factor - 1.0) > FactorEpsilon;
        }

        /// <summary>
        /// Pure decision for the one Warn: a live recording, not yet warned, under a hacked
        /// factor.
        /// </summary>
        internal static bool ShouldWarn(bool recordingLive, bool alreadyWarned, double factor)
        {
            return recordingLive && !alreadyWarned && IsHackedFactor(factor);
        }

        internal static string FormatWarnLine(string trigger, double factor, uint vesselPid)
        {
            return "Gravity hack active while recording (Alt+F12 Hack Gravity): trigger="
                + (trigger ?? "?")
                + " factor=" + factor.ToString("R", CultureInfo.InvariantCulture)
                + " vesselPid=" + vesselPid.ToString(CultureInfo.InvariantCulture)
                + " - orbit segments store elements, not mu, so this recording's orbits"
                + " replay at the wrong rate once gravity is back to normal";
        }

        /// <summary>
        /// The live hacked factor, or NaN when no body's gravity is hacked (or the widget
        /// cannot be read, e.g. headless).
        /// </summary>
        internal static double ReadLiveHackedFactor()
        {
            if (LiveFactorOverrideForTesting != null)
                return LiveFactorOverrideForTesting();
            try
            {
                return ReadLiveHackedFactorCore();
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadLiveHackedFactorCore()
        {
            // Includes the inactive widget of a closed debug screen; the widget prefab
            // itself reads 1.0.
            HackGravity[] widgets = UnityEngine.Resources.FindObjectsOfTypeAll<HackGravity>();
            if (widgets == null)
                return double.NaN;
            for (int i = 0; i < widgets.Length; i++)
            {
                HackGravity w = widgets[i];
                if (w != null && IsHackedFactor(w.gravityFactor))
                    return w.gravityFactor;
            }
            return double.NaN;
        }

        internal static void ResetForTesting()
        {
            LiveFactorOverrideForTesting = null;
        }
    }

    public partial class FlightRecorder
    {
        private bool gravityHackWarned;

        internal bool GravityHackWarnedForTesting => gravityHackWarned;

        /// <summary>
        /// Writes the one gravity-hack Warn for this recorder when the decision says so.
        /// Called at record start and from <see cref="Patches.HackGravityPatch"/> when the
        /// cheat changes mid-recording. Returns true when it warned.
        /// </summary>
        internal bool NoteGravityHack(string trigger, double factor)
        {
            if (!GravityHackDetector.ShouldWarn(IsRecording, gravityHackWarned, factor))
                return false;
            gravityHackWarned = true;
            ParsekLog.Warn(GravityHackDetector.Tag,
                GravityHackDetector.FormatWarnLine(trigger, factor, RecordingVesselId));
            return true;
        }
    }
}
