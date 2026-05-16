using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the RouteOriginProof producer split across three layers:
    ///   1. Pure helper <see cref="RouteProofCapture.TryResolveStartDockedOriginPartner"/>
    ///      — decision contract under every input combination.
    ///   2. Producer log-assertion tests via the FlightRecorder test seam — verify the
    ///      log line for each branch (Info on Captured, Warn on degenerate states,
    ///      Verbose on benign rejections).
    ///   3. End-to-end forwarding through <c>ApplyRouteOriginProofToCaptureForTesting</c>
    ///      including round-trip through <see cref="RouteProofCodec"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RouteOriginProofCaptureTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteOriginProofCaptureTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------- Pure helper tests ----------

        [Fact]
        public void Resolves_SinglePartner_ReturnsCapturedAndPid()
        {
            // FAILS IF: the resolver does not normalize a single distinct valid partner
            // to Captured, or fails to surface the partner pid in the out parameter.
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(partPersistentId: 100,
                    parentVesselPersistentId: 9001,
                    parentVesselSituation: (int)Vessel.Situations.ORBITING),
            };

            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedOriginPartner(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                externallyParentedParts: candidates,
                out uint partnerPid);

            Assert.Equal(OriginProofDetection.Captured, outcome);
            Assert.Equal(9001u, partnerPid);
        }

        [Fact]
        public void EmptyCandidates_ReturnsNoExternalCoupling()
        {
            // FAILS IF: an empty candidate list is treated as a degenerate state rather
            // than the common "vessel started uncoupled" case.
            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedOriginPartner(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                externallyParentedParts: new List<OriginPartnerCandidate>(),
                out uint partnerPid);

            Assert.Equal(OriginProofDetection.NoExternalCoupling, outcome);
            Assert.Equal(0u, partnerPid);
        }

        [Fact]
        public void ActiveVesselPrelaunch_ReturnsActiveVesselPrelaunch()
        {
            // FAILS IF: PRELAUNCH of the active vessel is not classified before walking
            // candidates, allowing a tower or launchpad clamp to slip through as a partner.
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
            };

            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedOriginPartner(
                activeVesselSituation: (int)Vessel.Situations.PRELAUNCH,
                activeVesselIsEva: false,
                externallyParentedParts: candidates,
                out uint partnerPid);

            Assert.Equal(OriginProofDetection.ActiveVesselPrelaunch, outcome);
            Assert.Equal(0u, partnerPid);
        }

        [Fact]
        public void EvaActiveVessel_ReturnsNoExternalCoupling()
        {
            // FAILS IF: an EVA kerbal grabbing onto a ladder or part is recognized as
            // a docked origin rather than rejected as "no external coupling".
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
            };

            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedOriginPartner(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: true,
                externallyParentedParts: candidates,
                out uint partnerPid);

            Assert.Equal(OriginProofDetection.NoExternalCoupling, outcome);
            Assert.Equal(0u, partnerPid);
        }

        [Fact]
        public void AllPartnersZeroPid_ReturnsPartnerPidZero()
        {
            // FAILS IF: a partner pid of 0 (KSP not yet assigning persistentId) is
            // silently captured as a valid origin instead of flagged.
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 0, (int)Vessel.Situations.ORBITING),
                new OriginPartnerCandidate(101, 0, (int)Vessel.Situations.LANDED),
            };

            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedOriginPartner(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                externallyParentedParts: candidates,
                out uint partnerPid);

            Assert.Equal(OriginProofDetection.PartnerPidZero, outcome);
            Assert.Equal(0u, partnerPid);
        }

        [Fact]
        public void AllPartnersPrelaunch_ReturnsPartnerPrelaunch()
        {
            // FAILS IF: a partner still on the launchpad (PRELAUNCH) is treated as a
            // depot rather than as a non-route-relevant clamp.
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.PRELAUNCH),
                new OriginPartnerCandidate(101, 9001, (int)Vessel.Situations.PRELAUNCH),
            };

            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedOriginPartner(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                externallyParentedParts: candidates,
                out uint partnerPid);

            Assert.Equal(OriginProofDetection.PartnerPrelaunch, outcome);
            Assert.Equal(0u, partnerPid);
        }

        [Fact]
        public void MultipleDistinctPartners_ReturnsPartnerAmbiguous()
        {
            // FAILS IF: two distinct depot partners coupled at start are reduced to
            // an arbitrary single pid instead of flagged ambiguous.
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
                new OriginPartnerCandidate(101, 9002, (int)Vessel.Situations.ORBITING),
            };

            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedOriginPartner(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                externallyParentedParts: candidates,
                out uint partnerPid);

            Assert.Equal(OriginProofDetection.PartnerAmbiguous, outcome);
            Assert.Equal(0u, partnerPid);
        }

        [Fact]
        public void SinglePartner_OneCandidatePrelaunchOneFlying_PicksFlying()
        {
            // FAILS IF: a PRELAUNCH candidate is counted as a distinct valid partner,
            // poisoning the single-valid-partner check when the real depot is the
            // only orbiting parent.
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
                new OriginPartnerCandidate(101, 9002, (int)Vessel.Situations.PRELAUNCH),
            };

            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedOriginPartner(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                externallyParentedParts: candidates,
                out uint partnerPid);

            Assert.Equal(OriginProofDetection.Captured, outcome);
            Assert.Equal(9001u, partnerPid);
        }

        // ---------- Producer + log-assertion tests ----------

        [Fact]
        public void CapturedBranch_LogsInfo()
        {
            // FAILS IF: the captured branch does not emit an Info-level [Recorder] line
            // including the partner pid (player-visible in KSP.log).
            var recorder = new FlightRecorder();
            ConfigNode snapshot = MakeVessel(MakePart(100, "fuelTank",
                MakeResource("LiquidFuel", 80.0, 100.0)));
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
            };

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: snapshot);

            Assert.Contains(logLines, l => l.Contains("[INFO]")
                && l.Contains("[Recorder]")
                && l.Contains("RouteOriginProof captured")
                && l.Contains("partnerPid=9001"));
        }

        [Fact]
        public void NoExternalCouplingBranch_LogsVerbose()
        {
            // FAILS IF: the empty-candidates branch is silent or misclassified.
            var recorder = new FlightRecorder();
            ConfigNode snapshot = MakeVessel(MakePart(100, "fuelTank"));

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: new List<OriginPartnerCandidate>(),
                snapshot: snapshot);

            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("no external coupling"));
        }

        [Fact]
        public void ActiveVesselPrelaunchBranch_LogsVerbose()
        {
            // FAILS IF: a PRELAUNCH active vessel does not log the specific PRELAUNCH
            // branch label (and instead falls through to a generic skip).
            var recorder = new FlightRecorder();
            ConfigNode snapshot = MakeVessel(MakePart(100, "fuelTank"));
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
            };

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.PRELAUNCH,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: snapshot);

            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("active vessel PRELAUNCH"));
        }

        [Fact]
        public void PartnerPrelaunchBranch_LogsVerbose()
        {
            // FAILS IF: a launchpad clamp / pre-launch parent vessel is not reported
            // through the dedicated partner PRELAUNCH branch.
            var recorder = new FlightRecorder();
            ConfigNode snapshot = MakeVessel(MakePart(100, "fuelTank"));
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.PRELAUNCH),
            };

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: snapshot);

            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("partner PRELAUNCH"));
        }

        [Fact]
        public void PartnerPidZeroBranch_LogsWarn()
        {
            // FAILS IF: a partner pid of 0 is treated as a benign Verbose case rather
            // than the Warn-worthy degenerate state it represents.
            var recorder = new FlightRecorder();
            ConfigNode snapshot = MakeVessel(MakePart(100, "fuelTank"));
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 0, (int)Vessel.Situations.ORBITING),
            };

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: snapshot);

            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("[Recorder]")
                && l.Contains("partner pid=0"));
        }

        [Fact]
        public void PartnerAmbiguousBranch_LogsWarn()
        {
            // FAILS IF: two distinct valid partners are not flagged at Warn level OR
            // the log does not include both candidate pids for diagnostics.
            var recorder = new FlightRecorder();
            ConfigNode snapshot = MakeVessel(MakePart(100, "fuelTank"));
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
                new OriginPartnerCandidate(101, 9002, (int)Vessel.Situations.ORBITING),
            };

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: snapshot);

            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("[Recorder]")
                && l.Contains("ambiguous partners")
                && l.Contains("9001")
                && l.Contains("9002"));
        }

        [Fact]
        public void GloopsMode_LogsVerboseSkip()
        {
            // FAILS IF: gloops-mode recordings (ghost-only) attempt to capture an origin
            // proof or fail to log the gloops-skip branch.
            var recorder = new FlightRecorder { IsGloopsMode = true };
            ConfigNode snapshot = MakeVessel(MakePart(100, "fuelTank"));
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
            };

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: snapshot);

            Assert.Null(recorder.PendingRouteOriginProofForTesting);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("gloops mode"));
        }

        [Fact]
        public void NullSnapshot_LogsWarnSkip()
        {
            // FAILS IF: missing lastGoodVesselSnapshot does not Warn-log a skip and
            // instead crashes inside the manifest extractor.
            var recorder = new FlightRecorder();
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
            };

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: null);

            Assert.Null(recorder.PendingRouteOriginProofForTesting);
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("[Recorder]")
                && l.Contains("no last good snapshot"));
        }

        // ---------- Producer-integration tests (the test seam) ----------

        [Fact]
        public void CaptureProducer_DockedStart_FillsRouteOriginProof()
        {
            // FAILS IF: the producer either fails to build the proof on the captured
            // branch, picks the wrong partner pid, or extracts manifests from a part
            // pid set that does NOT correspond to the transport snapshot.
            var recorder = new FlightRecorder();
            ConfigNode transportSnapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 80.0, 100.0)),
                MakePart(101, "transportInv",
                    MakeInventoryModule(MakeStoredPart("evaJetpack", "white", 1))));
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
            };

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: transportSnapshot);

            RouteOriginProof proof = recorder.PendingRouteOriginProofForTesting;
            Assert.NotNull(proof);
            Assert.Equal(9001u, proof.StartDockedOriginVesselPid);
            Assert.NotNull(proof.StartTransportResources);
            Assert.Equal(80.0, proof.StartTransportResources["LiquidFuel"].amount);
            Assert.NotNull(proof.StartTransportInventory);
            Assert.Single(proof.StartTransportInventory);
            Assert.Equal("evaJetpack", proof.StartTransportInventory[0].PartName);

            IReadOnlyList<uint> pids = recorder.PendingRouteOriginProofStartPartPidsForTesting;
            Assert.NotNull(pids);
            Assert.Contains(100u, pids);
            Assert.Contains(101u, pids);

            // End manifests are NOT filled by the start-time producer — they only
            // populate inside BuildCaptureRecording's forwarding block. Verify the
            // contract here so end manifests don't drift in later refactors.
            Assert.Null(proof.EndTransportResources);
            Assert.Null(proof.EndTransportInventory);
        }

        [Fact]
        public void CaptureProducer_NotDocked_LeavesRouteOriginProofNull()
        {
            // FAILS IF: a no-coupling start somehow ends up with a non-null pending
            // proof, which would silently grant the recording origin-debit authority.
            var recorder = new FlightRecorder();
            ConfigNode snapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 80.0, 100.0)));

            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: new List<OriginPartnerCandidate>(),
                snapshot: snapshot);

            Assert.Null(recorder.PendingRouteOriginProofForTesting);
            Assert.Null(recorder.PendingRouteOriginProofStartPartPidsForTesting);
        }

        [Fact]
        public void BuildCaptureRecording_DockedStart_FillsEndManifestsFromTransportPartSet()
        {
            // FAILS IF: end manifests are extracted from the wrong part set (e.g. the
            // whole capture snapshot including the depot-side parts that fused at dock)
            // or fail to populate at all when the proof exists.
            var recorder = new FlightRecorder();
            ConfigNode startSnapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 80.0, 100.0)));
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
            };
            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: startSnapshot);

            // Simulate the post-flight capture snapshot: transport part 100 has burned
            // fuel, and an unrelated part 200 (e.g. picked up during the run) carries
            // resources that must NOT bleed into the end manifest.
            ConfigNode endSnapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 25.0, 100.0)),
                MakePart(200, "unrelatedTank", MakeResource("LiquidFuel", 500.0, 500.0)));
            var capture = new Recording
            {
                RecordingId = Guid.NewGuid().ToString("N"),
                VesselSnapshot = endSnapshot,
            };

            recorder.ApplyRouteOriginProofToCaptureForTesting(capture);

            Assert.NotNull(capture.RouteOriginProof);
            Assert.Equal(9001u, capture.RouteOriginProof.StartDockedOriginVesselPid);
            Assert.Equal(80.0, capture.RouteOriginProof.StartTransportResources["LiquidFuel"].amount);
            Assert.Equal(25.0, capture.RouteOriginProof.EndTransportResources["LiquidFuel"].amount);
            // Critical: the unrelated 500.0 must NOT appear in the end manifest because
            // part 200 is not in the captured transport pid set.
            Assert.Equal(25.0, capture.RouteOriginProof.EndTransportResources["LiquidFuel"].amount);
            Assert.True(capture.RouteOriginProof.EndTransportResources.Count == 1);
        }

        [Fact]
        public void BuildCaptureRecording_DockedStart_RoundTripsThroughRouteProofCodec()
        {
            // FAILS IF: any of the five proof fields (partner pid + start/end res +
            // start/end inv) is lost on the round trip through ConfigNode.
            var recorder = new FlightRecorder();
            ConfigNode startSnapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 80.0, 100.0)),
                MakePart(101, "transportInv",
                    MakeInventoryModule(MakeStoredPart("evaJetpack", "white", 1))));
            var candidates = new List<OriginPartnerCandidate>
            {
                new OriginPartnerCandidate(100, 9001, (int)Vessel.Situations.ORBITING),
            };
            recorder.CaptureStartRouteOriginProofIfDockedForTesting(
                activeVesselSituation: (int)Vessel.Situations.ORBITING,
                activeVesselIsEva: false,
                candidates: candidates,
                snapshot: startSnapshot);

            ConfigNode endSnapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 25.0, 100.0)),
                MakePart(101, "transportInv",
                    MakeInventoryModule(MakeStoredPart("evaRepairKit", null, 2))));
            var capture = new Recording
            {
                RecordingId = Guid.NewGuid().ToString("N"),
                VesselSnapshot = endSnapshot,
            };
            recorder.ApplyRouteOriginProofToCaptureForTesting(capture);

            var node = new ConfigNode("ROOT");
            RouteProofCodec.SerializeRouteProofMetadata(node, capture);

            var restored = new Recording { RecordingId = capture.RecordingId };
            RouteProofCodec.DeserializeRouteProofMetadata(node, restored);

            Assert.NotNull(restored.RouteOriginProof);
            Assert.Equal(9001u, restored.RouteOriginProof.StartDockedOriginVesselPid);
            Assert.Equal(80.0, restored.RouteOriginProof.StartTransportResources["LiquidFuel"].amount);
            Assert.Equal(25.0, restored.RouteOriginProof.EndTransportResources["LiquidFuel"].amount);
            Assert.Single(restored.RouteOriginProof.StartTransportInventory);
            Assert.Equal("evaJetpack", restored.RouteOriginProof.StartTransportInventory[0].PartName);
            Assert.Single(restored.RouteOriginProof.EndTransportInventory);
            Assert.Equal("evaRepairKit", restored.RouteOriginProof.EndTransportInventory[0].PartName);
        }

        // ---------- ConfigNode helpers (mirror RouteProofCaptureTests) ----------

        private static ConfigNode MakeVessel(params ConfigNode[] parts)
        {
            ConfigNode vessel = new ConfigNode("VESSEL");
            for (int i = 0; i < parts.Length; i++)
                vessel.AddNode(parts[i]);
            return vessel;
        }

        private static ConfigNode MakePart(uint persistentId, string name, params ConfigNode[] children)
        {
            ConfigNode part = new ConfigNode("PART");
            part.AddValue("name", name);
            part.AddValue("persistentId", persistentId.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < children.Length; i++)
                part.AddNode(children[i]);
            return part;
        }

        private static ConfigNode MakeResource(string name, double amount, double maxAmount)
        {
            ConfigNode resource = new ConfigNode("RESOURCE");
            resource.AddValue("name", name);
            resource.AddValue("amount", amount.ToString("R", CultureInfo.InvariantCulture));
            resource.AddValue("maxAmount", maxAmount.ToString("R", CultureInfo.InvariantCulture));
            return resource;
        }

        private static ConfigNode MakeInventoryModule(params ConfigNode[] storedParts)
        {
            ConfigNode module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleInventoryPart");
            module.AddValue("InventorySlots", "4");
            ConfigNode storedPartsNode = module.AddNode("STOREDPARTS");
            for (int i = 0; i < storedParts.Length; i++)
                storedPartsNode.AddNode(storedParts[i]);
            return module;
        }

        private static ConfigNode MakeStoredPart(
            string partName,
            string variantName,
            int quantity)
        {
            ConfigNode storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);
            if (!string.IsNullOrEmpty(variantName))
                storedPart.AddValue("variantName", variantName);
            storedPart.AddValue("quantity", quantity.ToString(CultureInfo.InvariantCulture));
            return storedPart;
        }
    }
}
