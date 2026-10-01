using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// D18-CHAIN-SPAWN-BLOCKED-GHOST-6B4-NOOP: a ghost-chain tip whose spawn is blocked by a
    /// collision keeps its playback ghost past the tip UT (design 12.9.2 / 13.5, phase 6b-4).
    /// Pure cells for the pose source and release decisions, the policy's timeout exemption,
    /// and real-policy cells that drive RetryHeldGhostSpawns end to end headless.
    /// </summary>
    [Collection("Sequential")]
    public class ChainTipBlockedGhostTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainTipBlockedGhostTests()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
        }

        // ---- pose source ----

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void ResolvePoseSource_FollowsTheOrbitOnlyWhenTheSpawnUsesIt(
            bool spawnUsesOrbit, bool orbitBuilt, bool expectOrbit)
        {
            BlockedChainTipPoseSource expected = expectOrbit
                ? BlockedChainTipPoseSource.RecordedTerminalOrbit
                : BlockedChainTipPoseSource.BodyFixedHold;
            Assert.Equal(expected, ChainTipBlockedGhost.ResolvePoseSource(spawnUsesOrbit, orbitBuilt));
        }

        // ---- release decision ----

        [Fact]
        public void DecideReleaseReason_AllConditionsHold_KeepsTheGhost()
        {
            Assert.Null(ChainTipBlockedGhost.DecideReleaseReason(
                tipSpawned: false, chainActive: true, chainSpawnBlocked: true,
                indexStillTip: true, heldByPolicy: true, engineGhostIntact: true));
        }

        [Fact]
        public void DecideReleaseReason_SpawnedWinsOverEveryOtherCondition()
        {
            // A spawn removes the chain, clears the block and ends the hold in one frame;
            // the reason must still say the vessel spawned, so two copies can never coexist
            // unnoticed.
            Assert.Equal(ChainTipBlockedGhost.ReleaseSpawned,
                ChainTipBlockedGhost.DecideReleaseReason(
                    tipSpawned: true, chainActive: false, chainSpawnBlocked: false,
                    indexStillTip: true, heldByPolicy: false, engineGhostIntact: false));
        }

        [Theory]
        [InlineData(false, true, true, true, true, ChainTipBlockedGhost.ReleaseChainClosed)]
        [InlineData(true, false, true, true, true, ChainTipBlockedGhost.ReleaseUnblocked)]
        [InlineData(true, true, false, true, true, ChainTipBlockedGhost.ReleaseIndexShifted)]
        [InlineData(true, true, true, false, true, ChainTipBlockedGhost.ReleaseHoldEnded)]
        [InlineData(true, true, true, true, false, ChainTipBlockedGhost.ReleaseGhostGone)]
        public void DecideReleaseReason_EachLostConditionNamesItsReason(
            bool chainActive, bool chainSpawnBlocked, bool indexStillTip,
            bool heldByPolicy, bool engineGhostIntact, string expected)
        {
            Assert.Equal(expected, ChainTipBlockedGhost.DecideReleaseReason(
                tipSpawned: false, chainActive: chainActive, chainSpawnBlocked: chainSpawnBlocked,
                indexStillTip: indexStillTip, heldByPolicy: heldByPolicy,
                engineGhostIntact: engineGhostIntact));
        }

        [Fact]
        public void DecideReleaseReason_ChainClosedOutranksTheLaterChecks()
        {
            Assert.Equal(ChainTipBlockedGhost.ReleaseChainClosed,
                ChainTipBlockedGhost.DecideReleaseReason(
                    tipSpawned: false, chainActive: false, chainSpawnBlocked: false,
                    indexStillTip: false, heldByPolicy: false, engineGhostIntact: false));
        }

        // ---- capture decision ----

        [Fact]
        public void ShouldCapture_HeldBlockedUnspawnedTipWithAGhost_Captures()
        {
            Assert.True(ChainTipBlockedGhost.ShouldCapture(
                alreadyCaptured: false, tipSpawned: false, chainActive: true,
                chainSpawnBlocked: true, heldByPolicy: true, engineHasGhost: true));
        }

        [Theory]
        [InlineData(true, false, true, true, true, true)]   // already captured
        [InlineData(false, true, true, true, true, true)]   // tip spawned
        [InlineData(false, false, false, true, true, true)] // chain gone
        [InlineData(false, false, true, false, true, true)] // spawn failed, not blocked
        [InlineData(false, false, true, true, false, true)] // policy not holding
        [InlineData(false, false, true, true, true, false)] // no ghost to draw
        public void ShouldCapture_RefusesWhenAnyConditionFails(
            bool alreadyCaptured, bool tipSpawned, bool chainActive,
            bool chainSpawnBlocked, bool heldByPolicy, bool engineHasGhost)
        {
            Assert.False(ChainTipBlockedGhost.ShouldCapture(
                alreadyCaptured, tipSpawned, chainActive, chainSpawnBlocked,
                heldByPolicy, engineHasGhost));
        }

        // ---- messages ----

        [Fact]
        public void BuildHeldMessage_IsInvariantAndCarriesTheIds()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string msg = ChainTipBlockedGhost.BuildHeldMessage(
                    7, "rec-tip", "Station", 3620499050u,
                    BlockedChainTipPoseSource.RecordedTerminalOrbit, "Kerbin", 8960.26, 8951.5);
                Assert.Equal(
                    "Blocked chain tip ghost held: #7 \"Station\" rec=rec-tip chainPid=3620499050 " +
                    "source=orbit body=Kerbin blockedSince=8951.5 UT=8960.3 - the ghost follows " +
                    "the tip until the spawn clears",
                    msg);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void BuildReleasedMessage_IsInvariantAndCarriesReasonAndDuration()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string msg = ChainTipBlockedGhost.BuildReleasedMessage(
                    7, "rec-tip", "Station", 42u, ChainTipBlockedGhost.ReleaseSpawned, 100.0, 112.5);
                Assert.Equal(
                    "Blocked chain tip ghost released: #7 \"Station\" rec=rec-tip chainPid=42 " +
                    "reason=spawned heldSince=100.0 UT=112.5 heldFor=12.5s",
                    msg);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void BuildHeldMessage_NullBody_SaysNone_AndHoldSource()
        {
            string msg = ChainTipBlockedGhost.BuildHeldMessage(
                0, null, null, 1u, BlockedChainTipPoseSource.BodyFixedHold, null, 1.0, 0.0);
            Assert.Contains("source=hold body=(none)", msg);
            Assert.Contains("rec=(none)", msg);
        }

        // ---- policy timeout exemption (pure) ----

        private static Recording MakeRecording(string id, bool spawned = false)
        {
            return new Recording { RecordingId = id, VesselName = "Station", VesselSpawned = spawned };
        }

        [Fact]
        public void DecideHeldGhostAction_BlockedChainTipPastTimeout_KeepsRetrying()
        {
            var committed = new List<Recording> { MakeRecording("rec-tip") };
            var info = new HeldGhostInfo { holdStartTime = 0f, lastRetryTime = 10f, recordingId = "rec-tip" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 60f, timeoutSeconds: 5f,
                retryIntervalSeconds: 1f, spawnBlockedChainTip: true);

            Assert.Equal(HeldGhostAction.RetrySpawn, action);
        }

        [Fact]
        public void DecideHeldGhostAction_BlockedChainTipInsideRetryInterval_Holds()
        {
            var committed = new List<Recording> { MakeRecording("rec-tip") };
            var info = new HeldGhostInfo { holdStartTime = 0f, lastRetryTime = 59.5f, recordingId = "rec-tip" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 60f, timeoutSeconds: 5f,
                retryIntervalSeconds: 1f, spawnBlockedChainTip: true);

            Assert.Equal(HeldGhostAction.Hold, action);
        }

        [Fact]
        public void DecideHeldGhostAction_NotABlockedChainTip_StillTimesOut()
        {
            var committed = new List<Recording> { MakeRecording("rec-tip") };
            var info = new HeldGhostInfo { holdStartTime = 0f, lastRetryTime = 10f, recordingId = "rec-tip" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 60f, timeoutSeconds: 5f,
                retryIntervalSeconds: 1f, spawnBlockedChainTip: false);

            Assert.Equal(HeldGhostAction.Timeout, action);
        }

        [Fact]
        public void DecideHeldGhostAction_BlockedChainTipSpawned_ReleasesAtOnce()
        {
            var committed = new List<Recording> { MakeRecording("rec-tip", spawned: true) };
            var info = new HeldGhostInfo { holdStartTime = 0f, lastRetryTime = 59.9f, recordingId = "rec-tip" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 60f, timeoutSeconds: 5f,
                retryIntervalSeconds: 1f, spawnBlockedChainTip: true);

            Assert.Equal(HeldGhostAction.ReleaseSpawned, action);
        }

        [Fact]
        public void DecideHeldGhostAction_BlockedChainTipSuperseded_Releases()
        {
            var committed = new List<Recording> { MakeRecording("rec-tip") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-tip" };
            var inactive = new Dictionary<string, TimelineInactiveReason>
            {
                { "rec-tip", TimelineInactiveReason.SupersededByRelation },
            };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 60f, timeoutSeconds: 5f,
                retryIntervalSeconds: 1f, timelineInactiveIds: inactive, spawnBlockedChainTip: true);

            Assert.Equal(HeldGhostAction.ReleaseSupersededByRelation, action);
        }

        [Fact]
        public void DecideHeldGhostAction_BlockedChainTipCannotSpawnSafely_StillReleases()
        {
            // The 2026-09-27 ruling: a refusal that can never clear is not held. The exemption
            // covers only the collision block, which can clear.
            var rec = MakeRecording("rec-tip");
            rec.TerminalSpawnCannotSpawnSafely = true;
            rec.TerminalSpawnSafetyReasonCode = TerminalOrbitSpawnSafety.ReasonTerminalOrbitResolutionFailed;
            var committed = new List<Recording> { rec };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-tip" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 60f, timeoutSeconds: 5f,
                retryIntervalSeconds: 1f, spawnBlockedChainTip: true);

            Assert.Equal(HeldGhostAction.ReleaseCannotSpawnSafely, action);
        }

        [Fact]
        public void IsExemptFromHeldGhostTimeout_OnlyForABlockedChainTip()
        {
            Assert.True(ChainTipBlockedGhost.IsExemptFromHeldGhostTimeout(true));
            Assert.False(ChainTipBlockedGhost.IsExemptFromHeldGhostTimeout(false));
        }

        // ---- host predicate ----

        private static ParsekFlight MakeHost(Dictionary<uint, GhostChain> chains)
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            typeof(ParsekFlight)
                .GetField("activeGhostChains", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, chains);
            return host;
        }

        private static GhostChain MakeChain(string tipId, bool blocked, bool terminated = false)
        {
            return new GhostChain
            {
                OriginalVesselPid = 4242u,
                TipRecordingId = tipId,
                SpawnBlocked = blocked,
                IsTerminated = terminated,
            };
        }

        [Fact]
        public void IsSpawnBlockedChainTipFromPolicy_BlockedTip_True()
        {
            var host = MakeHost(new Dictionary<uint, GhostChain> { { 4242u, MakeChain("rec-tip", blocked: true) } });
            Assert.True(host.IsSpawnBlockedChainTipFromPolicy(MakeRecording("rec-tip")));
        }

        [Fact]
        public void IsSpawnBlockedChainTipFromPolicy_UnblockedTipOrOtherRecordingOrTerminated_False()
        {
            var unblocked = MakeHost(new Dictionary<uint, GhostChain> { { 4242u, MakeChain("rec-tip", blocked: false) } });
            Assert.False(unblocked.IsSpawnBlockedChainTipFromPolicy(MakeRecording("rec-tip")));

            var other = MakeHost(new Dictionary<uint, GhostChain> { { 4242u, MakeChain("rec-tip", blocked: true) } });
            Assert.False(other.IsSpawnBlockedChainTipFromPolicy(MakeRecording("rec-mid")));

            var terminated = MakeHost(new Dictionary<uint, GhostChain>
            {
                { 4242u, MakeChain("rec-tip", blocked: true, terminated: true) },
            });
            Assert.False(terminated.IsSpawnBlockedChainTipFromPolicy(MakeRecording("rec-tip")));

            Assert.False(MakeHost(null).IsSpawnBlockedChainTipFromPolicy(MakeRecording("rec-tip")));
            Assert.False(unblocked.IsSpawnBlockedChainTipFromPolicy(null));
        }

        // ---- quiet indefinite retry: the 1 s retry path's log lines are rate-limited ----

        private int Count(string needle)
        {
            int n = 0;
            for (int i = 0; i < logLines.Count; i++)
                if (logLines[i].Contains(needle)) n++;
            return n;
        }

        [Fact]
        public void RetryPathLogLines_AreRateLimitedOverManySimulatedRetries()
        {
            double clock = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => clock;
            var orbitRec = new Recording
            {
                RecordingId = "rec-tip-orbit",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000.0,
            };
            var surfaceRec = new Recording
            {
                RecordingId = "rec-tip-surface",
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition { body = "Kerbin", latitude = 1, longitude = 2, altitude = 3 },
            };
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART").AddValue("pos", "0,0,0");
            snapshot.AddNode("PART").AddValue("pos", "0,2,0");

            const int retries = 60; // one minute of 1 s retries
            for (int k = 0; k < retries; k++)
            {
                GhostExtender.ChooseStrategy(orbitRec);
                GhostExtender.ChooseStrategy(surfaceRec);
                GhostExtender.PropagateSurface(surfaceRec);
                SpawnCollisionDetector.ComputeVesselBounds(snapshot);
                SpawnCollisionDetector.ShouldTriggerWalkback(clock - 1.0, clock, 5.0, 0.5f, 1.0f);
                VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(4242u, 4242u, 0u);
                clock += 1.0;
            }

            // 60 s at the 5 s default interval: at most 13 lines each, never one per retry.
            int maxLines = (int)(retries / ParsekLog.DefaultRateLimitSeconds) + 1;
            foreach (string needle in new[]
            {
                "ChooseStrategy: Orbital",
                "ChooseStrategy: Surface (surface terminal",
                "PropagateSurface: returning terminal position",
                "ComputeVesselBounds: parsed 2/2 parts",
                "ParsePartPositions: parsed 2/2 parts",
                "ShouldTriggerWalkback:",
                "ShouldAllowExistingSourceDuplicate=true",
            })
            {
                int n = Count(needle);
                Assert.True(n >= 1, needle + " never logged");
                Assert.True(n <= maxLines, needle + " logged " + n + " times over " + retries + " retries");
            }
        }

        [Fact]
        public void RetryPathLogLines_AChangedVerdictPrintsAtOnce()
        {
            double clock = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => clock;

            SpawnCollisionDetector.ShouldTriggerWalkback(999.0, 1000.0, 5.0, 0.5f, 1.0f);
            clock += 0.5;
            SpawnCollisionDetector.ShouldTriggerWalkback(999.0, 1000.5, 5.0, 0.5f, 1.0f); // same verdict: throttled
            Assert.Equal(1, Count("ShouldTriggerWalkback:"));

            clock += 0.5;
            SpawnCollisionDetector.ShouldTriggerWalkback(990.0, 1001.0, 5.0, 0.5f, 1.0f); // timeout reached
            Assert.Equal(2, Count("ShouldTriggerWalkback:"));

            VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(7u, 7u, 1u);
            VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(7u, 7u, 1u);
            Assert.Equal(1, Count("ShouldAllowExistingSourceDuplicate=true"));
            VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(7u, 7u, 2u); // active vessel changed
            Assert.Equal(2, Count("ShouldAllowExistingSourceDuplicate=true"));
        }

        [Fact]
        public void StrategyLogKey_SeparatesRecordingsAndBranches()
        {
            var a = new Recording { RecordingId = "a" };
            var b = new Recording { RecordingId = "b" };
            Assert.NotEqual(GhostExtender.StrategyLogKey(a, "propagated-orbit"),
                GhostExtender.StrategyLogKey(b, "propagated-orbit"));
            Assert.NotEqual(GhostExtender.StrategyLogKey(a, "propagated-orbit"),
                GhostExtender.StrategyLogKey(a, "propagated-surface"));
            Assert.Equal("extend|(none)|x", GhostExtender.StrategyLogKey(null, "x"));
        }

        // ---- bounded walkback rescan ----

        [Fact]
        public void WalkbackRescan_BacksOffAfterASpawnFailureAndLogsOnce()
        {
            var chain = MakeChain("rec-tip", blocked: true);
            Assert.True(VesselGhoster.ShouldRunWalkbackRescan(chain, 100.0));

            Assert.True(VesselGhoster.RecordWalkbackSpawnFailure(chain, 100.0));
            Assert.False(VesselGhoster.ShouldRunWalkbackRescan(chain, 101.0));
            Assert.False(VesselGhoster.ShouldRunWalkbackRescan(
                chain, 100.0 + VesselGhoster.WalkbackRescanBackoffSeconds - 0.01));
            Assert.True(VesselGhoster.ShouldRunWalkbackRescan(
                chain, 100.0 + VesselGhoster.WalkbackRescanBackoffSeconds));

            // A second failure backs off again but is not logged again.
            Assert.False(VesselGhoster.RecordWalkbackSpawnFailure(chain, 110.0));
            Assert.False(VesselGhoster.ShouldRunWalkbackRescan(chain, 115.0));
            Assert.True(VesselGhoster.ShouldRunWalkbackRescan(chain, 120.0));
        }

        [Fact]
        public void WalkbackRescan_NeverRunsOnceExhausted_NullChainNever()
        {
            var chain = MakeChain("rec-tip", blocked: true);
            chain.WalkbackExhausted = true;
            Assert.False(VesselGhoster.ShouldRunWalkbackRescan(chain, 1e9));
            Assert.False(VesselGhoster.ShouldRunWalkbackRescan(null, 1e9));
            Assert.False(VesselGhoster.RecordWalkbackSpawnFailure(null, 1.0));
        }

        [Fact]
        public void EndCollisionBlock_NonCollisionFailure_UnblocksOnceAndLogs()
        {
            var chain = MakeChain("rec-tip", blocked: true);
            VesselGhoster.EndCollisionBlockForNonCollisionFailure(chain, "spawn-failed-at-clear-position");
            Assert.False(chain.SpawnBlocked);
            VesselGhoster.EndCollisionBlockForNonCollisionFailure(chain, "spawn-failed-at-clear-position");
            Assert.Equal(1, Count("Blocked chain tip no longer collision-blocked"));
            Assert.Contains(logLines, l => l.Contains("[Ghoster]")
                && l.Contains("originalPid=4242") && l.Contains("reason=spawn-failed-at-clear-position"));
        }

        // ---- real policy: the blocked tip's ghost outlives the 5 s window, then the spawn releases it ----

        private static GhostPlaybackEngine MakeEngine()
        {
            return new GhostPlaybackEngine(null)
            {
                DestroyGhostResourcesOverrideForTesting = state => { },
            };
        }

        private int AddCommitted(Recording rec)
        {
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            for (int k = 0; k < RecordingStore.CommittedRecordings.Count; k++)
                if (ReferenceEquals(RecordingStore.CommittedRecordings[k], rec)) return k;
            return -1;
        }

        [Fact]
        public void RealPolicy_BlockedChainTip_HeldPastTimeout_ThenReleasedOnSpawn()
        {
            var rec = MakeRecording("rec-chain-tip");
            int index = AddCommitted(rec);
            Assert.True(index >= 0);

            float now = 10f;
            bool blocked = true;
            int spawnAttempts = 0;
            bool clearOnNextAttempt = false;
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            typeof(ParsekFlight).GetField("watchMode", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, new WatchModeController(host));
            var engine = MakeEngine();
            var policy = new ParsekPlaybackPolicy(engine, host)
            {
                CurrentRealTimeOverrideForTesting = () => now,
                CurrentUTOverrideForTesting = () => 500.0,
                TimelineInactiveIdsOverrideForTesting = committed =>
                    new Dictionary<string, TimelineInactiveReason>(),
                IsSpawnBlockedChainTipOverrideForTesting = r => blocked,
                SpawnVesselOrChainTipOverrideForTesting = (recording, i) =>
                {
                    spawnAttempts++;
                    if (clearOnNextAttempt)
                    {
                        recording.VesselSpawned = true;
                        recording.SpawnedVesselPersistentId = 777u;
                        blocked = false;
                    }
                },
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };
            policy.heldGhosts[index] = new HeldGhostInfo
            {
                holdStartTime = now,
                recordingId = rec.RecordingId,
                vesselName = rec.VesselName,
            };

            // Well past the 5 s window, three retries: the ghost and the hold survive.
            for (int k = 1; k <= 3; k++)
            {
                now = 10f + ParsekPlaybackPolicy.HeldGhostTimeoutSeconds + k * 2f;
                policy.RetryHeldGhostSpawns();
                Assert.True(engine.HasGhost(index));
                Assert.True(policy.heldGhosts.ContainsKey(index));
            }
            Assert.Equal(3, spawnAttempts);
            Assert.DoesNotContain(logLines, l => l.Contains("Held ghost timed out"));

            // The blocker moves away: the next retry spawns and the ghost goes in the same call.
            clearOnNextAttempt = true;
            now += 2f;
            policy.RetryHeldGhostSpawns();

            Assert.Equal(4, spawnAttempts);
            Assert.False(engine.HasGhost(index));
            Assert.False(policy.heldGhosts.ContainsKey(index));
            Assert.Contains(logLines, l => l.Contains("[Policy]") && l.Contains("Held ghost spawn succeeded on retry"));
            Assert.Contains(logLines, l => l.Contains("destroyed (held-spawn-succeeded)"));
        }

        [Fact]
        public void RealPolicy_ChainNoLongerBlocked_FallsBackToTheTimeout()
        {
            // A chain-tip spawn that failed outright (not a collision) is an ordinary hold.
            var rec = MakeRecording("rec-chain-failed");
            int index = AddCommitted(rec);
            Assert.True(index >= 0);

            float now = 10f;
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var engine = MakeEngine();
            var policy = new ParsekPlaybackPolicy(engine, host)
            {
                CurrentRealTimeOverrideForTesting = () => now,
                CurrentUTOverrideForTesting = () => 500.0,
                TimelineInactiveIdsOverrideForTesting = committed =>
                    new Dictionary<string, TimelineInactiveReason>(),
                IsSpawnBlockedChainTipOverrideForTesting = r => false,
                SpawnVesselOrChainTipOverrideForTesting = (recording, i) => { },
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };
            policy.heldGhosts[index] = new HeldGhostInfo
            {
                holdStartTime = now,
                recordingId = rec.RecordingId,
                vesselName = rec.VesselName,
            };

            now = 10f + ParsekPlaybackPolicy.HeldGhostTimeoutSeconds + 0.1f;
            policy.RetryHeldGhostSpawns();

            Assert.False(engine.HasGhost(index));
            Assert.False(policy.heldGhosts.ContainsKey(index));
            Assert.Contains(logLines, l => l.Contains("Held ghost timed out"));
        }

        [Fact]
        public void RealPolicy_UninitializedHostWithoutChains_IsNotABlockedTip()
        {
            // No override: the policy asks the real host predicate, which reads a null chain
            // set as "no chain" and leaves the ordinary timeout in force.
            var rec = MakeRecording("rec-plain");
            int index = AddCommitted(rec);
            Assert.True(index >= 0);

            float now = 10f;
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var engine = MakeEngine();
            var policy = new ParsekPlaybackPolicy(engine, host)
            {
                CurrentRealTimeOverrideForTesting = () => now,
                CurrentUTOverrideForTesting = () => 500.0,
                TimelineInactiveIdsOverrideForTesting = committed =>
                    new Dictionary<string, TimelineInactiveReason>(),
                SpawnVesselOrChainTipOverrideForTesting = (recording, i) => { },
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };
            policy.heldGhosts[index] = new HeldGhostInfo
            {
                holdStartTime = now,
                recordingId = rec.RecordingId,
                vesselName = rec.VesselName,
            };

            now = 10f + ParsekPlaybackPolicy.HeldGhostTimeoutSeconds + 0.1f;
            policy.RetryHeldGhostSpawns();

            Assert.False(policy.heldGhosts.ContainsKey(index));
            Assert.Contains(logLines, l => l.Contains("Held ghost timed out"));
        }
    }
}
