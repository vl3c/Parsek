using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static Parsek.Tests.OptimizerSplitShapes;

namespace Parsek.Tests
{
    /// <summary>
    /// The version-2 split rules (RecordingOptimizer.SplitRules.cs): atmosphere passes (owner
    /// rulings Q7, A1, B1; every shape of docs/handoff/recording-split-review/
    /// atmosphere-pass-analysis.md section 3), one row across an SOI change in space (Q9), a
    /// brief touchdown as a graze (Q8), a mission split in one pass (Q19), the rule-version
    /// gate (Q5) and the row role tag. Each shape runs the real optimization pass twice; the
    /// second pass must change nothing (the merge pass runs before the split pass, so an
    /// unstable role would re-glue and re-cut on every load).
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerSplitRulesV2Tests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OptimizerSplitRulesV2Tests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ------------------------------------------------------------ harness

        private static Recording V2(Recording rec)
        {
            rec.SplitRuleVersion = RecordingOptimizer.SplitRuleVersionPassRules;
            return rec;
        }

        private static Recording V1(Recording rec)
        {
            rec.SplitRuleVersion = RecordingOptimizer.SplitRuleVersionLegacy;
            return rec;
        }

        private static string Snapshot(string treeId)
        {
            return string.Join(" | ", ChainOf(treeId).Select(r =>
                $"{r.RecordingId}:{r.StartUT:R}-{r.EndUT:R}:{r.SegmentPhase}:{r.SegmentRole}:{r.ChainIndex}"));
        }

        /// <summary>Commits, runs the pass twice, asserts the second pass changed nothing, and
        /// returns the rows' (start UT, role) in chain order.</summary>
        private List<(double start, string role)> Optimize(Recording rec)
        {
            CommitAlone(rec);
            CommittedListOptimizer.RunOptimizationPass();
            string first = Snapshot(rec.TreeId);
            CommittedListOptimizer.RunOptimizationPass();
            Assert.Equal(first, Snapshot(rec.TreeId));
            return ChainOf(rec.TreeId).Select(r => (r.StartUT, r.SegmentRole)).ToList();
        }

        private static double[] Starts(List<(double start, string role)> rows)
            => rows.Select(r => r.start).ToArray();

        private static string[] Roles(List<(double start, string role)> rows)
            => rows.Select(r => r.role).ToArray();

        // ------------------------------------------------------------ section 3 shapes

        [Fact]
        public void PlainReentry_SpaceReentrySurface()
        {
            var rows = Optimize(V2(Make("plain", 0, Exo(2000), Atmo(300), Ground(100))));
            Assert.Equal(new[] { 0.0, 2000.0, 2300.0 }, Starts(rows));
            Assert.Equal(new[] { "space", "reentry", "surface" }, Roles(rows));
            Assert.Contains(logLines, l => l.Contains("Split candidate (FinalDescentEntry)"));
        }

        [Fact]
        public void RowLabel_ReadsTheRole_AVersionOneRowKeepsItsPhase()
        {
            Optimize(V2(Make("label", 0, Exo(2000), Atmo(300), Ground(100))));
            var rows = ChainOf("tree-label");
            Assert.Equal("Kerbin reentry", RecordingSegmentLabels.GetSegmentPhaseLabel(rows[1], "Kerbin"));
            var legacy = new Recording { SegmentPhase = "atmo" };
            Assert.Equal("Kerbin atmo", RecordingSegmentLabels.GetSegmentPhaseLabel(legacy, "Kerbin"));
        }

        [Fact]
        public void SkipReentry_ShortHops_ReentryRowFromTheFirstDip()
        {
            var shape = new[] { Exo(2000), Atmo(100), Exo(60), Atmo(200), Ground(100) };
            var v2 = Optimize(V2(Make("skip-v2", 0, shape)));
            Assert.Equal(new[] { 0.0, 2000.0, 2360.0 }, Starts(v2));
            Assert.Equal(new[] { "space", "reentry", "surface" }, Roles(v2));
            Assert.Contains(logLines, l => l.Contains("Split summary") && l.Contains("passKept=2")
                && l.Contains("rules=v2"));

            // Version 1 (the pin): the dips are grazes of the orbit row.
            var v1 = Optimize(V1(Make("skip-v1", 0, shape)));
            Assert.Equal(new[] { 0.0, 2360.0 }, Starts(v1));
            Assert.All(v1, r => Assert.Null(r.role));
        }

