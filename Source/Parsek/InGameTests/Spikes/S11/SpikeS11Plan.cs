using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.InGameTests.Spikes.S11
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S11.
    //
    // The pure half of the ghost cost sweep: the step plan, the env switches, the ghost
    // layout the injector writes and the sweep reads back, the frame statistics, the slope
    // fit and the line / row formatting. Nothing here touches Unity or KSP, so every
    // method is xUnit-tested (Source/Parsek.Tests/Spikes/SpikeS11Tests.cs).

    internal enum SpikeS11Pass { AsIs, Uncapped }

    internal enum SpikeS11View { Flight, Map }

    internal static class SpikeS11Plan
    {
        internal const string SpikeId = "S11";
        internal const string Category = "SpikeS11";

        /// <summary>Ghost counts per step, ascending, so each step builds only the new ghosts.</summary>
        internal static readonly int[] GhostCounts = { 0, 8, 16, 32, 64 };

        internal const int MaxGhosts = 64;
        internal const int DefaultFramesPerStep = 600;
        internal const int MinFramesPerStep = 60;
        internal const int MaxFramesPerStep = 6000;

        /// <summary>Frames after the ghosts are built (or after map entry) before a window opens.</summary>
        internal const int SettleFrames = 120;

        /// <summary>Upper bound on the wait for the engine to build / destroy a step's ghosts.</summary>
        internal const int SpawnWaitMaxFrames = 1800;

        /// <summary>Warm repeats of the sidecar open after the first one.</summary>
        internal const int WarmOpenRepeats = 5;

        internal const string EnvPass = "PARSEK_SPIKE_S11_PASS";
        internal const string EnvFrames = "PARSEK_SPIKE_S11_FRAMES";
        internal const string EnvShots = "PARSEK_SPIKE_S11_SHOTS";

        /// <summary>Folder in the save directory that holds the copied "cold" sidecar pair.</summary>
        internal const string ColdFolderName = "SpikeS11";

        internal const string RecordingIdPrefix = "spike-s11-ghost-";
        internal const string VesselNamePrefix = "S11 Ghost ";

        internal static string Tag => SpikeArming.SpikeTag(SpikeId);

        internal static string RecordingIdFor(int index)
            => RecordingIdPrefix + index.ToString("00", CultureInfo.InvariantCulture);

        internal static string VesselNameFor(int index)
            => VesselNamePrefix + index.ToString("00", CultureInfo.InvariantCulture);

        /// <summary>The ghost index of a spike recording id, or -1 for any other id.</summary>
        internal static int IndexOfRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId) || !recordingId.StartsWith(RecordingIdPrefix, StringComparison.Ordinal))
                return -1;
            string tail = recordingId.Substring(RecordingIdPrefix.Length);
            if (tail.Length != 2) return -1;
            int n;
            if (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return -1;
            return n < MaxGhosts ? n : -1;
        }

        /// <summary>Ghost i plays in a step of n ghosts when i &lt; n (prefix; the layout spreads any prefix).</summary>
        internal static bool EnabledInStep(int ghostIndex, int stepCount) => ghostIndex >= 0 && ghostIndex < stepCount;

        /// <summary>"asis" (default) or "uncapped"; anything else falls back to asis and says so.</summary>
        internal static SpikeS11Pass ParsePass(string raw, out string note)
        {
            note = null;
            string v = (raw ?? "").Trim().ToLowerInvariant();
            if (v.Length == 0 || v == "asis") return SpikeS11Pass.AsIs;
            if (v == "uncapped") return SpikeS11Pass.Uncapped;
            note = "unknown " + EnvPass + "='" + raw + "', using asis";
            return SpikeS11Pass.AsIs;
        }

        internal static string PassToken(SpikeS11Pass pass) => pass == SpikeS11Pass.Uncapped ? "uncapped" : "asis";

        internal static string ViewToken(SpikeS11View view) => view == SpikeS11View.Map ? "map" : "flight";

        /// <summary>Frames per window: 600 by default, clamped to [60, 6000].</summary>
        internal static int ParseFrames(string raw, out string note)
        {
            note = null;
            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0) return DefaultFramesPerStep;
            int n;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            {
                note = "unparsable " + EnvFrames + "='" + raw + "', using " + DefaultFramesPerStep;
                return DefaultFramesPerStep;
            }
            if (n < MinFramesPerStep || n > MaxFramesPerStep)
            {
                int clamped = Math.Max(MinFramesPerStep, Math.Min(MaxFramesPerStep, n));
                note = EnvFrames + "=" + n + " clamped to " + clamped;
                return clamped;
            }
            return n;
        }

        /// <summary>Screenshots are on unless the env says 0 / false / off / no.</summary>
        internal static bool ParseShots(string raw)
        {
            string v = (raw ?? "").Trim().ToLowerInvariant();
            return !(v == "0" || v == "false" || v == "off" || v == "no");
        }

        /// <summary>The run label that ties a run's log lines and CSV rows together.</summary>
        internal static string RunLabel(SpikeS11Pass pass, DateTime utc)
            => PassToken(pass) + "-" + utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        internal static string ShotFileName(string runLabel, SpikeS11View view, int n)
            => runLabel + "-" + ViewToken(view) + "-n" + n.ToString("00", CultureInfo.InvariantCulture) + ".png";

        /// <summary>
        /// Null when a measured window counts, else why not: every requested ghost built and
        /// no other spike ghost left over, the spawn wait not timed out, the whole window
        /// measured, and in flight view every built ghost inside the camera's view.
        /// </summary>
        internal static string JudgeStep(SpikeS11StepResult r, int plannedFrames)
        {
            if (r == null) return "no result";
            if (r.SpawnWaitTimedOut) return "spawn wait timed out";
            if (r.Built != r.Requested) return "built " + r.Built + " of " + r.Requested;
            if (r.Leftover != 0) return r.Leftover + " ghosts of a later step still built";
            if (r.FrameMs.Count != plannedFrames) return "measured " + r.FrameMs.Count + " of " + plannedFrames + " frames";
            if (r.View == SpikeS11View.Flight && r.OnScreen >= 0 && r.OnScreen < r.Built)
                return "only " + r.OnScreen + " of " + r.Built + " ghosts in the camera view";
            return null;
        }
    }

    /// <summary>
    /// Where ghost i flies: a circle around the pad (the focused vessel of the
    /// pad-runway-pair host), radius 40 to 110 m by i % 8, altitude 100 to 170 m ASL
    /// (30 to 100 m above the pad) by i / 8, start angle i times the golden angle so any
    /// prefix of the 64 is spread around the pad, one lap per 90 s, alternate layers
    /// turning the other way. Two ghosts differ by at least 10 m in radius or in
    /// altitude, so they never overlap. Close enough that the flight camera, 300 m from
    /// the pad, shows every ghost as a rocket rather than a dot.
    /// </summary>
    internal static class SpikeS11Layout
    {
        // The Logi Cargo Rig on pad-runway-pair's pad (persistent.sfs VESSEL lat / lon).
        internal const double CenterLatitude = -0.097207801750065659;
        internal const double CenterLongitude = -74.557674910757655;
        internal const double PadAltitudeMeters = 70.0;

        internal const double KerbinRadiusMeters = 600000.0;
        internal const double MinRadiusMeters = 40.0;
        internal const double RadiusStepMeters = 10.0;
        internal const double MinAltitudeMeters = 100.0;
        internal const double AltitudeStepMeters = 10.0;
        internal const double LapSeconds = 90.0;
        internal const double GoldenAngleRadians = 2.39996322972865332;

        internal const double StartAfterSaveSeconds = 2.0;
        internal const double DurationSeconds = 1800.0;
        internal const double SampleSpacingSeconds = 2.0;

        internal static int PointCount => (int)Math.Round(DurationSeconds / SampleSpacingSeconds) + 1;

        internal static double RadiusFor(int i) => MinRadiusMeters + RadiusStepMeters * (i % 8);

        internal static double AltitudeFor(int i) => MinAltitudeMeters + AltitudeStepMeters * (i / 8);

        internal static double StartAngleFor(int i)
        {
            double a = (i * GoldenAngleRadians) % (2.0 * Math.PI);
            return a < 0 ? a + 2.0 * Math.PI : a;
        }

        internal static int DirectionFor(int i) => (i / 8) % 2 == 0 ? 1 : -1;

        /// <summary>Angle (radians, from north, clockwise toward east) of ghost i at t seconds after its start.</summary>
        internal static double AngleAt(int i, double t)
            => StartAngleFor(i) + DirectionFor(i) * 2.0 * Math.PI * (t / LapSeconds);

        /// <summary>lat / lon (degrees) and altitude (m ASL) of ghost i at t seconds after its start.</summary>
        internal static void PositionAt(int i, double t, out double lat, out double lon, out double alt)
        {
            double r = RadiusFor(i);
            double a = AngleAt(i, t);
            double north = r * Math.Cos(a);
            double east = r * Math.Sin(a);
            double rad2deg = 180.0 / Math.PI;
            lat = CenterLatitude + north / KerbinRadiusMeters * rad2deg;
            lon = CenterLongitude + east / (KerbinRadiusMeters * Math.Cos(CenterLatitude / rad2deg)) * rad2deg;
            alt = AltitudeFor(i);
        }

        /// <summary>Straight-line distance (m) from the pad vessel to ghost i at any time.</summary>
        internal static double SlantRangeMeters(int i)
        {
            double r = RadiusFor(i);
            double h = AltitudeFor(i) - PadAltitudeMeters;
            return Math.Sqrt(r * r + h * h);
        }
    }

    internal struct SpikeS11FrameStats
    {
        internal int Count;
        internal double Mean;
        internal double Min;
        internal double Max;
        internal double P50;
        internal double P95;
        internal double P99;
        internal double StdDev;
    }

    internal struct SpikeS11Fit
    {
        internal int Points;
        internal double Slope;
        internal double Intercept;
        internal double R2;
        internal bool Valid;
    }

    internal static class SpikeS11Stats
    {
        /// <summary>
        /// Nearest-rank percentile of an ascending array: the value at rank ceil(p n), 1-based.
        /// p in (0, 1]. NaN for an empty array.
        /// </summary>
        internal static double Percentile(double[] sorted, int count, double p)
        {
            if (sorted == null || count <= 0) return double.NaN;
            int rank = (int)Math.Ceiling(p * count);
            if (rank < 1) rank = 1;
            if (rank > count) rank = count;
            return sorted[rank - 1];
        }

        /// <summary>Summary of the first <paramref name="count"/> values. All NaN when empty.</summary>
        internal static SpikeS11FrameStats Summarize(double[] values, int count)
        {
            var s = new SpikeS11FrameStats();
            if (values == null || count <= 0)
            {
                s.Mean = s.Min = s.Max = s.P50 = s.P95 = s.P99 = s.StdDev = double.NaN;
                return s;
            }
            if (count > values.Length) count = values.Length;
            var sorted = new double[count];
            Array.Copy(values, sorted, count);
            Array.Sort(sorted);
            double sum = 0;
            for (int i = 0; i < count; i++) sum += sorted[i];
            double mean = sum / count;
            double sq = 0;
            for (int i = 0; i < count; i++) { double d = sorted[i] - mean; sq += d * d; }
            s.Count = count;
            s.Mean = mean;
            s.Min = sorted[0];
            s.Max = sorted[count - 1];
            s.P50 = Percentile(sorted, count, 0.50);
            s.P95 = Percentile(sorted, count, 0.95);
            s.P99 = Percentile(sorted, count, 0.99);
            s.StdDev = count > 1 ? Math.Sqrt(sq / (count - 1)) : 0.0;
            return s;
        }

        /// <summary>Ordinary least squares y = intercept + slope x. Invalid with fewer than two distinct x.</summary>
        internal static SpikeS11Fit Fit(IList<double> x, IList<double> y)
        {
            var f = new SpikeS11Fit { Slope = double.NaN, Intercept = double.NaN, R2 = double.NaN };
            if (x == null || y == null) return f;
            int n = Math.Min(x.Count, y.Count);
            int used = 0;
            double sx = 0, sy = 0;
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(x[i]) || double.IsNaN(y[i])) continue;
                sx += x[i]; sy += y[i]; used++;
            }
            f.Points = used;
            if (used < 2) return f;
            double mx = sx / used, my = sy / used;
            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(x[i]) || double.IsNaN(y[i])) continue;
                double dx = x[i] - mx, dy = y[i] - my;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }
            if (sxx <= 0) return f;
            f.Slope = sxy / sxx;
            f.Intercept = my - f.Slope * mx;
            f.R2 = syy <= 0 ? 1.0 : (sxy * sxy) / (sxx * syy);
            f.Valid = true;
            return f;
        }
    }

    /// <summary>One measured window: one ghost count in one view.</summary>
    internal sealed class SpikeS11StepResult
    {
        internal string Run;
        internal SpikeS11Pass Pass;
        internal SpikeS11View View;
        internal int Requested;
        internal int Built;          // spike ghosts with a ghost GameObject
        internal int Active;         // ... whose GameObject is active
        internal int Leftover;       // spike ghosts of index >= Requested still built (must be 0)
        internal int OnScreen;       // ... with at least one renderer Unity reports visible (-1: not measured)
        internal int Zone1;          // engine observability: primary ghosts in the full-fidelity zone (all ghosts)
        internal int Zone2;
        internal int EngineFxGhosts;
        internal int EngineParticleSystems;
        internal int SpawnWaitFrames;
        internal long SpawnMicroseconds;
        internal long SpawnMaxFrameMicroseconds;
        internal bool SpawnWaitTimedOut;
        internal SpikeS11FrameStats FrameMs;
        internal SpikeS11FrameStats EngineUs;
        internal int Gen0Collections;
        internal long ManagedDeltaKb;
        internal double MeanFps;
        internal string InvalidReason; // null when the window counts (SpikeS11Plan.JudgeStep)
    }

    internal static class SpikeS11Format
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static string Ms(double v) => double.IsNaN(v) ? "NaN" : v.ToString("F3", Inv);

        internal static string Num(double v, string fmt) => double.IsNaN(v) ? "NaN" : v.ToString(fmt, Inv);

        internal static readonly string[] StepHeader =
        {
            "run", "pass", "view", "requested", "built", "active", "leftover", "onScreen", "zone1", "zone2",
            "engineFxGhosts", "engineParticleSystems", "frames", "meanMs", "p50Ms", "p95Ms", "p99Ms",
            "minMs", "maxMs", "stdMs", "meanFps", "engineMeanUs", "engineP50Us", "engineP99Us",
            "engineMaxUs", "gen0", "managedDeltaKb", "spawnWaitFrames", "spawnWaitTimedOut",
            "spawnUs", "spawnMaxFrameUs", "valid", "invalidReason",
        };

        internal static object[] StepRow(SpikeS11StepResult r)
        {
            return new object[]
            {
                r.Run, SpikeS11Plan.PassToken(r.Pass), SpikeS11Plan.ViewToken(r.View), r.Requested, r.Built,
                r.Active, r.Leftover, r.OnScreen, r.Zone1, r.Zone2, r.EngineFxGhosts, r.EngineParticleSystems,
                r.FrameMs.Count, r.FrameMs.Mean, r.FrameMs.P50, r.FrameMs.P95, r.FrameMs.P99,
                r.FrameMs.Min, r.FrameMs.Max, r.FrameMs.StdDev, r.MeanFps, r.EngineUs.Mean, r.EngineUs.P50,
                r.EngineUs.P99, r.EngineUs.Max, r.Gen0Collections, r.ManagedDeltaKb, r.SpawnWaitFrames,
                r.SpawnWaitTimedOut, r.SpawnMicroseconds, r.SpawnMaxFrameMicroseconds,
                r.InvalidReason == null, r.InvalidReason ?? "",
            };
        }

        internal static string StepLine(SpikeS11StepResult r)
        {
            var sb = new StringBuilder();
            sb.Append("step run=").Append(r.Run)
              .Append(" pass=").Append(SpikeS11Plan.PassToken(r.Pass))
              .Append(" view=").Append(SpikeS11Plan.ViewToken(r.View))
              .Append(" n=").Append(r.Requested.ToString(Inv))
              .Append(" built=").Append(r.Built.ToString(Inv))
              .Append(" active=").Append(r.Active.ToString(Inv))
              .Append(" leftover=").Append(r.Leftover.ToString(Inv))
              .Append(" onScreen=").Append(r.OnScreen < 0 ? "n/a" : r.OnScreen.ToString(Inv))
              .Append(" zone1=").Append(r.Zone1.ToString(Inv))
              .Append(" zone2=").Append(r.Zone2.ToString(Inv))
              .Append(" engineFx=").Append(r.EngineFxGhosts.ToString(Inv))
              .Append(" frames=").Append(r.FrameMs.Count.ToString(Inv))
              .Append(" meanMs=").Append(Ms(r.FrameMs.Mean))
              .Append(" p50Ms=").Append(Ms(r.FrameMs.P50))
              .Append(" p95Ms=").Append(Ms(r.FrameMs.P95))
              .Append(" p99Ms=").Append(Ms(r.FrameMs.P99))
              .Append(" maxMs=").Append(Ms(r.FrameMs.Max))
              .Append(" fps=").Append(Num(r.MeanFps, "F1"))
              .Append(" engineMeanUs=").Append(Num(r.EngineUs.Mean, "F1"))
              .Append(" engineP99Us=").Append(Num(r.EngineUs.P99, "F1"))
              .Append(" gen0=").Append(r.Gen0Collections.ToString(Inv))
              .Append(" managedDeltaKb=").Append(r.ManagedDeltaKb.ToString(Inv))
              .Append(" spawnWaitFrames=").Append(r.SpawnWaitFrames.ToString(Inv))
              .Append(r.SpawnWaitTimedOut ? " spawnWait=TIMEOUT" : "")
              .Append(" spawnUs=").Append(r.SpawnMicroseconds.ToString(Inv))
              .Append(" spawnMaxFrameUs=").Append(r.SpawnMaxFrameMicroseconds.ToString(Inv))
              .Append(" valid=").Append(r.InvalidReason == null ? "true" : "false");
            if (r.InvalidReason != null) sb.Append(" invalidReason=\"").Append(r.InvalidReason).Append('"');
            return sb.ToString();
        }

        internal static readonly string[] FrameHeader = { "run", "pass", "view", "n", "frame", "frameMs", "engineUs" };

        internal static readonly string[] FitHeader =
            { "run", "pass", "view", "metric", "points", "slopePerGhost", "intercept", "r2" };

        /// <summary>The fitted metrics: frame mean / p99 (ms) and Parsek's engine update mean (us), each against n.</summary>
        internal static readonly string[] FitMetrics = { "meanMs", "p99Ms", "engineMeanUs" };

        internal static double MetricOf(SpikeS11StepResult r, string metric)
        {
            switch (metric)
            {
                case "meanMs": return r.FrameMs.Mean;
                case "p99Ms": return r.FrameMs.P99;
                case "engineMeanUs": return r.EngineUs.Mean;
                default: return double.NaN;
            }
        }

        /// <summary>
        /// One fit per (view, metric) over the steps of one run: x is the number of spike
        /// ghosts the engine had BUILT in the window (not the request), so a step whose spawn
        /// wait timed out still lands where it really was.
        /// </summary>
        internal static List<KeyValuePair<string, SpikeS11Fit>> FitAll(IList<SpikeS11StepResult> steps, SpikeS11View view)
        {
            var result = new List<KeyValuePair<string, SpikeS11Fit>>();
            foreach (string metric in FitMetrics)
            {
                var xs = new List<double>();
                var ys = new List<double>();
                if (steps != null)
                {
                    foreach (var s in steps)
                    {
                        if (s == null || s.View != view) continue;
                        xs.Add(s.Built);
                        ys.Add(MetricOf(s, metric));
                    }
                }
                result.Add(new KeyValuePair<string, SpikeS11Fit>(metric, SpikeS11Stats.Fit(xs, ys)));
            }
            return result;
        }

        internal static string FitLine(string run, SpikeS11Pass pass, SpikeS11View view, string metric, SpikeS11Fit f)
        {
            return "fit run=" + run + " pass=" + SpikeS11Plan.PassToken(pass) + " view=" + SpikeS11Plan.ViewToken(view)
                + " metric=" + metric + " points=" + f.Points.ToString(Inv)
                + " slopePerGhost=" + Num(f.Slope, "F4") + " intercept=" + Num(f.Intercept, "F3")
                + " r2=" + Num(f.R2, "F3") + (f.Valid ? "" : " (invalid: fewer than two distinct ghost counts)");
        }

        internal static object[] FitRow(string run, SpikeS11Pass pass, SpikeS11View view, string metric, SpikeS11Fit f)
            => new object[] { run, SpikeS11Plan.PassToken(pass), SpikeS11Plan.ViewToken(view), metric, f.Points, f.Slope, f.Intercept, f.R2 };
    }
}
