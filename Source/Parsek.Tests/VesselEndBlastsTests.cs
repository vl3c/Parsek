using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Fix Y7 (design-part-ghost-parity.md G.8 item 5, "Y7 as built" under G.7): a Destroyed
    /// ending explodes like stock's crash, one blast per part still on the ghost, merged as
    /// FXMonger merges same-frame blasts.
    /// </summary>
    public class VesselEndBlastsTests
    {
        private const int StockPrefabs = 7;

        private static Vector3d V(double x, double y = 0, double z = 0) => new Vector3d(x, y, z);

        #region FXMonger merge

        [Fact]
        public void FloorPower_RaisesBelowMinPower()
        {
            Assert.Equal(0.001, FxMongerBlastMerge.FloorPower(0.0));
            Assert.Equal(0.001, FxMongerBlastMerge.FloorPower(-3.0));
            Assert.Equal(0.5, FxMongerBlastMerge.FloorPower(0.5));
            Assert.Equal(0.001, FxMongerBlastMerge.FloorPower(0.001));
        }

        [Fact]
        public void MergeTarget_IsTheFirstQueuedBlastStrictlyInside10m()
        {
            var queued = new List<Vector3d> { V(0), V(4) };
            Assert.Equal(0, FxMongerBlastMerge.FindMergeTarget(queued, V(5)));      // both in range: the first
            Assert.Equal(1, FxMongerBlastMerge.FindMergeTarget(queued, V(13.9)));   // 13.9 from 0, 9.9 from 4
            Assert.Equal(-1, FxMongerBlastMerge.FindMergeTarget(queued, V(14)));    // exactly 10 m: not below
            Assert.Equal(-1, FxMongerBlastMerge.FindMergeTarget(new List<Vector3d>(), V(0)));
            Assert.Equal(-1, FxMongerBlastMerge.FindMergeTarget(null, V(0)));
        }

        [Fact]
        public void MergedPosition_IsPowerWeighted_AndTheMidpointAtZeroSum()
        {
            Vector3d m = FxMongerBlastMerge.MergedPosition(V(0), 0.5, V(6), 0.25);
            Assert.Equal(2.0, m.x, 9);
            Vector3d mid = FxMongerBlastMerge.MergedPosition(V(0), 0.0, V(6), 0.0);
            Assert.Equal(3.0, mid.x, 9);
        }

        [Fact]
        public void Plan_TwoMergedBlastsKeepTheFirstPower()
        {
            // X0 U1: two 0.7 blasts 5 m apart in one frame draw one Explosion 4 at the midpoint.
            List<FxMongerPlannedBlast> plan = FxMongerBlastMerge.Plan(
                new[] { V(0), V(5) }, new[] { 0.7, 0.7 });
            Assert.Single(plan);
            Assert.Equal(0.7, plan[0].Power);
            Assert.Equal(2.5, plan[0].Position.x, 9);
            Assert.Equal(2, plan[0].Sources);
            Assert.Equal(4, StockExplosionPrefabCache.ResolvePrefabIndex(plan[0].Power, StockPrefabs));
        }

        [Fact]
        public void Plan_12mApartStaysTwoBlasts()
        {
            List<FxMongerPlannedBlast> plan = FxMongerBlastMerge.Plan(
                new[] { V(0), V(12) }, new[] { 0.5, 0.5 });
            Assert.Equal(2, plan.Count);
        }

        [Fact]
        public void Plan_ComparesWithTheMovedPositionAndNeverMergesQueuedBlastsTogether()
        {
            // 0 and 12 queue apart; 6 merges into the first (moves it to 3); 14 is 11 from 3 and 2
            // from 12, so it merges into the second; the two queued blasts (3 and 13) stay two.
            List<FxMongerPlannedBlast> plan = FxMongerBlastMerge.Plan(
                new[] { V(0), V(12), V(6), V(14) }, new[] { 0.5, 0.5, 0.5, 0.5 });
            Assert.Equal(2, plan.Count);
            Assert.Equal(3.0, plan[0].Position.x, 9);
            Assert.Equal(13.0, plan[1].Position.x, 9);
            Assert.Equal(2, plan[0].Sources);
            Assert.Equal(2, plan[1].Sources);
        }

        [Fact]
        public void Plan_FloorsPowerBeforeTheMergeWeight()
        {
            List<FxMongerPlannedBlast> plan = FxMongerBlastMerge.Plan(new[] { V(0), V(1) }, new[] { 0.0, 0.0 });
            Assert.Single(plan);
            Assert.Equal(0.001, plan[0].Power);
            Assert.Equal(0.5, plan[0].Position.x, 9);
        }

        [Fact]
        public void Plan_NullOrMismatchedInputs()
        {
            Assert.Empty(FxMongerBlastMerge.Plan(null, new[] { 1.0 }));
            Assert.Single(FxMongerBlastMerge.Plan(new[] { V(0), V(50) }, new[] { 1.0 }));
        }

        [Fact]
        public void FrameQueue_MergesWithinAFrameOnly()
        {
            var q = new FxMongerFrameQueue<string>();
            Assert.Equal(-1, q.Offer(5, V(0), 0.5, out Vector3d at, out string into));
            Assert.Null(into);
            Assert.Equal(V(0).x, at.x);
            q.Add(5, V(0), 0.5, "a");
            Assert.Equal(0, q.Offer(5, V(6), 0.25, out at, out into));
            Assert.Equal("a", into);
            Assert.Equal(2.0, at.x, 9);
            // The merged blast is compared at its new position.
            Assert.Equal(0, q.Offer(5, V(11.5), 0.5, out at, out into));
            // Another frame: the queue starts empty.
            Assert.Equal(-1, q.Offer(6, V(1), 0.5, out at, out into));
            Assert.Equal(0, q.Count);
        }

        [Fact]
        public void FrameQueue_MatchesThePlan()
        {
            Vector3d[] pos = { V(0), V(12), V(6), V(14), V(40) };
            double[] pow = { 0.5, 0.1, 0.0, 0.5, 0.75 };
            List<FxMongerPlannedBlast> plan = FxMongerBlastMerge.Plan(pos, pow);
            var q = new FxMongerFrameQueue<string>();
            var spawned = new List<double>();
            for (int i = 0; i < pos.Length; i++)
            {
                if (q.Offer(1, pos[i], pow[i], out _, out _) < 0)
                {
                    q.Add(1, pos[i], pow[i], "b" + i);
                    spawned.Add(FxMongerBlastMerge.FloorPower(pow[i]));
                }
            }
            Assert.Equal(plan.Count, spawned.Count);
            for (int i = 0; i < plan.Count; i++)
                Assert.Equal(plan[i].Power, spawned[i]);
        }

        [Fact]
        public void CopyPath_MergesAndRecordsOnce()
        {
            string root = SyntheticRecordingTests.ResolveProjectRoot();
            string cache = PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                root, "Source", "Parsek", "Ghost", "StockExplosionPrefabCache.cs")));
            string spawn = PartParityStateRulesTests.MethodBody(cache, "internal static GameObject TrySpawn(");
            Assert.True(spawn != null, "TrySpawn moved; this cell is vacuous");
            Assert.True(spawn.IndexOf("frameBlasts.Offer(", StringComparison.Ordinal)
                < spawn.IndexOf("ResolvePrefabIndex(", StringComparison.Ordinal));
            Assert.Contains("frameBlasts.Add(frameNumber, worldPosition, power, blast);", spawn);
            string fx = PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                root, "Source", "Parsek", "Ghost", "GhostVisualBuilder.TransientFx.cs")));
            string fallback = PartParityStateRulesTests.MethodBody(fx, "internal static GameObject SpawnFallbackExplosionFx(");
            Assert.Contains("if (!merged)", fallback);
        }

        [Fact]
        public void CopyCache_CatchBlocksDestroyWhatTheyMade()
        {
            string cache = PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "Ghost", "StockExplosionPrefabCache.cs")));
            string capture = PartParityStateRulesTests.MethodBody(cache, "internal static void CaptureFrom(");
            string spawn = PartParityStateRulesTests.MethodBody(cache, "internal static GameObject TrySpawn(");
            Assert.True(capture != null && spawn != null, "the cache moved; this cell is vacuous");
            string captureCatch = capture.Substring(capture.LastIndexOf("catch (Exception", StringComparison.Ordinal));
            Assert.Contains("DestroyQuietly(keep,", captureCatch);
            Assert.True(capture.IndexOf("GameObject keep = null;", StringComparison.Ordinal)
                < capture.IndexOf("try", StringComparison.Ordinal));
            string spawnCatch = spawn.Substring(spawn.LastIndexOf("catch (Exception", StringComparison.Ordinal));
            Assert.Contains("DestroyQuietly(blast,", spawnCatch);
            Assert.True(spawn.IndexOf("GameObject blast = null;", StringComparison.Ordinal)
                < spawn.IndexOf("blast = UnityEngine.Object.Instantiate(", StringComparison.Ordinal));
        }

        #endregion

        #region Part collection

        private static ConfigNode Snapshot(params (string name, uint pid)[] parts)
        {
            var s = new ConfigNode("VESSEL");
            foreach (var p in parts)
            {
                ConfigNode n = s.AddNode("PART");
                n.AddValue("name", p.name);
                n.AddValue("persistentId", p.pid.ToString(CultureInfo.InvariantCulture));
            }
            return s;
        }

        private static float? Potentials(string name)
        {
            switch (name)
            {
                case "probe": return 0.5f;
                case "panel": return 0.1f;
                case "strut": return 0f;
                default: return null;
            }
        }

        [Fact]
        public void Collect_TakesPresentPartsInSnapshotOrderWithStocksPower()
        {
            ConfigNode snap = Snapshot(("probe", 11), ("panel_4290000001", 22), ("strut", 33), ("unknownMod", 44), ("probe", 55));
            var present = new HashSet<uint> { 55, 11, 33, 44 };   // 22 was decoupled earlier
            var pids = new List<uint>();
            var pos = new List<Vector3d>();
            var pow = new List<double>();
            int skipped = GhostPlaybackLogic.CollectVesselEndBlastParts(snap, present,
                pid => pid == 44 ? (Vector3d?)null : V(pid), Potentials, 100.0, pids, pos, pow);

            Assert.Equal(1, skipped);                       // 44 has no position (hidden)
            Assert.Equal(new uint[] { 11, 33, 55 }, pids);
            Assert.Equal(new[] { 0.5, 0.0, 0.5 }, pow);
            Assert.Equal(33.0, pos[1].x);
        }

        [Fact]
        public void Collect_AddsTheSpeedTermFrom2kmps()
        {
            ConfigNode snap = Snapshot(("probe", 11), ("panel", 22), ("unknownMod", 33));
            var pids = new List<uint>();
            var pos = new List<Vector3d>();
            var pow = new List<double>();
            GhostPlaybackLogic.CollectVesselEndBlastParts(snap, new HashSet<uint> { 11, 22, 33 },
                pid => V(pid), Potentials, 2100.0, pids, pos, pow);
            Assert.Equal(0.75, pow[0]);
            Assert.Equal((double)(0.1f + 0.25f), pow[1]);
            Assert.Equal(0.75, pow[2]);                     // unknown part: stock's default 0.5
        }

        [Fact]
        public void Collect_NothingToExplode()
        {
            var pids = new List<uint>();
            var pos = new List<Vector3d>();
            var pow = new List<double>();
            Assert.Equal(0, GhostPlaybackLogic.CollectVesselEndBlastParts(null, new HashSet<uint> { 1 },
                pid => V(1), Potentials, 0, pids, pos, pow));
            Assert.Equal(0, GhostPlaybackLogic.CollectVesselEndBlastParts(Snapshot(("probe", 1)), new HashSet<uint>(),
                pid => V(1), Potentials, 0, pids, pos, pow));
            Assert.Equal(0, GhostPlaybackLogic.CollectVesselEndBlastParts(Snapshot(("probe", 1)), null,
                pid => V(1), Potentials, 0, pids, pos, pow));
            Assert.Empty(pids);
        }

        [Fact]
        public void Collect_ReadsEachPidOnce()
        {
            ConfigNode snap = Snapshot(("probe", 7), ("probe", 7));
            var pids = new List<uint>();
            GhostPlaybackLogic.CollectVesselEndBlastParts(snap, new HashSet<uint> { 7 },
                pid => V(1), Potentials, 0, pids, new List<Vector3d>(), new List<double>());
            Assert.Single(pids);
        }

        #endregion

        #region What stock would draw (the Y7 table)

        private static List<FxMongerPlannedBlast> CrashOf(int parts, double spacingM, double speed)
        {
            var names = new List<(string, uint)>();
            var present = new HashSet<uint>();
            for (int i = 0; i < parts; i++)
            {
                names.Add(("probe", (uint)(i + 1)));
                present.Add((uint)(i + 1));
            }
            var pids = new List<uint>();
            var pos = new List<Vector3d>();
            var pow = new List<double>();
            GhostPlaybackLogic.CollectVesselEndBlastParts(Snapshot(names.ToArray()), present,
                pid => V(0, (pid - 1) * spacingM), Potentials, speed, pids, pos, pow);
            return FxMongerBlastMerge.Plan(pos, pow);
        }

        [Fact]
        public void Crash_OnePartProbe_OneExplosion2()
        {
            List<FxMongerPlannedBlast> plan = CrashOf(1, 2, 80);
            Assert.Single(plan);
            Assert.Equal(3, StockExplosionPrefabCache.ResolvePrefabIndex(plan[0].Power, StockPrefabs));
        }

        [Fact]
        public void Crash_ThreePartsSixMetres_OneMergedExplosion2()
        {
            List<FxMongerPlannedBlast> plan = CrashOf(3, 2, 80);
            Assert.Single(plan);
            Assert.Equal(3, plan[0].Sources);
            Assert.Equal(3, StockExplosionPrefabCache.ResolvePrefabIndex(plan[0].Power, StockPrefabs));
        }

        [Fact]
        public void Crash_TwentyPartsThirtyMetres_OneChasingExplosion2()
        {
            // Each merge moves the blast halfway to the new part (equal powers), so along a 1.5 m
            // spaced stack it trails the newest part by about 1.5 m and swallows all twenty.
            List<FxMongerPlannedBlast> plan = CrashOf(20, 1.5, 80);
            Assert.Single(plan);
            Assert.Equal(20, plan[0].Sources);
            Assert.Equal(3, StockExplosionPrefabCache.ResolvePrefabIndex(plan[0].Power, StockPrefabs));
        }

        [Fact]
        public void Crash_PartsSpreadWide_OneBlastEach()
        {
            List<FxMongerPlannedBlast> plan = CrashOf(4, 15, 80);
            Assert.Equal(4, plan.Count);
        }

        [Fact]
        public void Crash_At2kmps_EachBlastIsExplosion4()
        {
            foreach (FxMongerPlannedBlast b in CrashOf(3, 2, 2300))
                Assert.Equal(4, StockExplosionPrefabCache.ResolvePrefabIndex(b.Power, StockPrefabs));
        }

        #endregion

        [Fact]
        public void Format_IsGrepStableAndInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var plan = new List<FxMongerPlannedBlast>
                {
                    new FxMongerPlannedBlast { Position = V(0), Power = 0.5, Sources = 3 },
                    new FxMongerPlannedBlast { Position = V(20), Power = 0.001, Sources = 1 },
                };
                Assert.Equal(
                    "parts=4 skipped=1 srfSpeed=82.4 speedSource=recorded speedOffset=0 route=fxmonger blasts=2 " +
                    "[0] power=0.5 prefabIndex=3 sources=3 [1] power=0.001 prefabIndex=0 sources=1",
                    GhostPlaybackLogic.FormatVesselEndBlastPlan(4, 1, 82.41, "recorded", "fxmonger", plan));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Format_CapsTheBlastList()
        {
            var plan = new List<FxMongerPlannedBlast>();
            for (int i = 0; i < GhostPlaybackLogic.VesselEndBlastLogLimit + 3; i++)
                plan.Add(new FxMongerPlannedBlast { Position = V(i * 20), Power = 0.5, Sources = 1 });
            string s = GhostPlaybackLogic.FormatVesselEndBlastPlan(23, 0, 0, null, "copy-paused", plan);
            Assert.EndsWith(" ... 3 more", s);
            Assert.DoesNotContain("[20]", s);
        }

        [Fact]
        public void TheDestroyedEnding_PlaysThePerPartBlastsFirst()
        {
            string text = PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "Ghost",
                "GhostPlaybackEngine.SpawnDestroy.cs")));
            string body = PartParityStateRulesTests.MethodBody(text, "private void PlayDestroyedExplosion(");
            Assert.True(body != null, "PlayDestroyedExplosion moved; this cell is vacuous");
            int perPart = body.IndexOf("GhostPlaybackLogic.TryPlayVesselEndBlasts(", StringComparison.Ordinal);
            int single = body.IndexOf("double power = TerminalExplosionPower(vesselLength);", StringComparison.Ordinal);
            Assert.True(perPart >= 0 && single > perPart);
            // The anchor is written to the root before the parts are read.
            Assert.True(body.IndexOf("state.ghost.transform.position = worldPos;", StringComparison.Ordinal) < perPart);
        }

        [Fact]
        public void LoudestPower_IsTheStrongestPlannedBlastClamped()
        {
            Assert.Equal(0.0, GhostPlaybackLogic.VesselEndBlastLoudestPower(null));
            Assert.Equal(0.0, GhostPlaybackLogic.VesselEndBlastLoudestPower(new List<FxMongerPlannedBlast>()));
            var plan = new List<FxMongerPlannedBlast>
            {
                new FxMongerPlannedBlast { Power = 0.5, Sources = 3 },
                new FxMongerPlannedBlast { Power = double.NaN, Sources = 1 },
                new FxMongerPlannedBlast { Power = 0.75, Sources = 1 },
                new FxMongerPlannedBlast { Power = 0.1, Sources = 1 },
            };
            Assert.Equal(0.75, GhostPlaybackLogic.VesselEndBlastLoudestPower(plan));
            plan.Add(new FxMongerPlannedBlast { Power = 1.4, Sources = 1 });
            Assert.Equal(1.0, GhostPlaybackLogic.VesselEndBlastLoudestPower(plan));
        }

        [Fact]
        public void TheSpaceCenterEnding_PlaysThePerPartBlastsFirst()
        {
            string text = PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "Controllers", "ParsekKSC.cs")));
            string body = PartParityStateRulesTests.MethodBody(text, "void TriggerExplosionIfDestroyed(");
            Assert.True(body != null, "TriggerExplosionIfDestroyed moved; this cell is vacuous");
            int perPart = body.IndexOf(
                "GhostPlaybackLogic.TryPlayVesselEndBlasts(state, GhostVisualBuilder.GetGhostSnapshot(rec),",
                StringComparison.Ordinal);
            int live = body.IndexOf("else if (GhostVisualBuilder.IsFxMongerLive())", StringComparison.Ordinal);
            Assert.True(perPart >= 0 && live > perPart);
            string perPartCall = body.Substring(perPart, live - perPart);
            // Same pause rule as flight, and the Space Center's own one-shot follows the result.
            Assert.Contains("vesselLength, pauseMenuOpen, context, out bool fxMongerQueued, out double loudestPower)",
                perPartCall);
            Assert.Contains("power = loudestPower;", perPartCall);
            Assert.Contains("stockResult = GhostPlaybackLogic.StockExplosionFxResult.StockQueued;", perPartCall);
            Assert.DoesNotContain("VesselLengthPowerDivisor", perPartCall);
            Assert.True(body.IndexOf("ShouldQueueKscExplicitExplosionAudio(pauseMenuOpen, stockResult)",
                StringComparison.Ordinal) > live);
        }

        [Fact]
        public void TheManualPreviewEnding_PlaysThePerPartBlastsFirst()
        {
            string text = PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "Controllers",
                "ParsekFlightPlaybackHost.cs")));
            string body = PartParityStateRulesTests.MethodBody(text, "internal void UpdatePlayback(");
            Assert.True(body != null, "UpdatePlayback moved; this cell is vacuous");
            int perPart = body.IndexOf("GhostPlaybackLogic.TryPlayVesselEndBlasts(previewGhostState,",
                StringComparison.Ordinal);
            int single = body.IndexOf("double power = Mathf.Clamp01(len / GhostAudioPresets.VesselLengthPowerDivisor);",
                StringComparison.Ordinal);
            Assert.True(perPart >= 0 && single > perPart);
        }
    }
}