        [Fact]
        public void SkipReentry_LongHop_BrakingRowThenReentry()
        {
            var rows = Optimize(V2(Make("skip-long", 0,
                Exo(2000), Atmo(100), Exo(300), Atmo(200), Ground(100))));
            Assert.Equal(new[] { 0.0, 2000.0, 2400.0, 2600.0 }, Starts(rows));
            Assert.Equal(new[] { "space", "aerobraking", "reentry", "surface" }, Roles(rows));
        }

        [Fact]
        public void AerobrakingCampaign_LongDips_OneRowFromTheFirstTouch()
        {
            var shape = new[] { Exo(2000), Atmo(150), Exo(3000), Atmo(150), Exo(3000), Atmo(150), Exo(5000) };
            var v2 = Optimize(V2(Make("campaign-v2", 0, shape)));
            Assert.Equal(new[] { 0.0, 2000.0 }, Starts(v2));
            Assert.Equal(new[] { "space", "aerobraking" }, Roles(v2));

            var v1 = Optimize(V1(Make("campaign-v1", 0, shape)));
            Assert.Equal(7, v1.Count); // every long pass its own pair of rows
        }

        [Fact]
        public void AerobrakingCampaign_ShortDips_FirstTouchIsCut()
        {
            var shape = new[] { Exo(2000), Atmo(60), Exo(3000), Atmo(60), Exo(3000) };
            var v2 = Optimize(V2(Make("short-dips-v2", 0, shape)));
            Assert.Equal(new[] { 0.0, 2000.0 }, Starts(v2));
            Assert.Equal(new[] { "space", "aerobraking" }, Roles(v2));

            var v1 = Optimize(V1(Make("short-dips-v1", 0, shape)));
            Assert.Single(v1); // invisible grazes under version 1
        }

        [Fact]
        public void AerobrakingCampaign_EndingInReentry()
        {
            var rows = Optimize(V2(Make("campaign-reentry", 0,
                Exo(2000), Atmo(150), Exo(3000), Atmo(150), Exo(3000), Atmo(300), Ground(100))));
            Assert.Equal(new[] { 0.0, 2000.0, 8300.0, 8600.0 }, Starts(rows));
            Assert.Equal(new[] { "space", "aerobraking", "reentry", "surface" }, Roles(rows));
        }

        [Fact]
        public void Aerocapture_ArrivalThenCaptureRow()
        {
            var rows = Optimize(V2(Make("aerocapture", 0, Exo(4000), Atmo(90), Exo(5000))));
            Assert.Equal(new[] { 0.0, 4000.0 }, Starts(rows));
            Assert.Equal(new[] { "space", "aerobraking" }, Roles(rows));
        }

        [Fact]
        public void DipWithTheEngineOn_SameRowsAsWithout()
        {
            var burning = Optimize(V2(Make("dip-burn", 0, Burn(200), Atmo(60), Exo(3000))));
            RecordingStore.ResetForTesting();
            var coasting = Optimize(V2(Make("dip-coast", 0, Exo(200), Atmo(60), Exo(3000))));
            Assert.Equal(Starts(coasting), Starts(burning));
            Assert.Equal(Roles(coasting), Roles(burning));
            Assert.Equal(new[] { 0.0, 200.0 }, Starts(burning));
        }

