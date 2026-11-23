using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// EVA-JETPACK-DEPLOY-LOST-AT-PROMOTION: nothing polls a kerbal between the switch to him and
    /// the post-switch trigger, and a promotion skips the general seeds, so a pack deployed in
    /// that window was never recorded and the ghost's plume gate stayed shut. The promotion now
    /// emits the jetpack and ragdoll state wherever the recording's own events do not say so.
    /// </summary>
    [Collection("Sequential")]
    public class EvaPromotionSeedTests : System.IDisposable
    {
        private const uint Pid = 723310870u;
        private const double SeedUT = 423.87;
        private const double NowUT = 435.09;
        private readonly List<string> logLines = new List<string>();

        public EvaPromotionSeedTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static PartEvent Ev(double ut, PartEventType type, uint pid = Pid)
        {
            return new PartEvent { ut = ut, partPersistentId = pid, eventType = type, partName = "kerbalEVA" };
        }

        private static List<PartEvent> Seed(bool deployed, bool ragdoll, params PartEvent[] recorded)
        {
            var sets = new PartTrackingSets();
            if (deployed) sets.jetpackDeployedParts.Add(Pid);
            if (ragdoll) sets.ragdollParts.Add(Pid);
            var events = new List<PartEvent>();
            int emitted = PartStateSeeder.EmitEvaStateSeedEvents(
                sets, new[] { Pid }, new List<PartEvent>(recorded), events,
                SeedUT, NowUT, "Recorder", _ => "kerbalEVAfemale");
            Assert.Equal(events.Count, emitted);
            return events;
        }

        /// <summary>Replays a recording's EVA events; returns whether the plume is ever open.</summary>
        private static bool PlumeEverOpens(IEnumerable<PartEvent> events)
        {
            var state = new GhostPlaybackState();
            bool opened = false;
            foreach (PartEvent evt in FlightRecorder.StableSortPartEventsByUT(events.ToList()))
            {
                GhostPlaybackLogic.TryUpdateEvaFlags(state, evt.eventType);
                opened |= GhostPlaybackLogic.ShouldEmitEvaJetpackPlume(
                    state.evaJetpackDeployed, state.evaJetpackThrusting, state.evaRagdoll);
            }
            return opened;
        }

        [Fact]
        public void PackOutAndNothingRecorded_EmitsDeployedAtTheSeedUT()
        {
            PartEvent e = Assert.Single(Seed(deployed: true, ragdoll: false));
            Assert.Equal(PartEventType.EvaJetpackDeployed, e.eventType);
            Assert.Equal(SeedUT, e.ut);
            Assert.Equal(Pid, e.partPersistentId);
            Assert.Equal("kerbalEVAfemale", e.partName);
            Assert.Equal(0f, e.value);
            Assert.Equal(0, e.moduleIndex);
            Assert.Contains(logLines, l => l.Contains("[Recorder]") && l.Contains(
                "Seed event: EvaJetpackDeployed pid=723310870 part='kerbalEVAfemale' ut=423.87 (promotion)"));
        }

        [Fact]
        public void RagdollNowAndNothingRecorded_EmitsRagdollStartedAtTheSeedUT()
        {
            PartEvent e = Assert.Single(Seed(deployed: false, ragdoll: true));
            Assert.Equal(PartEventType.EvaRagdollStarted, e.eventType);
            Assert.Equal(SeedUT, e.ut);
        }

        [Fact]
        public void StowedAndUprightWithNothingRecorded_EmitsNothing()
        {
            Assert.Empty(Seed(deployed: false, ragdoll: false));
        }

        [Fact]
        public void AlreadyRecorded_EmitsNoDuplicate()
        {
            Assert.Empty(Seed(true, true,
                Ev(424, PartEventType.EvaJetpackDeployed),
                Ev(425, PartEventType.EvaRagdollStarted)));
        }

        [Fact]
        public void TheBackgroundRecorderAlreadyRecordedTheDeploy_EmitsNothing()
        {
            // The same Layer-1 transitions BackgroundRecorder.CheckEvaState appends to the tree
            // recording, up to and including the promotion's own tick.
            var bgJetpack = new HashSet<uint>();
            var bgRagdoll = new HashSet<uint>();
            PartEvent deploy = FlightRecorder.CheckEvaJetpackTransition(
                Pid, "kerbalEVAfemale", true, bgJetpack, NowUT).Value;
            PartEvent ragdoll = FlightRecorder.CheckEvaRagdollTransition(
                Pid, "kerbalEVAfemale", true, bgRagdoll, NowUT).Value;
            Assert.Equal(PartEventType.EvaJetpackDeployed, deploy.eventType);

            Assert.Empty(Seed(true, false, deploy));
            Assert.Empty(Seed(true, true, deploy, ragdoll));
        }

        [Fact]
        public void StowedNowWhileTheRecordingSaysDeployed_EmitsNothing()
        {
            Assert.Empty(Seed(false, false, Ev(424, PartEventType.EvaJetpackDeployed)));
        }

        [Fact]
        public void LastRecordedWordStowed_EmitsDeployedAtTheCurrentUT()
        {
            PartEvent e = Assert.Single(Seed(true, false,
                Ev(424, PartEventType.EvaJetpackDeployed),
                Ev(430, PartEventType.EvaJetpackStowed)));
            Assert.Equal(PartEventType.EvaJetpackDeployed, e.eventType);
            Assert.Equal(NowUT, e.ut);
        }

        [Fact]
        public void LastWordIsReadByUT_NotByListOrder_AndTiesGoToTheLaterEntry()
        {
            Assert.Empty(Seed(true, false,
                Ev(430, PartEventType.EvaJetpackDeployed),
                Ev(424, PartEventType.EvaJetpackStowed)));
            Assert.Empty(Seed(true, false,
                Ev(430, PartEventType.EvaJetpackStowed),
                Ev(430, PartEventType.EvaJetpackDeployed)));
            Assert.Single(Seed(true, false,
                Ev(430, PartEventType.EvaJetpackDeployed),
                Ev(430, PartEventType.EvaJetpackStowed)));
        }

        [Fact]
        public void RagdollRecordedOpenAndUprightNow_EmitsRagdollEndedAtTheCurrentUT()
        {
            PartEvent e = Assert.Single(Seed(false, false, Ev(425, PartEventType.EvaRagdollStarted)));
            Assert.Equal(PartEventType.EvaRagdollEnded, e.eventType);
            Assert.Equal(NowUT, e.ut);
            Assert.Empty(Seed(false, false,
                Ev(425, PartEventType.EvaRagdollStarted), Ev(426, PartEventType.EvaRagdollEnded)));
        }

        [Fact]
        public void AnotherPartsEvents_DoNotCount()
        {
            PartEvent e = Assert.Single(Seed(true, false, Ev(424, PartEventType.EvaJetpackDeployed, pid: 5u)));
            Assert.Equal(SeedUT, e.ut);
        }

        [Fact]
        public void APartAlreadyCarryingThrust_IsSeededNowSoItsPastDoesNotChange()
        {
            // A resumed recording from before this seed existed: thrust, no deploy. A backdated
            // deploy would draw plumes over what was already recorded.
            var recorded = new[]
            {
                Ev(425, PartEventType.EvaJetpackThrustStarted),
                Ev(426, PartEventType.EvaJetpackThrustStopped),
            };
            List<PartEvent> seeds = Seed(true, true, recorded);
            Assert.Equal(2, seeds.Count);
            Assert.All(seeds, s => Assert.Equal(NowUT, s.ut));
            Assert.False(PlumeEverOpens(recorded.Concat(seeds)));
        }

        [Fact]
        public void NullAndEmptyInputs_EmitNothing()
        {
            var events = new List<PartEvent>();
            var sets = new PartTrackingSets();
            sets.jetpackDeployedParts.Add(Pid);
            Assert.Equal(0, PartStateSeeder.EmitEvaStateSeedEvents(
                null, new[] { Pid }, null, events, 1, 2, "Recorder", null));
            Assert.Equal(0, PartStateSeeder.EmitEvaStateSeedEvents(
                sets, null, null, events, 1, 2, "Recorder", null));
            Assert.Empty(events);
            // A missing recording (no active tree) still seeds.
            Assert.Equal(1, PartStateSeeder.EmitEvaStateSeedEvents(
                sets, new[] { Pid }, null, events, 1, 2, "Recorder", null));
        }

        [Fact]
        public void TheReportedFlight_OpensThePlumeOnlyWithTheSeed()
        {
            // EVA-17's recording 9cee0a67: pack out before the promotion, four bursts after it.
            var polled = new List<PartEvent>
            {
                Ev(435.25, PartEventType.EvaJetpackThrustStarted),
                Ev(435.59, PartEventType.EvaJetpackThrustStopped),
                Ev(437.27, PartEventType.EvaJetpackStowed),
            };
            Assert.False(PlumeEverOpens(polled));
            Assert.True(PlumeEverOpens(Seed(true, false).Concat(polled)));
            // A promotion in mid-thrust: the thrust edge can land in the seed's own tick and the
            // recorder's seed-first insertion keeps the order.
            Assert.True(PlumeEverOpens(Seed(true, false, Ev(424, PartEventType.EvaJetpackStowed))
                .Concat(new[] { Ev(NowUT, PartEventType.EvaJetpackThrustStarted) })));
        }

        [Fact]
        public void RagdollMirror_OpensThePlumeOnlyWithTheSeed()
        {
            var recorded = new[]
            {
                Ev(424, PartEventType.EvaJetpackDeployed),
                Ev(425, PartEventType.EvaRagdollStarted),
            };
            var burst = new[] { Ev(436, PartEventType.EvaJetpackThrustStarted) };
            Assert.False(PlumeEverOpens(recorded.Concat(burst)));
            Assert.True(PlumeEverOpens(recorded.Concat(Seed(true, false, recorded)).Concat(burst)));
        }

        [Fact]
        public void TheSeedDoesNotMoveTheLastInterestingUT()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = SeedUT, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 2000.0, bodyName = "Kerbin" });
            double seedUT = FlightRecorder.ResolveChainPromotionSeedUT(rec, 2000.0);
            var sets = new PartTrackingSets();
            sets.jetpackDeployedParts.Add(Pid);
            sets.ragdollParts.Add(Pid);

            PartStateSeeder.EmitEvaStateSeedEvents(
                sets, new[] { Pid }, rec.PartEvents.ToList(), rec.PartEvents, seedUT, 2000.0, "Recorder", null);

            Assert.Equal(2, rec.PartEvents.Count);
            Assert.Equal(SeedUT, RecordingOptimizer.FindLastInterestingUT(rec));
        }

        [Fact]
        public void NoOpClassifier_StartSeedIsPassive_ALaterDeployIsThePlayers()
        {
            Recording Segment(double deployUT)
            {
                var rec = new Recording { RecordingId = "seg", ExplicitStartUT = 100.0 };
                rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
                rec.Points.Add(new TrajectoryPoint { ut = 200.0, bodyName = "Kerbin" });
                rec.PartEvents.Add(Ev(deployUT, PartEventType.EvaJetpackDeployed));
                return rec;
            }

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(Segment(100.0), false, out string reason));
            Assert.Null(reason);
            Assert.Contains(logLines, l => l.Contains("[SwitchSegment]") && l.Contains("jetpackSeeds=1"));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(Segment(100.5), false, out reason));
            Assert.Equal("part-event:EvaJetpackDeployed", reason);
            // Without an owner there is no start to compare with: kept.
            Assert.True(SwitchSegmentNoOpClassifier.IsMeaningfulPartEvent(
                Ev(100.0, PartEventType.EvaJetpackDeployed)));
        }

        [Fact]
        public void ResetPartEventTrackingState_EmitsTheSeedsOnTheEvaPromotionBranchOnly()
        {
            string text = File.ReadAllText(Path.GetFullPath(Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "Source", "Parsek", "Recording", "FlightRecorder.PartEvents.cs"))).Replace("\r\n", "\n");
            int branch = text.IndexOf("if (!emitSeedEvents)\n", System.StringComparison.Ordinal);
            int call = text.IndexOf(
                "if (v.isEVA)\n                    EmitEvaStateSeedEventsForPromotion(v, activeRec, activeRecId);\n"
                + "                return;", System.StringComparison.Ordinal);
            int full = text.IndexOf("PartStateSeeder.EmitSeedEvents(", System.StringComparison.Ordinal);
            Assert.True(branch >= 0 && call > branch && full > call,
                "the EVA promotion seeds must be the last step of the promotion branch");
        }
    }
}
