using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization and unit tests for ParsekFlightSurfaceCapture: the flag pick-up routing
    /// (foreground recorder, background tree member, nobody), the ground-part retrieval-death
    /// seam, the null guards of the stock handlers, the foreign-transfer append to a member that
    /// went missing, and the slot / ground-part readers on what a headless host can hand them.
    /// </summary>
    [Collection("Sequential")]
    public class SurfaceCaptureUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SurfaceCaptureUnitTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // --- builders ---

        private static ParsekFlight Flight(RecordingTree tree)
        {
            var flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            flight.activeTree = tree;
            return flight;
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

        private static Vessel FakeVessel(uint pid, string name)
        {
            var v = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            v.persistentId = pid;
            v.vesselName = name;
            v.latitude = 1.25;
            v.longitude = 2.5;
            v.altitude = 3.75;
            return v;
        }

        private static FlightRecorder RecordingRecorder(uint pid)
        {
            var recorder = new FlightRecorder();
            recorder.IsRecording = true;
            typeof(FlightRecorder).GetProperty("RecordingVesselId",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(recorder, pid);
            return recorder;
        }

        // ================= characterization: flag pick-up =================

        [Fact]
        public void FlagPickUp_NoFlagOrNoPicker_RecordsNothing()
        {
            var flight = Flight(Tree(new Recording { RecordingId = "act" }));
            flight.SurfaceCapture.TryCaptureFlagPickUp(null, 5);
            flight.SurfaceCapture.TryCaptureFlagPickUp(FakeVessel(9, "Flag"), 0);
            Assert.Empty(flight.activeTree.Recordings["act"].SegmentEvents);
            Assert.DoesNotContain(logLines, l => l.Contains("Flag pick-up") || l.Contains("picked up"));
        }

        [UnityEntryFact]
        public void FlagPlanted_NoSite_DoesNothing()
        {
            Flight(null).SurfaceCapture.OnAfterFlagPlanted(null);
            Assert.DoesNotContain(logLines, l => l.Contains("Flag"));
        }

        // ================= characterization: ground parts =================

        [Fact]
        public void GroundScienceSignals_WithoutAPart_DoNothing()
        {
            var capture = Flight(null).SurfaceCapture;
            capture.OnGroundSciencePartDeployed(null);
            capture.OnGroundSciencePartRemoved(null);
            Assert.Empty(logLines);
        }

        [Fact]
        public void RetrievalDeath_NotAPlacedMember_IsFalseAndConsumesThePendingEntry()
        {
            var rec = new Recording { RecordingId = "r1", VesselPersistentId = 9 };
            var flight = Flight(Tree(rec));
            flight.SurfaceCapture.groundPartRetrievalPending.Add(9);

            Assert.False(flight.SurfaceCapture.IsGroundPartRetrievalDeath(FakeVessel(9, "RTG"), rec));
            Assert.Empty(flight.SurfaceCapture.groundPartRetrievalPending);
        }

        [Fact]
        public void RetrievalDeath_PlacedMember_TrueOnlyWhenTheRemovalWasSeen()
        {
            var parent = new Recording { RecordingId = "kerbal" };
            var member = new Recording { RecordingId = "rtg", VesselPersistentId = 9, ParentBranchPointId = "bp-1" };
            var tree = Tree(parent, member);
            tree.BranchPoints.Add(new BranchPoint { Id = "bp-1", Type = BranchPointType.GroundPartPlaced });
            var flight = Flight(tree);

            // No removal signal and the root part cannot be read: not a retrieval.
            Assert.False(flight.SurfaceCapture.IsGroundPartRetrievalDeath(FakeVessel(9, "RTG"), member));

            flight.SurfaceCapture.groundPartRetrievalPending.Add(9);
            Assert.True(flight.SurfaceCapture.IsGroundPartRetrievalDeath(FakeVessel(9, "RTG"), member));
            // The entry is single-use.
            Assert.False(flight.SurfaceCapture.IsGroundPartRetrievalDeath(FakeVessel(9, "RTG"), member));
        }

        [Fact]
        public void DeployedOnGroundRead_NoVesselOrNoRoot_IsUnknown()
        {
            Assert.Null(ParsekFlightSurfaceCapture.TryReadDeployedOnGroundCore(null));
            Assert.Null(ParsekFlightSurfaceCapture.TryReadDeployedOnGroundCore(FakeVessel(9, "RTG")));
        }

        // ================= characterization: foreign inventory =================

        [Fact]
        public void ForeignTransferAppend_MemberMissing_RecordsNothing()
        {
            var tree = Tree(new Recording { RecordingId = "act" });
            tree.BackgroundMap[8] = "gone";
            var flight = Flight(tree);
            var transfer = new SegmentEvent { ut = 10, type = SegmentEventType.PartRemoved };

            flight.SurfaceCapture.AppendForeignTransfer(FakeVessel(9, "Box"), transfer);
            flight.SurfaceCapture.AppendForeignTransfer(FakeVessel(8, "Crate"), transfer);

            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]")
                && l.Contains("Foreign inventory transfer: cargo partner vesselPid=9 lost its member recording before the append, nothing recorded"));
            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]")
                && l.Contains("cargo partner vesselPid=8 lost its member recording"));
        }

        [Fact]
        public void SlotKey_PacksThePartPidAboveTheSlot()
        {
            Assert.Equal(1L << 20, ParsekFlightSurfaceCapture.ForeignInventorySlotKey(1, 0));
            Assert.Equal((1L << 20) ^ 3, ParsekFlightSurfaceCapture.ForeignInventorySlotKey(1, 3));
            Assert.Equal(((long)uint.MaxValue << 20) ^ 1, ParsekFlightSurfaceCapture.ForeignInventorySlotKey(uint.MaxValue, 1));
            Assert.NotEqual(ParsekFlightSurfaceCapture.ForeignInventorySlotKey(2, 0),
                ParsekFlightSurfaceCapture.ForeignInventorySlotKey(1, 0));
        }

        [Fact]
        public void SlotReaders_NoModule_ReadNothing()
        {
            ParsekFlightSurfaceCapture.ReadLiveSlot(null, 2, out string name, out int quantity);
            Assert.Null(name);
            Assert.Equal(0, quantity);

            Assert.False(ParsekFlightSurfaceCapture.TryReadSavedSlotCore(null, 2, out string savedName, out int savedQuantity));
            Assert.Null(savedName);
            Assert.Equal(0, savedQuantity);
        }
    }
}