        [Fact]
        public void Ascent_Unchanged()
        {
            Func<string, Recording> launch = id =>
            {
                var rec = Make(id, 0, Pad(30), Atmo(300), Burn(100), Exo(600));
                rec.StartBodyName = "Kerbin";
                rec.LaunchSiteName = "Launch Pad";
                return rec;
            };
            var v2 = Optimize(V2(launch("ascent-v2")));
            RecordingStore.ResetForTesting();
            var v1 = Optimize(V1(launch("ascent-v1")));
            Assert.Equal(Starts(v1), Starts(v2));
            Assert.Equal(new[] { 0.0, 330.0 }, Starts(v2));
            Assert.Equal(new[] { "ascent", "space" }, Roles(v2));
        }

        [Fact]
        public void AirlessApproachBand_Unchanged()
        {
            var shape = new[]
            {
                Exo(3000, "Mun"), Approach(200), Ground(500, "Mun"), Approach(200), Exo(1000, "Mun"),
                Approach(60), Exo(1000, "Mun"),
            };
            var v2 = Optimize(V2(Make("mun-v2", 0, shape)));
            RecordingStore.ResetForTesting();
            var v1 = Optimize(V1(Make("mun-v1", 0, shape)));
            Assert.Equal(Starts(v1), Starts(v2));
            Assert.Equal(new[] { "space", "approach", "surface", "ascent", "space" }, Roles(v2));
        }

        [Fact]
        public void DebrisFollowsTheAtmospherePassRules()
        {
            var debris = V2(Make("booster", 0, Exo(200), Atmo(100), Exo(60), Atmo(200), Ground(30)));
            debris.IsDebris = true;
            var rows = Optimize(debris);
            Assert.Equal(new[] { "space", "reentry", "surface" }, Roles(rows));
        }

        [Fact]
        public void EvaKerbal_KeepsVersionOneRulesAndNoRole()
        {
            var shape = new[] { Exo(2000), Atmo(100), Exo(60), Atmo(200), Ground(100) };
            var eva = V2(Make("kerbal", 0, shape));
            eva.EvaCrewName = "Jebediah Kerman";
            var rows = Optimize(eva);
            RecordingStore.ResetForTesting();
            var evaV1 = V1(Make("kerbal-v1", 0, shape));
            evaV1.EvaCrewName = "Jebediah Kerman";
            var v1 = Optimize(evaV1);
            Assert.Equal(Starts(v1), Starts(rows));
            Assert.All(rows, r => Assert.Null(r.role));
        }

        // ------------------------------------------------------------ review follow-ups

        // B1: a takeoff with a short bounce (a brief touchdown inside the launch-site leading
        // run) keeps the pad time with the ascent, as version 1 does (ruling 5b).
        [Fact]
        public void LaunchWithAShortBounce_KeepsThePadWithTheAscent()
        {
            Func<string, Recording> launch = id =>
            {
                var rec = Make(id, 0, Pad(60), Atmo(3), Ground(8), Atmo(600), Exo(600));
                rec.StartBodyName = "Kerbin";
                rec.LaunchSiteName = "Launch Pad";
                return rec;
            };
            var v2 = Optimize(V2(launch("bounce-v2")));
            RecordingStore.ResetForTesting();
            var v1 = Optimize(V1(launch("bounce-v1")));
            Assert.Equal(Starts(v1), Starts(v2));
            Assert.Equal(new[] { 0.0, 671.0 }, Starts(v2));
        }

        private static void Append(Recording rec, params (SegmentEnvironment, double, string)[] sections)
        {
            var extra = Make("tmp", rec.TrackSections[rec.TrackSections.Count - 1].endUT, sections);
            rec.TrackSections.AddRange(extra.TrackSections);
            rec.Points.AddRange(extra.Points);
            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;
        }

