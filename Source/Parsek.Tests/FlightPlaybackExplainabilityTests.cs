using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
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
            PlaybackScopeTracker.ResetForTesting();
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            // Pin the resolver live-bind ledger to a fixed frame so the Step-2 gate's
            // WasLiveBoundThisOrLastFrame reads a deterministic frame; tests stamp at the
            // same frame via StampLiveAnchorBind.
            RelativeAnchorResolver.ResetForTesting();
            RelativeAnchorResolver.FrameProviderForTesting = () => HarnessBindFrame;
        }

        public void Dispose()
        {
            ReFlySettleStabilityTracker.Reset();
            PlaybackScopeTracker.ResetForTesting();
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            RelativeAnchorResolver.ResetForTesting();
            FlightRecorder.FrameCountProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private const int HarnessBindFrame = 7;

        // Stamps the resolver live-bind ledger for an anchor id at the harness frame by
        // driving a real resolve through the public TryResolveAnchorPose path with a
        // live-anchor delegate (the exact stamp site). Step-1 fires source=live, which is
        // what ComputePlaybackFlags' Step-2 gate consumes one frame later.
        private static void StampLiveAnchorBind(string anchorRecordingId)
        {
            var anchorRec = new Recording
            {
                RecordingId = anchorRecordingId,
                VesselName = "Live Depot",
                VesselPersistentId = AnchorDepotPid,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            var tree = new RecordingTree { Id = "tree-explainability" };
            tree.Recordings[anchorRecordingId] = anchorRec;

            var context = new RelativeAnchorResolverContext(
                focusTree: tree,
                focusRecordingId: "focus-member",
                focusTreeId: tree.Id,
                tryResolveLiveLaunchMatchedAnchorPose: (rec, ut) =>
                    (new Vector3d(1.0, 2.0, 3.0), Quaternion.identity));

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context, anchorRecordingId, 200.0, null, out _, out _);
            Assert.True(resolved);
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

        // Simulate that the player rewound to before this recording, so it is a
        // legitimate live replay rather than a purely-historical committed recording
        // (BUG-B). Without this, a committed recording evaluated past its end UT is
        // classified HistoricalNotReplayed and skipped.
        private static void ArmReplayScope(Recording rec)
        {
            PlaybackScopeTracker.NotePlayhead(
                rec.RecordingId, 0.0, GhostPlaybackEngine.ResolveGhostActivationStartUT(rec));
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
                // rec-new is the live re-flight (player rewound into it): keep it in
                // replay scope so the test isolates supersession, not BUG-B history.
                ArmReplayScope(newRec);

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
        public void ComputePlaybackFlags_EmitsOneBatchedSpawnSuppressedSummary()
        {
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();
                // Three non-debris recordings with no vessel snapshot: all spawn-suppressed.
                var committed = new List<Recording>
                {
                    MakeRecording("rec-a", "Vessel A"),
                    MakeRecording("rec-b", "Vessel B"),
                    MakeRecording("rec-c", "Vessel C"),
                };

                ComputePlaybackFlagsForTesting(host, committed, 100.0);

                // One batched summary for the whole list, NOT one line per recording.
                var summaryLines = logLines
                    .Where(l => l.Contains("[Spawner]") && l.Contains("Spawn suppressed:"))
                    .ToList();
                Assert.Single(summaryLines);
                Assert.Contains("3 recording(s)", summaryLines[0]);
                // The former per-recording "Spawn suppressed for #N" line is gone.
                Assert.DoesNotContain(logLines, l =>
                    l.Contains("[Spawner]") && l.Contains("Spawn suppressed for #"));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        private const uint AnchorDepotPid = 4277041026u;
        private const string AnchorDepotLaunchGuid = "424fd14c8870407f81dffdf606a92db2";
        private const string AnchorDepotRelaunchGuid = "05d3ea0f00000000000000000000ffff";

        private static Recording MakeLiveAnchorRecording(string recordingId)
        {
            var anchor = MakeRecording(recordingId, "Live Depot");
            anchor.VesselSnapshot = new ConfigNode("VESSEL");
            anchor.TerminalStateValue = TerminalState.Orbiting;
            anchor.VesselPersistentId = AnchorDepotPid;
            anchor.RecordedVesselGuid = AnchorDepotLaunchGuid;
            // The Step-2 whole-loop suppression is scoped to LOOP MEMBERS (mirroring the
            // map-side loopMemberInWindow carve-out): only a looping member's own ghost is
            // hidden when its launch-matched live vessel is loaded. The route Depot anchor
            // is a loop member, so mark it as such for the suppression gate to fire.
            anchor.LoopPlayback = true;
            return anchor;
        }

        [Fact]
        public void ComputePlaybackFlags_LiveBindSuppression_HidesAnchorGhostWhenLaunchMatchedLiveVesselLoadedAndLiveBound()
        {
            // Step-2 (live-bind-event double-suppression): when the anchor's launch-matched
            // live vessel is loaded AND a delivery member is docking it (live-bound this/
            // last frame), its OWN loop ghost is a duplicate of the live station and is
            // hidden. The in-bubble mesh suppression is the externalVesselSuppressed skip;
            // the anchor still stays spawnable (needsSpawn unaffected). The spawn-suppressed
            // head total counts ONLY the genuinely spawn-suppressed recording, since the
            // Step-2 firing is a skip-path suppression, not a spawn suppression.
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();

                var anchor = MakeLiveAnchorRecording("rec-anchor");
                ArmReplayScope(anchor);

                // A genuinely spawn-suppressed recording (no snapshot / no terminal) whose
                // launch-matched live vessel is absent: it must remain the sole
                // contributor to the head "Spawn suppressed: N recording(s)" total.
                var spawnSuppressed = MakeRecording("rec-spawn-suppressed", "Plain Ghost");

                var committed = new List<Recording> { anchor, spawnSuppressed };

                // The anchor's launch-matched live vessel is loaded (same pid + same
                // launch guid) => RealVesselExistsForRecording(anchor) is true, AND a
                // delivery member is docking it this frame (live-bound).
                GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == AnchorDepotPid);
                GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => AnchorDepotLaunchGuid);
                StampLiveAnchorBind("rec-anchor");

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                // Live-bind suppression fired on the anchor: suppressed via the skip path,
                // but stays spawnable (not in the spawn total).
                Assert.True(flags[0].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.ExternalVesselSuppressed, flags[0].skipReason);
                Assert.True(flags[0].needsSpawn);

                // A dedicated per-anchor line is emitted at the decision site, plus the
                // batched live-anchor-double summary.
                Assert.Contains(
                    logLines,
                    l => l.Contains("[Anchor]")
                        && l.Contains("Step-2 suppressed live-anchor double (live-bind event)"));
                Assert.Contains(
                    logLines,
                    l => l.Contains("[Anchor]")
                        && l.Contains("Step-2 live-anchor-double: suppressed=1 this frame (gate=live-bind-event)"));

                var summaryLines = logLines
                    .Where(l => l.Contains("[Spawner]") && l.Contains("Spawn suppressed:"))
                    .ToList();
                Assert.Single(summaryLines);
                // The head total counts ONLY the genuinely spawn-suppressed recording; the
                // Step-2 skip-path firing on the anchor did not inflate it.
                Assert.Contains("Spawn suppressed: 1 recording(s)", summaryLines[0]);
                Assert.DoesNotContain("live-anchor-double-suppressed", summaryLines[0]);
            }
            finally
            {
                GhostPlaybackLogic.ResetVesselExistsOverride();
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_LiveBindSuppression_AnchorWithLiveVesselButNoLiveBind_NotSuppressed()
        {
            // THE scene fix: a loop member whose launch-matched live vessel is loaded but
            // which is NOT being docked this/last frame (no live bind) must NOT be Step-2
            // suppressed - its mesh draws. The earlier whole-loop gate hid it (and every
            // other parked route craft). No [Anchor] Step-2 line, no skip.
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();

                var anchor = MakeLiveAnchorRecording("rec-anchor");
                ArmReplayScope(anchor);
                var committed = new List<Recording> { anchor };

                GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == AnchorDepotPid);
                GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => AnchorDepotLaunchGuid);
                // No StampLiveAnchorBind: the live vessel is loaded + looping, but nobody
                // is docking it this frame.

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                Assert.False(flags[0].skipGhost);
                Assert.NotEqual(GhostPlaybackSkipReason.ExternalVesselSuppressed, flags[0].skipReason);
                Assert.DoesNotContain(
                    logLines,
                    l => l.Contains("[Anchor]") && l.Contains("Step-2 suppressed live-anchor double"));
            }
            finally
            {
                GhostPlaybackLogic.ResetVesselExistsOverride();
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_LiveBindSuppression_RelativeMemberWithLiveAnchorLoaded_NotSuppressed()
        {
            // THE user-visible regression: the inbound delivery member's anchor TARGET is
            // live-bound, but the member's OWN id never enters the bind set. With the
            // member's live vessel loaded + looping it still must NOT be suppressed - its
            // delivery mesh draws docking the live station.
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();

                // The member shares the same craft-baked pid/guid fixtures (the delivery
                // mesh is a loop member whose live vessel is loaded). Its id is "rec-member".
                var member = MakeLiveAnchorRecording("rec-member");
                ArmReplayScope(member);
                var committed = new List<Recording> { member };

                GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == AnchorDepotPid);
                GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => AnchorDepotLaunchGuid);
                // The ANCHOR (a different id) is live-bound, never the member.
                StampLiveAnchorBind("rec-anchor");

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                Assert.False(flags[0].skipGhost);
                Assert.NotEqual(GhostPlaybackSkipReason.ExternalVesselSuppressed, flags[0].skipReason);
                Assert.DoesNotContain(
                    logLines,
                    l => l.Contains("[Anchor]") && l.Contains("Step-2 suppressed live-anchor double"));
            }
            finally
            {
                GhostPlaybackLogic.ResetVesselExistsOverride();
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_LiveBindSuppression_DifferentLaunch_DoesNotSuppressAnchorGhost()
        {
            // Guid gate (criterion B): a same-craft DIFFERENT-launch live vessel (same
            // craft-baked pid, conclusively different launch guid) must NOT trigger
            // suppression even when live-bound. RealVesselExistsForRecording returns false,
            // so the anchor ghost is not skipped via the Step-2 path. Stamping the bind set
            // isolates the guid term: it is the ONLY reason this case is not suppressed.
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();

                var anchor = MakeLiveAnchorRecording("rec-anchor");
                ArmReplayScope(anchor);
                var committed = new List<Recording> { anchor };

                GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == AnchorDepotPid);
                GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => AnchorDepotRelaunchGuid);
                StampLiveAnchorBind("rec-anchor");

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                Assert.False(flags[0].skipGhost);
                Assert.NotEqual(GhostPlaybackSkipReason.ExternalVesselSuppressed, flags[0].skipReason);
                Assert.DoesNotContain(
                    logLines,
                    l => l.Contains("[Anchor]") && l.Contains("Step-2 suppressed live-anchor double"));
            }
            finally
            {
                GhostPlaybackLogic.ResetVesselExistsOverride();
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_LiveBindSuppression_NoLiveVessel_DoesNotSuppressAnchorGhost()
        {
            // Watch-from-afar (criterion D): the anchor's launch-matched live vessel is NOT
            // loaded, so the Step-2 gate does not fire even when live-bound. Stamping the
            // bind set isolates the existence term as the only blocker here.
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();

                var anchor = MakeLiveAnchorRecording("rec-anchor");
                ArmReplayScope(anchor);
                var committed = new List<Recording> { anchor };

                GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);
                GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => AnchorDepotLaunchGuid);
                StampLiveAnchorBind("rec-anchor");

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                Assert.False(flags[0].skipGhost);
                Assert.NotEqual(GhostPlaybackSkipReason.ExternalVesselSuppressed, flags[0].skipReason);
                Assert.DoesNotContain(
                    logLines,
                    l => l.Contains("[Anchor]") && l.Contains("Step-2 suppressed live-anchor double"));
            }
            finally
            {
                GhostPlaybackLogic.ResetVesselExistsOverride();
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_LiveBindSuppression_NonLoopRecording_NotSuppressedEvenWithLaunchMatchedLiveVessel()
        {
            // Scope gate (must-fix): the Step-2 suppression is gated on loop membership
            // (loopingLike), MIRRORING the map-side loopMemberInWindow carve-out. A NON-loop
            // recording whose launch-matched live vessel is loaded AND which is live-bound
            // and in active replay scope (e.g. an active re-fly history ghost, or a rewound
            // non-loop recording) must NOT be suppressed: only its own engine path decides
            // its visibility. Stamping the bind set isolates the loopingLike term as the
            // only blocker.
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();

                // Same as the route anchor, but NOT a loop member (LoopPlayback stays
                // false). Its launch-matched live vessel is loaded (same pid + guid) and it
                // is live-bound, so RealVesselExistsForRecording and the live-bind term are
                // both true, yet the loop-membership gate keeps the Step-2 suppression from
                // firing.
                var anchor = MakeLiveAnchorRecording("rec-anchor");
                anchor.LoopPlayback = false;
                ArmReplayScope(anchor);
                var committed = new List<Recording> { anchor };

                GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == AnchorDepotPid);
                GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => AnchorDepotLaunchGuid);
                StampLiveAnchorBind("rec-anchor");

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                // Not suppressed via the Step-2 external-vessel path, and no Step-2 line.
                Assert.False(flags[0].skipGhost);
                Assert.NotEqual(GhostPlaybackSkipReason.ExternalVesselSuppressed, flags[0].skipReason);
                Assert.DoesNotContain(
                    logLines,
                    l => l.Contains("[Anchor]") && l.Contains("Step-2 suppressed live-anchor double"));
            }
            finally
            {
                GhostPlaybackLogic.ResetVesselExistsOverride();
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_HistoricalNeverReplayed_SkipsGhostAndSpawnDuringForwardPlay()
        {
            // BUG-B: a committed recording the player flew and progressed past (window
            // ~[100,101]) with NO rewind is historical at live UT 200 — it must not
            // render a ghost or spawn its terminal vessel even though its terminal
            // state (Orbiting) would otherwise mark it needsSpawn.
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();
                var rec = MakeRecording("rec-hist", "Past Probe");
                rec.VesselSnapshot = new ConfigNode("VESSEL");
                rec.TerminalStateValue = TerminalState.Orbiting;
                var committed = new List<Recording> { rec };

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                Assert.True(flags[0].skipGhost);
                Assert.Equal(GhostPlaybackSkipReason.HistoricalNotReplayed, flags[0].skipReason);
                Assert.False(flags[0].needsSpawn);
                Assert.Contains(logLines, line =>
                    line.Contains("id=rec-hist")
                    && line.Contains("reason=historical-not-replayed"));
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_RewoundIntoReplayScope_RendersAndSpawns()
        {
            // The same recording, but the player rewound to before its launch — it is
            // now a legitimate live replay, so it renders and spawns at its terminal.
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();
                var rec = MakeRecording("rec-replay", "Replayed Probe");
                rec.VesselSnapshot = new ConfigNode("VESSEL");
                rec.TerminalStateValue = TerminalState.Orbiting;
                var committed = new List<Recording> { rec };
                ArmReplayScope(rec);

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                Assert.False(flags[0].skipGhost);
                Assert.NotEqual(GhostPlaybackSkipReason.HistoricalNotReplayed, flags[0].skipReason);
                Assert.True(flags[0].needsSpawn);
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void ComputePlaybackFlags_LoopingRecording_NotHistoricalDuringForwardPlay()
        {
            // A recording the user explicitly opted into looping must keep replaying
            // during normal forward play (no rewind), so it is never classified
            // historical even though its window is in the past.
            RecordingStore.ResetForTesting();
            try
            {
                ParsekFlight host = CreateFlightHostForPlaybackFlagTests();
                var rec = MakeRecording("rec-loop", "Looping Probe");
                rec.VesselSnapshot = new ConfigNode("VESSEL");
                rec.TerminalStateValue = TerminalState.Orbiting;
                rec.LoopPlayback = true;
                var committed = new List<Recording> { rec };

                TrajectoryPlaybackFlags[] flags = ComputePlaybackFlagsForTesting(host, committed, 200.0);

                Assert.NotEqual(GhostPlaybackSkipReason.HistoricalNotReplayed, flags[0].skipReason);
                Assert.False(flags[0].skipGhost);
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
                // rec-restored is the recording the rewind restored (the live replay):
                // keep it in replay scope so the test isolates retirement, not BUG-B.
                ArmReplayScope(restored);

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
                orphanDebris.ParentAnchorRecordingId = "rec-retired-parent";

                var restored = MakeRecording("rec-restored", "Restored Vessel");

                var committed = new List<Recording> { retiredParent, orphanDebris, restored };
                // The restored sibling is the live replay after the rewind: keep it in
                // replay scope so the test isolates the retirement cascade, not BUG-B.
                ArmReplayScope(restored);

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
            SetPrivateField(host, "spawnSuppressedReasonScratch", new Dictionary<string, int>(StringComparer.Ordinal));
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
