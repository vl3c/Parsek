using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// ROUTE-ORIGIN-PROOF-NEVER-REACHES-A-TREE-RECORDING, pinned headlessly.
    ///
    /// <para>The start-time proof is attached to <c>FlightRecorder.CaptureAtStop</c>, and in
    /// always-tree mode the capture recording is never committed as-is, so before this fix the
    /// proof died there on every path any flight had taken - measured by H57's first flight and
    /// corroborated by <c>ROUTE_ORIGIN_PROOF=0</c> in H56's and H57's produced saves. The
    /// adoption lives in <c>ParsekFlight.ApplyCapturedLogisticsMetadataToRecording</c>, which is
    /// where the ORDINARY tree-mode stop flush reaches (FlushRecorderToTreeRecording calls it
    /// directly); an adoption written into <c>AppendCapturedDataToRecording</c> instead would
    /// fix the undock split and the dock merge and miss the stop entirely.</para>
    ///
    /// <para>WRITE-ONCE is not decoration. <c>FlightRecorder</c> nulls
    /// <c>pendingRouteOriginProof</c> at EVERY recorder start, so a split-then-stop sequence
    /// hands this helper a proof-less source after a real proof already landed.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteOriginProofForwardingTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteOriginProofForwardingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RouteOriginProof Proof(uint originRoot, string name = "Mun Depot")
        {
            return new RouteOriginProof
            {
                StartDockedOriginRootPartUId = originRoot,
                StartDockedOriginVesselName = name,
                StartDockedOriginVesselType = (int)VesselType.Base,
                StartDockedTransportRootPartUId = originRoot + 1u,
                StartDockedTransportVesselType = (int)VesselType.Ship,
                StartDockedOriginBodyName = "Mun",
                StartDockedOriginIsSurface = true,
                StartDockedOriginSituation = (int)Vessel.Situations.LANDED,
                // BOUND + PICKUP-VALIDATED: the forwarding cells assert the docked-origin
                // gate, and since P12 that gate needs the undock bind, not just an identity.
                StartDockedOriginBindState = StartDockedOriginBindState.BoundAtUndock,
                StartDockedOriginPickupValidated = true,
                StartDockedOriginPickupKind = OriginPickupKind.Gain,
            };
        }

        [Fact]
        public void StopFlush_ForwardsTheProofOntoTheTreeRecording()
        {
            // FAILS IF: the helper stops adopting. This is the exact call the tree-mode stop
            // flush makes, and it is the path H57's subject cell red on.
            var target = new Recording { RecordingId = "tree-rec" };
            var source = new Recording { RecordingId = "capture", RouteOriginProof = Proof(4242u) };

            bool changed = ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(
                target, source, "FlushRecorderToTreeRecording");

            Assert.True(changed);
            Assert.NotNull(target.RouteOriginProof);
            Assert.Equal(4242u, target.RouteOriginProof.StartDockedOriginRootPartUId);
            Assert.Equal("Mun Depot", target.RouteOriginProof.StartDockedOriginVesselName);
            Assert.Contains(logLines,
                l => l.Contains("RouteOriginProof adopted (write-once)")
                    && l.Contains("originRoot=4242"));
        }

        [Fact]
        public void AdoptedProofIsADeepClone_NotTheCaptureInstance()
        {
            // FAILS IF: the adoption aliases the capture's instance. The capture recording is
            // transient and its manifests are re-extracted per stop; sharing the object would
            // let a later stop mutate a committed recording's proof underneath it.
            var target = new Recording { RecordingId = "tree-rec" };
            var source = new Recording { RecordingId = "capture", RouteOriginProof = Proof(4242u) };

            ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(target, source, "stop");

            Assert.NotSame(source.RouteOriginProof, target.RouteOriginProof);
            source.RouteOriginProof.StartDockedOriginRootPartUId = 9999u;
            Assert.Equal(4242u, target.RouteOriginProof.StartDockedOriginRootPartUId);
        }

        [Fact]
        public void NullSourceProof_DoesNotClobberAnAdoptedProof()
        {
            // THE SPLIT-THEN-STOP CASE. FlightRecorder nulls pendingRouteOriginProof at every
            // recorder start, so the second call here is exactly what an undock split followed
            // by an ordinary stop hands the helper. A bare assignment would blank a real proof.
            var target = new Recording
            {
                RecordingId = "tree-rec",
                RouteOriginProof = Proof(4242u),
            };
            var proofless = new Recording { RecordingId = "later-capture" };

            ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(
                target, proofless, "AppendCapturedDataToRecording");

            Assert.NotNull(target.RouteOriginProof);
            Assert.Equal(4242u, target.RouteOriginProof.StartDockedOriginRootPartUId);
        }

        [Fact]
        public void SecondProof_DoesNotOverwriteTheFirst_OriginIsABirthFact()
        {
            // FAILS IF: adoption becomes last-wins. A run's origin is where the run BEGAN; a
            // later recorder start that happened to be docked somewhere else must not rewrite
            // it, or a two-leg run would report its second depot as its origin.
            var target = new Recording
            {
                RecordingId = "tree-rec",
                RouteOriginProof = Proof(4242u, "First Depot"),
            };
            var second = new Recording
            {
                RecordingId = "later-capture",
                RouteOriginProof = Proof(8888u, "Second Depot"),
            };

            bool changed = ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(
                target, second, "AppendCapturedDataToRecording");

            Assert.Equal(4242u, target.RouteOriginProof.StartDockedOriginRootPartUId);
            Assert.Equal("First Depot", target.RouteOriginProof.StartDockedOriginVesselName);
            Assert.False(changed);
            Assert.Contains(logLines,
                l => l.Contains("RouteOriginProof NOT adopted (target already carries one)")
                    && l.Contains("targetOriginRoot=4242")
                    && l.Contains("sourceOriginRoot=8888"));
        }

        [Fact]
        public void AppendCapturedData_AlsoForwards_SoTheSplitAndMergePathsAreCovered()
        {
            // The undock split and the dock merge reach the helper THROUGH this wrapper; the
            // stop flush calls the helper directly. Both entry points must land the proof, or
            // the run whose recording changed identity mid-flight loses its origin.
            var target = new Recording { RecordingId = "split-child" };
            var source = new Recording { RecordingId = "capture", RouteOriginProof = Proof(1234u) };

            ParsekFlight.AppendCapturedDataToRecording(target, source, endUT: 123.0);

            Assert.NotNull(target.RouteOriginProof);
            Assert.Equal(1234u, target.RouteOriginProof.StartDockedOriginRootPartUId);
        }

        [Fact]
        public void ForwardedProof_RoundTripsThroughTheCodec()
        {
            // FAILS IF: the identity fields are forwarded but not serialized - the proof would
            // then reach the tree recording and vanish at the next save, which reads from the
            // outside exactly like the defect this fix closes.
            var target = new Recording { RecordingId = "tree-rec" };
            var source = new Recording { RecordingId = "capture", RouteOriginProof = Proof(4242u) };
            ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(target, source, "stop");

            var node = new ConfigNode("RECORDING");
            RouteProofCodec.SerializeRouteProofMetadata(node, target);
            var restored = new Recording { RecordingId = "tree-rec" };
            RouteProofCodec.DeserializeRouteProofMetadata(node, restored);

            Assert.NotNull(restored.RouteOriginProof);
            Assert.Equal(4242u, restored.RouteOriginProof.StartDockedOriginRootPartUId);
            Assert.Equal("Mun Depot", restored.RouteOriginProof.StartDockedOriginVesselName);
            Assert.Equal((int)VesselType.Base, restored.RouteOriginProof.StartDockedOriginVesselType);
            Assert.Equal(4243u, restored.RouteOriginProof.StartDockedTransportRootPartUId);
            Assert.Equal((int)VesselType.Ship, restored.RouteOriginProof.StartDockedTransportVesselType);
            // The M1 descriptor must survive on a pid-less proof too: it is the ONLY thing
            // RouteEndpointResolver can resolve a captured surface depot from.
            Assert.Equal("Mun", restored.RouteOriginProof.StartDockedOriginBodyName);
            Assert.True(restored.RouteOriginProof.StartDockedOriginIsSurface);
        }

        [Fact]
        public void ForwardedProof_SurvivesTheTreeRecordCodec_TheScenarioSaveLoadPath()
        {
            // POST-CHANGE CHECKLIST ITEM 1, and the instrument H57 could not be:
            // RecordingTreeRecordCodec.SaveRecordingInto / LoadRecordingFrom is the exact
            // pair ParsekScenario.OnSave / OnLoad drives for every tree recording (the
            // analyzer's INV-10 round-trip rule names the same pair). H57's cells run with
            // RestoreBatchFlightBaselineAfterExecution, so their world is reverted before
            // the produced save is written and the save can NEVER show the node - this cell
            // is where the save/load round trip is actually proven.
            var target = new Recording { RecordingId = "tree-rec" };
            var source = new Recording { RecordingId = "capture", RouteOriginProof = Proof(4242u) };
            ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(target, source, "stop");

            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, target);
            var loaded = new Recording { RecordingId = "tree-rec" };
            RecordingTreeRecordCodec.LoadRecordingFrom(node, loaded);

            Assert.NotNull(loaded.RouteOriginProof);
            Assert.Equal(4242u, loaded.RouteOriginProof.StartDockedOriginRootPartUId);
            Assert.Equal("Mun Depot", loaded.RouteOriginProof.StartDockedOriginVesselName);
            Assert.Equal((int)VesselType.Base, loaded.RouteOriginProof.StartDockedOriginVesselType);
            Assert.Equal(4243u, loaded.RouteOriginProof.StartDockedTransportRootPartUId);
            Assert.Equal((int)VesselType.Ship, loaded.RouteOriginProof.StartDockedTransportVesselType);
            Assert.Equal("Mun", loaded.RouteOriginProof.StartDockedOriginBodyName);
            Assert.True(loaded.RouteOriginProof.StartDockedOriginIsSurface);
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(loaded));
        }

        [Fact]
        public void ARecordingWithNoProof_RoundTripsWithNoProofNode()
        {
            // The absent case through the same pair: a recording that never had an origin
            // must not gain one, and the ROUTE_ORIGIN_PROOF node must simply not be written.
            var rec = new Recording { RecordingId = "no-proof" };
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);
            Assert.False(node.HasNode("ROUTE_ORIGIN_PROOF"));

            var loaded = new Recording { RecordingId = "no-proof" };
            RecordingTreeRecordCodec.LoadRecordingFrom(node, loaded);
            Assert.Null(loaded.RouteOriginProof);
        }

        [Fact]
        public void PidLessProof_StillPassesTheDockedOriginGate()
        {
            // FAILS IF: the consumer gate keeps keying on the pid. Every captured proof now
            // carries pid 0, so a pid-only gate would reject every real start-docked origin -
            // the feature would look exactly as dead as it did before the forwarding fix.
            var rec = new Recording { RecordingId = "r", RouteOriginProof = Proof(4242u) };
            Assert.Equal(0u, rec.RouteOriginProof.StartDockedOriginVesselPid);
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.IsUndockedStartOrigin(rec));
        }

        [Fact]
        public void NoProofAnywhere_LeavesTheSlotNullAndTheGateClosed()
        {
            var target = new Recording { RecordingId = "tree-rec" };
            var source = new Recording { RecordingId = "capture" };

            ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(target, source, "stop");

            Assert.Null(target.RouteOriginProof);
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(target));
            Assert.DoesNotContain(logLines, l => l.Contains("RouteOriginProof adopted"));
        }
    }
}
