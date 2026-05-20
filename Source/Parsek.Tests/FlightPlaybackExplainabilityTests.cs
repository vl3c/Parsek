using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FlightPlaybackExplainabilityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FlightPlaybackExplainabilityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            FlightRecorder.FrameCountProviderForTesting = () => 123;
            ReFlySettleStabilityTracker.Reset();
        }

        public void Dispose()
        {
            ReFlySettleStabilityTracker.Reset();
            FlightRecorder.FrameCountProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording MakeRecording(
            string recordingId,
            string vesselName = "Ghost",
            bool playbackEnabled = true)
        {
            return new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                PlaybackEnabled = playbackEnabled,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 101.0 }
                }
            };
        }

        [Fact]
        public void GhostPlaybackSkipReasonTokens_AreStable()
        {
            Assert.Equal("none", GhostPlaybackSkipReason.None.ToLogToken());
            Assert.Equal("no-renderable-data", GhostPlaybackSkipReason.NoRenderableData.ToLogToken());
            Assert.Equal("playback-disabled", GhostPlaybackSkipReason.PlaybackDisabled.ToLogToken());
            Assert.Equal("external-vessel-suppressed", GhostPlaybackSkipReason.ExternalVesselSuppressed.ToLogToken());
            Assert.Equal("before-activation", GhostPlaybackSkipReason.BeforeActivation.ToLogToken());
            Assert.Equal("anchor-missing", GhostPlaybackSkipReason.AnchorMissing.ToLogToken());
            Assert.Equal("loop-sync-failed", GhostPlaybackSkipReason.LoopSyncFailed.ToLogToken());
            Assert.Equal("parent-loop-paused", GhostPlaybackSkipReason.ParentLoopPaused.ToLogToken());
            Assert.Equal("warp-hidden", GhostPlaybackSkipReason.WarpHidden.ToLogToken());
            Assert.Equal("visual-load-failed", GhostPlaybackSkipReason.VisualLoadFailed.ToLogToken());
            Assert.Equal("session-suppressed", GhostPlaybackSkipReason.SessionSuppressed.ToLogToken());
            Assert.Equal("superseded-by-relation", GhostPlaybackSkipReason.SupersededByRelation.ToLogToken());
            Assert.Equal("rewind-retired", GhostPlaybackSkipReason.RewindRetired.ToLogToken());
            Assert.Equal("anchor-rotation-unreliable", GhostPlaybackSkipReason.AnchorRotationUnreliable.ToLogToken());
            Assert.Equal("anchor-refly-unstable", GhostPlaybackSkipReason.AnchorReFlyUnstable.ToLogToken());
        }

        [Fact]
        public void ResolveGhostPlaybackSkipReason_PrioritizesExplicitReasons()
        {
            Assert.Equal(
                GhostPlaybackSkipReason.NoRenderableData,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: false,
                    playbackEnabled: false,
                    externalVesselSuppressed: true));
            Assert.Equal(
                GhostPlaybackSkipReason.PlaybackDisabled,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: true,
                    playbackEnabled: false,
                    externalVesselSuppressed: true));
            Assert.Equal(
                GhostPlaybackSkipReason.ExternalVesselSuppressed,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: true,
                    playbackEnabled: true,
                    externalVesselSuppressed: true));
            Assert.Equal(
                GhostPlaybackSkipReason.SupersededByRelation,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: true,
                    playbackEnabled: true,
                    externalVesselSuppressed: true,
                    supersededByRelation: true));
            Assert.Equal(
                GhostPlaybackSkipReason.RewindRetired,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: true,
                    playbackEnabled: true,
                    externalVesselSuppressed: true,
                    supersededByRelation: true,
                    rewindRetired: true));
            Assert.Equal(
                GhostPlaybackSkipReason.None,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: true,
                    playbackEnabled: true,
                    externalVesselSuppressed: false));
        }

        [Fact]
        public void LogGhostSkipReasonChangeForTesting_LogsOnlyOnReasonChange()
        {
            ParsekFlight.LogGhostSkipReasonChangeForTesting(
                4,
                "rec-skip",
                "Skip Vessel",
                GhostPlaybackSkipReason.NoRenderableData,
                hasRenderableData: false,
                playbackEnabled: true,
                externalVesselSuppressed: false);
            ParsekFlight.LogGhostSkipReasonChangeForTesting(
                4,
                "rec-skip",
                "Skip Vessel",
                GhostPlaybackSkipReason.NoRenderableData,
                hasRenderableData: false,
                playbackEnabled: true,
                externalVesselSuppressed: false);
            ParsekFlight.LogGhostSkipReasonChangeForTesting(
                4,
                "rec-skip",
                "Skip Vessel",
                GhostPlaybackSkipReason.PlaybackDisabled,
                hasRenderableData: true,
                playbackEnabled: false,
                externalVesselSuppressed: false);

            Assert.Equal(
                1,
                logLines.Count(line => line.Contains("reason=no-renderable-data")));
            Assert.Contains(
                logLines,
                line => line.Contains("reason=playback-disabled")
                    && line.Contains("suppressed=1"));
        }

        [Fact]
        public void ComputePlaybackFlags_SupersededByRelation_SkipsGhostAndSpawn()
        {
            RecordingStore.ResetForTesting();
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_old_new",
                        OldRecordingId = "rec-old",
                        NewRecordingId = "rec-new"
                    }
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();
                var oldRec = MakeRecording("rec-old", "Old Probe");
                oldRec.VesselSnapshot = new ConfigNode("VESSEL");
                oldRec.TerminalStateValue = TerminalState.Orbiting;
                var newRec = MakeRecording("rec-new", "New Probe");
                var committed = new List<Recording> { oldRec, newRec };

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                Assert.True(flags[0].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.SupersededByRelation, flags[0].skipReason);
                Assert.False(flags[0].needsSpawn);
                Assert.False(flags[1].skipGhost);
                Assert.False(flags[0].anchorReFlyUnstable);
                Assert.False(flags[1].anchorReFlyUnstable);
                Assert.Contains(logLines, line =>
                    line.Contains("id=rec-old")
                    && line.Contains("reason=superseded-by-relation")
                    && line.Contains("supersededByRelation=True"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
                RecordingStore.ResetForTesting();
            }
        }

                [Fact]
        public void ComputePlaybackFlags_LogsGhostSkipReasonAndSuppressesStableRepeats()
        {
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();
                var noData = new Recording
                {
                    RecordingId = "rec-flags",
                    VesselName = "Flagged Vessel",
                    PlaybackEnabled = true
                };
                var committed = new List<Recording> { noData };

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 100.0);

                Assert.Single(flags);
                Assert.True(flags[0].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.NoRenderableData, flags[0].skipReason);
                Assert.False(flags[0].anchorReFlyUnstable);
                Assert.Equal(1, logLines.Count(line =>
                    line.Contains("[Parsek][VERBOSE][Flight]")
                    && line.Contains("Ghost playback skip state")
                    && line.Contains("id=rec-flags")
                    && line.Contains("reason=no-renderable-data")));

                flags = ComputePlaybackFlagsForTesting(host, committed, 100.0);

                Assert.Equal(GhostPlaybackSkipReason.NoRenderableData, flags[0].skipReason);
                Assert.False(flags[0].anchorReFlyUnstable);
                Assert.Equal(1, logLines.Count(line =>
                    line.Contains("Ghost playback skip state")
                    && line.Contains("id=rec-flags")));

                var playbackDisabled = MakeRecording(
                    "rec-flags",
                    "Flagged Vessel",
                    playbackEnabled: false);
                committed[0] = playbackDisabled;

                flags = ComputePlaybackFlagsForTesting(host, committed, 100.0);

                Assert.True(flags[0].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.PlaybackDisabled, flags[0].skipReason);
                Assert.False(flags[0].anchorReFlyUnstable);
                Assert.Contains(logLines, line =>
                    line.Contains("[Parsek][VERBOSE][Flight]")
                    && line.Contains("Ghost playback skip state")
                    && line.Contains("id=rec-flags")
                    && line.Contains("reason=playback-disabled")
                    && line.Contains("suppressed=1"));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_RewindRetired_SkipsGhostAndSpawn()
        {
            RecordingStore.ResetForTesting();
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    new RecordingRewindRetirement
                    {
                        RetirementId = "rrt_old",
                        RecordingId = "rec-retired",
                        RestoredRecordingId = "rec-restored",
                        Reason = RecordingRewindRetirement.DefaultReason
                    }
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();
                var retired = MakeRecording("rec-retired", "Retired Probe");
                retired.VesselSnapshot = new ConfigNode("VESSEL");
                retired.TerminalStateValue = TerminalState.Orbiting;
                var restored = MakeRecording("rec-restored", "Restored Probe");
                var committed = new List<Recording> { retired, restored };

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                Assert.True(flags[0].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.RewindRetired, flags[0].skipReason);
                Assert.False(flags[0].needsSpawn);
                Assert.False(flags[1].skipGhost);
                Assert.False(flags[0].anchorReFlyUnstable);
                Assert.False(flags[1].anchorReFlyUnstable);
                Assert.Contains(logLines, line =>
                    line.Contains("id=rec-retired")
                    && line.Contains("reason=rewind-retired")
                    && line.Contains("rewindRetired=True"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_RetiredParentCascade_SkipsOrphanDebrisChildGhostAndSpawn()
        {
            // End-to-end through the flight-scene flag computation:
            // ComputePlaybackFlags -> ComputeTimelineInactiveRecordingIds
            // (cascade) -> rewindRetired bool -> GhostPlaybackSkipReason.
            // Pins the user-visible bug path: a parent-anchored debris child
            // of a retired re-fly fork must skip ghost + spawn even though
            // the child carries no retirement row of its own.
            RecordingStore.ResetForTesting();
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    new RecordingRewindRetirement
                    {
                        RetirementId = "rrt_parent",
                        RecordingId = "rec-retired-parent",
                        RestoredRecordingId = "rec-restored",
                        Reason = RecordingRewindRetirement.DefaultReason
                    }
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();

                var retiredParent = MakeRecording("rec-retired-parent", "Retired Fork");
                retiredParent.VesselSnapshot = new ConfigNode("VESSEL");
                retiredParent.TerminalStateValue = TerminalState.Orbiting;

                // Orphan debris child: no retirement of its own, only the
                // parent-anchor link to the retired fork.
                var orphanDebris = MakeRecording("rec-orphan-debris", "Fork Debris");
                orphanDebris.VesselSnapshot = new ConfigNode("VESSEL");
                orphanDebris.TerminalStateValue = TerminalState.Orbiting;
                orphanDebris.IsDebris = true;
                orphanDebris.DebrisParentRecordingId = "rec-retired-parent";

                var restored = MakeRecording("rec-restored", "Restored Vessel");

                var committed = new List<Recording> { retiredParent, orphanDebris, restored };

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                // Parent retired directly.
                Assert.True(flags[0].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.RewindRetired, flags[0].skipReason);
                Assert.False(flags[0].needsSpawn);

                // Orphan debris child cascaded to RewindRetired.
                Assert.True(flags[1].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.RewindRetired, flags[1].skipReason);
                Assert.False(flags[1].needsSpawn);

                // Restored sibling stays visible.
                Assert.False(flags[2].skipGhost);

                Assert.Contains(logLines, line =>
                    line.Contains("id=rec-orphan-debris")
                    && line.Contains("reason=rewind-retired")
                    && line.Contains("rewindRetired=True"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_RewindRetiredRelativeAnchorChain_SkipsBothBeforeAnchorResolution()
        {
            RecordingStore.ResetForTesting();
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    new RecordingRewindRetirement
                    {
                        RetirementId = "rrt_anchor",
                        RecordingId = "rec-retired-anchor",
                        RestoredRecordingId = "rec-restored-root",
                        Reason = RecordingRewindRetirement.DefaultReason
                    },
                    new RecordingRewindRetirement
                    {
                        RetirementId = "rrt_child",
                        RecordingId = "rec-retired-child",
                        RestoredRecordingId = "rec-restored-root",
                        Reason = RecordingRewindRetirement.DefaultReason
                    }
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();
                var anchor = MakeRecording("rec-retired-anchor", "Retired Anchor");
                anchor.VesselSnapshot = new ConfigNode("VESSEL");
                anchor.TerminalStateValue = TerminalState.Orbiting;

                var child = MakeRecording("rec-retired-child", "Retired Relative Child");
                child.VesselSnapshot = new ConfigNode("VESSEL");
                child.TerminalStateValue = TerminalState.Orbiting;
                child.TrackSections.Add(new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    anchorRecordingId = anchor.RecordingId,
                    startUT = 100.0,
                    endUT = 101.0,
                    frames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 100.0 },
                        new TrajectoryPoint { ut = 101.0 }
                    }
                });

                var restored = MakeRecording("rec-restored-root", "Restored Root");
                var committed = new List<Recording> { anchor, child, restored };

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 100.5);

                Assert.True(flags[0].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.RewindRetired, flags[0].skipReason);
                Assert.False(flags[0].needsSpawn);
                Assert.True(flags[1].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.RewindRetired, flags[1].skipReason);
                Assert.False(flags[1].needsSpawn);
                Assert.False(flags[2].skipGhost);
                Assert.False(flags[0].anchorReFlyUnstable);
                Assert.False(flags[1].anchorReFlyUnstable);
                Assert.False(flags[2].anchorReFlyUnstable);
                Assert.Contains(logLines, line =>
                    line.Contains("id=rec-retired-anchor")
                    && line.Contains("reason=rewind-retired"));
                Assert.Contains(logLines, line =>
                    line.Contains("id=rec-retired-child")
                    && line.Contains("reason=rewind-retired"));
            }
            finally
            {
                ParsekScenario.SetInstanceForTesting(null);
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_ReFlySettleClearHold_SetsAnchorUnstableAndLogsTransitions()
        {
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();
                var rec = MakeRecording("rec-refly-hold", "Held Probe");
                var committed = new List<Recording> { rec };
                ReFlySettleStabilityTracker.RecordSettleCleared("rec-refly-hold", frame: 123);

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 100.0);

                Assert.Single(flags);
                Assert.True(flags[0].anchorReFlyUnstable);
                Assert.Contains(logLines, line =>
                    line.Contains("[Parsek][INFO][ReFlySettle]")
                    && line.Contains("Anchor ghost visibility hold engaged")
                    && line.Contains("anchorRec=rec-refl")
                    && line.Contains("ghosts=1")
                    && line.Contains("reason=clear-hold"));

                ReFlySettleStabilityTracker.Reset();
                flags = ComputePlaybackFlagsForTesting(host, committed, 100.0);

                Assert.False(flags[0].anchorReFlyUnstable);
                Assert.Contains(logLines, line =>
                    line.Contains("[Parsek][INFO][ReFlySettle]")
                    && line.Contains("Anchor ghost visibility hold released")
                    && line.Contains("anchorRec=rec-refl"));
            }
            finally
            {
                ReFlySettleStabilityTracker.Reset();
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void FrameSummary_IncludesAggregateSkipCounters()
        {
            var counters = new GhostPlaybackFrameCounters
            {
                spawned = 1,
                destroyed = 2,
                deferred = 3,
                beforeActivation = 4,
                anchorMissing = 5,
                loopSyncFailed = 6,
                parentLoopPaused = 7,
                warpHidden = 8,
                visualLoadFailed = 9,
                noRenderableData = 10,
                playbackDisabled = 11,
                externalVesselSuppressed = 12,
                sessionSuppressed = 13,
                supersededByRelation = 14,
                rewindRetired = 15,
                spawnSuppressedDeadOnArrival = 16,
                anchorRotationUnreliable = 17,
                anchorReFlyUnstable = 18,
                active = 19
            };

            string message = GhostPlaybackEngine.BuildFrameSummaryMessage(counters);

            Assert.True(GhostPlaybackEngine.ShouldEmitFrameSummary(counters));
            Assert.Contains("spawned=1", message);
            Assert.Contains("destroyed=2", message);
            Assert.Contains("deferred=3", message);
            Assert.Contains("beforeActivation=4", message);
            Assert.Contains("anchorMissing=5", message);
            Assert.Contains("loopSyncFailed=6", message);
            Assert.Contains("parentLoopPaused=7", message);
            Assert.Contains("warpHidden=8", message);
            Assert.Contains("visualLoadFailed=9", message);
            Assert.Contains("noRenderableData=10", message);
            Assert.Contains("playbackDisabled=11", message);
            Assert.Contains("externalVesselSuppressed=12", message);
            Assert.Contains("sessionSuppressed=13", message);
            Assert.Contains("supersededByRelation=14", message);
            Assert.Contains("rewindRetired=15", message);
            Assert.Contains("spawnSuppressedDeadOnArrival=16", message);
            Assert.Contains("anchorRotationUnreliable=17", message);
            Assert.Contains("anchorReFlyUnstable=18", message);
            Assert.Contains("active=19", message);
        }

        [Fact]
        public void FrameSummary_DoesNotEmitForStableActiveOnlyFrame()
        {
            var counters = new GhostPlaybackFrameCounters { active = 3 };

            Assert.False(GhostPlaybackEngine.ShouldEmitFrameSummary(counters));
        }

        [Fact]
        public void FrameSummary_EmitsWhenSpawnSuppressedDeadOnArrivalNonZero()
        {
            var counters = new GhostPlaybackFrameCounters { spawnSuppressedDeadOnArrival = 1 };
            Assert.True(GhostPlaybackEngine.ShouldEmitFrameSummary(counters));
        }

        [Fact]
        public void FrameSummary_EmitsWhenAnchorReFlyUnstableNonZero()
        {
            var counters = new GhostPlaybackFrameCounters { anchorReFlyUnstable = 1 };
            Assert.True(GhostPlaybackEngine.ShouldEmitFrameSummary(counters));
        }

        [Fact]
        public void FrameSummary_EmitsWhenRewindRetiredNonZero()
        {
            var counters = new GhostPlaybackFrameCounters { rewindRetired = 1 };
            Assert.True(GhostPlaybackEngine.ShouldEmitFrameSummary(counters));
        }

        [Fact]
        public void FrameSummary_EmitsWhenAnchorRotationUnreliableNonZero()
        {
            var counters = new GhostPlaybackFrameCounters { anchorRotationUnreliable = 1 };
            Assert.True(GhostPlaybackEngine.ShouldEmitFrameSummary(counters));
        }

        [Fact]
        public void FastForwardWatchHandoff_ClassifiesActiveGhostAsReady()
        {
            var recordings = new List<Recording> { MakeRecording("rec-a", "Vessel A") };
            var flags = new[]
            {
                new TrajectoryPlaybackFlags { skipReason = GhostPlaybackSkipReason.None }
            };

            ParsekFlight.FastForwardWatchHandoffDecision decision =
                ParsekFlight.ClassifyFastForwardWatchHandoff(
                    "rec-a",
                    recordings,
                    flags,
                    index => index == 0);

            Assert.True(decision.canEnterWatch);
            Assert.False(decision.warn);
            Assert.Equal(0, decision.index);
            Assert.Equal("active-ghost", decision.reason);
            Assert.Contains("Deferred FF watch handoff ready",
                ParsekFlight.BuildFastForwardWatchHandoffMessage(decision, 123.4));
        }

        [Fact]
        public void FastForwardWatchHandoff_ReportsSkipReasonWhenGhostMissing()
        {
            var recordings = new List<Recording> { MakeRecording("rec-b", "Vessel B") };
            var flags = new[]
            {
                new TrajectoryPlaybackFlags { skipReason = GhostPlaybackSkipReason.NoRenderableData }
            };

            ParsekFlight.FastForwardWatchHandoffDecision decision =
                ParsekFlight.ClassifyFastForwardWatchHandoff(
                    "rec-b",
                    recordings,
                    flags,
                    index => false);

            Assert.False(decision.canEnterWatch);
            Assert.True(decision.warn);
            Assert.Equal("no-renderable-data", decision.reason);
            Assert.Contains("skipReason=no-renderable-data",
                ParsekFlight.BuildFastForwardWatchHandoffMessage(decision, 123.4));
        }

        [Fact]
        public void FastForwardWatchHandoff_StaleRecordingIsQuietFailure()
        {
            ParsekFlight.FastForwardWatchHandoffDecision decision =
                ParsekFlight.ClassifyFastForwardWatchHandoff(
                    "missing",
                    new List<Recording> { MakeRecording("rec-c", "Vessel C") },
                    flags: null,
                    hasActiveGhost: index => true);

            Assert.False(decision.canEnterWatch);
            Assert.False(decision.warn);
            Assert.False(decision.recordingExists);
            Assert.Equal("stale-recording-id", decision.reason);
        }

        private static ParsekFlight CreateFlightHostForPlaybackFlagTests()
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            SetPrivateField(host, "chainManager", new ChainSegmentManager());
            SetPrivateField(host, "activeGhostChains", new Dictionary<uint, GhostChain>());
            SetPrivateField(host, "activeGhostSkipReasonLogIdentities", new HashSet<string>());
            SetPrivateField(host, "reFlyAnchorHoldStartFrameByAnchor", new Dictionary<string, int>(StringComparer.Ordinal));
            SetPrivateField(host, "reFlyAnchorHoldCountsThisFrame", new Dictionary<string, int>(StringComparer.Ordinal));
            SetPrivateField(host, "reFlyAnchorHoldReasonsThisFrame", new Dictionary<string, string>(StringComparer.Ordinal));
            SetPrivateField(host, "reFlyAnchorHoldReleaseScratch", new List<string>());
            return host;
        }

        private static TrajectoryPlaybackFlags[] ComputePlaybackFlagsForTesting(
            ParsekFlight host,
            IReadOnlyList<Recording> committed,
            double currentUT)
        {
            MethodInfo method = typeof(ParsekFlight).GetMethod(
                "ComputePlaybackFlags",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (TrajectoryPlaybackFlags[])method.Invoke(
                host,
                new object[] { committed, currentUT, null });
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(target, value);
        }
    }
}