        // B2: a committed row that grows (a resumed tip) and is cut again must not leave equal
        // roles on two halves the merge pass would glue and the split pass cut again on every
        // load: the second and third passes after the growth leave the same rows and ids.
        [Fact]
        public void GrownTip_CutAgain_NoMergeSplitPingPong()
        {
            var rec = V2(Make("grown", 0, Exo(2000), Atmo(100))); // ends in the air
            CommitAlone(rec);
            CommittedListOptimizer.RunOptimizationPass();
            var rows = ChainOf(rec.TreeId);
            Assert.Equal(new[] { "space", "reentry" }, rows.Select(r => r.SegmentRole).ToArray());

            Append(rows[1], Exo(3000), Atmo(300), Ground(100));
            CommittedListOptimizer.RunOptimizationPass();
            string afterGrowth = Snapshot(rec.TreeId);
            CommittedListOptimizer.RunOptimizationPass();
            Assert.Equal(afterGrowth, Snapshot(rec.TreeId));
            CommittedListOptimizer.RunOptimizationPass();
            Assert.Equal(afterGrowth, Snapshot(rec.TreeId));
            Assert.Equal(new[] { "space", "aerobraking", "reentry", "surface" },
                ChainOf(rec.TreeId).Select(r => r.SegmentRole).ToArray());
        }

        // B2, the merge side: two version-2 rows with equal role, phase and body whose join the
        // version-2 split predicate would cut again never merge.
        [Fact]
        public void CanAutoMerge_RefusesAJoinTheSplitRulesWouldCutAgain()
        {
            var a = V2(Make("j-a", 0, Atmo(100), Exo(3000)));
            var b = V2(Make("j-b", 3100, Atmo(300), Ground(100)));
            a.ChainId = b.ChainId = "chain-j";
            a.ChainIndex = 0;
            b.ChainIndex = 1;
            a.SegmentPhase = b.SegmentPhase = "atmo";
            a.SegmentBodyName = b.SegmentBodyName = "Kerbin";
            a.SegmentRole = b.SegmentRole = "reentry";

            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
            Assert.Contains(logLines, l => l.Contains("the split rules would cut the join again"));

            // Control: a join with nothing to cut still merges.
            var c = V2(Make("j-c", 0, Exo(100)));
            var d = V2(Make("j-d", 100, Exo(100)));
            c.ChainId = d.ChainId = "chain-k";
            c.ChainIndex = 0;
            d.ChainIndex = 1;
            c.SegmentPhase = d.SegmentPhase = "exo";
            c.SegmentBodyName = d.SegmentBodyName = "Kerbin";
            c.SegmentRole = d.SegmentRole = "space";
            Assert.True(RecordingOptimizer.CanAutoMerge(c, d));
        }

        // S1: an aerobraking row's "first touch" resets at a body change: a Duna aerocapture
        // after a Kerbin aerobrake opens its own row.
        [Fact]
        public void AerobrakingRow_ABrakingPassAtAnotherBody_OpensItsOwnRow()
        {
            var rows = Optimize(V2(Make("two-bodies", 0,
                Exo(2000, "Kerbin"), Atmo(150, "Kerbin"), Exo(3000, "Kerbin"),
                Exo(5000, "Duna"), Atmo(100, "Duna"), Exo(4000, "Duna"))));
            Assert.Equal(new[] { 0.0, 2000.0, 10150.0 }, Starts(rows));
            Assert.Equal(new[] { "space", "aerobraking", "aerobraking" }, Roles(rows));
        }

        // S2: the table's phase sort keeps reading the phase, not the role label.
        [Fact]
        public void PhaseSort_ReadsThePhaseNotTheRole()
        {
            var exoRow = new Recording { SegmentPhase = "exo", SegmentRole = "aerobraking", SegmentBodyName = "Kerbin" };
            var atmoRow = new Recording { SegmentPhase = "atmo", SegmentRole = "space", SegmentBodyName = "Kerbin" };
            int cmp = RecordingsTableUI.CompareRecordings(atmoRow, exoRow,
                RecordingsTableUI.SortColumn.Phase, ascending: true, now: 0);
            Assert.True(cmp < 0, "atmo sorts before exo by phase");
        }

        // ------------------------------------------------------------ Q8 / Q9

