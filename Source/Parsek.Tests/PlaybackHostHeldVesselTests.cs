using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit cells for the flight playback host's held-vessel bookkeeping: the hide / release
    /// reasons of a claimed-vessel hold ghost, the per-frame release pass, the Re-Fly invoke
    /// hold entry points up to the live vessel lookup, and the rewind point FLIGHTSTATE read a
    /// reload restores held vessels from. Every cell stays off the live-KSP paths (vessel
    /// lookup, ghost build, ghost positioning), which the in-game hold tests cover.
    /// </summary>
    [Collection("Sequential")]
    public class PlaybackHostHeldVesselTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public PlaybackHostHeldVesselTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            GhostPlaybackLogic.ResetForTesting();
            RewindInvokeContext.Clear();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-pbhost-held-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            RewindInvokeContext.Clear();
            GhostPlaybackLogic.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            try { Directory.Delete(tempDir, true); } catch (Exception) { }
        }

        // ---- helpers ----

        private const uint HeldPid = 4242u;

        private static ParsekFlight NewHost()
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            host.activeGhostChains = new Dictionary<uint, GhostChain>();
            // An uninitialized MonoBehaviour skips field initializers; the readonly invoke-hold
            // map is set the way the constructor would.
            FieldInfo holds = typeof(ParsekFlight).GetField(
                "reFlyInvokeHolds", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(holds);
            holds.SetValue(host, new Dictionary<uint, ChainClaimedVessel>());
            return host;
        }

        private static Recording CoveringRecording(uint pid, double startUT, double endUT, string guid = null)
        {
            var rec = new Recording
            {
                RecordingId = "cover-" + pid + "-" + startUT,
                VesselName = "Cover",
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT },
                    new TrajectoryPoint { ut = endUT },
                },
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        private static ChainClaimedVessel Claim(uint pid, double firstClaimUT, string guid = null)
        {
            return new ChainClaimedVessel { Pid = pid, FirstClaimUT = firstClaimUT, LaunchGuid = guid };
        }

        private static GhostChain ChainHolding(uint pid, double firstClaimUT, string guid = null)
        {
            return new GhostChain { OriginalVesselPid = pid, GhostStartUT = firstClaimUT, LaunchGuid = guid };
        }

        private bool AnyLog(string tag, string text)
        {
            return logLines.Any(l => l.Contains("[" + tag + "]") && l.Contains(text));
        }

        // ---- DecideReFlyInvokeHoldHideReason ----

        [Fact]
        public void InvokeHoldHide_ClaimAheadAndNothingDrawingIt_Shows()
        {
            Assert.Null(ParsekFlightPlaybackHost.DecideReFlyInvokeHoldHideReason(
                Claim(HeldPid, 500.0), 100.0, new List<Recording>()));
        }

        [Fact]
        public void InvokeHoldHide_ClockAtOrPastTheClaim_Claimed()
        {
            Assert.Equal("claimed", ParsekFlightPlaybackHost.DecideReFlyInvokeHoldHideReason(
                Claim(HeldPid, 500.0), 500.0, new List<Recording>()));
            Assert.Equal("claimed", ParsekFlightPlaybackHost.DecideReFlyInvokeHoldHideReason(
                Claim(HeldPid, 500.0), 600.0, new List<Recording>()));
        }

        [Fact]
        public void InvokeHoldHide_UnknownClaimUT_Claimed()
        {
            Assert.Equal("claimed", ParsekFlightPlaybackHost.DecideReFlyInvokeHoldHideReason(
                Claim(HeldPid, double.NaN), 100.0, new List<Recording>()));
        }

        [Fact]
        public void InvokeHoldHide_ARecordingOfTheVesselIsInItsWindow_CoveredByRecording()
        {
            var ers = new List<Recording> { CoveringRecording(HeldPid, 50.0, 150.0) };
            Assert.Equal("covered-by-recording", ParsekFlightPlaybackHost.DecideReFlyInvokeHoldHideReason(
                Claim(HeldPid, 500.0), 100.0, ers));
        }

        [Fact]
        public void InvokeHoldHide_RecordingOfAnotherLaunch_DoesNotCover()
        {
            var ers = new List<Recording>
            {
                CoveringRecording(HeldPid, 50.0, 150.0, guid: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            };
            Assert.Null(ParsekFlightPlaybackHost.DecideReFlyInvokeHoldHideReason(
                Claim(HeldPid, 500.0, guid: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), 100.0, ers));
        }

        [Fact]
        public void InvokeHoldHide_RecordingOutsideItsWindow_DoesNotCover()
        {
            var ers = new List<Recording> { CoveringRecording(HeldPid, 200.0, 300.0) };
            Assert.Null(ParsekFlightPlaybackHost.DecideReFlyInvokeHoldHideReason(
                Claim(HeldPid, 500.0), 100.0, ers));
        }

        // ---- ResolveHoldReleaseReason ----

        [Fact]
        public void HoldReleaseReason_ChainHoldPastItsClaim_Claimed()
        {
            var host = NewHost();
            host.activeGhostChains[HeldPid] = ChainHolding(HeldPid, 500.0);
            Assert.Equal("claimed", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 600.0));
        }

        [Fact]
        public void HoldReleaseReason_ChainHoldCoveredByACommittedRecording_CoveredByRecording()
        {
            var host = NewHost();
            CoveringRecording(HeldPid, 50.0, 150.0);
            host.activeGhostChains[HeldPid] = ChainHolding(HeldPid, 500.0);
            Assert.Equal("covered-by-recording", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 100.0));
        }

        [Fact]
        public void HoldReleaseReason_ChainHoldStillWanted_Unknown()
        {
            var host = NewHost();
            host.activeGhostChains[HeldPid] = ChainHolding(HeldPid, 500.0);
            Assert.Equal("unknown", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 100.0));
        }

        [Fact]
        public void HoldReleaseReason_ClaimedVesselListEntryUsesItsOwnClaimUT()
        {
            var host = NewHost();
            var chain = ChainHolding(1u, 900.0);
            chain.ClaimedVessels.Add(Claim(HeldPid, 300.0));
            host.activeGhostChains[1u] = chain;

            Assert.Equal("claimed", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 400.0));
            Assert.Equal("unknown", host.PlaybackHost.ResolveHoldReleaseReason(1u, 400.0));
        }

        [Fact]
        public void HoldReleaseReason_OnlyATerminatedChainHoldsIt_ChainInactive()
        {
            var host = NewHost();
            var chain = ChainHolding(HeldPid, 500.0);
            chain.IsTerminated = true;
            host.activeGhostChains[HeldPid] = chain;
            Assert.Equal("chain-inactive", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 600.0));
        }

        [Fact]
        public void HoldReleaseReason_NullChainEntryIsSkipped()
        {
            var host = NewHost();
            host.activeGhostChains[HeldPid] = null;
            Assert.Equal("chain-inactive", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 600.0));
        }

        [Fact]
        public void HoldReleaseReason_HeldNowhere_ChainInactive()
        {
            var host = NewHost();
            host.activeGhostChains = null;
            Assert.Equal("chain-inactive", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 600.0));
        }

        [Fact]
        public void HoldReleaseReason_InvokeHoldPastItsClaim_Claimed()
        {
            var host = NewHost();
            host.reFlyInvokeHolds[HeldPid] = Claim(HeldPid, 500.0);
            Assert.Equal("claimed", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 600.0));
        }

        [Fact]
        public void HoldReleaseReason_InvokeHoldStillWanted_Unknown()
        {
            var host = NewHost();
            host.reFlyInvokeHolds[HeldPid] = Claim(HeldPid, 500.0);
            Assert.Equal("unknown", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 100.0));
        }

        [Fact]
        public void HoldReleaseReason_AnActiveChainHoldIsAskedBeforeTheInvokeHold()
        {
            var host = NewHost();
            host.activeGhostChains[HeldPid] = ChainHolding(HeldPid, 900.0);
            host.reFlyInvokeHolds[HeldPid] = Claim(HeldPid, 300.0);
            Assert.Equal("unknown", host.PlaybackHost.ResolveHoldReleaseReason(HeldPid, 400.0));
        }

        // ---- UpdateClaimedVesselHoldGhosts: the release pass ----
        // Each cell leaves the desired set empty: a desired hold is built or positioned
        // through live KSP, which the in-game hold tests cover.

        private sealed class HoldFixture
        {
            public ParsekFlight Host;
            public ConfigNode Snapshot;
            public ParsekFlight.ClaimedVesselHoldGhostState State;
        }

        private static HoldFixture ShownHold(bool viaChain = true, double firstClaimUT = 500.0)
        {
            var f = new HoldFixture { Host = NewHost(), Snapshot = new ConfigNode("VESSEL") };
            f.Host.vesselGhoster = new VesselGhoster();
            f.Host.vesselGhoster.AddGhostedForTesting(HeldPid, "Held Probe", f.Snapshot);
            if (viaChain)
                f.Host.activeGhostChains[HeldPid] = ChainHolding(HeldPid, firstClaimUT);
            else
                f.Host.reFlyInvokeHolds[HeldPid] = Claim(HeldPid, firstClaimUT);
            f.State = new ParsekFlight.ClaimedVesselHoldGhostState
            {
                pid = HeldPid,
                vesselName = "Held Probe",
                snapshot = f.Snapshot,
                visual = f.Snapshot,
                shownSinceUT = 100.0,
            };
            f.Host.PlaybackHost.claimedVesselHoldGhosts[HeldPid] = f.State;
            return f;
        }

        private void AssertHoldReleased(HoldFixture f, string reason)
        {
            Assert.Empty(f.Host.PlaybackHost.claimedVesselHoldGhosts);
            Assert.True(AnyLog(ClaimedVesselHoldGhost.Tag,
                    "Claimed vessel hold ghost released: pid=4242 name='Held Probe' reason=" + reason + " "),
                "expected release reason " + reason + "; log:\n" + string.Join("\n", logLines));
        }

        [Fact]
        public void HoldPass_NothingHeldOrShown_IsANoOp()
        {
            var host = NewHost();
            host.PlaybackHost.UpdateClaimedVesselHoldGhosts(100.0);
            Assert.Empty(host.PlaybackHost.claimedVesselHoldGhosts);
            Assert.Empty(logLines.Where(l => l.Contains("[" + ClaimedVesselHoldGhost.Tag + "]")));
        }

        [Fact]
        public void HoldPass_ChainClaimReached_ReleasesAsClaimed()
        {
            HoldFixture f = ShownHold();
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(500.0);
            AssertHoldReleased(f, "claimed");
        }

        [Fact]
        public void HoldPass_ReleaseLineCarriesTheShownWindow()
        {
            HoldFixture f = ShownHold();
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(600.0);
            Assert.True(AnyLog(ClaimedVesselHoldGhost.Tag, "reason=claimed shownSinceUT=100.00 UT=600.00"));
        }

        [Fact]
        public void HoldPass_RecordingOfTheVesselNowDrawsIt_ReleasesAsCoveredByRecording()
        {
            HoldFixture f = ShownHold();
            CoveringRecording(HeldPid, 50.0, 150.0);
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(100.0);
            AssertHoldReleased(f, "covered-by-recording");
        }

        [Fact]
        public void HoldPass_ChainTerminated_ReleasesAsChainInactive()
        {
            HoldFixture f = ShownHold();
            f.Host.activeGhostChains[HeldPid].IsTerminated = true;
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(100.0);
            AssertHoldReleased(f, "chain-inactive");
        }

        [Fact]
        public void HoldPass_GhosterNoLongerTracksTheVessel_ReleasesAsGhostingEnded()
        {
            HoldFixture f = ShownHold();
            f.Host.vesselGhoster.ResetForTesting();
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(100.0);
            AssertHoldReleased(f, "ghosting-ended");
        }

        [Fact]
        public void HoldPass_NoGhoster_ReleasesAsGhostingEnded()
        {
            HoldFixture f = ShownHold();
            f.Host.vesselGhoster = null;
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(100.0);
            AssertHoldReleased(f, "ghosting-ended");
        }

        [Fact]
        public void HoldPass_SnapshotReplaced_GhostingEndedWinsOverTheClaimReason()
        {
            HoldFixture f = ShownHold();
            f.Host.vesselGhoster.AddGhostedForTesting(HeldPid, "Held Probe", new ConfigNode("VESSEL"));
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(600.0);
            AssertHoldReleased(f, "ghosting-ended");
        }

        [Fact]
        public void HoldPass_InvokeHoldClaimReached_ReleasesAsClaimed()
        {
            HoldFixture f = ShownHold(viaChain: false);
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(500.0);
            AssertHoldReleased(f, "claimed");
        }

        [Fact]
        public void HoldPass_InvokeHoldCoveredByRecording_ReleasesAsCoveredByRecording()
        {
            HoldFixture f = ShownHold(viaChain: false);
            CoveringRecording(HeldPid, 50.0, 150.0);
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(100.0);
            AssertHoldReleased(f, "covered-by-recording");
        }

        [Fact]
        public void HoldPass_FailedBuildReleasesWithoutAReleaseLine()
        {
            HoldFixture f = ShownHold();
            f.State.buildFailed = true;
            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(600.0);
            Assert.Empty(f.Host.PlaybackHost.claimedVesselHoldGhosts);
            Assert.False(AnyLog(ClaimedVesselHoldGhost.Tag, "Claimed vessel hold ghost released"));
        }

        [Fact]
        public void HoldPass_EachReleasedHoldGetsItsOwnReason()
        {
            HoldFixture f = ShownHold(firstClaimUT: 500.0);
            const uint otherPid = 777u;
            var otherSnapshot = new ConfigNode("VESSEL");
            f.Host.vesselGhoster.AddGhostedForTesting(otherPid, "Other", otherSnapshot);
            var otherChain = ChainHolding(otherPid, 900.0);
            otherChain.IsTerminated = true;
            f.Host.activeGhostChains[otherPid] = otherChain;
            f.Host.PlaybackHost.claimedVesselHoldGhosts[otherPid] = new ParsekFlight.ClaimedVesselHoldGhostState
            {
                pid = otherPid, vesselName = "Other", snapshot = otherSnapshot, shownSinceUT = 100.0,
            };

            f.Host.PlaybackHost.UpdateClaimedVesselHoldGhosts(600.0);

            Assert.Empty(f.Host.PlaybackHost.claimedVesselHoldGhosts);
            Assert.Equal(2, logLines.Count(l => l.Contains("Claimed vessel hold ghost released")));
            Assert.True(AnyLog(ClaimedVesselHoldGhost.Tag, "pid=4242 name='Held Probe' reason=claimed"));
            Assert.True(AnyLog(ClaimedVesselHoldGhost.Tag, "pid=777 name='Other' reason=chain-inactive"));
        }

        // ---- HoldReFlyInvokeVessels: the gates before the live vessel lookup ----

        [Fact]
        public void InvokeHold_NullOrEmptyEntries_HoldNothingAndBuildNoGhoster()
        {
            var host = NewHost();

            List<ReFlyHoldEntry> heldNull = host.PlaybackHost.HoldReFlyInvokeVessels(
                null, out int notLiveNull, out int alreadyNull);
            List<ReFlyHoldEntry> heldEmpty = host.PlaybackHost.HoldReFlyInvokeVessels(
                new List<ReFlyHoldEntry>(), out int notLiveEmpty, out int alreadyEmpty);

            Assert.Empty(heldNull);
            Assert.Empty(heldEmpty);
            Assert.Equal(0, notLiveNull + alreadyNull + notLiveEmpty + alreadyEmpty);
            Assert.Null(host.vesselGhoster);
        }

        [Fact]
        public void InvokeHold_NullAndZeroPidEntries_AreSkippedUncounted()
        {
            var host = NewHost();
            var entries = new List<ReFlyHoldEntry> { null, new ReFlyHoldEntry { Pid = 0u } };

            List<ReFlyHoldEntry> held = host.PlaybackHost.HoldReFlyInvokeVessels(
                entries, out int notLive, out int alreadyHeld);

            Assert.Empty(held);
            Assert.Equal(0, notLive);
            Assert.Equal(0, alreadyHeld);
            Assert.NotNull(host.vesselGhoster);
            Assert.Empty(host.reFlyInvokeHolds);
        }

        [Fact]
        public void InvokeHold_VesselAlreadyHeld_CountsAlreadyHeldAndAddsNoInvokeHold()
        {
            var host = NewHost();
            host.vesselGhoster = new VesselGhoster();
            host.vesselGhoster.AddGhostedForTesting(HeldPid, "Held Probe", new ConfigNode("VESSEL"));
            var entries = new List<ReFlyHoldEntry>
            {
                new ReFlyHoldEntry { Pid = HeldPid, FirstClaimUT = 500.0 },
            };

            List<ReFlyHoldEntry> held = host.PlaybackHost.HoldReFlyInvokeVessels(
                entries, out int notLive, out int alreadyHeld);

            Assert.Empty(held);
            Assert.Equal(0, notLive);
            Assert.Equal(1, alreadyHeld);
            Assert.Empty(host.reFlyInvokeHolds);
        }

        // ---- RestoreReFlyInvokeHoldsFromMarker: the gates before the live vessel lookup ----

        [Fact]
        public void InvokeHoldRestore_NoScenario_DoesNothing()
        {
            var host = NewHost();
            host.PlaybackHost.RestoreReFlyInvokeHoldsFromMarker(100.0);
            Assert.Null(host.vesselGhoster);
            Assert.Empty(logLines.Where(l => l.Contains("[" + ClaimedVesselHoldGhost.Tag + "]")));
        }

        [Fact]
        public void InvokeHoldRestore_NoMarkerOrNoSavedHolds_DoesNothing()
        {
            var host = NewHost();
            ParsekScenario.SetInstanceForTesting(new ParsekScenario { ActiveReFlySessionMarker = null });
            host.PlaybackHost.RestoreReFlyInvokeHoldsFromMarker(100.0);

            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker { SessionId = "sess-empty" },
            });
            host.PlaybackHost.RestoreReFlyInvokeHoldsFromMarker(100.0);

            Assert.Null(host.vesselGhoster);
            Assert.Empty(logLines.Where(l => l.Contains("[" + ClaimedVesselHoldGhost.Tag + "]")));
        }

        [Fact]
        public void InvokeHoldRestore_InvokeInFlight_DefersToTheInvoke()
        {
            var host = NewHost();
            var marker = new ReFlySessionMarker { SessionId = "sess-pending" };
            marker.InvokeHolds.Add(new ReFlyHoldEntry { Pid = HeldPid, FirstClaimUT = 500.0 });
            ParsekScenario.SetInstanceForTesting(new ParsekScenario { ActiveReFlySessionMarker = marker });
            RewindInvokeContext.Pending = true;

            host.PlaybackHost.RestoreReFlyInvokeHoldsFromMarker(100.0);

            Assert.Null(host.vesselGhoster);
            Assert.Empty(host.reFlyInvokeHolds);
            Assert.True(AnyLog(ClaimedVesselHoldGhost.Tag,
                "Re-Fly invoke holds not restored: an invoke is in flight sess=sess-pending"));
        }

        [Fact]
        public void InvokeHoldRestore_OnlyUnusableEntries_LogsAZeroSummary()
        {
            var host = NewHost();
            var marker = new ReFlySessionMarker { SessionId = "sess-junk", LoadedFromSave = true };
            marker.InvokeHolds.Add(null);
            marker.InvokeHolds.Add(new ReFlyHoldEntry { Pid = 0u });
            ParsekScenario.SetInstanceForTesting(new ParsekScenario { ActiveReFlySessionMarker = marker });

            host.PlaybackHost.RestoreReFlyInvokeHoldsFromMarker(123.4);

            Assert.NotNull(host.vesselGhoster);
            Assert.Empty(host.reFlyInvokeHolds);
            Assert.True(AnyLog(ClaimedVesselHoldGhost.Tag,
                "Re-Fly invoke holds restored after reload: sess=sess-junk saved=2 restored=0 heldLive=0 " +
                "alreadyHeld=0 claimPassed=0 otherLaunch=0 noSnapshot=0 UT=123.4 marker=save"));
        }

        // ---- ReadRewindPointFlightState ----

        private string WriteSave(string name, string text)
        {
            string path = Path.Combine(tempDir, name);
            File.WriteAllText(path, text);
            return path;
        }

        private static ParsekScenario ScenarioWithPoints(params RewindPoint[] points)
        {
            return new ParsekScenario { RewindPoints = new List<RewindPoint>(points) };
        }

        private void RouteQuicksavesToTempDir()
        {
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting =
                rp => Path.Combine(tempDir, rp.QuicksaveFilename);
        }

        private const string GameWrappedSave =
            "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t\tVESSEL\n\t\t{\n\t\t\tpid = abc\n\t\t}\n\t}\n}\n";

        [Fact]
        public void RewindPointFlightState_GameWrappedSave_ReturnsItsFlightState()
        {
            RouteQuicksavesToTempDir();
            WriteSave("rp1.sfs", GameWrappedSave);
            var scenario = ScenarioWithPoints(new RewindPoint { RewindPointId = "rp_1", QuicksaveFilename = "rp1.sfs" });

            ConfigNode flightState = ParsekFlightPlaybackHost.ReadRewindPointFlightState(scenario, "rp_1");

            Assert.NotNull(flightState);
            Assert.Equal("1.12.5", flightState.GetValue("version"));
            Assert.Single(flightState.GetNodes("VESSEL"));
        }

        [Fact]
        public void RewindPointFlightState_UnwrappedSave_ReadsFlightStateFromTheRoot()
        {
            RouteQuicksavesToTempDir();
            WriteSave("rp2.sfs", "FLIGHTSTATE\n{\n\tversion = 9\n}\n");
            var scenario = ScenarioWithPoints(new RewindPoint { RewindPointId = "rp_2", QuicksaveFilename = "rp2.sfs" });

            ConfigNode flightState = ParsekFlightPlaybackHost.ReadRewindPointFlightState(scenario, "rp_2");

            Assert.NotNull(flightState);
            Assert.Equal("9", flightState.GetValue("version"));
        }

        [Fact]
        public void RewindPointFlightState_SaveWithoutFlightState_Null()
        {
            RouteQuicksavesToTempDir();
            WriteSave("rp3.sfs", "GAME\n{\n\tversion = 1\n}\n");
            var scenario = ScenarioWithPoints(new RewindPoint { RewindPointId = "rp_3", QuicksaveFilename = "rp3.sfs" });

            Assert.Null(ParsekFlightPlaybackHost.ReadRewindPointFlightState(scenario, "rp_3"));
            Assert.False(AnyLog(ClaimedVesselHoldGhost.Tag, "unreadable"));
        }

        [Fact]
        public void RewindPointFlightState_ReadsTheNamedRewindPointsFile()
        {
            RouteQuicksavesToTempDir();
            WriteSave("a.sfs", "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = A\n\t}\n}\n");
            WriteSave("b.sfs", "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = B\n\t}\n}\n");
            var scenario = ScenarioWithPoints(
                null,
                new RewindPoint { RewindPointId = "rp_a", QuicksaveFilename = "a.sfs" },
                new RewindPoint { RewindPointId = "rp_b", QuicksaveFilename = "b.sfs" });

            Assert.Equal("B", ParsekFlightPlaybackHost.ReadRewindPointFlightState(scenario, "rp_b").GetValue("version"));
            Assert.Equal("A", ParsekFlightPlaybackHost.ReadRewindPointFlightState(scenario, "rp_a").GetValue("version"));
        }

        [Fact]
        public void RewindPointFlightState_UnknownRewindPoint_WarnsAndReturnsNull()
        {
            RouteQuicksavesToTempDir();
            var scenario = ScenarioWithPoints(new RewindPoint { RewindPointId = "rp_1", QuicksaveFilename = "rp1.sfs" });

            Assert.Null(ParsekFlightPlaybackHost.ReadRewindPointFlightState(scenario, "rp_missing"));
            Assert.True(AnyLog(ClaimedVesselHoldGhost.Tag,
                "Re-Fly invoke hold reload: rewind point save unreadable rp=rp_missing path='<none>'"));
        }

        [Fact]
        public void RewindPointFlightState_FileMissing_WarnsWithThePath()
        {
            RouteQuicksavesToTempDir();
            var scenario = ScenarioWithPoints(new RewindPoint { RewindPointId = "rp_1", QuicksaveFilename = "gone.sfs" });

            Assert.Null(ParsekFlightPlaybackHost.ReadRewindPointFlightState(scenario, "rp_1"));
            Assert.True(AnyLog(ClaimedVesselHoldGhost.Tag, "rewind point save unreadable rp=rp_1 path='"));
            Assert.True(AnyLog(ClaimedVesselHoldGhost.Tag, "gone.sfs'"));
        }

        [Fact]
        public void RewindPointFlightState_NoRewindPointsOrNoId_NullWithoutAWarning()
        {
            RouteQuicksavesToTempDir();
            var noList = new ParsekScenario { RewindPoints = null };
            var withList = ScenarioWithPoints(new RewindPoint { RewindPointId = "rp_1", QuicksaveFilename = "rp1.sfs" });

            Assert.Null(ParsekFlightPlaybackHost.ReadRewindPointFlightState(noList, "rp_1"));
            Assert.Null(ParsekFlightPlaybackHost.ReadRewindPointFlightState(withList, null));
            Assert.Null(ParsekFlightPlaybackHost.ReadRewindPointFlightState(withList, ""));
            Assert.Empty(logLines.Where(l => l.Contains("[" + ClaimedVesselHoldGhost.Tag + "]")));
        }
    }
}
