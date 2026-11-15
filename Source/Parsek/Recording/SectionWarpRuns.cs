using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Per-frame time-warp classification of a TrackSection's committed frames, folded
    /// into contiguous runs for the section-close log line.
    ///
    /// <para>Why it exists: KSP physics warp (TimeWarp.Modes.LOW, 2x-4x) sets
    /// <c>Time.timeScale = rate</c> and <c>Time.fixedDeltaTime = 0.02 * rate</c>
    /// (decompiled <c>TimeWarp.updateRate</c>, KSP 1.12.5), so every physics frame
    /// still runs and the recorder still samples per frame, but one frame advances
    /// game time by <c>0.02 * rate</c> seconds. The recorder spaces samples by game UT
    /// (<see cref="TrajectoryMath.ShouldRecordPoint"/>), so its min / max interval
    /// bounds hold under physics warp up to that coarser frame quantum. The harness
    /// sampling verifier reads these runs to know which recorded gaps were taken under
    /// physics warp and at which rate, so it can hold them to the same bounds as 1x
    /// gaps with the right one-frame allowance.</para>
    ///
    /// <para>Diagnostic only: nothing here is persisted and nothing gates recording.</para>
    /// </summary>
    internal static class SectionWarpRuns
    {
        internal const string KindNormal = "1x";
        internal const string KindPhysics = "phys";
        internal const string KindRails = "rails";

        // A rate within this of 1.0 is 1x (the TimeWarp lerp settles on exact rungs,
        // but a float compare against 1.0 would misread a 1.00001 settle as warp).
        private const float NormalRateEpsilon = 0.001f;

        /// <summary>
        /// Encodes one frame's warp state as a single float: 1 for 1x, the live rate
        /// (&gt; 1) for physics warp, and -1 for rails warp or an on-rails sample.
        /// </summary>
        internal static float EncodeFrameRate(bool onRails, bool physicsWarpMode, float currentRate)
        {
            if (onRails)
                return -1f;
            if (float.IsNaN(currentRate) || float.IsInfinity(currentRate)
                || currentRate <= 1f + NormalRateEpsilon)
                return 1f;
            return physicsWarpMode ? currentRate : -1f;
        }

        internal static string ClassifyEncodedRate(float encodedRate)
        {
            if (encodedRate < 0f)
                return KindRails;
            if (encodedRate > 1f + NormalRateEpsilon)
                return KindPhysics;
            return KindNormal;
        }

        internal struct Run
        {
            public string Kind;
            public int Count;
            public double FirstUT;
            public double LastUT;
            public float MaxRate;
        }

        /// <summary>
        /// Folds one newly committed frame into the run list in place: extends the last
        /// run when the kind matches, else opens a new run. O(1), no per-frame storage.
        /// </summary>
        internal static void Append(List<Run> runs, double ut, float encodedRate)
        {
            if (runs == null)
                return;
            string kind = ClassifyEncodedRate(encodedRate);
            float rate = encodedRate < 0f ? 0f : encodedRate;
            int last = runs.Count - 1;
            if (last >= 0 && runs[last].Kind == kind)
            {
                Run r = runs[last];
                r.Count++;
                r.LastUT = ut;
                if (rate > r.MaxRate)
                    r.MaxRate = rate;
                runs[last] = r;
                return;
            }
            runs.Add(new Run { Kind = kind, Count = 1, FirstUT = ut, LastUT = ut, MaxRate = rate });
        }

        /// <summary>
        /// Re-fits the runs to a frame list that was truncated from the end (the
        /// recorder's trim-to-UT path): drops runs that now start after the last kept
        /// frame, clamps the last run's end, and recounts each run's frames from the
        /// kept list (a frame belongs to the run whose UT span holds it). A run's
        /// MaxRate stays as recorded (an upper bound over its kept frames).
        /// </summary>
        internal static void TrimToFrames(List<Run> runs, IList<TrajectoryPoint> frames)
        {
            if (runs == null)
                return;
            if (frames == null || frames.Count == 0)
            {
                runs.Clear();
                return;
            }
            double lastUT = frames[frames.Count - 1].ut;
            for (int i = runs.Count - 1; i >= 0; i--)
            {
                if (runs[i].FirstUT > lastUT)
                    runs.RemoveAt(i);
            }
            int f = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                Run r = runs[i];
                if (r.LastUT > lastUT)
                    r.LastUT = lastUT;
                int n = 0;
                while (f < frames.Count && frames[f].ut <= r.LastUT)
                {
                    if (frames[f].ut >= r.FirstUT)
                        n++;
                    f++;
                }
                r.Count = n;
                runs[i] = r;
            }
        }

        /// <summary>
        /// Folds index-aligned frame UTs and encoded rates into contiguous runs of the
        /// same kind. Returns an empty list when the inputs are missing or not aligned
        /// (a misaligned list would attribute rates to the wrong frames, so it reports
        /// nothing rather than something wrong).
        /// </summary>
        internal static List<Run> Build(IList<TrajectoryPoint> frames, IList<float> encodedRates)
        {
            var runs = new List<Run>();
            if (frames == null || encodedRates == null || frames.Count != encodedRates.Count)
                return runs;
            for (int i = 0; i < frames.Count; i++)
                Append(runs, frames[i].ut, encodedRates[i]);
            return runs;
        }

        /// <summary>
        /// One-token rendering for the section-close log line, e.g.
        /// <c>1x:52:83.140-142.460,phys:23:142.500-212.180:max=2.000</c>. "-" when empty.
        /// Invariant culture: the harness parses it.
        /// </summary>
        internal static string Format(IList<Run> runs)
        {
            if (runs == null || runs.Count == 0)
                return "-";
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            for (int i = 0; i < runs.Count; i++)
            {
                Run r = runs[i];
                if (i > 0)
                    sb.Append(',');
                sb.Append(r.Kind).Append(':')
                    .Append(r.Count.ToString(ic)).Append(':')
                    .Append(r.FirstUT.ToString("F3", ic)).Append('-')
                    .Append(r.LastUT.ToString("F3", ic));
                if (r.Kind == KindPhysics)
                    sb.Append(":max=").Append(r.MaxRate.ToString("F3", ic));
            }
            return sb.ToString();
        }

        /// <summary>Count of frames in physics-warp runs.</summary>
        internal static int PhysicsFrameCount(IList<Run> runs)
        {
            int n = 0;
            if (runs == null)
                return 0;
            foreach (Run r in runs)
                if (r.Kind == KindPhysics)
                    n += r.Count;
            return n;
        }
    }
}