        [Fact]
        public void BriefTouchdown_IsAGraze_ARealStopStillSplits()
        {
            var touchAndGo = new[] { Atmo(300), Ground(10), Atmo(300), Ground(500) };
            var v2 = Optimize(V2(Make("touch-v2", 0, touchAndGo)));
            Assert.Equal(new[] { 0.0, 610.0 }, Starts(v2));
            Assert.Contains(logLines, l => l.Contains("touchdownKept=2"));

            var v1 = Optimize(V1(Make("touch-v1", 0, touchAndGo)));
            Assert.Equal(4, v1.Count);

            var realStop = Optimize(V2(Make("stop-v2", 0, Atmo(300), Ground(60), Atmo(300), Ground(500))));
            Assert.Equal(new[] { 0.0, 300.0, 360.0, 660.0 }, Starts(realStop));
            Assert.Equal(new[] { "atmosphere", "surface", "ascent", "surface" }, Roles(realStop));
        }

        [Fact]
        public void SoiChangeInSpace_OneRowBurningOrNot()
        {
            var shape = new[] { Exo(500, "Kerbin"), Burn(100, "Kerbin"), Burn(100, "Mun"), Exo(500, "Mun") };
            var v2 = Optimize(V2(Make("soi-v2", 0, shape)));
            Assert.Single(v2);

            var v1 = Optimize(V1(Make("soi-v1", 0, shape)));
            Assert.Equal(new[] { 0.0, 600.0 }, Starts(v1)); // version 1 cuts the powered crossing
        }

        // ------------------------------------------------------------ Q19

        [Fact]
        public void OnePass_SplitsAMissionPastTheOldFiftySplitCap()
        {
            var shape = new List<(SegmentEnvironment, double, string)>();
            for (int i = 0; i < 40; i++)
            {
                shape.Add(Ground(200));
                shape.Add(Atmo(300));
            }
            var rec = Make("hopper", 0, shape.ToArray()); // version 0: the version-1 rules
            CommitAlone(rec);

            CommittedListOptimizer.RunOptimizationPass();

            Assert.Equal(80, ChainOf(rec.TreeId).Count);
            Assert.Contains(logLines, l => l.Contains("Optimization pass: split 79 recording(s)"));
            Assert.DoesNotContain(logLines, l => l.Contains("hit split"));
        }

        [Fact]
        public void SplitSafetyBound_CountsSectionsAndRecordings()
        {
            Assert.Equal(50, CommittedListOptimizer.ComputeSplitSafetyBound(new List<Recording>()));
            var big = Make("big", 0, Enumerable.Range(0, 60).Select(i => Exo(10)).ToArray());
            Assert.Equal(61, CommittedListOptimizer.ComputeSplitSafetyBound(new List<Recording> { big }));
        }

        // ------------------------------------------------------------ Q5 gate and roles

