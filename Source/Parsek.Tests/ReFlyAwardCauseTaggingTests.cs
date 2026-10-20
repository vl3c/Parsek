using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ReFlyAwardCauseTaggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly ReFlySessionMarker marker = new ReFlySessionMarker
        {
            SessionId = "sess-1",
            TreeId = "tree-1",
            ActiveReFlyRecordingId = "P",
            OriginChildRecordingId = "O",
        };

        public ReFlyAwardCauseTaggingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            ParsekScenario.ResetInstanceForTesting();
            RecoveryDispatchScope.ResetForTesting();
            ReFlySwitchGate.ResetForTesting();
            ReFlyAwardTagging.ResetForTesting();
        }

        public void Dispose()
        {
            ReFlySwitchGate.ResetForTesting();
            ReFlyAwardTagging.ResetForTesting();
            RecoveryDispatchScope.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording Rec(string id, uint pid)
        {
            return new Recording { RecordingId = id, VesselPersistentId = pid, TreeId = "tree-1" };
        }

        // The session: the re-fly P (vessel 100, active) split off a probe D (200, background).
        // O (300) is the superseded origin, still an open line end; E (400) is an open line end
        // the background map does not list.
        private static RecordingTree SessionTree()
        {
            var tree = new RecordingTree { Id = "tree-1", ActiveRecordingId = "P" };
            foreach (var r in new[] { Rec("P", 100u), Rec("D", 200u), Rec("O", 300u), Rec("E", 400u) })
                tree.Recordings[r.RecordingId] = r;
            tree.BackgroundMap[200u] = "D";
            return tree;
        }

        private void InstallLiveSession(string liveTag)
        {
            RecordingTree tree = SessionTree();
            ReFlySwitchGate.LiveSessionProviderForTesting = () =>
                new ReFlySwitchGate.LiveSession { Marker = marker, Tree = tree };
            ReFlyAwardTagging.InFlightProviderForTesting = () => true;
            GameStateRecorder.TagResolverForTesting = () => liveTag;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
        }

        [Fact]
        public void ResolveSessionRecordingForVessel_ActiveThenBackgroundThenOpenLineEnd()
        {
            RecordingTree tree = SessionTree();

            Assert.Equal("P", ReFlyAwardTagging.ResolveSessionRecordingForVessel(tree, 100u));
            Assert.Equal("D", ReFlyAwardTagging.ResolveSessionRecordingForVessel(tree, 200u));
            Assert.Equal("E", ReFlyAwardTagging.ResolveSessionRecordingForVessel(tree, 400u));
            Assert.Null(ReFlyAwardTagging.ResolveSessionRecordingForVessel(tree, 999u));
            Assert.Null(ReFlyAwardTagging.ResolveSessionRecordingForVessel(tree, 0u));
            Assert.Null(ReFlyAwardTagging.ResolveSessionRecordingForVessel(null, 200u));
        }

        [Fact]
        public void ResolveByCause_SplitOffProbe_FilesUnderItsOwnRecording()
        {
            var d = ReFlyAwardTagging.ResolveByCause(200u, true, marker, SessionTree());

            Assert.Equal("D", d.Tag);
            Assert.Equal(ReFlyAwardTagging.ReasonCauseVessel, d.Reason);
            Assert.False(d.Substituted);
        }

        [Fact]
        public void ResolveByCause_OriginVessel_LandsOnTheProvisional()
        {
            var d = ReFlyAwardTagging.ResolveByCause(300u, true, marker, SessionTree());

            Assert.Equal("P", d.Tag);
            Assert.True(d.Substituted);
            Assert.Equal(ReFlyAwardTagging.ReasonUnboundOrigin, d.Reason);
        }

        [Theory]
        [InlineData(999u, true, true)]
        [InlineData(0u, true, true)]
        [InlineData(200u, false, true)]
        [InlineData(200u, true, false)]
        public void ResolveByCause_UndecidedCases_LeaveTheLivePath(uint pid, bool inFlight, bool haveMarker)
        {
            var d = ReFlyAwardTagging.ResolveByCause(pid, inFlight, haveMarker ? marker : null, SessionTree());

            Assert.Null(d.Tag);
        }

        [Fact]
        public void ScienceFromTwoVesselsInTheSession_IsFiledUnderEachVessel()
        {
            InstallLiveSession("P");
            var recorder = new GameStateRecorder();

            recorder.CaptureScienceSubject(2f, "crewReport@KerbinFlyingLow", 2f, 5f, 1f, 50.0, "Probe", null, 200u);
            recorder.CaptureScienceSubject(3f, "crewReport@KerbinFlyingHigh", 3f, 5f, 1f, 60.0, "Upper", null, 100u);
            recorder.CaptureScienceSubject(1f, "crewReport@KerbinSrfLanded", 1f, 5f, 1f, 70.0, "Stranger", null, 999u);

            var tags = GameStateRecorder.PendingScienceSubjects.Select(s => s.recordingId).ToArray();
            Assert.Equal(new[] { "D", "P", "P" }, tags);
            Assert.Contains(logLines, l => l.Contains("outcome=tagged-by-cause") && l.Contains("causePid=200"));
        }

        [Fact]
        public void ScienceOutsideASession_KeepsTheLiveTag()
        {
            GameStateRecorder.TagResolverForTesting = () => "live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            ReFlySwitchGate.LiveSessionProviderForTesting = () => new ReFlySwitchGate.LiveSession();

            new GameStateRecorder().CaptureScienceSubject(
                2f, "crewReport@KerbinFlyingLow", 2f, 5f, 1f, 50.0, "Probe", null, 200u);

            Assert.Equal("live", Assert.Single(GameStateRecorder.PendingScienceSubjects).recordingId);
        }

        [Fact]
        public void LabPayoutFromTheSplitOffProbe_IsFiledUnderTheProbe()
        {
            InstallLiveSession("P");

            GameStateRecorder.OnScienceLabCredited(80.0, 10f, 30f, "ScienceTransmission", "Probe", 200u);

            var evt = GameStateStore.Events.Last(e => e.eventType == GameStateEventType.ScienceLabCredited);
            Assert.Equal("D", evt.recordingId);
        }

        [Fact]
        public void EventInsideARecoveryOfTheProbe_IsFiledUnderTheProbe()
        {
            InstallLiveSession("P");
            RecoveryDispatchScope.Open(RecoveredVesselIdentity.FromRawName("Probe"), 200u, "test");
            try
            {
                var evt = new GameStateEvent
                {
                    ut = 90.0,
                    eventType = GameStateEventType.FundsChanged,
                    key = "VesselRecovery",
                    valueBefore = 0,
                    valueAfter = 100,
                };
                GameStateRecorder.Emit(ref evt, "test");

                Assert.Equal("D", evt.recordingId);
            }
            finally
            {
                RecoveryDispatchScope.Close("test");
            }
        }

        [Fact]
        public void StrategyConversionInsideARecoveryOfTheProbe_FollowsItsAward()
        {
            InstallLiveSession("P");
            RecoveryDispatchScope.Open(RecoveredVesselIdentity.FromRawName("Probe"), 200u, "test");
            try
            {
                Assert.Equal("D", GameStateRecorder.ResolveStrategyConversionRecordingTag("VesselRecovery", 90.0));
            }
            finally
            {
                RecoveryDispatchScope.Close("test");
            }
        }

        [Fact]
        public void EventWithNoVessel_InASession_KeepsTheLiveRecording()
        {
            InstallLiveSession("P");
            var evt = new GameStateEvent
            {
                ut = 90.0,
                eventType = GameStateEventType.FundsChanged,
                key = "ContractReward",
                valueBefore = 0,
                valueAfter = 100,
            };

            GameStateRecorder.Emit(ref evt, "test");

            Assert.Equal("P", evt.recordingId);
        }
    }
}
