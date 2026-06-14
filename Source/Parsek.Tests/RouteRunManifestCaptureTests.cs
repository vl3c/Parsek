using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M2 Phase 2 (plan D3/D14): full-run cargo manifest capture lifecycle.
    /// Pins the BIRTH discriminator (round-2 BLOCKER 1: split/merge/chain
    /// births capture, BG-promotion/quickload resume never), the write-once
    /// START / overwrite-per-active-stop END contract, the snapshot-scoped END
    /// extraction (round-1 finding 5), the background-transition void, and the
    /// tree-mode stop forwarding (D14).
    /// </summary>
    [Collection("Sequential")]
    public class RouteRunManifestCaptureTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteRunManifestCaptureTests()
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

        // ---------- Fixture helpers ----------

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

        private static RouteRunCargoManifest StartOnlyManifest(
            double oreAmount = 10.0, params uint[] pids)
        {
            if (pids == null || pids.Length == 0)
                pids = new uint[] { 100u, 200u };
            return new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint>(pids),
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "Ore", new ResourceAmount { amount = oreAmount, maxAmount = 100.0 } }
                }
            };
        }

        // ---------- D3 rule 1/2: the birth discriminator + write-once guard ----------

        // catches (round-2 BLOCKER 1): isPromotion used as the discriminator.
        // A fresh recording (root/user start, undock-split child, dock-merge
        // child, chain-segment birth) has no samples of any kind and MUST
        // capture, no matter which recorder-start flavor created it.
        [Fact]
        public void ShouldCapture_RecordingAtBirth_True()
        {
            var rec = new Recording { RecordingId = "fresh-child" };

            Assert.True(RouteProofCapture.ShouldCaptureRunManifestStartHalf(rec, out string reason));
            Assert.Null(reason);
        }

        // catches: a BG-promoted or resumed recording (carries prior samples)
        // re-capturing a mid-run baseline, folding prior gains into "start
        // cargo" and bypassing the gain check.
        [Fact]
        public void ShouldCapture_RecordingWithPoints_False()
        {
            var rec = new Recording { RecordingId = "bg-promoted" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });

            Assert.False(RouteProofCapture.ShouldCaptureRunManifestStartHalf(rec, out string reason));
            Assert.Equal("not-at-birth", reason);
        }

        // catches: an on-rails BG recording (orbit segments, zero points)
        // slipping past a points-only birth check at promotion.
        [Fact]
        public void ShouldCapture_RecordingWithOrbitSegments_False()
        {
            var rec = new Recording { RecordingId = "bg-onrails" };
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 10.0, endUT = 20.0 });

            Assert.False(RouteProofCapture.ShouldCaptureRunManifestStartHalf(rec, out string reason));
            Assert.Equal("not-at-birth", reason);
        }

        [Fact]
        public void ShouldCapture_RecordingWithTrackSections_False()
        {
            var rec = new Recording { RecordingId = "bg-sections" };
            rec.TrackSections.Add(new TrackSection { startUT = 10.0, endUT = 20.0 });

            Assert.False(RouteProofCapture.ShouldCaptureRunManifestStartHalf(rec, out string reason));
            Assert.Equal("not-at-birth", reason);
        }

        // catches (D3 rule 2 pin): re-capture on a recording that already has a
        // START half. A quickload back to the recording's first sample can trim
        // the recording empty again - the write-once guard must still block.
        [Fact]
        public void ShouldCapture_StartHalfAlreadyCaptured_False_EvenAtBirth()
        {
            var rec = new Recording
            {
                RecordingId = "trimmed-to-birth",
                RouteRunManifest = StartOnlyManifest()
            };

            Assert.False(RouteProofCapture.ShouldCaptureRunManifestStartHalf(rec, out string reason));
            Assert.Equal("start-half-already-captured", reason);
        }

        [Fact]
        public void ShouldCapture_NullRecording_False()
        {
            Assert.False(RouteProofCapture.ShouldCaptureRunManifestStartHalf(null, out string reason));
            Assert.Equal("no-tree-recording", reason);
        }

        // ---------- START half builder ----------

        [Fact]
        public void BuildRunCargoManifestAtStart_ScopesToSnapshotPids()
        {
            ConfigNode snapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("Ore", 0.0, 100.0)),
                MakePart(200, "drill", MakeResource("ElectricCharge", 50.0, 50.0)));

            RouteRunCargoManifest manifest = RouteProofCapture.BuildRunCargoManifestAtStart(
                snapshot, isGloopsMode: false, vesselContext: "<test>", recordingVesselId: 42u);

            Assert.NotNull(manifest);
            Assert.Equal(new List<uint> { 100u, 200u }, manifest.TransportPartPersistentIds);
            // Capture stays permissive on names but the shared extractor's
            // EC/IntakeAir noise filter applies (same rule as the origin proof).
            Assert.NotNull(manifest.StartTransportResources);
            Assert.Single(manifest.StartTransportResources);
            Assert.Equal(0.0, manifest.StartTransportResources["Ore"].amount);
            Assert.True(manifest.HasStartHalf);
            Assert.False(manifest.EndCaptured);
            Assert.False(manifest.IsComplete);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("RouteRunManifest start:") && l.Contains("recId=42") && l.Contains("parts=2"));
        }

        // catches (D2 direction pin at capture): a definition check at capture
        // time baking mod-install state into recorded data. Undefined names are
        // recorded verbatim; exclusion is an analysis-time concern.
        [Fact]
        public void BuildRunCargoManifestAtStart_UndefinedModResource_RecordedVerbatim()
        {
            ConfigNode snapshot = MakeVessel(
                MakePart(100, "modTank",
                    MakeResource(Generators.CrpFixtures.UninstalledModResource, 12.5, 50.0)));

            RouteRunCargoManifest manifest = RouteProofCapture.BuildRunCargoManifestAtStart(
                snapshot, isGloopsMode: false, vesselContext: "<test>", recordingVesselId: 1u);

            Assert.NotNull(manifest);
            Assert.Equal(12.5,
                manifest.StartTransportResources[Generators.CrpFixtures.UninstalledModResource].amount);
        }

        [Fact]
        public void BuildRunCargoManifestAtStart_GloopsMode_SkipsWithLog()
        {
            ConfigNode snapshot = MakeVessel(MakePart(100, "tank", MakeResource("Ore", 1.0, 2.0)));

            RouteRunCargoManifest manifest = RouteProofCapture.BuildRunCargoManifestAtStart(
                snapshot, isGloopsMode: true, vesselContext: "<test>", recordingVesselId: 7u);

            Assert.Null(manifest);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("RouteRunManifest skipped: gloops mode") && l.Contains("recId=7"));
        }

        [Fact]
        public void BuildRunCargoManifestAtStart_NullSnapshot_SkipsWithWarn()
        {
            RouteRunCargoManifest manifest = RouteProofCapture.BuildRunCargoManifestAtStart(
                null, isGloopsMode: false, vesselContext: "<test>", recordingVesselId: 9u);

            Assert.Null(manifest);
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("RouteRunManifest skipped: no last good snapshot") && l.Contains("recId=9"));
        }

        // ---------- D3 rule 4: END half completion ----------

        // catches (round-1 finding 5): completing the END half from anything
        // other than the capture snapshot scoped to the START pid set. The
        // capture snapshot here contains a foreign (merged-stack) part that
        // must NOT leak into the END manifest.
        [Fact]
        public void CompleteRunCargoManifestAtStop_ReextractsSameScope_FromSnapshotNotLiveWalk()
        {
            var pending = StartOnlyManifest(oreAmount: 0.0, 100u, 200u);
            var capture = new Recording
            {
                RecordingId = "stop-capture",
                VesselSnapshot = MakeVessel(
                    MakePart(100, "transportTank", MakeResource("Ore", 80.0, 100.0)),
                    MakePart(999, "depotTank", MakeResource("Ore", 5000.0, 9000.0)))
            };

            RouteProofCapture.CompleteRunCargoManifestAtStop(capture, pending);

            Assert.True(pending.EndCaptured);
            Assert.NotNull(capture.RouteRunManifest);
            Assert.True(capture.RouteRunManifest.IsComplete);
            Assert.Single(capture.RouteRunManifest.EndTransportResources);
            Assert.Equal(80.0, capture.RouteRunManifest.EndTransportResources["Ore"].amount);
            // The forwarded manifest is a deep clone - later recorder-side
            // mutation must not reach the capture.
            pending.EndTransportResources["Ore"] = new ResourceAmount { amount = 1.0, maxAmount = 1.0 };
            Assert.Equal(80.0, capture.RouteRunManifest.EndTransportResources["Ore"].amount);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("RouteRunManifest end:") && l.Contains("overwrite=0"));
        }

        // catches (round-2 correction 6): an END half frozen by a false-alarm
        // chain-boundary stop. The eventual real stop must overwrite it or
        // post-resume drilling double-counts.
        [Fact]
        public void CompleteRunCargoManifestAtStop_OverwritesEndPerActiveStop()
        {
            var pending = StartOnlyManifest(oreAmount: 0.0, 100u);
            var abandonedStop = new Recording
            {
                RecordingId = "abandoned",
                VesselSnapshot = MakeVessel(MakePart(100, "tank", MakeResource("Ore", 30.0, 100.0)))
            };
            RouteProofCapture.CompleteRunCargoManifestAtStop(abandonedStop, pending);
            Assert.Equal(30.0, pending.EndTransportResources["Ore"].amount);

            var realStop = new Recording
            {
                RecordingId = "real",
                VesselSnapshot = MakeVessel(MakePart(100, "tank", MakeResource("Ore", 95.0, 100.0)))
            };
            RouteProofCapture.CompleteRunCargoManifestAtStop(realStop, pending);

            Assert.Equal(95.0, pending.EndTransportResources["Ore"].amount);
            Assert.Equal(95.0, realStop.RouteRunManifest.EndTransportResources["Ore"].amount);
            Assert.Contains(logLines, l => l.Contains("RouteRunManifest end:")
                && l.Contains("recording=real") && l.Contains("overwrite=1"));
        }

        [Fact]
        public void CompleteRunCargoManifestAtStop_NullPending_NoOp()
        {
            var capture = new Recording { RecordingId = "no-pending" };

            RouteProofCapture.CompleteRunCargoManifestAtStop(capture, null);

            Assert.Null(capture.RouteRunManifest);
        }

        // ---------- D3 rule 3: background-transition void ----------

        [Fact]
        public void Manifest_Voided_OnBackgroundTransition()
        {
            var rec = new Recording
            {
                RecordingId = "bg-leg",
                RouteRunManifest = StartOnlyManifest()
            };
            var tree = new RecordingTree { Id = "tree-1", ActiveRecordingId = "bg-leg" };
            tree.AddOrReplaceRecording(rec);

            bool voided = RouteProofCapture.VoidRunManifestForBackgroundTransition(tree, "bg-leg");

            Assert.True(voided);
            Assert.Null(rec.RouteRunManifest);
            // Sticky tombstone (review follow-up MINOR 3): the void must leave
            // a durable marker, not just a null manifest.
            Assert.True(rec.RunManifestVoided);
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("RouteRunManifest voided: recording=bg-leg")
                && l.Contains("reason=background-transition"));
        }

        [Fact]
        public void Manifest_Void_NoManifest_StillSetsTombstone_NoWarn()
        {
            // The void can land BEFORE any start half was captured (skipped
            // capture, no snapshot) - the tombstone must still be stamped so
            // the leg never captures later, but the Warn (manifest actually
            // cleared) is not emitted.
            var rec = new Recording { RecordingId = "plain-leg" };
            var tree = new RecordingTree { Id = "tree-2", ActiveRecordingId = "plain-leg" };
            tree.AddOrReplaceRecording(rec);

            bool voided = RouteProofCapture.VoidRunManifestForBackgroundTransition(tree, "plain-leg");

            Assert.False(voided);
            Assert.True(rec.RunManifestVoided);
            Assert.DoesNotContain(logLines, l => l.Contains("RouteRunManifest voided"));
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("RouteRunManifest void tombstone set: recording=plain-leg"));
        }

        // catches (review follow-up MINOR 3): a leg that voided BEFORE its
        // first sample landed still looks "at birth" - without the sticky
        // tombstone a later promotion would re-capture a mid-life START
        // baseline, folding BG-period production into "start cargo".
        [Fact]
        public void VoidThenPromote_DoesNotRecapture()
        {
            var rec = new Recording
            {
                RecordingId = "void-at-birth",
                RouteRunManifest = StartOnlyManifest()
            };
            var tree = new RecordingTree { Id = "tree-3", ActiveRecordingId = "void-at-birth" };
            tree.AddOrReplaceRecording(rec);

            RouteProofCapture.VoidRunManifestForBackgroundTransition(tree, "void-at-birth");
            // The recording is payload-empty (no points/orbit segments/track
            // sections): the birth check alone would say "capture".
            Assert.Null(rec.RouteRunManifest);

            Assert.False(RouteProofCapture.ShouldCaptureRunManifestStartHalf(rec, out string reason));
            Assert.Equal("manifest-voided", reason);
        }

        // ---------- Review follow-up MINOR 5: null capture snapshot ----------

        // catches: completion against a null capture snapshot stamping
        // EndCaptured=true with a null END - that reads as "complete,
        // resource-less" and inflates the next leg's bridge delta. The
        // manifest must stay start-only (degrades to legacy).
        [Fact]
        public void CompleteRunCargoManifestAtStop_NullSnapshot_LeavesStartOnly()
        {
            var pending = StartOnlyManifest(oreAmount: 0.0, 100u);
            var capture = new Recording { RecordingId = "no-snapshot" };

            RouteProofCapture.CompleteRunCargoManifestAtStop(capture, pending);

            Assert.False(pending.EndCaptured);
            Assert.Null(pending.EndTransportResources);
            Assert.Null(capture.RouteRunManifest);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("RouteRunManifest end skipped: no capture snapshot")
                && l.Contains("recording=no-snapshot"));
        }

        // ---------- Review follow-up MINOR 4: empty -> null normalization ----------

        // catches: an empty-but-non-null manifest surviving capture. The
        // codec drops empty manifests on save (reload yields null) while the
        // hasher emits ".count=0" for an empty dict, so an empty manifest
        // would flip the route hash after one save/load (SourceChanged).
        [Fact]
        public void EmptyScopedCapture_NormalizesToNull_AndRoundTripsHashStable()
        {
            // Parts with no RESOURCE nodes: scoped extraction yields no
            // entries on either half.
            ConfigNode resourcelessSnapshot = MakeVessel(
                MakePart(100, "probeCore"),
                MakePart(200, "antenna"));

            RouteRunCargoManifest manifest = RouteProofCapture.BuildRunCargoManifestAtStart(
                resourcelessSnapshot, isGloopsMode: false, vesselContext: "<test>", recordingVesselId: 5u);
            Assert.NotNull(manifest);
            Assert.Null(manifest.StartTransportResources);

            var capture = new Recording
            {
                RecordingId = "resource-less",
                VesselSnapshot = resourcelessSnapshot
            };
            RouteProofCapture.CompleteRunCargoManifestAtStop(capture, manifest);
            Assert.True(manifest.EndCaptured);
            Assert.Null(manifest.EndTransportResources);

            // Hash stability across one save/load round trip.
            var rec = new Recording
            {
                RecordingId = "resource-less",
                RouteRunManifest = capture.RouteRunManifest
            };
            string hashBefore =
                Parsek.Logistics.RouteProofHasher.ComputeRouteProofHashFromRecording(rec);

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);
            var loaded = new Recording { RecordingId = "resource-less" };
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.NotNull(loaded.RouteRunManifest);
            Assert.Null(loaded.RouteRunManifest.StartTransportResources);
            Assert.Null(loaded.RouteRunManifest.EndTransportResources);
            Assert.True(loaded.RouteRunManifest.EndCaptured);
            Assert.Equal(hashBefore,
                Parsek.Logistics.RouteProofHasher.ComputeRouteProofHashFromRecording(loaded));
        }

        // ---------- D14: forwarding through the tree-mode stop flush ----------

        // catches (D14 pin): the TREE-MODE stop path losing the manifest. Both
        // tree flush entry points (FlushRecorderToTreeRecording and
        // AppendCapturedDataToRecording) route through
        // ApplyCapturedLogisticsMetadataToRecording; this drives the
        // AppendCapturedDataToRecording hop end to end.
        [Fact]
        public void TreeModeStop_PersistsRunManifest()
        {
            var target = new Recording { RecordingId = "tree-rec" };
            var capture = new Recording { RecordingId = "capture" };
            capture.Points.Add(new TrajectoryPoint { ut = 100.0 });
            capture.RouteRunManifest = new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint> { 100u },
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "Ore", new ResourceAmount { amount = 0.0, maxAmount = 100.0 } }
                },
                EndTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "Ore", new ResourceAmount { amount = 80.0, maxAmount = 100.0 } }
                },
                EndCaptured = true
            };

            ParsekFlight.AppendCapturedDataToRecording(target, capture, 100.0);

            Assert.NotNull(target.RouteRunManifest);
            Assert.True(target.RouteRunManifest.IsComplete);
            Assert.Equal(80.0, target.RouteRunManifest.EndTransportResources["Ore"].amount);
            Assert.Contains(logLines, l => l.Contains("Logistics metadata copied")
                && l.Contains("runManifest=complete"));
        }

        // catches (D14 adopt-once START): the flush clobbering the birth-time
        // START half written directly onto the tree recording. Only the END
        // half adopts unconditionally.
        [Fact]
        public void TreeModeStop_AdoptsEndHalfOnly_WhenStartHalfAlreadyOnTarget()
        {
            var target = new Recording
            {
                RecordingId = "tree-rec",
                RouteRunManifest = StartOnlyManifest(oreAmount: 0.0, 100u)
            };
            var capture = new Recording { RecordingId = "capture" };
            capture.RouteRunManifest = new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint> { 100u },
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    // Deliberately different from the target's birth START -
                    // adopt-once must keep the target's value.
                    { "Ore", new ResourceAmount { amount = 55.0, maxAmount = 100.0 } }
                },
                EndTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "Ore", new ResourceAmount { amount = 80.0, maxAmount = 100.0 } }
                },
                EndCaptured = true
            };

            bool changed = ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(
                target, capture, "test");

            Assert.True(changed);
            Assert.Equal(0.0, target.RouteRunManifest.StartTransportResources["Ore"].amount);
            Assert.Equal(80.0, target.RouteRunManifest.EndTransportResources["Ore"].amount);
            Assert.True(target.RouteRunManifest.EndCaptured);
        }

        // catches (review follow-up MINOR 3): the void tombstone losing to a
        // late capture forwarding - a voided leg never re-adopts a manifest,
        // and the tombstone itself propagates sticky one-way.
        [Fact]
        public void TreeModeStop_VoidedTarget_NeverAdoptsManifest()
        {
            var target = new Recording
            {
                RecordingId = "voided-rec",
                RunManifestVoided = true
            };
            var capture = new Recording { RecordingId = "capture" };
            capture.RouteRunManifest = new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint> { 100u },
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "Ore", new ResourceAmount { amount = 5.0, maxAmount = 100.0 } }
                },
                EndCaptured = true
            };

            ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(target, capture, "test");

            Assert.Null(target.RouteRunManifest);
            Assert.True(target.RunManifestVoided);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("run manifest NOT adopted (target voided)"));
        }

        [Fact]
        public void TreeModeStop_PropagatesVoidTombstone()
        {
            var target = new Recording { RecordingId = "tree-rec" };
            var capture = new Recording
            {
                RecordingId = "capture",
                RunManifestVoided = true
            };

            bool changed = ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(
                target, capture, "test");

            Assert.True(changed);
            Assert.True(target.RunManifestVoided);
        }

        [Fact]
        public void TreeModeStop_NoCaptureManifest_TargetUntouched()
        {
            var target = new Recording
            {
                RecordingId = "tree-rec",
                RouteRunManifest = StartOnlyManifest()
            };
            var capture = new Recording { RecordingId = "capture" };

            ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(target, capture, "test");

            Assert.NotNull(target.RouteRunManifest);
            Assert.False(target.RouteRunManifest.EndCaptured);
        }

        // ---------- Clone-site coverage (D14: both DeepClone sites + chain commit) ----------

        [Fact]
        public void DeepClone_CarriesRunManifest()
        {
            var source = new Recording
            {
                RecordingId = "src",
                RouteRunManifest = StartOnlyManifest()
            };

            Recording clone = Recording.DeepClone(source);

            Assert.NotNull(clone.RouteRunManifest);
            Assert.NotSame(source.RouteRunManifest, clone.RouteRunManifest);
            Assert.Equal(10.0, clone.RouteRunManifest.StartTransportResources["Ore"].amount);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CarriesRunManifest()
        {
            var source = new Recording
            {
                RecordingId = "src",
                RouteRunManifest = StartOnlyManifest()
            };
            var target = new Recording { RecordingId = "dst" };

            target.ApplyPersistenceArtifactsFrom(source);

            Assert.NotNull(target.RouteRunManifest);
            Assert.NotSame(source.RouteRunManifest, target.RouteRunManifest);
            Assert.Equal(new List<uint> { 100u, 200u },
                target.RouteRunManifest.TransportPartPersistentIds);
        }

        [Fact]
        public void CloneSites_CarryVoidTombstone()
        {
            var source = new Recording
            {
                RecordingId = "src",
                RunManifestVoided = true
            };

            Assert.True(Recording.DeepClone(source).RunManifestVoided);

            var target = new Recording { RecordingId = "dst" };
            target.ApplyPersistenceArtifactsFrom(source);
            Assert.True(target.RunManifestVoided);
        }

        [Fact]
        public void DeepClone_NullRunManifest_StaysNull()
        {
            var source = new Recording { RecordingId = "src" };

            Recording clone = Recording.DeepClone(source);

            Assert.Null(clone.RouteRunManifest);
        }
    }
}