        [Fact]
        public void CommitAppend_StampsTheCurrentRules_OnlyOnAnUnsetVersion()
        {
            var fresh = new Recording { RecordingId = "fresh" };
            var old = new Recording { RecordingId = "old", SplitRuleVersion = 1 };
            RecordingStore.AppendCommittedUnbumped(fresh);
            RecordingStore.AppendCommittedUnbumped(old);
            Assert.Equal(RecordingOptimizer.SplitRuleVersionCurrent, fresh.SplitRuleVersion);
            Assert.Equal(3, RecordingOptimizer.SplitRuleVersionCurrent);
            Assert.Equal(1, old.SplitRuleVersion);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("stamped version 3 on rec=fresh at tree commit"));
        }

        [Fact]
        public void InFlightTreeSave_StampsUnsetRecordings_KeepsAResumedOne()
        {
            var tree = new RecordingTree { Id = "live" };
            var fresh = new Recording { RecordingId = "fresh" };
            var resumed = new Recording { RecordingId = "resumed", SplitRuleVersion = 1 };
            tree.AddOrReplaceRecording(fresh);
            tree.AddOrReplaceRecording(resumed);

            Assert.Equal(1, RecordingOptimizer.StampSplitRulesForSave(tree, "active tree save"));
            Assert.Equal(RecordingOptimizer.SplitRuleVersionCurrent, fresh.SplitRuleVersion);
            Assert.Equal(1, resumed.SplitRuleVersion);
            Assert.Contains(logLines, l => l.Contains("on 1 recording(s) of tree live at active tree save"));
        }

        [Fact]
        public void ReFlyProvisional_IsStampedWithTheCurrentRules()
        {
            var provisional = new Recording { RecordingId = "prov", MergeState = MergeState.NotCommitted };
            RecordingStore.AddProvisional(provisional);
            Assert.Equal(RecordingOptimizer.SplitRuleVersionCurrent, provisional.SplitRuleVersion);
        }

        [Fact]
        public void Codec_VersionAndRole_RoundTrip_AbsentKeyIsVersionOne()
        {
            foreach (int version in new[] { 0, 1, 2, 3 })
            {
                var rec = new Recording
                {
                    RecordingId = "codec" + version,
                    SplitRuleVersion = version,
                    SegmentRole = version >= 2 ? (version == 3 ? "circularization" : "reentry") : null,
                };
                var node = new ConfigNode("RECORDING");
                RecordingTreeRecordCodec.SaveRecordingResourceAndState(node, rec);
                Assert.Equal(version >= 2, node.HasValue("splitRuleVersion"));
                var back = new Recording();
                RecordingTreeRecordCodec.LoadRecordingResourceAndState(node, back);
                Assert.Equal(version >= 2 ? version : 1, back.SplitRuleVersion);
                Assert.Equal(rec.SegmentRole, back.SegmentRole);
            }
            var legacy = new Recording();
            RecordingTreeRecordCodec.LoadRecordingResourceAndState(new ConfigNode("RECORDING"), legacy);
            Assert.Equal(1, legacy.SplitRuleVersion);
        }

        [Fact]
        public void DeepClone_CarriesVersionAndRole()
        {
            var rec = new Recording { SplitRuleVersion = 2, SegmentRole = "aerobraking" };
            var clone = Recording.DeepClone(rec);
            Assert.Equal(2, clone.SplitRuleVersion);
            Assert.Equal("aerobraking", clone.SegmentRole);
        }

        private static (Recording a, Recording b) MergeablePair()
        {
            var a = Make("m-a", 0, Exo(100));
            var b = Make("m-b", 100, Exo(100));
            a.ChainId = b.ChainId = "chain-m";
            a.ChainIndex = 0;
            b.ChainIndex = 1;
            a.SegmentPhase = b.SegmentPhase = "exo";
            a.SegmentBodyName = b.SegmentBodyName = "Kerbin";
            return (a, b);
        }

        [Fact]
        public void CanAutoMerge_RefusesMixedVersionsAndDifferentRoles()
        {
            var (a, b) = MergeablePair();
            Assert.True(RecordingOptimizer.CanAutoMerge(a, b)); // control (0 and 0 = version 1)

            b.SplitRuleVersion = 2;
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
            Assert.Contains(logLines, l => l.Contains("split rules v1/v2"));

            a.SplitRuleVersion = 2;
            Assert.True(RecordingOptimizer.CanAutoMerge(a, b));
            a.SegmentRole = "aerobraking";
            b.SegmentRole = "reentry";
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
            b.SegmentRole = "aerobraking";
            Assert.True(RecordingOptimizer.CanAutoMerge(a, b));

            // An unset version counts as version 1.
            var (c, d) = MergeablePair();
            c.SplitRuleVersion = 1;
            Assert.True(RecordingOptimizer.CanAutoMerge(c, d));
        }

        [Fact]
        public void SplitHalves_CarryTheVersion()
        {
            var original = new Recording { RecordingId = "o", SplitRuleVersion = 2 };
            var second = new Recording();
            CommittedListOptimizer.CopySplitIdentityFields(original, second);
            Assert.Equal(2, second.SplitRuleVersion);
        }

        [Fact]
        public void ForceHook_JudgesAFixtureByTheCurrentRules()
        {
            var rec = Make("forced", 0, Exo(2000), Atmo(100), Exo(60), Atmo(200), Ground(100));
            RecordingOptimizer.ForceSplitRuleVersionForTesting(new[] { rec }, 2);
            Assert.True(RecordingOptimizer.UsesCurrentSplitRules(rec));
            var rows = Optimize(rec);
            Assert.Equal(3, rows.Count);
        }

        [Fact]
        public void VersionOne_PredicateIsUnchangedForEveryBoundary()
        {
            // Pin: version 0 and version 1 answer exactly what the version-1 body answers.
            var shape = new[] { Exo(2000), Atmo(100), Exo(60), Atmo(200), Ground(10), Atmo(300), Ground(100) };
            var unset = Make("pin0", 0, shape);
            var legacy = V1(Make("pin1", 0, shape));
            for (int s = 1; s < unset.TrackSections.Count; s++)
            {
                RecordingOptimizer.SplitBoundaryReason r0, r1;
                bool s0 = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(unset, s, out r0);
                bool s1 = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(legacy, s, out r1);
                Assert.Equal(s0, s1);
                Assert.Equal(r0, r1);
                Assert.True(r0 < RecordingOptimizer.SplitBoundaryReason.FinalDescentEntry,
                    $"boundary {s} used a version-2 reason {r0}");
            }
        }

        // ------------------------------------------------------------ forward gate (BATCH-B3)

        private static Recording Newer(Recording rec)
        {
            rec.SplitRuleVersion = RecordingOptimizer.SplitRuleVersionCurrent + 1;
            return rec;
        }

        // A recording a newer build cut under rules this build does not know is held verbatim:
        // the pass neither splits it (the version-2 predicate would cut this shape twice) nor
        // trims it, and the codec writes its version back.
        // MUTATION NOTE: dropping the newer-version skip in FindSplitCandidatesForOptimizer reds this.
        [Fact]
        public void NewerSplitRuleVersion_IsNeverSplit_AndRoundTrips()
        {
            var rec = Newer(Make("newer", 0, Exo(2000), Atmo(300), Ground(100)));
            CommitAlone(rec);
            CommittedListOptimizer.RunOptimizationPass();
            var rows = ChainOf(rec.TreeId);
            Assert.Single(rows);
            Assert.Equal(0.0, rows[0].StartUT);
            Assert.Equal(2400.0, rows[0].EndUT);
            Assert.Equal(RecordingOptimizer.SplitRuleVersionCurrent + 1, rows[0].SplitRuleVersion);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("held 1 recording(s) whole: split rules newer than this build"));
            Assert.Equal("newer-split-rules",
                CommittedListOptimizer.DescribeTrimHold(rec, null, null, null));

            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingResourceAndState(node, rec);
            var back = new Recording();
            RecordingTreeRecordCodec.LoadRecordingResourceAndState(node, back);
            Assert.Equal(RecordingOptimizer.SplitRuleVersionCurrent + 1, back.SplitRuleVersion);
        }

        // The merge side: two rows of one chain cut under a newer version, with nothing else
        // keeping them apart, are never joined, and a covered newer piece is never folded.
        [Fact]
        public void NewerSplitRuleVersion_IsNeverMergedOrFolded()
        {
            var c = Newer(Make("n-c", 0, Exo(100)));
            var d = Newer(Make("n-d", 100, Exo(100)));
            c.ChainId = d.ChainId = "chain-n";
            c.ChainIndex = 0;
            d.ChainIndex = 1;
            c.SegmentPhase = d.SegmentPhase = "exo";
            c.SegmentBodyName = d.SegmentBodyName = "Kerbin";
            c.SegmentRole = d.SegmentRole = "space";
            Assert.False(RecordingOptimizer.CanAutoMerge(c, d));
            Assert.Contains(logLines, l => l.Contains("CanAutoMerge: rejecting merge of 'n-c' + 'n-d'")
                && l.Contains("newer than this build"));
            Assert.NotNull(CommittedListOptimizer.SplitPieceFoldRefusal(d, c));

            // Mirror: the same pair at the current version still merges.
            c.SplitRuleVersion = d.SplitRuleVersion = RecordingOptimizer.SplitRuleVersionCurrent;
            Assert.True(RecordingOptimizer.CanAutoMerge(c, d));
            Assert.Null(CommittedListOptimizer.DescribeTrimHold(c, null, null, null));
        }

        // ------------------------------------------------------------ row roles (BATCH-B6)

        // (a) A resumed reentry row that climbs back to orbit is no longer cut (its own pass
        // entry and a braking exit are never cut), so the role stamped at the old cut stayed
        // "reentry". The pass re-reads a pass role from the row's content.
        // MUTATION NOTE: removing the ReReadRowRoles call from RunOptimizationPass reds this.
        [Fact]
        public void ResumedReentryRow_ClimbsBackToOrbit_RoleReReadAsAerobraking()
        {
            var rec = V2(Make("climb", 0, Atmo(100), Exo(3000)));
            rec.SegmentRole = "reentry";
            var rows = Optimize(rec);
            Assert.Single(rows);
            Assert.Equal("aerobraking", rows[0].role);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("Row role re-read: 'climb' reentry -> aerobraking"));
        }

        // (b) An unsplit version-2 row gets the role of how it starts, so the labels of one
        // mission's rows never mix a phase ("exo") with roles ("space").
        [Fact]
        public void UnsplitVersionTwoRow_GetsItsStartRole_VersionOneRowStaysUnset()
        {
            var rows = Optimize(V2(Make("whole", 0, Exo(500))));
            Assert.Single(rows);
            Assert.Equal("space", rows[0].role);

            RecordingStore.ResetForTesting();
            var v1 = Optimize(V1(Make("whole-v1", 0, Exo(500))));
            Assert.Null(v1[0].role);
        }

        // (c) Loopability still reads the phase, not the role: a re-read role leaves it alone.
        [Fact]
        public void RoleReRead_DoesNotChangeLoopability()
        {
            var rec = V2(Make("loopable", 0, Atmo(100), Exo(3000)));
            rec.SegmentRole = "reentry";
            rec.SegmentPhase = "exo";
            bool before = Recording.IsLoopableRecording(rec);
            Optimize(rec);
            Assert.Equal("aerobraking", rec.SegmentRole);
            Assert.Equal(before, Recording.IsLoopableRecording(rec));
        }

        // ------------------------------------------------------------ fixed point (BATCH-B7)

        // A split can leave a new piece next to one it may join (the cut's air half beside the
        // chain's next air piece): the merge phase ran before the split phase, so that join waited
        // for the next pass, a later load, and the ids changed after it. The pass now repeats
        // merge + split until nothing changes: running it twice equals running it once.
        // MUTATION NOTE: running the merge and split phases once in RunOptimizationPass reds this.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void SplitThenJoinableNeighbour_SettlesInOnePass(int version)
        {
            var head = Make("fp-x", 0, Exo(2000), Atmo(300));
            var next = Make("fp-y", 2300, Atmo(200));
            head.SplitRuleVersion = next.SplitRuleVersion = version;
            head.TreeId = next.TreeId = "tree-fp";
            head.ChainId = next.ChainId = "chain-fp";
            head.ChainIndex = 0;
            next.ChainIndex = 1;
            head.SegmentPhase = "exo";
            next.SegmentPhase = "atmo";
            head.SegmentBodyName = next.SegmentBodyName = "Kerbin";
            var tree = new RecordingTree { Id = "tree-fp", TreeName = "fp", RootRecordingId = head.RecordingId };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(next);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddRecordingWithTreeForTesting(head);
            RecordingStore.AddRecordingWithTreeForTesting(next);

            CommittedListOptimizer.RunOptimizationPass();
            string once = Snapshot("tree-fp");
            CommittedListOptimizer.RunOptimizationPass();
            Assert.Equal(once, Snapshot("tree-fp"));
            // Version 1 joins the air half to the next air piece in the same pass; version 2
            // keeps the reentry row apart from the uncut air piece (different roles).
            Assert.Equal(version == 1 ? 2 : 3, ChainOf("tree-fp").Count);
        }
    }
}
