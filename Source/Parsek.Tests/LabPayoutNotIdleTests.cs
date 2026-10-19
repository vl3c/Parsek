using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A stationary flight whose science lab paid out (a lab transmission or a lab recovery,
    /// <see cref="GameStateEventType.ScienceLabCredited"/>) banked science the player earned,
    /// so the idle-on-pad scene-exit auto-discard must not throw the recording away (owner
    /// ruling 2026-10-17, IDLE-ON-PAD-DISCARDS-LAB-PAYOUT-FLIGHT). Only a payout tagged to one
    /// of the tree's own recordings vetoes.
    /// </summary>
    [Collection("Sequential")]
    public class LabPayoutNotIdleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LabPayoutNotIdleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree LabTree()
        {
            var rec = new Recording
            {
                RecordingId = "lab-rec",
                VesselName = "Pad Lab",
                StartSituation = "Landed",
                MaxDistanceFromLaunch = 0.3
            };
            rec.Points.Add(new TrajectoryPoint { ut = 10.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 900.0, bodyName = "Kerbin" });
            return new RecordingTree
            {
                Id = "tree-lab",
                TreeName = "Pad Lab",
                Recordings = new Dictionary<string, Recording> { { "lab-rec", rec } }
            };
        }

        private static GameStateEvent LabCredit(string recordingId, string reason, double ut)
        {
            GameStateEvent e = GameStateRecorder.BuildScienceLabCreditEvent(
                ut, 100f, 112.5f, reason, "Pad Lab");
            e.recordingId = recordingId;
            return e;
        }

        private static void AddToStore(GameStateEvent e)
        {
            GameStateStore.AddEvent(ref e);
        }

        [Fact]
        public void LabTransmissionTaggedToTree_NotIdle()
        {
            RecordingTree tree = LabTree();
            AddToStore(LabCredit("lab-rec", "ScienceTransmission", 600.0));

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: not idle - tree has 1 lab payout(s) tree='Pad Lab'"));
            Assert.DoesNotContain(logLines, l => l.Contains("idle on pad"));
        }

        [Fact]
        public void TwoTaggedTransmissions_CountsEveryPayout()
        {
            RecordingTree tree = LabTree();
            var events = new List<GameStateEvent>
            {
                LabCredit("lab-rec", "ScienceTransmission", 300.0),
                LabCredit("lab-rec", "ScienceTransmission", 700.0)
            };

            Assert.Equal(2, ParsekFlight.CountTaggedLabPayouts(tree, events));
            Assert.True(ParsekFlight.TreeHasLabPayoutsForIdle(tree, "IsActiveTreeIdleOnPad", events));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsActiveTreeIdleOnPad: not idle - tree has 2 lab payout(s) tree='Pad Lab'"));
        }

        // The production shape of a flight Recover's lab credit: stock pays it at the Space
        // Center after every idle check, untagged (RecoveryDispatchScope does not own lab
        // credits). This veto never sees it; the recovery half of the ruling is covered by
        // TreeHasPendingLabRecoveryForIdle (RecoveryLabIdleVetoTests).
        [Fact]
        public void UntaggedRecoveryCredit_DoesNotVetoThroughThePayoutScan()
        {
            RecordingTree tree = LabTree();
            AddToStore(LabCredit("", "VesselRecovery", 905.0));

            Assert.Equal(0, ParsekFlight.CountTaggedLabPayouts(tree, GameStateStore.Events));
            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        [Fact]
        public void LabPayoutOnAnotherTree_StaysIdle()
        {
            RecordingTree tree = LabTree();
            AddToStore(LabCredit("other-tree-rec", "ScienceTransmission", 600.0));
            AddToStore(LabCredit("", "ScienceTransmission", 610.0));

            Assert.Equal(0, ParsekFlight.CountTaggedLabPayouts(tree, GameStateStore.Events));
            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.DoesNotContain(logLines, l => l.Contains("lab payout(s)"));
        }

        [Fact]
        public void NonLabEventTaggedToTree_StaysIdle()
        {
            RecordingTree tree = LabTree();
            var e = new GameStateEvent
            {
                ut = 600.0,
                eventType = GameStateEventType.ScienceChanged,
                key = "ScienceTransmission",
                valueBefore = 100f,
                valueAfter = 112.5f,
                recordingId = "lab-rec"
            };
            AddToStore(e);

            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        [Fact]
        public void LabPayoutAndConverterToggle_ConverterVetoStillFirst()
        {
            RecordingTree tree = LabTree();
            tree.Recordings["lab-rec"].PartEvents.Add(new PartEvent
            {
                ut = 400.0,
                eventType = PartEventType.ConverterActivated,
                partPersistentId = 3u,
                partName = "MiniISRU"
            });
            AddToStore(LabCredit("lab-rec", "ScienceTransmission", 600.0));

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: not idle - tree has 1 converter toggle(s) tree='Pad Lab'"));
            // The converter veto short-circuits; the lab veto is ordered after it.
            Assert.DoesNotContain(logLines, l => l.Contains("lab payout(s)"));
        }

        [Fact]
        public void NoPayout_StaysIdle()
        {
            RecordingTree tree = LabTree();

            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.False(ParsekFlight.TreeHasLabPayoutsForIdle(tree, "IsTreeIdleOnPad"));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: all 1 recordings within 30m"));
            Assert.DoesNotContain(logLines, l => l.Contains("lab payout(s)"));
        }

        [Fact]
        public void CountTaggedLabPayouts_NullSafe()
        {
            Assert.Equal(0, ParsekFlight.CountTaggedLabPayouts(null, null));
            Assert.Equal(0, ParsekFlight.CountTaggedLabPayouts(LabTree(), null));
            Assert.Equal(0, ParsekFlight.CountTaggedLabPayouts(
                new RecordingTree { Recordings = null }, new List<GameStateEvent>()));
        }
    }
}
