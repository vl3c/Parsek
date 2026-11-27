using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Parsek.InGameTests;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The pure wave and route decisions of a motion run (design-part-ghost-parity.md
    /// Appendix H.2, H.3): the wave kind, the admission bound, the framing box growth, the
    /// render primes, the altered routes of the two ghost controls. No Unity call.
    /// </summary>
    internal static class PartParityMotionPlan
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal const string KindPrefix = "motion:";
        internal const string KindPairSuffix = ":pair";

        /// <summary>The ghost rows, their measures and the atlas of one case after its window,
        /// seconds (136 renders at about 2.2 ms plus the scout primes, H.4, doubled).</summary>
        internal const double CaptureSecondsPerCase = 2.0;

        /// <summary>The largest step between two render primes (the sun tracker's synthesis
        /// sample bound, <c>MaxSynthSampleSeconds</c>, H.2 step 3).</summary>
        internal const double MaxPrimeStepSeconds = 1.0;

        /// <summary>The suffix of the real-vs-real control's copy B case id (logs only).</summary>
        internal const string TwinSuffix = "#b";

        /// <summary>A motion wave holds one family (one context class for the air chutes, the
        /// stage too), and the real-vs-real pair flies in a wave of its own.</summary>
        internal static string WaveKind(PartParityCaseKey key, bool pair)
        {
            PartParityMotionOption o = key?.MotionOption;
            string family = o?.Family ?? "unknown";
            bool air = o != null && o.Context == PartParityStateRules.ContextAir;
            return KindPrefix + family + (air ? ":" + o.State : string.Empty) + (pair ? KindPairSuffix : string.Empty);
        }

        internal static bool IsMotionKind(string kind) => kind != null && kind.StartsWith(KindPrefix, StringComparison.Ordinal);

        internal static int MaxWaveCases(bool air)
            => air ? PartParitySweepPlan.MaxAirWaveCases : PartParityMotionRules.MaxMotionWaveCases;

        /// <summary>A motion wave's worst case: the spawn wait, the settle, a capped window,
        /// every case's ghost rows, the teardown.</summary>
        internal static double WaveWorstCaseSeconds(int cases)
            => PartParitySweepPlan.SpawnTimeoutSeconds + PartParitySweepPlan.MinSettleSeconds
               + PartParityMotionRules.WindowCapSeconds + CaptureSecondsPerCase * Math.Max(0, cases)
               + PartParitySweepPlan.StateTeardownSeconds;

        /// <summary>The first wave of a step always starts; a later one only when its worst
        /// case fits the box (the state wave rule, D.3).</summary>
        internal static bool AdmitWave(double elapsedSeconds, int maxSeconds, bool firstWaveOfStep, int cases)
            => firstWaveOfStep || PartParitySweepPlan.ShouldStartStateWave(elapsedSeconds, maxSeconds, WaveWorstCaseSeconds(cases));

        /// <summary>A box grown about its centre by <paramref name="fraction"/> of its size on
        /// every axis; an empty box stays empty.</summary>
        internal static PartParityAabb Grow(PartParityAabb box, double fraction)
        {
            if (box == null || box.IsEmpty) return box;
            var min = new double[3];
            var max = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double c = (box.Min[i] + box.Max[i]) / 2.0, h = (box.Max[i] - box.Min[i]) / 2.0 * (1.0 + fraction);
                min[i] = c - h;
                max[i] = c + h;
            }
            return PartParityAabb.FromMinMax(min, max);
        }

        /// <summary>One render prime: its UT and the frame it captures (-1 for an uncaptured
        /// sub-prime), and whether that capture is the matched row.</summary>
        internal struct RenderPrime
        {
            internal double Ut;
            internal int Frame;
            internal bool Matched;
        }

        /// <summary>
        /// The render ghost's primes (H.2 step 3): every same-time UT and every matched UT (NaN
        /// skipped), in increasing UT (a same-time capture before a matched one at an equal UT),
        /// with uncaptured sub-primes so no step from <paramref name="startUT"/> on exceeds
        /// <paramref name="maxStep"/>.
        /// </summary>
        internal static List<RenderPrime> RenderPrimes(IList<double> sameUts, IList<double> matchedUts, double startUT, double maxStep)
        {
            var wanted = new List<RenderPrime>();
            for (int k = 0; sameUts != null && k < sameUts.Count; k++)
                if (!double.IsNaN(sameUts[k])) wanted.Add(new RenderPrime { Ut = sameUts[k], Frame = k });
            for (int k = 0; matchedUts != null && k < matchedUts.Count; k++)
                if (!double.IsNaN(matchedUts[k])) wanted.Add(new RenderPrime { Ut = matchedUts[k], Frame = k, Matched = true });
            wanted.Sort((a, b) =>
            {
                int c = a.Ut.CompareTo(b.Ut);
                if (c != 0) return c;
                c = a.Matched.CompareTo(b.Matched);
                return c != 0 ? c : a.Frame.CompareTo(b.Frame);
            });
            var primes = new List<RenderPrime>();
            double last = startUT;
            foreach (RenderPrime p in wanted)
            {
                if (maxStep > 0.0 && !double.IsNaN(last))
                    while (p.Ut - last > maxStep)
                    {
                        last += maxStep;
                        primes.Add(new RenderPrime { Ut = last, Frame = -1 });
                    }
                primes.Add(p);
                last = p.Ut;
            }
            return primes;
        }

        /// <summary><c>ctl-motion-ghost-late</c>'s route (H.3): every polled event
        /// <paramref name="shiftS"/> later, the stowed seeds where they were.</summary>
        internal static List<PartEvent> ShiftEvents(IList<PartEvent> polled, double shiftS)
        {
            var list = new List<PartEvent>();
            if (polled == null) return list;
            foreach (PartEvent e in polled)
            {
                PartEvent c = e;
                c.ut += shiftS;
                list.Add(c);
            }
            return list;
        }

        /// <summary>The ghost's own expected run after its event (H.2): a built clip (clamped
        /// as playback clamps it) for a deploy-info family, the stop hold for a servo, none for
        /// a canopy (written at once) or a light.</summary>
        internal static double GhostExpectedRunSeconds(string kind, double ghostClipS)
        {
            switch (kind)
            {
                case PartParityMotionRules.KindClip:
                case PartParityMotionRules.KindSurface:
                    return double.IsNaN(ghostClipS) ? 0.0 : ghostClipS;
                case PartParityMotionRules.KindServo:
                    return PartParityMotionRules.ServoStopHoldSeconds;
                default:
                    return 0.0;
            }
        }

        /// <summary>A control route's own window extension (H.3): the slow route's doubled clip
        /// adds one clip, the late route its shift.</summary>
        internal static double ControlExtraSeconds(string controlId, double ghostClipS)
        {
            if (controlId == TestCommandPartParitySweep.ControlMotionGhostSlow)
                return double.IsNaN(ghostClipS) ? 0.0 : ghostClipS * (PartParityMotionRules.SlowClipFactor - 1.0);
            if (controlId == TestCommandPartParitySweep.ControlMotionGhostLate) return PartParityMotionRules.LateShiftSeconds;
            return 0.0;
        }

        /// <summary>
        /// A surface's done signal (H.2, the airbrake rule): the deflection moved past the still
        /// threshold from where it started and changed no more than that over the last
        /// <see cref="PartParityMotionRules.SurfaceStillWindowSeconds"/>, the samples covering the
        /// window. Samples are (seconds since the press, deflection degrees), in time order.
        /// </summary>
        internal static bool SurfaceDone(IList<KeyValuePair<double, double>> samples, double startDeg)
        {
            if (samples == null || samples.Count < 2) return false;
            KeyValuePair<double, double> last = samples[samples.Count - 1];
            if (Math.Abs(last.Value - startDeg) <= PartParityMotionRules.SurfaceStillDeg) return false;
            if (last.Key - samples[0].Key < PartParityMotionRules.SurfaceStillWindowSeconds) return false;
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                if (last.Key - samples[i].Key > PartParityMotionRules.SurfaceStillWindowSeconds) break;
                if (Math.Abs(samples[i].Value - last.Value) > PartParityMotionRules.SurfaceStillDeg) return false;
            }
            return true;
        }

        /// <summary>The predicted motion time from what the first frames after the press read
        /// (H.2), and where it came from: a playing clip, a servo's or a surface's formula,
        /// else the part's longest clip (provisional), else the cap.</summary>
        internal static double ChoosePredicted(string kind, double playingClipS, double servoS, double surfaceS,
            double longestClipS, out string source)
        {
            if (kind == PartParityMotionRules.KindServo && IsTime(servoS)) { source = "servo"; return PartParityMotionRules.ClampPredictedSeconds(servoS); }
            if (kind == PartParityMotionRules.KindSurface && IsTime(surfaceS)) { source = "surface"; return PartParityMotionRules.ClampPredictedSeconds(surfaceS); }
            if (IsTime(playingClipS)) { source = "playing-clip"; return PartParityMotionRules.ClampPredictedSeconds(playingClipS); }
            if (IsTime(longestClipS)) { source = "longest-clip"; return PartParityMotionRules.ClampPredictedSeconds(longestClipS); }
            source = "cap";
            return PartParityMotionRules.MaxPredictedSeconds;
        }

        private static bool IsTime(double s) => !double.IsNaN(s) && !double.IsInfinity(s) && s > 0.0;

        /// <summary>Whether a P6-driven motion case's real motion is done: P6's reach for every
        /// family, and for a surface (the airbrake, whose deploy flag flips at the press while
        /// its surface still turns) also P6's settle (the surface still for 0.5 s, D.3).</summary>
        internal static bool P6MotionDone(string kind, bool reached, bool settled)
            => reached && (kind != PartParityMotionRules.KindSurface || settled);

        /// <summary>The servo's predicted run, or NaN.</summary>
        internal static double ServoPredicted(double rest, double target, double traverseVelocity)
            => PartParityMotionRules.ServoSeconds(rest, target, traverseVelocity);

        internal static string Fmt(double v) => double.IsNaN(v) ? "-" : v.ToString("0.###", Ic);

        /// <summary>Why a real-vs-real control's copy B stopped counting mid-motion: its vessel
        /// or part is gone with no fault recorded (null while it is healthy or already faulted).
        /// The control then reads as a setup fault instead of a mismatch over empty tiles.</summary>
        internal static string TwinLossDetail(bool twinFaulted, bool twinHealthy)
            => twinFaulted || twinHealthy ? null : "copy B vessel lost mid-motion";

        /// <summary>
        /// The window rule (H.3's error row, partparity.py's completeness): a captured case needs
        /// at least <see cref="PartParityMotionRules.MinFrames"/> frames and some real frame at
        /// <see cref="PartParityMotionRules.RealReachedProgress"/> or more. Returns why it failed,
        /// or null; the driver turns a failure into the case's <c>state-timeout</c> setup fault,
        /// so the sidecar reads as an error, never as an incomplete capture.
        /// </summary>
        internal static string WindowRuleProblem(IList<double> realProgress)
        {
            int n = realProgress?.Count ?? 0;
            if (n < PartParityMotionRules.MinFrames)
                return "window rule: " + n.ToString(Ic) + " frame(s), fewer than " + PartParityMotionRules.MinFrames.ToString(Ic);
            double best = double.NaN;
            foreach (double p in realProgress)
                if (!double.IsNaN(p) && (double.IsNaN(best) || p > best)) best = p;
            if (double.IsNaN(best) || best < PartParityMotionRules.RealReachedProgress)
                return "window rule: no real frame reached " + PartParityMotionRules.RealReachedProgress.ToString("0.##", Ic)
                       + " (best " + Fmt(best) + ")";
            return null;
        }
    }

    /// <summary>What a motion control's judge reads back from its own sidecar (H.3), the FX
    /// control precedent (<see cref="PartParityFxControls"/>): one flag carries both routes'
    /// timing so a later step judges a control it did not fly.</summary>
    internal sealed class PartParityMotionControlEvidence
    {
        internal PartParityMotionTiming Normal;
        internal PartParityMotionTiming Altered;
        internal double FrameS = double.NaN;
        internal double PredictedS = double.NaN;
        internal bool EndsWithinAllowance;
        internal bool LastFramePasses;
        internal string SetupStatus;
    }

    /// <summary>The motion controls' evidence flag and judge dispatch (H.3). Pure.</summary>
    internal static class PartParityMotionControls
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal const string EvidenceFlagPrefix = "motion-evidence:";

        private static readonly string[] TimingKeys = { "lagS", "onsetLagS", "durationRatio", "curveShapeDev", "t02", "t98" };

        private static double[] Values(PartParityMotionTiming t)
            => t == null ? new[] { double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN }
                : new[]
                {
                    t.LagS, t.OnsetLagS, t.DurationRatio, t.CurveShapeDev,
                    t.RealCrossings != null ? t.RealCrossings[0] : double.NaN,
                    t.RealCrossings != null ? t.RealCrossings[4] : double.NaN,
                };

        private static string N(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "nan" : v.ToString("R", Ic);

        private static double P(string s)
            => s != null && double.TryParse(s, NumberStyles.Float, Ic, out double v) ? v : double.NaN;

        /// <summary><c>motion-evidence:n.lagS=..;...;a.lagS=..;...;frameS=..;predictedS=..;ends=0|1;last=0|1</c>.</summary>
        internal static string FormatEvidence(PartParityMotionControlEvidence e)
        {
            if (e == null) return null;
            var sb = new StringBuilder(EvidenceFlagPrefix);
            double[] n = Values(e.Normal), a = Values(e.Altered);
            for (int i = 0; i < TimingKeys.Length; i++) sb.Append("n.").Append(TimingKeys[i]).Append('=').Append(N(n[i])).Append(';');
            for (int i = 0; i < TimingKeys.Length; i++) sb.Append("a.").Append(TimingKeys[i]).Append('=').Append(N(a[i])).Append(';');
            sb.Append("frameS=").Append(N(e.FrameS)).Append(";predictedS=").Append(N(e.PredictedS))
                .Append(";ends=").Append(e.EndsWithinAllowance ? "1" : "0").Append(";last=").Append(e.LastFramePasses ? "1" : "0");
            return sb.ToString();
        }

        /// <summary>The evidence a sidecar's flags carry (its setup status stamped in), or null.</summary>
        internal static PartParityMotionControlEvidence ParseEvidence(IList<string> flags, string setupStatus)
        {
            if (flags == null) return null;
            foreach (string f in flags)
            {
                if (f == null || !f.StartsWith(EvidenceFlagPrefix, StringComparison.Ordinal)) continue;
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string pair in f.Substring(EvidenceFlagPrefix.Length).Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0) values[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                }
                var e = new PartParityMotionControlEvidence
                {
                    Normal = TimingOf(values, "n."), Altered = TimingOf(values, "a."),
                    FrameS = P(Get(values, "frameS")), PredictedS = P(Get(values, "predictedS")),
                    EndsWithinAllowance = Get(values, "ends") == "1", LastFramePasses = Get(values, "last") == "1",
                    SetupStatus = setupStatus ?? TestCommandPartParitySweep.SetupOk,
                };
                return e;
            }
            return null;
        }

        private static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out string v) ? v : null;

        private static PartParityMotionTiming TimingOf(Dictionary<string, string> d, string prefix)
        {
            var t = new PartParityMotionTiming
            {
                LagS = P(Get(d, prefix + "lagS")), OnsetLagS = P(Get(d, prefix + "onsetLagS")),
                DurationRatio = P(Get(d, prefix + "durationRatio")), CurveShapeDev = P(Get(d, prefix + "curveShapeDev")),
            };
            t.RealCrossings = new[] { P(Get(d, prefix + "t02")), double.NaN, double.NaN, double.NaN, P(Get(d, prefix + "t98")) };
            return t;
        }

        /// <summary>The judge of one motion control over its own sidecar's flags (H.3): the
        /// real-vs-real controls read the altered route (copy B against copy A), the slow and
        /// late controls compare it with the normal route. No evidence is <c>bad</c>.</summary>
        internal static string Judge(string controlId, IList<string> flags, string setupStatus, out string detail)
        {
            PartParityMotionControlEvidence e = ParseEvidence(flags, setupStatus);
            if (e == null)
            {
                if (TestCommandPartParitySweep.IsSetupFault(setupStatus))
                {
                    detail = "setup " + (setupStatus ?? "<none>");
                    return TestCommandPartParitySweep.ControlsBad;
                }
                detail = "no motion evidence";
                return TestCommandPartParitySweep.ControlsBad;
            }
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlMotionRealVsReal:
                    return PartParityMotionRules.JudgeRealVsReal(e.Altered, e.FrameS, e.PredictedS, e.EndsWithinAllowance,
                        e.SetupStatus, out detail);
                case TestCommandPartParitySweep.ControlMotionRealVsRealAir:
                    // The canopy-scale curves only (H.3): stock aims each open canopy along its own
                    // airflow every tick, so two chutes falling side by side never draw the same
                    // last frame (the chute pictures are report-only, D.4).
                    return PartParityMotionRules.JudgeRealVsReal(e.Altered, e.FrameS, e.PredictedS, true,
                        e.SetupStatus, out detail);
                case TestCommandPartParitySweep.ControlMotionGhostSlow:
                    return PartParityMotionRules.JudgeGhostSlow(e.Normal, e.Altered, e.FrameS, e.LastFramePasses,
                        e.SetupStatus, out detail);
                case TestCommandPartParitySweep.ControlMotionGhostLate:
                    return PartParityMotionRules.JudgeGhostLate(e.Normal, e.Altered, e.SetupStatus, out detail);
                default:
                    detail = "not a motion control";
                    return TestCommandPartParitySweep.ControlsBad;
            }
        }

        /// <summary>
        /// Frame 0 and the last frame of a real-vs-real control within the real-vs-real
        /// allowance of 4.5 (the <c>ctl-real-vs-real</c> predicate, D.2), as partparity.py
        /// <c>motion_end_frames_within_allowance</c> reads it: in both frames every view has a
        /// same-time block with no real-only or ghost-only pixel and at most
        /// <see cref="PartParitySweepPlan.RealVsRealColorNoisePx"/> colour pixels over the line,
        /// and some view has real coverage.
        /// </summary>
        internal static bool EndFramesWithinAllowance(IList<PartParityMotionFrame> frames)
        {
            if (frames == null || frames.Count == 0) return false;
            foreach (PartParityMotionFrame f in new[] { frames[0], frames[frames.Count - 1] })
            {
                if (f?.Views == null || f.Views.Count == 0) return false;
                bool covered = false;
                foreach (PartParityMotionFrameView v in f.Views)
                {
                    PartParityViewMeasures mm = v?.Same?.Measures;
                    if (mm == null || mm.RealOnlyPx != 0 || mm.GhostOnlyPx != 0
                        || mm.ColorOverPx > PartParitySweepPlan.RealVsRealColorNoisePx)
                        return false;
                    if (mm.RealPx > 0) covered = true;
                }
                if (!covered) return false;
            }
            return true;
        }

        /// <summary>
        /// The slow route's last frame passes the P6 end-state rules, as partparity.py
        /// <c>judge_route</c> over the last frame's same-time blocks and the case's
        /// <c>structure</c> reads it: no view over the shape or colour fail lines (a
        /// <c>context-dependent</c> part caps those at warn), and no rendered, visual renderer
        /// missing on or extra on the ghost. False with no frame.
        /// </summary>
        internal static bool LastFramePasses(IList<PartParityMotionFrame> frames, PartParityStructureDiff structure,
            IList<string> tags)
        {
            if (frames == null || frames.Count == 0) return false;
            PartParityMotionFrame last = frames[frames.Count - 1];
            bool viewFails = false;
            if (last?.Views != null)
                foreach (PartParityMotionFrameView v in last.Views)
                {
                    PartParityViewMeasures mm = v?.Same?.Measures;
                    if (mm != null && (PartParitySweepPlan.ShapeFails(mm) || PartParitySweepPlan.ColorFails(mm))) viewFails = true;
                }
            if (viewFails && tags != null && tags.Contains(PartParityCaseRules.TagContextDependent)) viewFails = false;
            if (structure != null && (HasVisualRendered(structure.MissingOnGhost) || HasVisualRendered(structure.ExtraOnGhost)))
                return false;
            return !viewFails;
        }

        private static bool HasVisualRendered(IList<PartParityRendererRef> refs)
        {
            if (refs == null) return false;
            foreach (PartParityRendererRef r in refs)
                if (r != null && r.Rendered && string.IsNullOrEmpty(r.NonVisual)) return true;
            return false;
        }
    }

    /// <summary>Everything one motion case owns beyond a state case (H.2), from the stowed
    /// framing to the ghost rows. Null on every other case.</summary>
    internal sealed class PartParityMotionRun
    {
        internal PartParityMotionOption Option;
        internal string ControlId;

        /// <summary>The real-vs-real control's copy B (its live frames are the ghost rows).</summary>
        internal WaveCase Twin;
        internal bool IsTwin;

        /// <summary>The slow and late controls' normal-route timing, kept for the evidence
        /// flag when the record is replaced by the altered route.</summary>
        internal PartParityMotionTiming NormalTiming;

        /// <summary>The P6 option drives the press and the done signal (D.3); false for a servo
        /// and a control surface, whose drive and done signal are this file's.</summary>
        internal bool P6Driven;

        // ----- the module a servo or a surface drives -----
        internal PartModule Module;
        internal bool Piston;
        internal double Rest = double.NaN, Target = double.NaN, TraverseVelocity = double.NaN;
        internal double SurfaceStartDeg;
        internal readonly List<KeyValuePair<double, double>> SurfaceSamples = new List<KeyValuePair<double, double>>();

        // ----- robotic poll containers (the recorder's own, per case) -----
        internal List<(Part part, PartModule module, int moduleIndex, string moduleName)> RoboticModules;
        internal readonly HashSet<ulong> ActiveRoboticKeys = new HashSet<ulong>();
        internal readonly Dictionary<ulong, float> LastRoboticPosition = new Dictionary<ulong, float>();
        internal readonly Dictionary<ulong, double> LastRoboticSampleUT = new Dictionary<ulong, double>();
        internal readonly Dictionary<ulong, FlightRecorder.RoboticMotionWindow> RoboticWindows = new Dictionary<ulong, FlightRecorder.RoboticMotionWindow>();
        internal readonly HashSet<ulong> LoggedRoboticKeys = new HashSet<ulong>();

        // ----- the press and the window -----
        internal double PressUT = double.NaN;
        internal float PressRealtime;
        internal double PredictedS = double.NaN;
        internal string PredictedSource;
        internal bool PredictedFinal;
        internal string ClockAnimation;
        internal string ClockState;
        internal double DoneS = double.NaN;
        internal double TimeoutS = double.NaN;
        internal double GhostClipS = double.NaN;
        internal double GhostExpectedRunS;
        internal double ControlExtraS;
        internal bool Capped;
        internal bool Finished;

        // ----- the progress transforms (paired by path) -----
        internal readonly List<string> TrackPaths = new List<string>();
        internal readonly List<Transform> RealTransforms = new List<Transform>();
        internal bool PartSpace;
        internal bool ScaleOnly;
        /// <summary>A rigged canopy (ReStock): its progress transforms are the animated bones
        /// under the canopy, read in full pose, not the canopy root's scale (H.1).</summary>
        internal bool CanopyRigged;
        /// <summary>The real canopy root, whose mean scale is a canopy's <c>realClock</c>.</summary>
        internal Transform CanopyClock;
        internal readonly List<PartParityMotionTrack> RealTracks = new List<PartParityMotionTrack>();

        // ----- the real frames -----
        internal readonly List<double> TargetsS = new List<double>();
        internal readonly List<double> RealS = new List<double>();
        internal readonly List<double> Uts = new List<double>();
        internal readonly List<double> RealClock = new List<double>();
        internal readonly List<string> StateValues = new List<string>();
        internal readonly List<List<string>> FrameFlags = new List<List<string>>();

        /// <summary>The real tiles, per view (in the case's view order), per frame.</summary>
        internal readonly List<List<byte[]>> RealTiles = new List<List<byte[]>>();
        internal readonly List<double> BoxAreaPx = new List<double>();
        internal string FieldName;

        /// <summary>The case's own route: the ghost the sidecar under the case id shows (the
        /// altered route for the slow and late controls). Null until the rows are built.</summary>
        internal PartParityMotionRoute Main;

        /// <summary>The slow and late controls' normal route, written as its own sidecar and
        /// motion atlas under <see cref="PartParityMotionRules.NormalRouteCaseId"/> (H.3).</summary>
        internal PartParityMotionRoute Normal;

        /// <summary>True while the normal route's sidecar is built (it reads <see cref="Normal"/>).</summary>
        internal bool WritingNormal;
        internal string Evidence;

        internal PartParityMotionRecord Record => Main?.Record;
    }

    /// <summary>One ghost route of a motion case over the case's real frames: its record,
    /// its case flags, the last frame's same-time views and its motion atlas.</summary>
    internal sealed class PartParityMotionRoute
    {
        internal PartParityMotionRecord Record;
        internal readonly List<string> CaseFlags = new List<string>();
        internal readonly List<PartParityViewRecord> LastViews = new List<PartParityViewRecord>();
        internal readonly List<string> LastViewNames = new List<string>();
        internal List<string> AtlasRows;
        internal readonly List<int> AtlasCols = new List<int>();
        internal byte[] Atlas;
        internal int AtlasWidth, AtlasHeight;
        internal int Disagree, NoMatch;
    }

    /// <summary>
    /// The live motion driver of the part parity sweep (design-part-ghost-parity.md Appendix
    /// H.2, H.3, piece M3): one wave pressed in one frame, the real side photographed live at
    /// the frame schedule, then per case a scout ghost primed densely, the render ghost primed
    /// at the same-time and matched UTs, the measures and the motion atlas. It reuses P6's
    /// authoring, stowed inputs, press, step, poll and observe (<see cref="PartParityStateDriver"/>)
    /// and every rule it reads is pure (<see cref="PartParityMotionRules"/>,
    /// <see cref="PartParityMotionPlan"/>, <see cref="PartParityMotionMetrics"/>). Ghosts are
    /// rig objects under the real part: nothing here writes game state beyond the case
    /// vessels it spawned, and every ghost it builds is destroyed in the method that built it.
    /// </summary>
    internal static class PartParityMotionDriver
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static string Tag => PartParitySweepRunner.Tag;

        // =====================================================================================
        // building the cases (the runner's queue)
        // =====================================================================================

        /// <summary>Attaches the motion run to a planned case (and its P6 state run for the
        /// press, poll and stateProof).</summary>
        internal static void Attach(WaveCase wc, PartParityCaseKey key, PartParityPartEntry entry, string controlId)
        {
            PartParityMotionOption o = key.MotionOption;
            var m = new PartParityMotionRun { Option = o, ControlId = controlId };
            wc.Motion = m;
            PartParityStateOption s = o.StateOption;
            m.P6Driven = s != null;
            if (s == null)
            {
                s = new PartParityStateOption
                {
                    Family = o.Family, Module = o.Module, ModuleIndex = o.ModuleIndex, State = o.State,
                    Action = o.Action, ActionIsKspAction = o.ActionIsKspAction,
                };
                s.ModuleIndices.Add(o.ModuleIndex);
            }
            var stateKey = new PartParityCaseKey
            {
                Part = key.Part, Source = key.Source, Skin = key.Skin, SkinOrdinal = key.SkinOrdinal,
                State = key.State, Context = o.Context, StateOption = s,
            };
            wc.State = PartParityStateDriver.NewRun(stateKey, entry, dropEvents: false);
            wc.State.FourRows = false;
            wc.Views = new List<string>(wc.Views ?? new List<string>(PartParityMotionRules.DefaultViews));
        }

        /// <summary>A motion control's case (H.3): the subject's own motion case, plus copy B
        /// for the real-vs-real controls. Null when the step is not a motion run or the id is
        /// not a motion control.</summary>
        internal static WaveCase BuildControlCase(StepContext ctx, string id, WaveCase wc)
        {
            string part, state;
            switch (id)
            {
                case TestCommandPartParitySweep.ControlMotionRealVsReal:
                case TestCommandPartParitySweep.ControlMotionGhostLate:
                    part = PartParityMotionRules.ControlPart; state = PartParityMotionRules.StateDeployed; break;
                case TestCommandPartParitySweep.ControlMotionGhostSlow:
                    part = PartParityMotionRules.ControlSlowPart; state = PartParityMotionRules.StateDeployed; break;
                case TestCommandPartParitySweep.ControlMotionRealVsRealAir:
                    part = PartParityMotionRules.ControlAirPart; state = PartParityMotionRules.StateSemi; break;
                default: return null;
            }
            wc.Key = new PartParityCaseKey { Part = part };
            wc.Views = new List<string>(ctx.Args.Views);
            if (!ctx.EntriesByName.TryGetValue(part, out PartParityPartEntry e))
            {
                wc.Detail = "control subject not loaded: " + part;
                return wc;
            }
            wc.Entry = e;
            wc.RealVariant = e.BaseVariant;
            PartParityStatePart sp = PartParityStateDriver.BuildStatePart(e.Info);
            foreach (PartParityCaseKey k in PartParityMotionRules.Enumerate(sp, PartParityCaseRules.DefaultCaseKey(e)).Cases)
            {
                if (k.MotionOption == null || k.MotionOption.State != state || k.MotionOption.ReadingOnly) continue;
                wc.Key = k;
                Attach(wc, k, e, id);
                break;
            }
            if (wc.Motion == null)
            {
                wc.Detail = "control subject has no " + state + " motion case: " + part;
                return wc;
            }
            if (id == TestCommandPartParitySweep.ControlMotionRealVsReal || id == TestCommandPartParitySweep.ControlMotionRealVsRealAir)
            {
                var b = new WaveCase
                {
                    CaseId = id + PartParityMotionPlan.TwinSuffix, Index = -1, Key = wc.Key, Entry = e,
                    RealVariant = e.BaseVariant, Views = wc.Views,
                };
                Attach(b, wc.Key, e, null);
                b.Motion.IsTwin = true;
                wc.Motion.Twin = b;
            }
            ParsekLog.Verbose(Tag, $"control {id}: motion subject {part} state={state} twin={(wc.Motion.Twin != null ? "true" : "false")}");
            return wc;
        }

        /// <summary>A wave of motion cases (a motion-only control whose subject has no motion
        /// case counts, so its fault is written by the motion flow).</summary>
        internal static bool IsMotionWave(List<WaveCase> wave)
        {
            if (wave == null) return false;
            foreach (var wc in wave)
                if (wc.Motion != null || (wc.ControlId != null && PartParitySweepPlan.IsMotionOnlyControl(wc.ControlId))) return true;
            return false;
        }

        /// <summary>Every vessel a wave spawns: its cases, each followed by its copy B.</summary>
        internal static List<WaveCase> SpawnSet(List<WaveCase> wave)
        {
            bool any = false;
            foreach (var wc in wave) if (wc.Motion?.Twin != null) { any = true; break; }
            if (!any) return wave;
            var all = new List<WaveCase>();
            foreach (var wc in wave)
            {
                all.Add(wc);
                if (wc.Motion?.Twin != null) all.Add(wc.Motion.Twin);
            }
            return all;
        }

        // =====================================================================================
        // the orbit wave
        // =====================================================================================

        /// <summary>One orbit motion wave, spawn to teardown (H.2): every case and copy B
        /// spawned every other slot, the wait and the settle, the motion phase, the teardown.</summary>
        internal static IEnumerator RunWave(StepContext ctx, List<WaveCase> wave)
        {
            ctx.Live = wave;
            bool solo = wave.Count == 1 && wave[0].Solo;
            List<WaveCase> spawned = SpawnSet(wave);
            List<double> offsets = PartParityStateWave.PlanStateWave(spawned.Count);
            for (int i = 0; i < spawned.Count && i < offsets.Count; i++) spawned[i].AlongTrackM = offsets[i];
            PartParitySweepRunner.WriteInflight(ctx, wave.ConvertAll(c => c.CaseId), solo);
            foreach (var wc in spawned) PartParitySweepRunner.Guard(ctx, wc, "spawn", () => Spawn(ctx, wc));
            IEnumerator ready = PartParitySweepRunner.WaitReady(ctx, spawned);
            try { while (ready.MoveNext()) yield return ready.Current; }
            finally { PartParitySweepRunner.DisposeNested(ready); }
            if (ctx.HostDamage == null)
            {
                IEnumerator settle = PartParitySweepRunner.WaitSettled(ctx, spawned);
                try { while (settle.MoveNext()) yield return settle.Current; }
                finally { PartParitySweepRunner.DisposeNested(settle); }
            }
            if (ctx.HostDamage == null)
            {
                IEnumerator phase = RunPhase(ctx, wave, false);
                try { while (phase.MoveNext()) yield return phase.Current; }
                finally { PartParitySweepRunner.DisposeNested(phase); }
            }
            else
            {
                PartParitySweepRunner.DeleteInflight(ctx);
                ParsekLog.Info(Tag, $"motion wave stopped by the host damage before the press: {wave.Count.ToString(Ic)} case(s) left unrun");
            }
            IEnumerator down = PartParitySweepRunner.TearDownWave(ctx, spawned);
            try { while (down.MoveNext()) yield return down.Current; }
            finally { PartParitySweepRunner.DisposeNested(down); }
            ctx.Live = null;
        }

        /// <summary>Authors and spawns one case's vessel co-orbital: P6's state authoring, a
        /// servo's electric charge, a control surface on its hidden radial filler.</summary>
        private static void Spawn(StepContext ctx, WaveCase wc)
        {
            if (wc.Entry == null || wc.Entry.Info == null)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = wc.Detail ?? "part not loaded";
                return;
            }
            if (wc.Motion == null)
            {
                wc.Status = TestCommandPartParitySweep.SetupCrashed;
                wc.Detail = wc.Detail ?? "no motion case";
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: not spawned: {wc.Detail}");
                return;
            }
            string fail;
            ConfigNode node = wc.Motion.Option.Context == PartParityMotionRules.ContextRadial
                ? AuthorRadialVesselNode(wc, ctx.Host, out fail)
                : PartParityStateDriver.AuthorVesselNode(wc, ctx.Host, out fail);
            if (node == null)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = wc.Detail ?? fail ?? "motion vessel node not authored";
                return;
            }
            if (wc.Motion.Option.Kind == PartParityMotionRules.KindServo)
            {
                ConfigNode[] parts = node.GetNodes("PART");
                if (parts.Length > 0) PartParityStateDriver.AddCharge(parts[0]);
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: servo authored with {PartParityStateDriver.LightChargeAmount.ToString("0", Ic)} ElectricCharge");
            }
            wc.Pid = GhostCoOrbitalRig.SpawnCoOrbital(node, ctx.Host, wc.AlongTrackM, wc.CaseId, Tag);
            wc.SpawnRealtime = Time.realtimeSinceStartup;
            if (wc.Pid == 0)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = "SpawnAtPosition returned pid=0";
            }
        }

        /// <summary>
        /// The radial context (H.2): the subject as the root, a
        /// <see cref="PartParityMotionRules.RadialFillerPart"/> surface-attached to it, its
        /// centre one filler radius out along the subject's surface attach node, so the
        /// vessel's centre of mass and stock's deflection sign are fixed. The filler is hidden
        /// on every side like a stacked filler.
        /// </summary>
        private static ConfigNode AuthorRadialVesselNode(WaveCase wc, Vessel anchor, out string failReason)
        {
            failReason = null;
            AvailablePart info = wc.Entry.Info;
            AvailablePart filler = PartLoader.getPartInfoByName(PartParityMotionRules.RadialFillerPart);
            if (filler == null) { failReason = "radial filler " + PartParityMotionRules.RadialFillerPart + " not loaded"; return null; }
            AttachNode srf = info.partPrefab.srfAttachNode;
            if (srf == null) { failReason = "subject has no surface attach node"; return null; }
            AttachNode fsrf = filler.partPrefab.srfAttachNode;
            double radius = fsrf != null && new Vector2(fsrf.position.x, fsrf.position.z).magnitude > 0.01f
                ? new Vector2(fsrf.position.x, fsrf.position.z).magnitude : 0.625;
            Vector3 dir = srf.orientation.sqrMagnitude > 0.0001f ? srf.orientation.normalized : Vector3.right;
            Vector3 fillerPos = srf.position + dir * (float)radius;
            List<uint> ids = GhostCoOrbitalRig.UniqueFlightIds(2);
            ConfigNode subject = GhostCoOrbitalRig.AuthorPartNode(info, ids[0], 0, Vector3.zero, null, wc.RealVariant, null);
            ConfigNode fn = GhostCoOrbitalRig.AuthorPartNode(filler, ids[1], 0, fillerPos, null, null, null);
            fn.SetValue("attm", "1", true);
            fn.SetValue("srfN", "srfAttach, 0", true);
            wc.State.Nodes.Add(new PartParityContextNode { Node = "srfAttach", Part = filler.name });
            wc.State.FillerFlightIds.Add(ids[1]);
            Orbit src = anchor.orbit;
            var placeholder = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: authored radial context filler={filler.name} " +
                $"offset=({fillerPos.x.ToString("0.###", Ic)},{fillerPos.y.ToString("0.###", Ic)},{fillerPos.z.ToString("0.###", Ic)}) " +
                $"radius={radius.ToString("0.###", Ic)}");
            return ProtoVessel.CreateVesselNode(PartParityCaseRules.VesselName(wc.CaseId), VesselType.Probe,
                placeholder, 0, new[] { subject, fn });
        }

        // =====================================================================================
        // the motion phase (H.2), orbit and air
        // =====================================================================================

        private static bool Healthy(WaveCase wc)
            => wc != null && wc.Motion != null && wc.State != null && wc.Status == null && wc.Real != null && wc.Real.rootPart != null;

        /// <summary>
        /// The settled wave from the stowed inputs to every sidecar: each case's preparation and
        /// framing ghost, frame 0, every case pressed in one frame, the live frames, and each
        /// case's ghost rows and sidecar the frame its window ends. An air run also ends a
        /// case that fell under the floor or ran past the wave cap.
        /// </summary>
        internal static IEnumerator RunPhase(StepContext ctx, List<WaveCase> wave, bool air)
        {
            bool solo = wave.Count == 1 && wave[0].Solo;
            var live = new List<WaveCase>();
            foreach (var wc in wave)
            {
                if (!Healthy(wc)) continue;
                PartParitySweepRunner.Guard(ctx, wc, "prepare", () => Prepare(ctx, wc, air));
                WaveCase b = wc.Motion.Twin;
                if (b != null)
                {
                    if (Healthy(b)) PartParitySweepRunner.Guard(ctx, b, "prepare", () => PrepareTwin(ctx, wc, b, air));
                    if (b.Status != null && wc.Status == null)
                    {
                        wc.Status = b.Status;
                        wc.Detail = wc.Detail ?? "copy B: " + (b.Detail ?? b.Status);
                    }
                }
                if (Healthy(wc)) live.Add(wc);
            }

            // Frame 0 at the press frame, before stock moves anything; then every case pressed
            // in that same frame.
            yield return new WaitForEndOfFrame();
            foreach (var wc in live)
            {
                PartParitySweepRunner.Guard(ctx, wc, "state-motion-frame", () => CaptureFrame(ctx, wc, 0.0));
                if (wc.Motion.Twin != null && Healthy(wc.Motion.Twin))
                    PartParitySweepRunner.Guard(ctx, wc.Motion.Twin, "state-motion-frame", () => CaptureFrame(ctx, wc.Motion.Twin, 0.0));
            }
            foreach (var wc in live)
            {
                PartParitySweepRunner.Guard(ctx, wc, "state-press", () => Press(wc));
                if (wc.Motion.Twin != null && Healthy(wc.Motion.Twin))
                    PartParitySweepRunner.Guard(ctx, wc.Motion.Twin, "state-press", () => Press(wc.Motion.Twin));
            }
            float waveStart = Time.realtimeSinceStartup;
            int finished = 0;
            var written = new HashSet<WaveCase>();
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (PartParitySweepRunner.CheckHost(ctx, "motion wave")) break;
                bool waveCapped = air && Time.realtimeSinceStartup - waveStart > PartParityAirPlan.WaveCapSeconds;
                bool pending = false;
                foreach (var wc in live)
                {
                    if (wc.Motion.Finished) continue;
                    if (ctx.HostDamage != null) break;
                    PartParitySweepRunner.Guard(ctx, wc, "state-motion", () => Advance(ctx, wc, air, waveCapped));
                    if (wc.Status != null || wc.Motion.Finished)
                    {
                        wc.Motion.Finished = true;
                        // inflight.txt still names this case and every other live one while its
                        // ghost rows and sidecar are built (design 5.3); once it is written the list
                        // is rewritten with the live cases still unwritten, deleted when none remain.
                        if (wc.Status == null)
                            PartParitySweepRunner.Guard(ctx, wc, "motion-ghost", () => GhostRows(ctx, wc));
                        PartParitySweepRunner.Guard(ctx, wc, "write", () => PartParitySweepRunner.FinishCase(ctx, wc));
                        written.Add(wc);
                        WriteLiveInflight(ctx, LiveUnwritten(live, written), solo);
                        finished++;
                        ReleaseTiles(wc);
                        continue;
                    }
                    pending = true;
                }
                if (!pending || ctx.HostDamage != null) break;
            }
            // A case that never ran past prepare still writes its sidecar (its setup fault).
            foreach (var wc in wave)
            {
                if (ctx.HostDamage != null || live.Contains(wc)) continue;
                if (wc.Status == null)
                {
                    wc.Status = TestCommandPartParitySweep.SetupCrashed;
                    wc.Detail = wc.Detail ?? "motion case not prepared";
                }
                PartParitySweepRunner.Guard(ctx, wc, "write", () => PartParitySweepRunner.FinishCase(ctx, wc));
                finished++;
            }
            if (ctx.HostDamage != null || LiveUnwritten(live, written).Count == 0) PartParitySweepRunner.DeleteInflight(ctx);
            int faults = 0;
            foreach (var wc in wave) if (wc.Status != null) faults++;
            ParsekLog.Info(Tag, $"motion wave: cases={wave.Count.ToString(Ic)} pressed={live.Count.ToString(Ic)} " +
                $"finished={finished.ToString(Ic)} faults={faults.ToString(Ic)} air={(air ? "true" : "false")}");
        }

        /// <summary>The live cases whose sidecar is not written yet, in wave order: what
        /// inflight.txt names while a motion wave runs (design 5.3), so a crash in any of them is
        /// attributed to it.</summary>
        internal static List<string> LiveUnwritten(IList<WaveCase> live, ICollection<WaveCase> written)
        {
            var left = new List<string>();
            if (live == null) return left;
            foreach (var wc in live)
                if (wc != null && (written == null || !written.Contains(wc))) left.Add(wc.CaseId);
            return left;
        }

        private static void WriteLiveInflight(StepContext ctx, List<string> left, bool solo)
        {
            if (left.Count == 0) PartParitySweepRunner.DeleteInflight(ctx);
            else PartParitySweepRunner.WriteInflight(ctx, left, solo);
            ParsekLog.Verbose(Tag, $"motion wave: inflight now names {left.Count.ToString(Ic)} live case(s)");
        }

        private static void DropAtlases(PartParityMotionRun m)
        {
            if (m.Main != null) m.Main.Atlas = null;
            if (m.Normal != null) m.Normal.Atlas = null;
        }

        private static void ReleaseTiles(WaveCase wc)
        {
            wc.Motion.RealTiles.Clear();
            DropAtlases(wc.Motion);
            if (wc.Motion.Twin != null) wc.Motion.Twin.Motion.RealTiles.Clear();
        }

        // ----- preparation -----

        /// <summary>
        /// The stowed inputs (P6), the real repairs and skin, the servo or surface module, the
        /// progress transforms, and the framing box from the real stowed records and a framing
        /// ghost read stowed and at the motion's target (destroyed here, before the press).
        /// </summary>
        private static void Prepare(StepContext ctx, WaveCase wc, bool air)
        {
            PartParityMotionRun m = wc.Motion;
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            Transform partT = part.transform;
            PartParityStateDriver.CaptureStowedInputs(wc);
            if (air) run.TimeoutSeconds = PartParityAirPlan.InitialTimeoutSeconds();
            PartParityCaseCapture.RepairDepthMasks(wc, part);
            PartParityCaseCapture.RepairColorChangerLeaks(wc, part, run.JettisonTransforms);
            wc.Skin = PartParitySkinRules.NewProof(wc.SkinOption, wc.RealVariant, wc.Entry?.B9Modules);
            wc.Skin.Real = PartParityCaseAuthoring.LiveSkinName(part, wc.Skin, out string realMember);
            wc.SkinRealMember = realMember;
            ReadModule(wc, m, part);

            PartParityStateDriver.ClassifyReal(wc, null, out List<Renderer> realShown, out List<Renderer> realHidden);
            run.ContextHidden = realHidden.Count;
            wc.RealRecords = PartParityCaseCapture.Records(partT, realShown, wc.RealLayers, null,
                GhostVisualBuilder.GetDepthMaskTransformNames(part.partInfo?.partConfig));

            // The framing ghost (H.2): the stowed route, posed stowed then at the target.
            uint pid = part.persistentId;
            if (!GhostCoOrbitalRig.TryBuildGhostFromSnapshot(run.StowedSnapshot?.CreateCopy(), PartParityStateDriver.NewRecId(),
                    "ParsekPartParity_" + wc.CaseId + "_framing", new List<PartEvent>(run.StowedSeeds), wc.Real.vesselName,
                    wc.Real.persistentId, run.SeedUT, out GhostCoOrbitalRig.LiveGhost framing, out string fail))
            {
                wc.Flags.Add(PartParityCaseCapture.FlagGhostNotBuilt + ":framing");
                wc.Status = TestCommandPartParitySweep.SetupCrashed;
                wc.Detail = wc.Detail ?? "framing ghost: " + fail;
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
                return;
            }
            try
            {
                PartParityStateDriver.Place(framing, partT);
                GhostCoOrbitalRig.ApplySeedPrefix(framing, run.SeedUT);
                var shown = new List<Renderer>();
                var hidden = new List<Renderer>();
                PartParityStateDriver.SplitGhost(framing, run.FillerPids, shown, hidden);
                wc.GhostRecords = PartParityCaseCapture.Records(partT, shown, wc.GhostLayers, null);
                m.GhostClipS = GhostClip(framing.State, pid, m.Option.Kind);
                ResolveTracks(wc, m, part, framing);
                PartParityCaseCapture.Frame(wc);
                PoseTarget(framing.State, pid, m);
                int grown = 0;
                foreach (PartParityRendererRecord r in PartParityCaseCapture.Records(partT, shown, wc.GhostLayers, null))
                    if (r != null && r.Rendered) { wc.Shared.Encapsulate(PartParityFramingRule.BoundsOf(r)); grown++; }
                wc.Shared = PartParityMotionPlan.Grow(wc.Shared, PartParityMotionRules.FramingGrowFraction);
                wc.OrthoSize = PartParityCaptureMath.OrthoSize(wc.Shared.Extents());
                int mask = wc.CullingMask;
                foreach (int l in wc.GhostLayers) mask |= 1 << l;
                wc.CullingMask = mask;
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: framing ghost targetRenderers={grown.ToString(Ic)} " +
                    $"ortho={wc.OrthoSize.ToString("0.###", Ic)} ghostClip={PartParityMotionPlan.Fmt(m.GhostClipS)}s");
            }
            finally
            {
                GhostCoOrbitalRig.DestroyLiveGhost(framing);
            }
            m.GhostExpectedRunS = PartParityMotionPlan.GhostExpectedRunSeconds(m.Option.Kind, m.GhostClipS);
            m.ControlExtraS = PartParityMotionPlan.ControlExtraSeconds(m.ControlId, m.GhostClipS);
            InitViews(wc, m);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: motion prepared family={m.Option.Family} kind={m.Option.Kind} " +
                $"module={m.Option.Module}[{m.Option.ModuleIndex.ToString(Ic)}] tracks={m.TrackPaths.Count.ToString(Ic)} " +
                $"partSpace={(m.PartSpace ? "true" : "false")} scaleOnly={(m.ScaleOnly ? "true" : "false")} " +
                $"fillers={run.FillerPids.Count.ToString(Ic)} ghostRun={PartParityMotionPlan.Fmt(m.GhostExpectedRunS)}s " +
                $"controlExtra={PartParityMotionPlan.Fmt(m.ControlExtraS)}s control={m.ControlId ?? "-"}");
        }

        /// <summary>Copy B of the real-vs-real control: its own stowed inputs and transforms
        /// at copy A's paths, copy A's box (the views are part-fixed, both copies one part).</summary>
        private static void PrepareTwin(StepContext ctx, WaveCase a, WaveCase b, bool air)
        {
            PartParityMotionRun m = b.Motion;
            Part part = b.Real.rootPart;
            PartParityStateDriver.CaptureStowedInputs(b);
            if (air) b.State.TimeoutSeconds = PartParityAirPlan.InitialTimeoutSeconds();
            PartParityCaseCapture.RepairDepthMasks(b, part);
            PartParityCaseCapture.RepairColorChangerLeaks(b, part, b.State.JettisonTransforms);
            ReadModule(b, m, part);
            m.PartSpace = a.Motion.PartSpace;
            m.ScaleOnly = a.Motion.ScaleOnly;
            m.CanopyRigged = a.Motion.CanopyRigged;
            if (m.Option.Kind == PartParityMotionRules.KindCanopy) m.CanopyClock = RealCanopyTransform(part);
            Dictionary<string, Transform> real = RealTransformsByPath(part);
            foreach (string path in a.Motion.TrackPaths)
            {
                real.TryGetValue(path, out Transform t);
                if (t == null) t = part.FindModelTransform(LeafName(path));
                m.TrackPaths.Add(path);
                m.RealTransforms.Add(t);
                m.RealTracks.Add(new PartParityMotionTrack { Path = path });
            }
            b.Shared = a.Shared;
            b.OrthoSize = a.OrthoSize;
            b.CullingMask = a.CullingMask;
            InitViews(b, m);
            ParsekLog.Verbose(Tag, $"case {b.CaseId}: copy B prepared tracks={m.TrackPaths.Count.ToString(Ic)}");
        }

        private static void InitViews(WaveCase wc, PartParityMotionRun m)
        {
            m.RealTiles.Clear();
            m.BoxAreaPx.Clear();
            foreach (string _ in wc.Views)
            {
                m.RealTiles.Add(new List<byte[]>());
                m.BoxAreaPx.Add(0.0);
            }
        }

        /// <summary>A servo's module, rest, limits, target and speed; a surface's module and
        /// start deflection.</summary>
        private static void ReadModule(WaveCase wc, PartParityMotionRun m, Part part)
        {
            int idx = m.Option.ModuleIndex;
            m.Module = idx >= 0 && idx < part.Modules.Count ? part.Modules[idx] : null;
            if (m.Option.Kind == PartParityMotionRules.KindServo && m.Module != null)
            {
                m.Piston = m.Option.Module == PartParityMotionRules.ModuleServoPiston;
                m.Rest = ReadFloat(m.Module, m.Piston ? "currentExtension" : "currentAngle");
                Vector2 limits = ReadVector2(m.Module, m.Piston ? "softMinMaxExtension" : "softMinMaxAngles");
                m.TraverseVelocity = ReadFloat(m.Module, "traverseVelocity");
                m.Target = PartParityMotionRules.ServoTarget(m.Piston, m.Rest, limits.x, limits.y);
                m.RoboticModules = FlightRecorder.CacheRoboticModules(wc.Real);
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: servo {m.Option.Module} rest={PartParityMotionPlan.Fmt(m.Rest)} " +
                    $"limits=[{limits.x.ToString("0.###", Ic)},{limits.y.ToString("0.###", Ic)}] target={PartParityMotionPlan.Fmt(m.Target)} " +
                    $"traverseVelocity={PartParityMotionPlan.Fmt(m.TraverseVelocity)} cached={m.RoboticModules.Count.ToString(Ic)}");
            }
            else if (m.Option.Kind == PartParityMotionRules.KindSurface && m.Module is ModuleControlSurface cs)
            {
                m.SurfaceStartDeg = SurfaceDeflectionDeg(cs, out string source);
                double angle = SurfaceDeployAngleDeg(cs);
                double clip = PartParityMotionRules.SurfaceSeconds(angle, cs.actuatorSpeed);
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: surface {m.Option.Module} start={PartParityMotionPlan.Fmt(m.SurfaceStartDeg)}deg ({source}) " +
                    $"deployAngle={PartParityMotionPlan.Fmt(angle)}deg actuatorSpeed={cs.actuatorSpeed.ToString("0.###", Ic)}deg/s " +
                    $"clip={PartParityMotionPlan.Fmt(clip)}s (no Animation: stock turns the surface transform itself)");
            }
        }

        private static FieldInfo SurfaceField(string name)
            => typeof(ModuleControlSurface).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

        /// <summary>The surface's actual deflection (stock's protected <c>deflection</c>, which
        /// moves toward the target at <c>actuatorSpeed</c> and is what turns the transform);
        /// <c>currentDeployAngle</c>, the deploy TARGET, when the field cannot be read.</summary>
        private static double SurfaceDeflectionDeg(ModuleControlSurface cs, out string source)
        {
            if (SurfaceField("deflection")?.GetValue(cs) is float d)
            {
                source = "deflection";
                return d;
            }
            source = "currentDeployAngle";
            return cs.currentDeployAngle;
        }

        /// <summary>The deploy angle the press drives to: an airbrake's <c>aeroDeployAngle</c>,
        /// any other surface's <c>deployAngle</c>.</summary>
        private static double SurfaceDeployAngleDeg(ModuleControlSurface cs)
            => cs is ModuleAeroSurface aero ? aero.aeroDeployAngle : cs.deployAngle;

        /// <summary>The transform the surface module turns: its own <c>ctrlSurface</c>, else
        /// its <c>transformName</c> model transform (stock's default <c>obj_ctrlSrf</c> when the
        /// config names none).</summary>
        private static Transform RealSurfaceTransform(PartModule module, Part part)
        {
            if (!(module is ModuleControlSurface cs)) return null;
            if (SurfaceField("ctrlSurface")?.GetValue(cs) is Transform t && t != null) return t;
            return !string.IsNullOrEmpty(cs.transformName) ? part.FindModelTransform(cs.transformName) : null;
        }

        // ----- progress transforms -----

        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int i = path.LastIndexOf('/');
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        private static Dictionary<string, Transform> RealTransformsByPath(Part part)
        {
            var d = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform t in part.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.GetComponentInParent<Part>() != part) continue;
                string path = PartParityStructure.NormalizePath(PartParityCaseCapture.RelativePath(part.transform, t));
                if (!d.ContainsKey(path)) d[path] = t;
            }
            return d;
        }

        /// <summary>The ghost's progress transforms for the case's kind, in a stable order
        /// (deploy infos: the info's own list, the sun tracker's pivot left out; a servo's
        /// moving transform; a canopy).</summary>
        private static List<Transform> GhostTrackTransforms(GhostPlaybackState state, uint pid, PartParityMotionOption o)
        {
            var list = new List<Transform>();
            if (state == null) return list;
            switch (o.Kind)
            {
                case PartParityMotionRules.KindClip:
                case PartParityMotionRules.KindSurface:
                    if (state.deployableInfos != null && state.deployableInfos.TryGetValue(pid, out DeployableGhostInfo d) && d?.transforms != null)
                    {
                        Transform pivot = null;
                        if (state.synthesizedMotionInfos?.sunTrackers != null)
                            foreach (var s in state.synthesizedMotionInfos.sunTrackers)
                                if (s != null && s.partPersistentId == pid) pivot = s.pivotTransform;
                        foreach (DeployableTransformState ts in d.transforms)
                            if (ts.t != null && ts.t != pivot && !list.Contains(ts.t)) list.Add(ts.t);
                    }
                    break;
                case PartParityMotionRules.KindServo:
                    if (state.roboticInfos != null
                        && state.roboticInfos.TryGetValue(FlightRecorder.EncodeEngineKey(pid, o.ModuleIndex), out RoboticGhostInfo r)
                        && r?.servoTransform != null)
                        list.Add(r.servoTransform);
                    break;
                case PartParityMotionRules.KindCanopy:
                    if (state.parachuteInfos != null && state.parachuteInfos.TryGetValue(pid, out ParachuteGhostInfo pc) && pc != null)
                    {
                        // A rigged canopy's deploy clips move the bones under the canopy, never the
                        // root's scale (ReStock); a stock canopy has no bones and keeps its root.
                        if (pc.canopyBones != null)
                            foreach (CanopyBonePose b in pc.canopyBones)
                                if (b?.bone != null && !list.Contains(b.bone)) list.Add(b.bone);
                        if (list.Count == 0 && pc.canopyTransform != null) list.Add(pc.canopyTransform);
                    }
                    break;
            }
            return list;
        }

        /// <summary>Whether the ghost's canopy is rigged: its info carries animated bones.</summary>
        private static bool HasCanopyBones(GhostPlaybackState state, uint pid)
        {
            if (state?.parachuteInfos == null || !state.parachuteInfos.TryGetValue(pid, out ParachuteGhostInfo pc) || pc?.canopyBones == null)
                return false;
            foreach (CanopyBonePose b in pc.canopyBones)
                if (b?.bone != null) return true;
            return false;
        }

        /// <summary>The ghost transform at a part-relative path, or null.</summary>
        private static Transform FindGhostByPath(Transform ghostRoot, string path)
        {
            if (ghostRoot == null || string.IsNullOrEmpty(path)) return null;
            foreach (Transform t in ghostRoot.GetComponentsInChildren<Transform>(true))
                if (t != null && string.Equals(GhostPath(ghostRoot, t), path, StringComparison.Ordinal)) return t;
            return null;
        }

        private static string GhostPath(Transform ghostRoot, Transform t)
            => PartParityStructure.NormalizePath(PartParityCaseCapture.RelativePath(ghostRoot, t));

        /// <summary>The progress transforms paired by path between the framing ghost and the
        /// real part (a canopy and a servo by the module's own transform name); an unpaired
        /// ghost transform is left out and counted.</summary>
        private static void ResolveTracks(WaveCase wc, PartParityMotionRun m, Part part, GhostCoOrbitalRig.LiveGhost ghost)
        {
            uint pid = part.persistentId;
            bool canopy = m.Option.Kind == PartParityMotionRules.KindCanopy;
            m.PartSpace = m.Option.Kind == PartParityMotionRules.KindServo;
            m.CanopyRigged = canopy && HasCanopyBones(ghost.State, pid);
            m.ScaleOnly = canopy && !m.CanopyRigged;
            if (canopy) m.CanopyClock = RealCanopyTransform(part);
            Transform root = ghost.Build.root.transform;
            Dictionary<string, Transform> real = RealTransformsByPath(part);
            int unpaired = 0;
            List<Transform> ghostTracks = GhostTrackTransforms(ghost.State, pid, m.Option);
            foreach (Transform gt in ghostTracks)
            {
                string path = GhostPath(root, gt);
                Transform rt = null;
                if (m.Option.Kind == PartParityMotionRules.KindServo)
                    rt = RealServoTransform(m.Module, part);
                else if (canopy && !m.CanopyRigged)
                    rt = m.CanopyClock;
                else if (!real.TryGetValue(path, out rt))
                    rt = part.FindModelTransform(gt.name);
                if (rt == null) { unpaired++; continue; }
                m.TrackPaths.Add(path);
                m.RealTransforms.Add(rt);
                m.RealTracks.Add(new PartParityMotionTrack { Path = path });
            }
            string source = canopy ? (m.CanopyRigged ? "canopy-bones" : "canopy-root") : "ghost-info";
            // A surface the ghost built no deploy info for still moves on the real part: its
            // progress is the module's own transform, paired by path with whatever the ghost
            // holds there (nothing turns it, so the ghost reads motion-not-modelled, H.1).
            if (ghostTracks.Count == 0 && m.Option.Kind == PartParityMotionRules.KindSurface)
            {
                Transform rt = RealSurfaceTransform(m.Module, part);
                if (rt != null)
                {
                    string path = PartParityStructure.NormalizePath(PartParityCaseCapture.RelativePath(part.transform, rt));
                    m.TrackPaths.Add(path);
                    m.RealTransforms.Add(rt);
                    m.RealTracks.Add(new PartParityMotionTrack { Path = path });
                    source = "real-module";
                    ParsekLog.Info(Tag, $"case {wc.CaseId}: the ghost holds no deploy transform for {m.Option.Module}; " +
                        $"progress from the real module's surface transform {path} (ghost at that path: " +
                        $"{(FindGhostByPath(root, path) != null ? "present" : "absent")})");
                }
                else
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: no surface transform on the real {m.Option.Module} and none on the ghost");
            }
            if (unpaired > 0) wc.Flags.Add("motion-transform-unpaired:" + unpaired.ToString(Ic));
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: progress transforms paired={m.TrackPaths.Count.ToString(Ic)} " +
                $"unpaired={unpaired.ToString(Ic)} source={source} [{string.Join(",", m.TrackPaths.ToArray())}]");
        }

        private static Transform RealCanopyTransform(Part part)
        {
            ModuleParachute chute = part.FindModuleImplementing<ModuleParachute>();
            return chute != null && !string.IsNullOrEmpty(chute.canopyName) ? part.FindModelTransform(chute.canopyName) : null;
        }

        private static Transform RealServoTransform(PartModule module, Part part)
        {
            if (module == null) return null;
            string name = module.GetType().GetField("servoTransformName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(module) as string;
            Transform t = !string.IsNullOrEmpty(name) ? part.FindModelTransform(name) : null;
            if (t != null) return t;
            // The moving half can sit on a separate jointed object outside the part's model.
            for (Type ty = module.GetType(); ty != null && t == null; ty = ty.BaseType)
            {
                FieldInfo f = ty.GetField("movingPartObject", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (f?.GetValue(module) is GameObject go) t = go.transform;
            }
            return t;
        }

        private static List<Transform> ResolveGhostTracks(PartParityMotionRun m, GhostCoOrbitalRig.LiveGhost g, uint pid)
        {
            var byPath = new Dictionary<string, Transform>(StringComparer.Ordinal);
            Transform root = g.Build.root.transform;
            foreach (Transform t in GhostTrackTransforms(g.State, pid, m.Option))
            {
                string p = GhostPath(root, t);
                if (!byPath.ContainsKey(p)) byPath[p] = t;
            }
            var list = new List<Transform>();
            foreach (string p in m.TrackPaths) list.Add(byPath.TryGetValue(p, out Transform t) ? t : FindGhostByPath(root, p));
            return list;
        }

        /// <summary>One transform's pose: local, or in the frame of <paramref name="space"/>
        /// (a servo, whose moving half is a separate jointed body); a canopy's scale only.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static PartParityMotionPose ReadPose(Transform t, Transform space, bool scaleOnly)
        {
            var p = new PartParityMotionPose();
            if (t == null) return p;
            Vector3 pos;
            Quaternion rot;
            Vector3 scale;
            if (space != null)
            {
                pos = space.InverseTransformPoint(t.position);
                rot = Quaternion.Inverse(space.rotation) * t.rotation;
                Vector3 ls = t.lossyScale, ps = space.lossyScale;
                scale = new Vector3(Div(ls.x, ps.x), Div(ls.y, ps.y), Div(ls.z, ps.z));
            }
            else
            {
                pos = t.localPosition;
                rot = t.localRotation;
                scale = t.localScale;
            }
            p.Scale = new double[] { scale.x, scale.y, scale.z };
            if (!scaleOnly)
            {
                p.Position = new double[] { pos.x, pos.y, pos.z };
                p.Rotation = new double[] { rot.x, rot.y, rot.z, rot.w };
            }
            return p;
        }

        private static float Div(float a, float b) => Math.Abs(b) > 1e-6f ? a / b : a;

        private static Transform GhostSpace(GhostCoOrbitalRig.LiveGhost g, uint pid, bool partSpace)
        {
            if (!partSpace) return null;
            Transform root = g.Build.root.transform;
            return PartParityStateDriver.FindChildNamed(root, PartParityStructure.GhostPartSegmentPrefix + pid.ToString(Ic)) ?? root;
        }

        private static List<PartParityMotionPose> ReadPoses(IList<Transform> transforms, Transform space, bool scaleOnly)
        {
            var poses = new List<PartParityMotionPose>();
            foreach (Transform t in transforms) poses.Add(ReadPose(t, space, scaleOnly));
            return poses;
        }

        // ----- the framing pose and the ghost clip -----

        private static double GhostClip(GhostPlaybackState state, uint pid, string kind)
        {
            if (kind != PartParityMotionRules.KindClip && kind != PartParityMotionRules.KindSurface) return double.NaN;
            return PartParityStateDriver.ClampedClip(state, pid);
        }

        /// <summary>Poses the framing ghost at the motion's target through the family's own
        /// state (H.2): the deploy infos at their deployed pose, the servo at its target value,
        /// the canopy at its stage's pose. For framing only, never measured or shown.</summary>
        private static void PoseTarget(GhostPlaybackState state, uint pid, PartParityMotionRun m)
        {
            switch (m.Option.Kind)
            {
                case PartParityMotionRules.KindClip:
                case PartParityMotionRules.KindSurface:
                    if (state.deployableInfos != null && state.deployableInfos.TryGetValue(pid, out DeployableGhostInfo d) && d?.transforms != null)
                        foreach (DeployableTransformState ts in d.transforms)
                        {
                            if (ts.t == null) continue;
                            ts.t.localPosition = ts.deployedPos;
                            ts.t.localRotation = ts.deployedRot;
                            ts.t.localScale = ts.deployedScale;
                        }
                    break;
                case PartParityMotionRules.KindServo:
                    if (!double.IsNaN(m.Target))
                        GhostPlaybackLogic.ApplyRoboticEvent(state, new PartEvent
                        {
                            partPersistentId = pid, moduleIndex = m.Option.ModuleIndex, value = (float)m.Target,
                            eventType = PartEventType.RoboticPositionSample,
                        }, 0.0);
                    break;
                case PartParityMotionRules.KindCanopy:
                    if (state.parachuteInfos != null && state.parachuteInfos.TryGetValue(pid, out ParachuteGhostInfo pc) && pc?.canopyTransform != null)
                    {
                        bool full = m.Option.State == PartParityMotionRules.StateFull;
                        pc.canopyTransform.localScale = full ? pc.deployedCanopyScale : pc.semiDeployedCanopyScale;
                        pc.canopyTransform.localPosition = full ? pc.deployedCanopyPos : pc.semiDeployedCanopyPos;
                        pc.canopyTransform.localRotation = full ? pc.deployedCanopyRot : pc.semiDeployedCanopyRot;
                        GhostPlaybackLogic.ApplyCanopyBonePoses(pc, full ? CanopyPoseStage.Deployed : CanopyPoseStage.SemiDeployed);
                    }
                    break;
            }
        }

        // ----- the press -----

        /// <summary>The press (H.2): P6's for every P6 family and a control surface (its deploy
        /// KSPAction), the PAW slider's drive for a servo (unlocked, motor engaged, the target
        /// field set through its BaseField).</summary>
        private static void Press(WaveCase wc)
        {
            PartParityMotionRun m = wc.Motion;
            m.PressUT = Planetarium.GetUniversalTime();
            m.PressRealtime = Time.realtimeSinceStartup;
            if (m.Option.Kind != PartParityMotionRules.KindServo)
            {
                PartParityStateDriver.Press(wc);
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: motion press at ut={m.PressUT.ToString("0.000", Ic)} result={wc.State.ActionResult ?? "-"}");
                return;
            }
            PartParityStateRun run = wc.State;
            run.PressRealtime = m.PressRealtime;
            if (m.Module == null || double.IsNaN(m.Target))
            {
                run.ActionResult = PartParityStateRules.ActionMissing;
                wc.Status = TestCommandPartParitySweep.SetupActionFailed;
                wc.Detail = wc.Detail ?? "servo module or target not readable";
                run.Done = true;
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
                return;
            }
            string set = SetServo(m.Module, m.Piston ? PartParityMotionRules.DriveTargetExtension : PartParityMotionRules.DriveTargetAngle, m.Target);
            run.Pressed = true;
            run.ActionResult = set == null ? PartParityStateRules.ActionInvoked : PartParityStateRules.ActionThrew;
            ParsekLog.Info(Tag, $"case {wc.CaseId}: servo driven {m.Option.Module}.{m.Option.DriveField}={PartParityMotionPlan.Fmt(m.Target)} " +
                $"from {PartParityMotionPlan.Fmt(m.Rest)} result={run.ActionResult}{(set != null ? " detail=" + set : string.Empty)}");
            if (set != null)
            {
                wc.Status = TestCommandPartParitySweep.SetupActionFailed;
                wc.Detail = wc.Detail ?? "servo drive: " + set;
                run.Done = true;
            }
        }

        /// <summary>Unlocks and engages the servo and sets its target; null on success, else why.</summary>
        private static string SetServo(PartModule m, string field, double target)
        {
            try
            {
                WriteBool(m, "servoIsLocked", false);
                WriteBool(m, "servoMotorIsEngaged", true);
                BaseField f = m.Fields[field];
                if (f != null) f.SetValue((float)target, m);
                else
                {
                    FieldInfo fi = m.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
                    if (fi == null) return "no field " + field;
                    fi.SetValue(m, (float)target);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void WriteBool(PartModule m, string name, bool value)
        {
            BaseField f = m.Fields[name];
            if (f != null) { f.SetValue(value, m); return; }
            for (Type t = m.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (fi != null && fi.FieldType == typeof(bool)) { fi.SetValue(m, value); return; }
            }
        }

        private static double ReadFloat(PartModule m, string name)
        {
            object v = ReadMember(m, name);
            return v is float f ? f : v is double d ? d : double.NaN;
        }

        private static Vector2 ReadVector2(PartModule m, string name)
            => ReadMember(m, name) is Vector2 v ? v : new Vector2(float.NaN, float.NaN);

        private static object ReadMember(PartModule m, string name)
        {
            if (m == null) return null;
            for (Type t = m.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(m);
            }
            return null;
        }

        // ----- one frame of the window -----

        /// <summary>
        /// One end-of-frame of a pressed case: the done signal (P6's step for its families;
        /// the servo and surface rules otherwise), the predicted time on the first frames, the
        /// air floor and cap, the frame capture when its target is due, and the window's end.
        /// </summary>
        private static void Advance(StepContext ctx, WaveCase wc, bool air, bool waveCapped)
        {
            PartParityMotionRun m = wc.Motion;
            if (PartParitySweepRunner.CaseVesselLost(wc))
            {
                wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                wc.Detail = wc.Detail ?? "vessel lost during the motion";
                return;
            }
            if (air)
            {
                double agl = PartParityAirContext.HeightAboveTerrain(wc.Real);
                if (PartParityAirPlan.BelowFloor(agl))
                {
                    wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                    wc.Detail = wc.Detail ?? PartParityAirPlan.FloorDetail(agl);
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
                    return;
                }
                if (waveCapped && double.IsNaN(m.DoneS))
                {
                    wc.Status = TestCommandPartParitySweep.SetupStateTimeout;
                    wc.Detail = wc.Detail ?? "air wave cap of " + PartParityAirPlan.WaveCapSeconds.ToString("0", Ic) + " s reached";
                    return;
                }
            }
            double offset = Planetarium.GetUniversalTime() - m.PressUT;
            if (m.Twin != null)
            {
                string lost = PartParityMotionPlan.TwinLossDetail(m.Twin.Status != null, Healthy(m.Twin));
                if (lost != null)
                {
                    m.Twin.Status = TestCommandPartParitySweep.SetupDiedSettling;
                    m.Twin.Detail = lost;
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: {lost} at {PartParityMotionPlan.Fmt(offset)}s");
                }
            }
            StepDone(wc, offset);
            if (m.Twin != null && Healthy(m.Twin)) StepDone(m.Twin, offset);
            if (m.Twin != null && m.Twin.Status != null && wc.Status == null)
            {
                wc.Status = m.Twin.Status;
                wc.Detail = wc.Detail ?? "copy B: " + (m.Twin.Detail ?? m.Twin.Status);
            }
            if (wc.Status != null) return;
            if (!m.PredictedFinal) ReadPredicted(wc, m, offset);

            double windowEnd = double.NaN;
            if (!double.IsNaN(m.DoneS))
            {
                double doneForWindow = m.DoneS;
                if (m.Twin != null && !double.IsNaN(m.Twin.Motion.DoneS)) doneForWindow = Math.Max(doneForWindow, m.Twin.Motion.DoneS);
                windowEnd = PartParityMotionRules.WindowEndSeconds(doneForWindow, m.GhostExpectedRunS, m.ControlExtraS, out bool capped);
                m.Capped = capped;
            }
            if (offset > PartParityMotionRules.WindowCapSeconds)
            {
                m.Capped = true;
                if (double.IsNaN(m.DoneS))
                {
                    wc.Status = TestCommandPartParitySweep.SetupStateTimeout;
                    wc.Detail = wc.Detail ?? "no done signal inside the " + PartParityMotionRules.WindowCapSeconds.ToString("0", Ic) + " s window cap";
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
                    return;
                }
                windowEnd = PartParityMotionRules.WindowCapSeconds;
            }
            int k = m.TargetsS.Count;
            double target = PartParityMotionRules.FrameTargetSeconds(k, m.PredictedS, ctx.Args.Frames);
            if (!double.IsNaN(windowEnd) && target > windowEnd + 1e-9)
            {
                m.Finished = true;
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: window ended at {PartParityMotionPlan.Fmt(windowEnd)}s frames={k.ToString(Ic)} " +
                    $"done={PartParityMotionPlan.Fmt(m.DoneS)}s capped={(m.Capped ? "true" : "false")} events={wc.State.Sink.Events.Count.ToString(Ic)}");
                return;
            }
            if (offset + 1e-9 < target) return;
            CaptureFrame(ctx, wc, target);
            if (m.Twin != null && Healthy(m.Twin)) CaptureFrame(ctx, m.Twin, target);
        }

        /// <summary>The done signal of one frame: P6's step (poll, done, settle, refused,
        /// timeout) until done and the poll after; a servo's moving signal and deadband; a
        /// surface's still deflection; the robotic poll every frame for a servo.</summary>
        private static void StepDone(WaveCase wc, double offset)
        {
            PartParityMotionRun m = wc.Motion;
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            if (m.P6Driven)
            {
                if (!run.Done) PartParityStateDriver.Step(wc);
                else PartParityStateDriver.Poll(wc.Real, run);
                bool settledSurface = m.Option.Kind == PartParityMotionRules.KindSurface;
                if (wc.Status == null && PartParityMotionPlan.P6MotionDone(m.Option.Kind, run.Reached, run.Done) && double.IsNaN(m.DoneS))
                {
                    // An airbrake's deploy flag flips on the press frame, before its surface
                    // turns: its motion is done when P6's settle saw the surface still (H.2).
                    m.DoneS = settledSurface || double.IsNaN(run.ReachedUT) ? offset : run.ReachedUT - m.PressUT;
                    ParsekLog.Verbose(Tag, $"case {wc.CaseId}: real done at {PartParityMotionPlan.Fmt(m.DoneS)}s ({run.EndpointField}={run.EndpointValue})" +
                        (settledSurface ? " surface settled" : string.Empty));
                }
                return;
            }
            PartParityStateDriver.Poll(wc.Real, run);
            if (m.Option.Kind == PartParityMotionRules.KindServo && m.RoboticModules != null)
                PartStatePoller.PollRobotics(wc.Real, m.RoboticModules, m.ActiveRoboticKeys, m.LastRoboticPosition,
                    m.LastRoboticSampleUT, m.RoboticWindows, m.LoggedRoboticKeys, FlightRecorder.roboticSampleIntervalSeconds,
                    Planetarium.GetUniversalTime(), run.Sink);
            if (!double.IsNaN(m.DoneS)) return;
            bool done = false;
            string field = null, value = null;
            if (m.Option.Kind == PartParityMotionRules.KindServo)
            {
                bool hasMoving = FlightRecorder.TryGetRoboticMovingState(m.Module, out bool moving);
                bool hasPos = FlightRecorder.TryGetRoboticPositionValue(m.Module, m.Option.Module, out float pos, out float deadband, out string src);
                done = hasPos && PartParityMotionRules.ServoDone(hasMoving && moving, pos, m.Target, Math.Max(deadband, 1e-3f));
                field = src;
                value = hasPos ? pos.ToString("0.###", Ic) : null;
            }
            else if (m.Module is ModuleControlSurface cs)
            {
                double a = SurfaceDeflectionDeg(cs, out field);
                m.SurfaceSamples.Add(new KeyValuePair<double, double>(offset, a));
                done = PartParityMotionPlan.SurfaceDone(m.SurfaceSamples, m.SurfaceStartDeg);
                value = a.ToString("0.###", Ic);
            }
            if (done)
            {
                m.DoneS = offset;
                run.Reached = true;
                run.ReachedUT = m.PressUT + offset;
                run.ReachedRealtime = Time.realtimeSinceStartup;
                run.WaitSeconds = run.ReachedRealtime - run.PressRealtime;
                run.EndpointField = field;
                run.EndpointValue = value;
                run.Done = true;
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: real done at {PartParityMotionPlan.Fmt(offset)}s ({field}={value}) " +
                    $"events={run.Sink.Events.Count.ToString(Ic)}");
                return;
            }
            double timeout = double.IsNaN(m.TimeoutS)
                ? PartParityStateRules.StateTimeoutSeconds(run.Option, double.IsNaN(m.PredictedS) ? double.NaN : m.PredictedS, 1.0)
                : m.TimeoutS;
            if (!double.IsNaN(m.PredictedS)) m.TimeoutS = timeout;
            if (offset > timeout)
            {
                wc.Status = TestCommandPartParitySweep.SetupStateTimeout;
                wc.Detail = wc.Detail ?? "never reached " + m.Option.State + " in " + timeout.ToString("0.0", Ic) + " s (" + field + "=" + value + ")";
                run.WaitSeconds = Time.realtimeSinceStartup - run.PressRealtime;
                run.EndpointField = field;
                run.EndpointValue = value;
                run.Done = true;
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
            }
        }

        /// <summary>T from the first frames after the press (H.2); a provisional reading (no
        /// clip playing yet, a chute still arming) is replaced while frame 1 is not taken.</summary>
        private static void ReadPredicted(WaveCase wc, PartParityMotionRun m, double offset)
        {
            Part part = wc.Real.rootPart;
            double playing = PlayingClipSeconds(part, out string anim, out string state);
            double servo = m.Option.Kind == PartParityMotionRules.KindServo
                ? PartParityMotionPlan.ServoPredicted(m.Rest, m.Target, m.TraverseVelocity) : double.NaN;
            double surface = double.NaN;
            if (m.Option.Kind == PartParityMotionRules.KindSurface && m.Module is ModuleControlSurface cs)
                surface = PartParityMotionRules.SurfaceSeconds(SurfaceDeployAngleDeg(cs), cs.actuatorSpeed);
            double t = PartParityMotionPlan.ChoosePredicted(m.Option.Kind, playing, servo, surface, LongestClipSeconds(part), out string source);
            bool final = source != "longest-clip" && source != "cap";
            if (double.IsNaN(m.PredictedS) || final || m.TargetsS.Count <= 1)
            {
                m.PredictedS = t;
                m.PredictedSource = source;
                if (source == "playing-clip") { m.ClockAnimation = anim; m.ClockState = state; }
            }
            if (final || m.TargetsS.Count > 1)
            {
                m.PredictedFinal = true;
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: predicted T={PartParityMotionPlan.Fmt(m.PredictedS)}s source={m.PredictedSource} " +
                    $"read at {PartParityMotionPlan.Fmt(offset)}s clip={m.ClockState ?? "-"}");
                if (m.Twin != null)
                {
                    m.Twin.Motion.PredictedS = m.PredictedS;
                    m.Twin.Motion.PredictedFinal = true;
                    m.Twin.Motion.ClockAnimation = m.ClockAnimation;
                    m.Twin.Motion.ClockState = m.ClockState;
                }
            }
        }

        private static double PlayingClipSeconds(Part part, out string anim, out string state)
        {
            anim = null;
            state = null;
            double best = double.NaN;
            foreach (Animation an in part.GetComponentsInChildren<Animation>(true))
            {
                if (an == null || !an.isPlaying) continue;
                foreach (AnimationState st in an)
                {
                    if (st == null || !an.IsPlaying(st.name) || !(st.length > 0f)) continue;
                    double s = PartParityMotionRules.ClipSeconds(st.length, st.speed);
                    if (!double.IsNaN(s) && (double.IsNaN(best) || s > best))
                    {
                        best = s;
                        anim = an.name;
                        state = st.name;
                    }
                }
            }
            return best;
        }

        private static double LongestClipSeconds(Part part)
        {
            double best = double.NaN;
            foreach (Animation an in part.GetComponentsInChildren<Animation>(true))
            {
                if (an == null) continue;
                foreach (AnimationState st in an)
                {
                    if (st == null || !(st.length > 0f)) continue;
                    double s = PartParityMotionRules.ClipSeconds(st.length, st.speed != 0f ? st.speed : 1f);
                    if (!double.IsNaN(s) && (double.IsNaN(best) || s > best)) best = s;
                }
            }
            return best;
        }

        /// <summary>Stock's own clock (H.2): the clip's normalized time, a servo's position, a
        /// surface's deflection, a canopy's scale.</summary>
        private static double ReadRealClock(WaveCase wc, PartParityMotionRun m, out string stateValue)
        {
            Part part = wc.Real.rootPart;
            stateValue = null;
            if (m.P6Driven && wc.State?.Option != null)
            {
                PartParityStateDriver.Observe(part, wc.State.Option, out string f, out string v);
                m.FieldName = f;
                stateValue = v;
            }
            switch (m.Option.Kind)
            {
                case PartParityMotionRules.KindServo:
                    if (FlightRecorder.TryGetRoboticPositionValue(m.Module, m.Option.Module, out float pos, out _, out string src))
                    {
                        m.FieldName = src;
                        stateValue = pos.ToString("0.###", Ic);
                        return pos;
                    }
                    return double.NaN;
                case PartParityMotionRules.KindSurface:
                    if (m.Module is ModuleControlSurface cs)
                    {
                        double deflection = SurfaceDeflectionDeg(cs, out string source);
                        if (!m.P6Driven) { m.FieldName = source; stateValue = deflection.ToString("0.###", Ic); }
                        return deflection;
                    }
                    return double.NaN;
                case PartParityMotionRules.KindCanopy:
                    Transform clock = m.CanopyClock ?? (m.RealTransforms.Count > 0 ? m.RealTransforms[0] : null);
                    if (clock != null)
                    {
                        Vector3 s = clock.localScale;
                        return (s.x + s.y + s.z) / 3.0;
                    }
                    return double.NaN;
                default:
                    if (m.ClockAnimation == null) return double.NaN;
                    foreach (Animation an in part.GetComponentsInChildren<Animation>(true))
                    {
                        if (an == null || an.name != m.ClockAnimation) continue;
                        AnimationState st = an[m.ClockState];
                        if (st != null) return st.normalizedTime;
                    }
                    return double.NaN;
            }
        }

        /// <summary>One real frame (H.2): the actual offset, UT, stock's clock, the state field,
        /// every progress transform's pose and the real side's tiles in memory.</summary>
        private static void CaptureFrame(StepContext ctx, WaveCase wc, double targetS)
        {
            PartParityMotionRun m = wc.Motion;
            double ut = Planetarium.GetUniversalTime();
            double realS = double.IsNaN(m.PressUT) ? 0.0 : ut - m.PressUT;
            m.TargetsS.Add(targetS);
            m.RealS.Add(realS);
            m.Uts.Add(ut);
            m.RealClock.Add(ReadRealClock(wc, m, out string stateValue));
            m.StateValues.Add(stateValue);
            var flags = new List<string>();
            string jitter = PartParityMotionRules.FrameJitterTag(targetS, realS, m.PredictedS, ctx.Args.Frames);
            if (jitter != null) flags.Add(jitter);
            m.FrameFlags.Add(flags);
            Transform space = m.PartSpace ? wc.Real.rootPart.transform : null;
            for (int i = 0; i < m.RealTransforms.Count; i++)
                m.RealTracks[i].Poses.Add(ReadPose(m.RealTransforms[i], space, m.ScaleOnly));

            Part part = wc.Real.rootPart;
            PartParityStateDriver.ClassifyReal(wc, null, out List<Renderer> shown, out List<Renderer> hidden);
            var all = new List<Renderer>(shown);
            all.AddRange(hidden);
            int tile = ctx.Capture.Tile;
            for (int v = 0; v < wc.Views.Count; v++)
            {
                PartParityViewShot shot = ctx.Capture.CaptureSides(part.transform, wc.Shared, wc.OrthoSize, wc.Views[v], all,
                    new List<IList<Renderer>> { shown }, wc.CullingMask);
                if (shot.Error != null)
                {
                    m.RealTiles[v].Add(new byte[tile * tile * 4]);
                    if (!wc.Flags.Contains(PartParityCaseCapture.FlagCaptureErrorPrefix + wc.Views[v]))
                        wc.Flags.Add(PartParityCaseCapture.FlagCaptureErrorPrefix + wc.Views[v]);
                    ParsekLog.Warn(Tag, $"case {wc.CaseId} view {wc.Views[v]}: motion frame capture error {shot.Error}");
                    continue;
                }
                m.RealTiles[v].Add(shot.Real);
                m.BoxAreaPx[v] = shot.ProjectedBoxAreaPx;
            }
        }

        // =====================================================================================
        // the ghost rows (H.2 steps 1 to 3) and the measures
        // =====================================================================================

        private sealed class ScoutResult
        {
            internal readonly List<double> S = new List<double>();
            internal readonly List<PartParityMotionTrack> Tracks = new List<PartParityMotionTrack>();
            internal readonly List<double> Clock = new List<double>();
            internal double StartDelayS = double.NaN;
        }

        /// <summary>The event route of a case: the stowed seeds, then the polled events (the
        /// late control's moved 1.0 s later).</summary>
        private static List<PartEvent> RouteEvents(WaveCase wc, bool altered)
        {
            var events = new List<PartEvent>(wc.State.StowedSeeds);
            bool late = altered && wc.Motion.ControlId == TestCommandPartParitySweep.ControlMotionGhostLate;
            events.AddRange(late ? PartParityMotionPlan.ShiftEvents(wc.State.Sink.Events, PartParityMotionRules.LateShiftSeconds)
                : new List<PartEvent>(wc.State.Sink.Events));
            return events;
        }

        /// <summary>Builds a route ghost under the real part; the slow control's altered route
        /// has its subject's deploy clip doubled on this instance after the build (H.3).</summary>
        private static GhostCoOrbitalRig.LiveGhost BuildRouteGhost(WaveCase wc, bool altered, string suffix, out string fail)
        {
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            if (!GhostCoOrbitalRig.TryBuildGhostFromSnapshot(run.StowedSnapshot?.CreateCopy(), PartParityStateDriver.NewRecId(),
                    "ParsekPartParity_" + wc.CaseId + "_" + suffix, RouteEvents(wc, altered), wc.Real.vesselName,
                    wc.Real.persistentId, run.SeedUT, out GhostCoOrbitalRig.LiveGhost g, out fail))
                return null;
            PartParityStateDriver.Place(g, part.transform);
            if (altered && wc.Motion.ControlId == TestCommandPartParitySweep.ControlMotionGhostSlow
                && g.State?.deployableInfos != null && g.State.deployableInfos.TryGetValue(part.persistentId, out DeployableGhostInfo d) && d != null)
            {
                float before = d.clipLengthSeconds;
                d.clipLengthSeconds = (float)(GhostPlaybackLogic.ClampDeployableClipSeconds(before) * PartParityMotionRules.SlowClipFactor);
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: slow route clip {before.ToString("0.###", Ic)}s -> {d.clipLengthSeconds.ToString("0.###", Ic)}s ({suffix})");
            }
            return g;
        }

        /// <summary>Step 1: a scout ghost primed at every scout offset in increasing UT,
        /// unrendered, its poses read at each (and a servo's value); destroyed here.</summary>
        private static ScoutResult Scout(WaveCase wc, bool altered, double windowEndS, out string fail)
        {
            PartParityMotionRun m = wc.Motion;
            uint pid = wc.Real.rootPart.persistentId;
            GhostCoOrbitalRig.LiveGhost g = BuildRouteGhost(wc, altered, altered ? "scout-altered" : "scout", out fail);
            if (g == null) return null;
            var r = new ScoutResult();
            try
            {
                List<Transform> ts = ResolveGhostTracks(m, g, pid);
                Transform space = GhostSpace(g, pid, m.PartSpace);
                foreach (string p in m.TrackPaths) r.Tracks.Add(new PartParityMotionTrack { Path = p });
                RoboticGhostInfo servo = null;
                if (m.Option.Kind == PartParityMotionRules.KindServo && g.State?.roboticInfos != null)
                    g.State.roboticInfos.TryGetValue(FlightRecorder.EncodeEngineKey(pid, m.Option.ModuleIndex), out servo);
                foreach (double s in PartParityMotionRules.PlanScoutOffsets(windowEndS, m.RealS))
                {
                    GhostCoOrbitalRig.ApplySeedPrefix(g, m.PressUT + s);
                    r.S.Add(s);
                    List<PartParityMotionPose> poses = ReadPoses(ts, space, m.ScaleOnly);
                    for (int i = 0; i < poses.Count; i++) r.Tracks[i].Poses.Add(poses[i]);
                    r.Clock.Add(servo != null ? servo.currentValue : double.NaN);
                }
                if (g.State?.deployableInfos != null && g.State.deployableInfos.TryGetValue(pid, out DeployableGhostInfo d) && d != null)
                {
                    double start = !double.IsNaN(d.transitionDurationSeconds) && !double.IsNaN(d.transitionEventUT)
                        ? d.transitionEventUT - d.transitionDurationSeconds : d.transitionStartUT;
                    if (start > 0.0) r.StartDelayS = start - m.PressUT;
                }
                return r;
            }
            finally
            {
                GhostCoOrbitalRig.DestroyLiveGhost(g);
            }
        }

        private sealed class RenderResult
        {
            internal List<List<byte[]>> Same;
            internal List<List<byte[]>> Matched;
            internal double[] SameProgress;
            internal double[] MatchedProgress;
            internal int Disagree;
        }

        /// <summary>Step 3: the render ghost primed once at every merged UT, each primed pose
        /// captured into its row with the real side forced off, its progress read on the scout
        /// track; destroyed here.</summary>
        private static RenderResult Render(StepContext ctx, WaveCase wc, PartParityMotionRecord record, bool altered, ScoutResult scout,
            PartParityMotionSideProgress scoutSide, double[] matchedS, out string fail)
        {
            PartParityMotionRun m = wc.Motion;
            Part part = wc.Real.rootPart;
            uint pid = part.persistentId;
            int frames = m.RealS.Count, tile = ctx.Capture.Tile;
            var res = new RenderResult
            {
                Same = NewRows(wc.Views.Count, frames), Matched = NewRows(wc.Views.Count, frames),
                SameProgress = Fill(frames), MatchedProgress = Fill(frames),
            };
            GhostCoOrbitalRig.LiveGhost g = BuildRouteGhost(wc, altered, altered ? "render-altered" : "render", out fail);
            if (g == null) return null;
            try
            {
                var shown = new List<Renderer>();
                var hidden = new List<Renderer>();
                PartParityStateDriver.SplitGhost(g, wc.State.FillerPids, shown, hidden);
                if (!altered)
                {
                    wc.GhostRecords = PartParityCaseCapture.Records(part.transform, shown, wc.GhostLayers, null);
                    ConfigNode snapPart = g.Snapshot != null ? g.Snapshot.GetNode("PART") : null;
                    PartParityCaseCapture.RecordSkinProof(wc, snapPart, g.Build);
                }
                PartParityStateDriver.ClassifyReal(wc, new List<Transform> { g.Build.root.transform }, out List<Renderer> realShown, out List<Renderer> realHidden);
                var all = new List<Renderer>(realShown);
                all.AddRange(realHidden);
                all.AddRange(shown);
                all.AddRange(hidden);
                var sides = new List<IList<Renderer>> { shown };
                List<Transform> ts = ResolveGhostTracks(m, g, pid);
                Transform space = GhostSpace(g, pid, m.PartSpace);
                var sameUts = new List<double>();
                var matchedUts = new List<double>();
                for (int k = 0; k < frames; k++)
                {
                    sameUts.Add(m.PressUT + m.RealS[k]);
                    matchedUts.Add(double.IsNaN(matchedS[k]) ? double.NaN : m.PressUT + matchedS[k]);
                }
                List<double> scoutP = new List<double>(scoutSide.Progress);
                foreach (PartParityMotionPlan.RenderPrime p in PartParityMotionPlan.RenderPrimes(sameUts, matchedUts,
                             m.PressUT, PartParityMotionPlan.MaxPrimeStepSeconds))
                {
                    GhostCoOrbitalRig.ApplySeedPrefix(g, p.Ut);
                    if (p.Frame < 0) continue;
                    double prog = PartParityMotionMetrics.ProgressOnScout(scout.Tracks, scoutSide, ReadPoses(ts, space, m.ScaleOnly));
                    double atS = p.Ut - m.PressUT;
                    double expected = p.Matched ? record.Frames[p.Frame].RealProgress
                        : PartParityMotionRules.Interpolate(scout.S, scoutP, atS);
                    double change = p.Matched ? PartParityMotionMetrics.IntervalChange(scout.S, scoutP, atS) : 0.0;
                    if (scoutSide.Transforms.Exists(t => t.Channel != null)
                        && !PartParityMotionRules.RenderAgrees(!p.Matched, prog, expected, change))
                    {
                        res.Disagree++;
                        List<string> ff = record.Frames[p.Frame].Flags;
                        if (!ff.Contains(PartParityMotionRules.FlagScoutRenderDisagree)) ff.Add(PartParityMotionRules.FlagScoutRenderDisagree);
                    }
                    if (p.Matched) res.MatchedProgress[p.Frame] = prog; else res.SameProgress[p.Frame] = prog;
                    for (int v = 0; v < wc.Views.Count; v++)
                    {
                        PartParityViewShot shot = ctx.Capture.CaptureSides(part.transform, wc.Shared, wc.OrthoSize, wc.Views[v], all, sides, wc.CullingMask);
                        byte[] tileBytes = shot.Error != null ? new byte[tile * tile * 4] : shot.Real;
                        if (shot.Error != null) ParsekLog.Warn(Tag, $"case {wc.CaseId} view {wc.Views[v]}: ghost row capture error {shot.Error}");
                        (p.Matched ? res.Matched : res.Same)[v][p.Frame] = tileBytes;
                    }
                }
                return res;
            }
            finally
            {
                GhostCoOrbitalRig.DestroyLiveGhost(g);
            }
        }

        private static List<List<byte[]>> NewRows(int views, int frames)
        {
            var rows = new List<List<byte[]>>();
            for (int v = 0; v < views; v++) rows.Add(new List<byte[]>(new byte[frames][]));
            return rows;
        }

        private static double[] Fill(int n)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = double.NaN;
            return a;
        }

        /// <summary>
        /// The ghost side of one case after its window (H.2): the scout, the matched offsets,
        /// the render rows (copy B's live frames for the real-vs-real controls), the per-frame
        /// measures, the timing, the atlas and, for a control, its evidence flag. The slow and
        /// late controls build two routes over the same real frames: the normal one (its own
        /// sidecar, <see cref="PartParityMotionRules.NormalRouteCaseId"/>) and the altered one
        /// (the control's sidecar).
        /// </summary>
        private static void GhostRows(StepContext ctx, WaveCase wc)
        {
            PartParityMotionRun m = wc.Motion;
            int frames = m.RealS.Count;
            double windowEnd = frames > 0 ? m.TargetsS[frames - 1] : 0.0;
            double doneForWindow = m.DoneS;
            if (m.Twin != null && !double.IsNaN(m.Twin.Motion.DoneS)) doneForWindow = Math.Max(doneForWindow, m.Twin.Motion.DoneS);
            PartParityMotionWindow window = PartParityMotionRules.PlanFrameTargets(m.PredictedS, doneForWindow,
                m.GhostExpectedRunS, m.ControlExtraS, ctx.Args.Frames);
            if (m.Capped && !window.Capped) window.Capped = true;
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: frames={frames.ToString(Ic)} field={m.FieldName ?? "-"} " +
                $"values=[{string.Join(",", m.StateValues.ConvertAll(v => v ?? "-").ToArray())}]");

            PartParityMotionRoute main;
            if (m.Twin != null) main = PairRoute(ctx, wc, window);
            else
            {
                PartParityMotionRoute normal = GhostRoute(ctx, wc, window, windowEnd, false);
                if (normal == null) return;
                main = normal;
                if (PartParityMotionRules.HasNormalRoute(m.ControlId))
                {
                    PartParityMotionRoute altered = GhostRoute(ctx, wc, window, windowEnd, true);
                    if (altered == null) return;
                    m.Normal = normal;
                    m.NormalTiming = normal.Record.Timing;
                    main = altered;
                    ParsekLog.Verbose(Tag, $"case {wc.CaseId}: control {m.ControlId} normal lagS={PartParityMotionPlan.Fmt(normal.Record.Timing.LagS)} " +
                        $"durationRatio={PartParityMotionPlan.Fmt(normal.Record.Timing.DurationRatio)} -> {PartParityMotionRules.NormalRouteCaseId(wc.CaseId)}; " +
                        $"altered lagS={PartParityMotionPlan.Fmt(altered.Record.Timing.LagS)} durationRatio={PartParityMotionPlan.Fmt(altered.Record.Timing.DurationRatio)}");
                }
            }
            m.Main = main;
            var realProgress = new List<double>();
            foreach (PartParityMotionFrame f in main.Record.Frames) realProgress.Add(f.RealProgress);
            string windowProblem = PartParityMotionPlan.WindowRuleProblem(realProgress);
            if (windowProblem != null)
            {
                wc.Status = TestCommandPartParitySweep.SetupStateTimeout;
                wc.Detail = wc.Detail ?? windowProblem;
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: {windowProblem}; recorded as {wc.Status}");
            }
            // The last frame's same-time pairs are the case's views (the end-state rules read them).
            wc.ViewRecords.Clear();
            wc.CapturedViews.Clear();
            wc.ViewRecords.AddRange(main.LastViews);
            wc.CapturedViews.AddRange(main.LastViewNames);
            if (m.ControlId != null) m.Evidence = PartParityMotionControls.FormatEvidence(BuildEvidence(wc, window));
        }

        private static PartParityMotionRecord NewRecord(StepContext ctx, WaveCase wc, PartParityMotionWindow window)
        {
            PartParityMotionRun m = wc.Motion;
            var r = new PartParityMotionRecord
            {
                Option = m.Option, PressUT = m.PressUT, PredictedS = m.PredictedS, GhostClipS = m.GhostClipS,
                FramesPerMotion = ctx.Args.Frames, Window = window,
            };
            foreach (PartEvent e in wc.State.Sink.Events)
                if (e.ut >= m.PressUT - 1e-9) { r.EventDelayS = e.ut - m.PressUT; break; }
            for (int k = 0; k < m.RealS.Count; k++)
            {
                var f = new PartParityMotionFrame
                {
                    K = k, TargetS = m.TargetsS[k], RealS = m.RealS[k], Ut = m.Uts[k], RealClock = m.RealClock[k],
                };
                f.Flags.AddRange(m.FrameFlags[k]);
                r.Frames.Add(f);
            }
            return r;
        }

        /// <summary>The real-vs-real controls' route: copy B's live frames are the ghost rows
        /// and the scout (no matched row, no scout step).</summary>
        private static PartParityMotionRoute PairRoute(StepContext ctx, WaveCase wc, PartParityMotionWindow window)
        {
            PartParityMotionRun m = wc.Motion;
            PartParityMotionRun b = m.Twin.Motion;
            PartParityMotionRecord r = NewRecord(ctx, wc, window);
            var channels = new List<string>();
            PartParityMotionSeries series = PartParityMotionMetrics.BuildSeries(m.RealS, m.RealTracks, b.RealS, b.RealTracks,
                null, null, channels, out PartParityMotionSideProgress realSide, out _);
            for (int k = 0; k < r.Frames.Count; k++)
            {
                r.Frames[k].RealProgress = realSide.Progress[k];
                r.Frames[k].GhostProgress = series.GhostSameProgress[k];
            }
            r.ScoutStepS = double.NaN;
            return FinishRoute(ctx, wc, r, series, channels, b.RealTiles, null, 0, 0);
        }

        /// <summary>One ghost route over the case's real frames (H.2 steps 1 to 3): the scout,
        /// the matched offsets, the render rows, then <see cref="FinishRoute"/>. Null when a
        /// ghost could not be built (the case then carries <c>ghost-not-built:&lt;route&gt;</c>).</summary>
        private static PartParityMotionRoute GhostRoute(StepContext ctx, WaveCase wc, PartParityMotionWindow window,
            double windowEnd, bool altered)
        {
            PartParityMotionRun m = wc.Motion;
            int frames = m.RealS.Count;
            PartParityMotionRecord r = NewRecord(ctx, wc, window);
            ScoutResult scout = Scout(wc, altered, windowEnd, out string fail);
            if (scout == null) { NotBuilt(wc, altered ? "scout-altered" : "scout", fail); return null; }
            bool servo = m.Option.Kind == PartParityMotionRules.KindServo;
            var ghostClock = new List<double>();
            if (servo)
                foreach (double s in m.RealS) ghostClock.Add(PartParityMotionRules.Interpolate(scout.S, scout.Clock, s));
            var channels = new List<string>();
            PartParityMotionSeries series = PartParityMotionMetrics.BuildSeries(m.RealS, m.RealTracks, scout.S, scout.Tracks,
                servo ? m.RealClock : null, servo ? ghostClock : null, channels,
                out PartParityMotionSideProgress realSide, out PartParityMotionSideProgress scoutSide);
            r.GhostStartDelayS = scout.StartDelayS;
            double[] matchedS = Fill(frames);
            int noMatch = 0;
            for (int k = 0; k < frames; k++)
            {
                PartParityMotionFrame f = r.Frames[k];
                f.RealProgress = realSide.Progress[k];
                f.GhostProgress = series.GhostSameProgress[k];
                if (servo) f.GhostClock = ghostClock[k];
                matchedS[k] = PartParityMotionRules.MatchedOffset(scout.S, series.ScoutProgress, f.RealProgress);
                f.MatchedS = matchedS[k];
                if (double.IsNaN(matchedS[k])) { f.Flags.Add(PartParityMotionRules.FlagNoMatch); noMatch++; }
            }
            RenderResult render = Render(ctx, wc, r, altered, scout, scoutSide, matchedS, out fail);
            if (render == null) { NotBuilt(wc, altered ? "render-altered" : "render", fail); return null; }
            for (int k = 0; k < frames; k++) r.Frames[k].MatchedProgress = render.MatchedProgress[k];
            return FinishRoute(ctx, wc, r, series, channels, render.Same, render.Matched, render.Disagree, noMatch);
        }

        /// <summary>The timing, the per-frame measures and picture progress, the case flags and
        /// the motion atlas of one route (rows <see cref="PartParityJson.MotionAtlasRows"/>,
        /// columns the frames' <c>k</c>). A matched pair with no matched tile has a null block
        /// over an empty tile and a null picture progress.</summary>
        private static PartParityMotionRoute FinishRoute(StepContext ctx, WaveCase wc, PartParityMotionRecord r,
            PartParityMotionSeries series, List<string> channels, List<List<byte[]>> ghostSame, List<List<byte[]>> ghostMatched,
            int disagree, int noMatch)
        {
            PartParityMotionRun m = wc.Motion;
            int frames = r.Frames.Count, tile = ctx.Capture.Tile;
            bool matchedRow = ghostMatched != null;
            r.Series = series;
            r.TransformChannels.AddRange(channels);
            r.Timing = PartParityMotionRules.Timing(series);
            var route = new PartParityMotionRoute { Record = r, Disagree = disagree, NoMatch = noMatch };

            route.AtlasRows = new List<string>(PartParityJson.MotionAtlasRows(wc.Views, matchedRow));
            var rowTiles = new List<List<byte[]>>();
            for (int v = 0; v < wc.Views.Count; v++)
            {
                rowTiles.Add(Pad(m.RealTiles[v], frames));
                rowTiles.Add(Pad(ghostSame[v], frames));
                if (matchedRow) rowTiles.Add(Pad(ghostMatched[v], frames));
            }
            var picture = new List<double[]>();
            foreach (List<byte[]> row in rowTiles) picture.Add(PartParityMotionMetrics.RowPictureProgress(row, tile, tile));
            bool border = false;
            for (int k = 0; k < frames; k++)
            {
                PartParityMotionFrame f = r.Frames[k];
                f.ImgProgress = new double[rowTiles.Count];
                for (int i = 0; i < rowTiles.Count; i++) f.ImgProgress[i] = picture[i][k];
                f.ImgProgressRows = route.AtlasRows;
                for (int v = 0; v < wc.Views.Count; v++)
                {
                    var fv = new PartParityMotionFrameView { View = wc.Views[v] };
                    byte[] real = rowTiles[v * (matchedRow ? 3 : 2)][k] ?? PartParityMotionMetrics.EmptyTile(tile, tile);
                    byte[] same = rowTiles[v * (matchedRow ? 3 : 2) + 1][k] ?? PartParityMotionMetrics.EmptyTile(tile, tile);
                    fv.Same = Measure(real, same, tile, m.BoxAreaPx[v]);
                    byte[] matched = matchedRow ? rowTiles[v * 3 + 2][k] : null;
                    if (matched != null) fv.Matched = Measure(real, matched, tile, m.BoxAreaPx[v]);
                    border |= Touches(fv.Same) || Touches(fv.Matched);
                    f.Views.Add(fv);
                }
            }
            if (frames > 0)
                foreach (PartParityMotionFrameView fv in r.Frames[frames - 1].Views)
                {
                    PartParityViewRecord rec = fv.Same;
                    rec.View = fv.View;
                    route.LastViews.Add(rec);
                    route.LastViewNames.Add(fv.View);
                }

            if (m.Capped || r.Window.Capped) route.CaseFlags.Add(PartParityMotionRules.FlagWindowCapped);
            if (border) route.CaseFlags.Add(PartParityMotionRules.FlagClippedAtBorder);
            if (disagree > 0) route.CaseFlags.Add(PartParityMotionRules.FlagScoutRenderDisagree);
            double pictureT50 = picture.Count > 0 ? PartParityMotionRules.Crossing(m.RealS, picture[0], 0.5) : double.NaN;
            double poseT50 = r.Timing.RealCrossings != null ? r.Timing.RealCrossings[2] : double.NaN;
            if (PartParityMotionRules.ProgressDisagrees(poseT50, pictureT50, r.Window.SpacingS))
                route.CaseFlags.Add(PartParityMotionRules.FlagProgressDisagree);
            foreach (string tf in r.Timing.Flags) if (!route.CaseFlags.Contains(tf)) route.CaseFlags.Add(tf);

            var atlasRows = new byte[rowTiles.Count][][];
            for (int i = 0; i < rowTiles.Count; i++)
            {
                atlasRows[i] = new byte[frames][];
                for (int k = 0; k < frames; k++) atlasRows[i][k] = rowTiles[i][k] ?? PartParityMotionMetrics.EmptyTile(tile, tile);
            }
            foreach (PartParityMotionFrame f in r.Frames) route.AtlasCols.Add(f.K);
            if (frames > 0)
                route.Atlas = PartParityImage.BuildAtlas(atlasRows, frames, tile, out route.AtlasWidth, out route.AtlasHeight);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: motion rows frames={frames.ToString(Ic)} rows={route.AtlasRows.Count.ToString(Ic)} " +
                $"lagS={PartParityMotionPlan.Fmt(r.Timing.LagS)} durationRatio={PartParityMotionPlan.Fmt(r.Timing.DurationRatio)} " +
                $"curveShapeDev={PartParityMotionPlan.Fmt(r.Timing.CurveShapeDev)} noMatch={noMatch.ToString(Ic)} " +
                $"scoutRenderDisagree={disagree.ToString(Ic)} flags=[{string.Join(",", route.CaseFlags.ToArray())}]");
            return route;
        }

        private static PartParityMotionControlEvidence BuildEvidence(WaveCase wc, PartParityMotionWindow window)
        {
            PartParityMotionRun m = wc.Motion;
            return new PartParityMotionControlEvidence
            {
                Normal = m.NormalTiming, Altered = m.Main.Record.Timing, FrameS = window.SpacingS, PredictedS = m.PredictedS,
                EndsWithinAllowance = PartParityMotionControls.EndFramesWithinAllowance(m.Main.Record.Frames),
                LastFramePasses = PartParityMotionControls.LastFramePasses(m.Main.Record.Frames, wc.Framing?.Structure,
                    wc.Entry?.Tags),
            };
        }

        private static void NotBuilt(WaveCase wc, string route, string fail)
        {
            wc.Flags.Add(PartParityCaseCapture.FlagGhostNotBuilt + ":" + route);
            wc.Detail = wc.Detail ?? route + " ghost: " + fail;
            wc.Motion.Main = null;
            wc.Motion.Normal = null;
            ParsekLog.Warn(Tag, $"case {wc.CaseId}: {route} ghost not built: {fail}");
        }

        private static List<byte[]> Pad(List<byte[]> row, int frames)
        {
            var r = new List<byte[]>(row ?? new List<byte[]>());
            while (r.Count < frames) r.Add(null);
            if (r.Count > frames) r.RemoveRange(frames, r.Count - frames);
            return r;
        }

        private static PartParityViewRecord Measure(byte[] real, byte[] ghost, int tile, double boxAreaPx)
            => new PartParityViewRecord { Measures = PartParityMetrics.Measure(real, ghost, tile, tile, boxAreaPx) };

        private static bool Touches(PartParityViewRecord r)
            => r?.Measures != null && (r.Measures.TouchesBorderReal || r.Measures.TouchesBorderGhost);

        // =====================================================================================
        // the sidecar (H.2) and the teardown
        // =====================================================================================

        /// <summary>The motion and motionAtlas blocks, the route's case flags and a control's
        /// evidence flag (the normal route's sidecar while <see cref="PartParityMotionRun.WritingNormal"/>).
        /// A case that faulted before its rows writes neither block.</summary>
        internal static void FillSidecar(StepContext ctx, WaveCase wc, PartParityCaseSidecar s)
        {
            PartParityMotionRun m = wc.Motion;
            if (m == null) return;
            PartParityMotionRoute route = m.WritingNormal ? m.Normal : m.Main;
            string caseId = m.WritingNormal ? PartParityMotionRules.NormalRouteCaseId(wc.CaseId) : wc.CaseId;
            if (route != null)
                foreach (string f in route.CaseFlags) if (!s.Flags.Contains(f)) s.Flags.Add(f);
            if (m.Evidence != null && !m.WritingNormal) s.Flags.Add(m.Evidence);
            if (route?.Record == null) return;
            s.Motion = PartParityMotionMetrics.MotionBlock(route.Record);
            if (route.Atlas != null)
                s.MotionAtlas = PartParityMotionMetrics.MotionAtlasBlock(TestCommandPartParitySweep.MotionAtlasFileName(caseId),
                    ctx.Capture.Tile, route.AtlasRows, route.AtlasCols);
        }

        /// <summary>The motion atlas <c>&lt;caseId&gt;.motion.png</c> through the shared image
        /// writer, and the normal route's beside it.</summary>
        internal static void WritePictures(StepContext ctx, WaveCase wc)
        {
            PartParityMotionRun m = wc.Motion;
            if (m == null) return;
            WriteAtlas(ctx, wc.CaseId, m.Main);
            if (m.Normal != null) WriteAtlas(ctx, PartParityMotionRules.NormalRouteCaseId(wc.CaseId), m.Normal);
        }

        private static void WriteAtlas(StepContext ctx, string caseId, PartParityMotionRoute route)
        {
            if (route?.Atlas == null) return;
            FileIOUtils.SafeWriteBytes(PartParityPng.EncodeRgba(route.AtlasWidth, route.AtlasHeight, route.Atlas),
                System.IO.Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.MotionAtlasFileName(caseId)), Tag);
            ParsekLog.Verbose(Tag, $"case {caseId}: motion atlas {route.AtlasWidth.ToString(Ic)}x{route.AtlasHeight.ToString(Ic)} written");
        }

        /// <summary>The slow or late control's normal route as its own sidecar (H.3): the
        /// case's sidecar with the normal route's motion blocks, flags and last-frame views,
        /// under <see cref="PartParityMotionRules.NormalRouteCaseId"/>. Its atlas is written by
        /// <see cref="WritePictures"/>.</summary>
        internal static void WriteNormalRoute(StepContext ctx, WaveCase wc)
        {
            PartParityMotionRun m = wc.Motion;
            if (m == null || !PartParityMotionRules.HasNormalRoute(wc.ControlId)) return;
            PartParityCaseSidecar s;
            m.WritingNormal = true;
            try { s = PartParityCaseCapture.BuildSidecar(ctx, wc); }
            finally { m.WritingNormal = false; }
            s.CaseId = PartParityMotionRules.NormalRouteCaseId(wc.CaseId);
            s.Views = m.Normal != null ? new List<PartParityViewRecord>(m.Normal.LastViews) : new List<PartParityViewRecord>();
            if (m.Normal == null && !TestCommandPartParitySweep.IsSetupFault(s.Setup?.Status))
                s.Setup = new PartParitySetup { Status = TestCommandPartParitySweep.SetupCrashed, Detail = "normal route not built" };
            PartParitySweepRunner.WriteSidecar(ctx, s);
            ParsekLog.Info(Tag, $"control {wc.CaseId}: normal route sidecar {s.CaseId} written setup={s.Setup?.Status ?? "<none>"} " +
                $"lagS={PartParityMotionPlan.Fmt(m.Normal?.Record?.Timing?.LagS ?? double.NaN)}");
        }

        /// <summary>The control's judge over its sidecar (H.3).</summary>
        internal static string JudgeControl(PartParityCaseSidecar s, out string detail)
            => PartParityMotionControls.Judge(s.CaseId, s.Flags, s.Setup?.Status, out detail);

        /// <summary>Copy B goes with its case (its vessel and state parts); the tiles are dropped.
        /// Idempotent.</summary>
        internal static void Release(WaveCase wc)
        {
            PartParityMotionRun m = wc?.Motion;
            if (m == null) return;
            m.RealTiles.Clear();
            DropAtlases(m);
            if (m.Twin != null)
            {
                PartParitySweepRunner.DestroyCase(m.Twin);
                m.Twin.Motion?.RealTiles.Clear();
            }
        }
    }
}
