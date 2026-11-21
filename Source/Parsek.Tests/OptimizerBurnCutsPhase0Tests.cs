using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Parsek.Reaim;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 0 of the version-3 burn cuts (atmosphere-pass-analysis.md section 8, todo
    /// REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS): the re-aim classifier needs the parking
    /// orbit, the coast around the Sun and the arrival in ONE recording, and the departure /
    /// capture cuts sit on those seams. Every committed fixture save is loaded headlessly and
    /// optimized under version 2, under version 3 and under version 3 with the interplanetary
    /// guard lifted (the guarded-out cuts applied by hand: the orbit segments partitioned at
    /// the cut, as SplitAtSection does). Each resulting recording is classified; a tree that
    /// had a Supported member under version 2 must keep one under version 3 with the same
    /// loiter cuts. The table is printed (run with a detailed console logger to read it).
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerBurnCutsPhase0Tests : IDisposable
    {
        private readonly ITestOutputHelper output;

        public OptimizerBurnCutsPhase0Tests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            OptimizerRepeatPassSweepTests.Reset();
        }

        public void Dispose()
        {
            OptimizerRepeatPassSweepTests.Reset();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>The stock system as the re-aim seam: parents, GM, SOI and radius from the
        /// optimizer's stock table, planet periods from KSP 1.12.</summary>
        internal sealed class StockBodies : IBodyInfo
        {
            private static readonly Dictionary<string, double> Periods = new Dictionary<string, double>
            {
                { "Moho", 2215754.2 }, { "Eve", 5657995.2 }, { "Kerbin", 9203545.0 },
                { "Duna", 17315400.0 }, { "Dres", 47893063.0 }, { "Jool", 104661432.0 },
                { "Eeloo", 156992048.0 }, { "Mun", 138984.38 }, { "Minmus", 1077310.5 },
                { "Gilly", 388587.42 }, { "Ike", 65517.862 }, { "Laythe", 52980.879 },
                { "Vall", 105962.09 }, { "Tylo", 211926.36 }, { "Bop", 544507.43 }, { "Pol", 901902.62 },
            };

            private static RecordingOptimizer.BodyFacts F(string b) => RecordingOptimizer.StockBodyFacts(b);
            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => Periods.TryGetValue(b ?? "", out double p) ? p : double.NaN;
            public string ReferenceBodyName(string b) => F(b).Known ? F(b).ParentName : null;
            public double SoiRadius(string b) => F(b).Known ? F(b).SoiRadius : double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b) => F(b).Known ? F(b).GravParameter : double.NaN;
            public double Radius(string b) => F(b).Known ? F(b).Radius : double.NaN;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }

        private sealed class MemberVerdict
        {
            internal string Tree;
            internal string Rows;
            internal string Supported; // "<member start>:<launch>-><target> cuts=<loiter cuts>" or null
            internal string SupportedKey; // the same without the member starts
            internal int GuardedOut;
        }

        private static string Verdict(List<OrbitSegment> segs, IBodyInfo bodies, out bool supported)
        {
            supported = false;
            if (segs == null || segs.Count == 0) return null;
            var sorted = new List<OrbitSegment>(segs);
            sorted.Sort((a, b) => a.startUT.CompareTo(b.startUT));
            ReaimMissionPlan plan = ReaimClassifier.Classify(sorted, bodies);
            if (!plan.Supported) return null;
            supported = true;
            int cuts = ReaimLoiterCompressor.ComputeCuts(sorted, bodies.GravParameter).Count;
            return string.Format(CultureInfo.InvariantCulture, "{0}->{1} cuts={2}",
                plan.LaunchBody, plan.TargetBody, cuts);
        }

        /// <summary>Per tree: rows (start:role), the Supported members' verdicts.</summary>
        private static Dictionary<string, MemberVerdict> Measure(
            string saveDir, int version, bool liftGuard, IBodyInfo bodies)
        {
            OptimizerRepeatPassSweepTests.Reset();
            var result = new Dictionary<string, MemberVerdict>(StringComparer.Ordinal);
            if (OptimizerRepeatPassSweepTests.LoadFixture(saveDir) == 0) return result;
            RecordingOptimizer.ForceSplitRuleVersionForTesting(RecordingStore.CommittedRecordings.ToList(), version);
            CommittedListOptimizer.RunOptimizationPass();

            foreach (var group in RecordingStore.CommittedRecordings
                .GroupBy(r => r.TreeId ?? "")
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var v = new MemberVerdict { Tree = group.Key };
                var rows = new StringBuilder();
                var supported = new List<string>();
                var keys = new List<string>();
                foreach (var rec in group.OrderBy(r => r.StartUT))
                {
                    rows.Append(string.Format(CultureInfo.InvariantCulture, "{0:F0}:{1} ",
                        rec.StartUT, rec.SegmentRole ?? rec.SegmentPhase ?? "-"));
                    foreach (var part in PartsAfterLiftedGuard(rec, liftGuard, v))
                    {
                        string verdict = Verdict(part.segs, bodies, out bool ok);
                        if (!ok) continue;
                        supported.Add(string.Format(CultureInfo.InvariantCulture, "{0:F0}:{1}", part.start, verdict));
                        keys.Add(verdict);
                    }
                }
                v.Rows = rows.ToString().Trim();
                v.Supported = supported.Count > 0 ? string.Join(" ; ", supported) : null;
                v.SupportedKey = keys.Count > 0 ? string.Join(" ; ", keys) : null;
                result[group.Key] = v;
            }
            return result;
        }

        /// <summary>The recording's orbit segments, cut at every guarded-out interplanetary
        /// kind cut when <paramref name="liftGuard"/> (and counting the kind cuts).</summary>
        private static List<(double start, List<OrbitSegment> segs)> PartsAfterLiftedGuard(
            Recording rec, bool liftGuard, MemberVerdict v)
        {
            var cutUTs = new List<double>();
            if (RecordingOptimizer.UsesBurnCutRules(rec) && rec.TrackSections != null && rec.TrackSections.Count > 1)
            {
                var map = RecordingOptimizer.BuildPassMap(rec);
                var burns = RecordingOptimizer.BuildBurnMap(rec, map, RecordingOptimizer.BurnStartContext.None);
                foreach (var d in burns.Burns)
                {
                    if (d.Kind == RecordingOptimizer.BurnCutKind.None) continue;
                    if (d.Reason == RecordingOptimizer.SplitBoundaryReason.SuppressedInterplanetaryTransfer)
                    {
                        v.GuardedOut++;
                        if (liftGuard && d.CutSection > 0) cutUTs.Add(rec.TrackSections[d.CutSection].startUT);
                    }
                }
            }
            var parts = new List<(double, List<OrbitSegment>)>();
            double start = rec.StartUT;
            var remaining = rec.OrbitSegments?.ToList() ?? new List<OrbitSegment>();
            foreach (double cut in cutUTs.OrderBy(u => u))
            {
                parts.Add((start, remaining.Where(s => s.startUT < cut).ToList()));
                remaining = remaining.Where(s => s.startUT >= cut).ToList();
                start = cut;
            }
            parts.Add((start, remaining));
            return parts;
        }

        [Fact]
        public void Phase0_EveryTransferThatClassifiesUnderVersionTwo_StillClassifiesUnderVersionThree()
        {
            var bodies = new StockBodies();
            var lines = new List<string>();
            // PARSEK_PHASE0_LOG=<file>: also write the optimizer's log lines of the version-3 runs.
            string logPath = Environment.GetEnvironmentVariable("PARSEK_PHASE0_LOG");
            var optimizerLog = new List<string>();
            if (!string.IsNullOrEmpty(logPath))
            {
                ParsekLog.TestSinkForTesting = line => { if (line.Contains("[Optimizer]") || line.Contains("Split recording")) optimizerLog.Add(line); };
                ParsekLog.VerboseOverrideForTesting = true;
            }
            var regressions = new List<string>();
            var unguardedRegressions = new List<string>();
            int fixtures = 0;
            foreach (string saveDir in OptimizerRepeatPassSweepTests.FixtureSaveDirs())
            {
                var v2 = Measure(saveDir, RecordingOptimizer.SplitRuleVersionPassRules, false, bodies);
                if (v2.Count == 0) continue;
                fixtures++;
                optimizerLog.Add("=== " + Path.GetFileName(saveDir));
                var v3 = Measure(saveDir, RecordingOptimizer.SplitRuleVersionBurnCuts, false, bodies);
                var lifted = Measure(saveDir, RecordingOptimizer.SplitRuleVersionBurnCuts, true, bodies);
                string fixture = Path.GetFileName(saveDir);
                foreach (var kv in v2)
                {
                    v3.TryGetValue(kv.Key, out var g);
                    lifted.TryGetValue(kv.Key, out var u);
                    bool changed = g != null && g.Rows != kv.Value.Rows;
                    bool reaim = kv.Value.Supported != null || g?.Supported != null || u?.Supported != null;
                    if (!changed && !reaim) continue;
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0} tree={1}\n  v2 rows: {2}\n  v3 rows: {3}\n  re-aim v2: {4} | v3: {5} | v3 unguarded: {6} (guardedOut={7})",
                        fixture, kv.Key.Length > 8 ? kv.Key.Substring(0, 8) : kv.Key,
                        kv.Value.Rows, g?.Rows ?? "-",
                        kv.Value.Supported ?? "faithful", g?.Supported ?? "faithful",
                        u?.Supported ?? "faithful", g?.GuardedOut ?? 0));
                    if (kv.Value.SupportedKey != null && g?.SupportedKey != kv.Value.SupportedKey)
                        regressions.Add(fixture + "/" + kv.Key);
                    if (kv.Value.SupportedKey != null && u?.SupportedKey != kv.Value.SupportedKey)
                        unguardedRegressions.Add(fixture + "/" + kv.Key);
                }
            }
            output.WriteLine("Phase 0 over " + fixtures + " fixture saves");
            foreach (var l in lines) output.WriteLine(l);
            output.WriteLine("unguarded re-aim regressions: " + (unguardedRegressions.Count == 0
                ? "none" : string.Join(", ", unguardedRegressions)));
            if (!string.IsNullOrEmpty(logPath)) File.WriteAllLines(logPath, optimizerLog);
            string tablePath = Environment.GetEnvironmentVariable("PARSEK_PHASE0_TABLE");
            if (!string.IsNullOrEmpty(tablePath))
                File.WriteAllText(tablePath, string.Join("\n", lines) + "\nunguarded regressions: "
                    + string.Join(", ", unguardedRegressions) + "\n");

            Assert.True(fixtures >= 50, "the measurement must cover the fixture inventory, covered " + fixtures);
            Assert.True(regressions.Count == 0,
                "re-aim must survive the version-3 rows: " + string.Join(", ", regressions));
        }
    }
}
