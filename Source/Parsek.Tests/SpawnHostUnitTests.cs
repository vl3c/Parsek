using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization and unit tests for ParsekFlightSpawnHost: the headless-reachable instance
    /// paths (ghost-chain evaluation with nothing to chain, the tree-leaf spawn guards, the warp /
    /// fast-forward / time-jump refusals, the next-candidate warp with no candidate, the drawn-ghost
    /// lookup and the spawn-warp watch guards) and the statics.
    /// </summary>
    [Collection("Sequential")]
    public class SpawnHostUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnHostUnitTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // --- builders ---

        private static ParsekFlight Flight()
        {
            var flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            // Field initializers do not run on an uninitialized object.
            SetField(flight, "nearbySpawnCandidates", new List<NearbySpawnCandidate>());
            SetField(flight, "notifiedSpawnRecordingIds", new HashSet<string>());
            SetField(flight, "proximityVelocitySamples",
                new Dictionary<string, ParsekFlight.ProximityVelocitySample>());
            return flight;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo f = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(f);
            f.SetValue(target, value);
        }

        private static RecordingTree Tree(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "tree-1", TreeName = "Tree One" };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.Recordings[r.RecordingId] = r;
            }
            if (recs.Length > 0)
            {
                tree.RootRecordingId = recs[0].RecordingId;
                tree.ActiveRecordingId = recs[0].RecordingId;
            }
            return tree;
        }

        // ================= characterization: instance paths =================

        [Fact]
        public void GhostChains_NoCommittedTrees_ClearsTheActiveSetAndCounts()
        {
            var flight = Flight();
            flight.activeGhostChains = new Dictionary<uint, GhostChain>();

            flight.SpawnHost.EvaluateAndApplyGhostChains();

            Assert.Null(flight.activeGhostChains);
            Assert.Equal(1, flight.GhostChainEvaluationCount);
            Assert.Contains(logLines, l => l.Contains("[Ghoster]")
                && l.Contains("EvaluateAndApplyGhostChains: no committed trees"));
        }

        [Fact]
        public void SpawnTreeLeaves_NoSpawnableLeaf_LogsAndReturns()
        {
            var flight = Flight();
            var destroyed = new Recording { RecordingId = "r1", VesselSnapshot = new ConfigNode("VESSEL") };
            destroyed.StampTerminalState(TerminalState.Destroyed, "test");
            var noSnapshot = new Recording { RecordingId = "r2" };

            flight.SpawnHost.SpawnTreeLeaves(Tree(destroyed, noSnapshot), "r1");

            Assert.Contains(logLines, l => l.Contains("[Flight]") && l.Contains("SpawnTreeLeaves: no spawnable leaves"));
        }

        [Fact]
        public void SpawnTreeLeaves_TheActiveLeafIsSkipped()
        {
            var flight = Flight();
            var active = new Recording { RecordingId = "act", VesselName = "Active", VesselSnapshot = new ConfigNode("VESSEL") };

            flight.SpawnHost.SpawnTreeLeaves(Tree(active), "act");

            Assert.Contains(logLines, l => l.Contains("SpawnTreeLeaves: 1 spawnable leaf/leaves"));
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]"));
            Assert.Equal(0u, active.SpawnedVesselPersistentId);
        }

        [Fact]
        public void WarpToRecordingEnd_IndexOutOfRange_Aborts()
        {
            var flight = Flight();
            flight.SpawnHost.WarpToRecordingEnd(-1);
            flight.SpawnHost.WarpToRecordingEnd(0);

            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]") && l.Contains("WarpToRecordingEnd: invalid index -1"));
            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]") && l.Contains("WarpToRecordingEnd: invalid index 0"));
        }

        [Fact]
        public void WarpToDeparture_IndexOutOfRange_Aborts()
        {
            var flight = Flight();
            flight.SpawnHost.WarpToDeparture(3, 100);
            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]") && l.Contains("WarpToDeparture: invalid index 3"));
            Assert.DoesNotContain(logLines, l => l.Contains("WarpToDeparture: jumping"));
        }


        [Fact]
        public void FastForwardToRecording_NullRecording_Aborts()
        {
            Flight().SpawnHost.FastForwardToRecording(null);
            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]")
                && l.Contains("FastForwardToRecording: null recording"));
        }

        [UnityEntryFact]
        public void DrawnGhostWorldPosition_NoIdOrNoEngine_IsNull()
        {
            var flight = Flight();
            Assert.Null(flight.SpawnHost.TryGetDrawnGhostWorldPosition(null));
            Assert.Null(flight.SpawnHost.TryGetDrawnGhostWorldPosition(""));
            Assert.Null(flight.SpawnHost.TryGetDrawnGhostWorldPosition("r1"));
        }

        [UnityEntryFact]
        public void SpawnWarpWatch_NothingArmed_IsANoOp()
        {
            Flight().SpawnHost.TickSpawnWarpWatch();
            Assert.DoesNotContain(logLines, l => l.Contains("Warp spawn"));
        }

        // ================= statics =================

        [Fact]
        public void LiveVesselGuid_UnknownPid_IsNull()
        {
            Assert.Null(ParsekFlightSpawnHost.LiveVesselGuidOrNull(424242));
        }

        [Fact]
        public void MoveGateLog_ReturnsTheVerdictAndLogsOnlyOnChange()
        {
            var rec = new Recording { RecordingId = "mg-1" };
            var refused = new MoveGateVerdict { Refusal = MoveRefusal.Atmosphere, Detail = "alt=1" };

            MoveGateVerdict back = ParsekFlightSpawnHost.LogMoveGateOnChange(rec, refused);
            Assert.Equal(MoveRefusal.Atmosphere, back.Refusal);
            Assert.Equal("alt=1", back.Detail);
            Assert.Single(logLines, l => l.Contains("Move gate: ref=mg-1 refusal=Atmosphere detail=alt=1"));

            ParsekFlightSpawnHost.LogMoveGateOnChange(rec, refused);
            Assert.Single(logLines, l => l.Contains("Move gate: ref=mg-1"));

            ParsekFlightSpawnHost.LogMoveGateOnChange(rec, MoveGateVerdict.Pass);
            Assert.Contains(logLines, l => l.Contains("Move gate: ref=mg-1 refusal=None") && !l.Contains("detail="));
        }

        // ================= extracted helpers =================

        [Fact]
        public void ChainTipGhostMapVessel_OpenOrbitingOrDockedOnly()
        {
            Assert.False(ParsekFlightSpawnHost.ShouldCreateGhostMapVesselForChainTip(null));
            Assert.True(ParsekFlightSpawnHost.ShouldCreateGhostMapVesselForChainTip(new Recording { RecordingId = "open" }));
            foreach (TerminalState ts in Enum.GetValues(typeof(TerminalState)))
            {
                var rec = new Recording { RecordingId = "t" };
                rec.StampTerminalState(ts, "test");
                bool expected = ts == TerminalState.Orbiting || ts == TerminalState.Docked;
                Assert.True(expected == ParsekFlightSpawnHost.ShouldCreateGhostMapVesselForChainTip(rec), ts.ToString());
            }
        }

        [Fact]
        public void KeptVesselReasonTally_CountsTheTwoNamedPrefixesOnly()
        {
            int preExisting = 0, differentLaunch = 0;
            ParsekFlightSpawnHost.TallyKeptVesselReason("pre-existing in the revert target", ref preExisting, ref differentLaunch);
            ParsekFlightSpawnHost.TallyKeptVesselReason("no same-launch recording", ref preExisting, ref differentLaunch);
            ParsekFlightSpawnHost.TallyKeptVesselReason("no same-launch again", ref preExisting, ref differentLaunch);
            ParsekFlightSpawnHost.TallyKeptVesselReason("prelaunch skipped", ref preExisting, ref differentLaunch);
            ParsekFlightSpawnHost.TallyKeptVesselReason(null, ref preExisting, ref differentLaunch);
            ParsekFlightSpawnHost.TallyKeptVesselReason("Pre-existing", ref preExisting, ref differentLaunch);
            Assert.Equal(1, preExisting);
            Assert.Equal(2, differentLaunch);
        }

        [Fact]
        public void SameNamedSpawnOfOtherVessel_NeedsALiveSpawnAndTheExactName()
        {
            var spawned = new Recording { RecordingId = "r", VesselName = "Lander", VesselSpawned = true, SpawnedVesselPersistentId = 5 };
            Assert.True(ParsekFlightSpawnHost.IsSameNamedSpawnOfOtherVessel(spawned, "Lander"));
            Assert.False(ParsekFlightSpawnHost.IsSameNamedSpawnOfOtherVessel(spawned, "lander"));
            Assert.False(ParsekFlightSpawnHost.IsSameNamedSpawnOfOtherVessel(spawned, ""));
            Assert.False(ParsekFlightSpawnHost.IsSameNamedSpawnOfOtherVessel(spawned, null));
            Assert.False(ParsekFlightSpawnHost.IsSameNamedSpawnOfOtherVessel(null, "Lander"));
            Assert.False(ParsekFlightSpawnHost.IsSameNamedSpawnOfOtherVessel(
                new Recording { VesselName = "Lander", VesselSpawned = false, SpawnedVesselPersistentId = 5 }, "Lander"));
            Assert.False(ParsekFlightSpawnHost.IsSameNamedSpawnOfOtherVessel(
                new Recording { VesselName = "Lander", VesselSpawned = true, SpawnedVesselPersistentId = 0 }, "Lander"));
        }

        [Fact]
        public void FindCommittedRecordingById_FirstNonNullOrdinalMatch()
        {
            var a = new Recording { RecordingId = "a" };
            var b1 = new Recording { RecordingId = "b" };
            var b2 = new Recording { RecordingId = "b" };
            var list = new List<Recording> { null, a, b1, b2 };
            Assert.Same(b1, ParsekFlightSpawnHost.FindCommittedRecordingById(list, "b"));
            Assert.Same(a, ParsekFlightSpawnHost.FindCommittedRecordingById(list, "a"));
            Assert.Null(ParsekFlightSpawnHost.FindCommittedRecordingById(list, "A"));
            Assert.Null(ParsekFlightSpawnHost.FindCommittedRecordingById(new List<Recording>(), "a"));
        }

        [Fact]
        public void IndexOfCommittedRecordingId_FirstMatchElseMinusOne()
        {
            var list = new List<Recording>
            {
                new Recording { RecordingId = "a" }, new Recording { RecordingId = "b" }, new Recording { RecordingId = "b" },
            };
            Assert.Equal(1, ParsekFlightSpawnHost.IndexOfCommittedRecordingId(list, "b"));
            Assert.Equal(-1, ParsekFlightSpawnHost.IndexOfCommittedRecordingId(list, "c"));
            // Every entry is read: a null entry before the match throws, as the inline loop did.
            Assert.Throws<NullReferenceException>(() => ParsekFlightSpawnHost.IndexOfCommittedRecordingId(
                new List<Recording> { null, new Recording { RecordingId = "a" } }, "a"));
        }

        [Fact]
        public void ProximitySamplePrune_DropsUnseenOnlyWhenTheCacheIsLarger()
        {
            var samples = new Dictionary<string, ParsekFlight.ProximityVelocitySample>
            {
                ["a"] = default(ParsekFlight.ProximityVelocitySample),
                ["b"] = default(ParsekFlight.ProximityVelocitySample),
                ["c"] = default(ParsekFlight.ProximityVelocitySample),
            };
            ParsekFlightSpawnHost.PruneStaleProximitySamples(samples, new HashSet<string> { "b" });
            Assert.Equal(new[] { "b" }, new List<string>(samples.Keys).ToArray());

            // Same count: nothing pruned, even when the ids differ.
            var even = new Dictionary<string, ParsekFlight.ProximityVelocitySample>
            {
                ["x"] = default(ParsekFlight.ProximityVelocitySample),
            };
            ParsekFlightSpawnHost.PruneStaleProximitySamples(even, new HashSet<string> { "y" });
            Assert.True(even.ContainsKey("x"));
        }

        [Fact]
        public void ProximityWarpGates_DistanceOrSpeedOutside()
        {
            var inside = new NearbySpawnCandidate { distance = ParsekFlight.NearbySpawnRadius, relativeSpeed = ParsekFlight.MaxRelativeSpeed };
            Assert.False(ParsekFlightSpawnHost.IsOutsideProximityWarpGates(inside));
            var far = inside; far.distance = ParsekFlight.NearbySpawnRadius + 1;
            Assert.True(ParsekFlightSpawnHost.IsOutsideProximityWarpGates(far));
            var fast = inside; fast.relativeSpeed = ParsekFlight.MaxRelativeSpeed + 0.01;
            Assert.True(ParsekFlightSpawnHost.IsOutsideProximityWarpGates(fast));
            var unsampled = inside; unsampled.relativeSpeed = double.PositiveInfinity;
            Assert.True(ParsekFlightSpawnHost.IsOutsideProximityWarpGates(unsampled));
        }
    }
}
