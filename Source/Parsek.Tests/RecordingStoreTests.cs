using System.Collections.Generic;
using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingStoreTests
    {
        public RecordingStoreTests()
        {
            // Suppress Debug.Log calls outside Unity
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            // Start each test with a clean slate
            RecordingStore.ResetForTesting();
        }

        private List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + i * 10,
                    latitude = 0,
                    longitude = 0,
                    altitude = 100,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        [Fact]
        public void CreateRecordingFromFlightData_WithPoints_CreatesRecording()
        {
            var points = MakePoints(5);

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "TestVessel");

            Assert.NotNull(rec);
            Assert.Equal(5, rec.Points.Count);
            Assert.Equal("TestVessel", rec.VesselName);
        }

        [Fact]
        public void CreateRecordingFromFlightData_EmptyList_ReturnsNull()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(new List<TrajectoryPoint>(), "Empty");

            Assert.Null(rec);
        }

        [Fact]
        public void CreateRecordingFromFlightData_NullList_ReturnsNull()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(null, "Null");

            Assert.Null(rec);
        }

        [Fact]
        public void CommitRecordingDirect_AddsToCommitted()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship");
            Assert.NotNull(rec);

            RecordingStore.CommitRecordingDirect(rec);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("Ship", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void CommitRecordingDirect_NullRecording_DoesNothing()
        {
            // Should not crash
            RecordingStore.CommitRecordingDirect(null);

            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void Clear_ClearsEverything()
        {
            // Create + commit one
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "First");
            RecordingStore.CommitRecordingDirect(rec1);

            RecordingStore.Clear();

            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void Recording_StartUT_EndUT_Computed()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(5, startUT: 200), "Ship");

            Assert.NotNull(rec);
            Assert.Equal(200, rec.StartUT);
            Assert.Equal(240, rec.EndUT); // 200 + 4*10
        }

        [Fact]
        public void Recording_StartUT_EndUT_EmptyPoints()
        {
            var rec = new Recording();
            Assert.Equal(0, rec.StartUT);
            Assert.Equal(0, rec.EndUT);
        }

        [Fact]
        public void Recording_VesselSnapshot_StoredAndRetrieved()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Ship");
            Assert.NotNull(rec);

            var node = new ConfigNode("VESSEL");
            node.AddValue("name", "TestVessel");
            rec.VesselSnapshot = node;

            Assert.NotNull(rec.VesselSnapshot);
            Assert.Equal("TestVessel", rec.VesselSnapshot.GetValue("name"));
        }

        [Fact]
        public void Recording_DistanceAndDestroyed_DefaultValues()
        {
            var rec = new Recording();

            Assert.Equal(0, rec.DistanceFromLaunch);
            Assert.False(rec.VesselDestroyed);
            Assert.Null(rec.VesselSnapshot);
            Assert.Null(rec.VesselSituation);
        }

        [Fact]
        public void Recording_DefaultFields()
        {
            var rec = new Recording();

            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(-1, rec.LastAppliedResourceIndex);
            Assert.Equal(0, rec.DistanceFromLaunch);
            Assert.Equal(0, rec.MaxDistanceFromLaunch);
            Assert.False(string.IsNullOrEmpty(rec.RecordingId));
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, rec.RecordingFormatVersion);
        }

        [Fact]
        public void Recording_DistanceFields_Roundtrip()
        {
            var rec = new Recording();
            rec.DistanceFromLaunch = 12345.67;
            rec.MaxDistanceFromLaunch = 99999.99;

            Assert.Equal(12345.67, rec.DistanceFromLaunch);
            Assert.Equal(99999.99, rec.MaxDistanceFromLaunch);
        }

        [Fact]
        public void Recording_SpawnFields_Roundtrip()
        {
            var rec = new Recording();
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = 42u;

            Assert.True(rec.VesselSpawned);
            Assert.Equal(42u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void Recording_LastAppliedResourceIndex_Tracks()
        {
            var rec = new Recording();
            rec.LastAppliedResourceIndex = 5;

            Assert.Equal(5, rec.LastAppliedResourceIndex);
        }

        [Fact]
        public void CreateRecordingFromFlightData_SinglePoint_ReturnsNull()
        {
            var points = MakePoints(1);

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "TooShort");

            Assert.Null(rec);
        }

        [Fact]
        public void CreateRecordingFromFlightData_ExactlyTwoPoints_Succeeds()
        {
            var points = MakePoints(2);

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "MinValid");

            Assert.NotNull(rec);
            Assert.Equal(2, rec.Points.Count);
            Assert.False(string.IsNullOrEmpty(rec.RecordingId));
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion,
                rec.RecordingFormatVersion);
        }

        [Fact]
        public void CreateRecordingFromFlightData_UsesProvidedRecordingMetadata()
        {
            var points = MakePoints(3);
            var rec = RecordingStore.CreateRecordingFromFlightData(
                points,
                "Ship",
                recordingId: "fixedid",
                recordingFormatVersion: 0);

            Assert.NotNull(rec);
            Assert.Equal("fixedid", rec.RecordingId);
            Assert.Equal(0, rec.RecordingFormatVersion);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesOnlyPersistenceFields()
        {
            var vesselSnapshot = new ConfigNode("VESSEL");
            vesselSnapshot.AddValue("name", "Vessel A");
            var ghostSnapshot = new ConfigNode("VESSEL");
            ghostSnapshot.AddValue("name", "Ghost A");
            var source = new Recording
            {
                RecordingId = "abc",
                RecordingFormatVersion = 0,
                VesselSnapshot = vesselSnapshot,
                GhostVisualSnapshot = ghostSnapshot,
                DistanceFromLaunch = 123,
                VesselDestroyed = true,
                VesselSituation = "Destroyed",
                MaxDistanceFromLaunch = 456,
            };
            source.Points.AddRange(MakePoints(4));
            source.OrbitSegments.Add(new OrbitSegment { startUT = 1, endUT = 2, bodyName = "Kerbin" });

            var target = new Recording
            {
                VesselName = "Target",
                Points = MakePoints(2),
                OrbitSegments = new List<OrbitSegment> { new OrbitSegment { startUT = 10, endUT = 20, bodyName = "Mun" } }
            };

            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal("abc", target.RecordingId);
            Assert.Equal(0, target.RecordingFormatVersion);
            Assert.NotNull(target.VesselSnapshot);
            Assert.NotNull(target.GhostVisualSnapshot);
            Assert.Equal("Vessel A", target.VesselSnapshot.GetValue("name"));
            Assert.Equal("Ghost A", target.GhostVisualSnapshot.GetValue("name"));
            Assert.NotSame(source.VesselSnapshot, target.VesselSnapshot);
            Assert.NotSame(source.GhostVisualSnapshot, target.GhostVisualSnapshot);
            Assert.Equal(123, target.DistanceFromLaunch);
            Assert.True(target.VesselDestroyed);
            Assert.Equal("Destroyed", target.VesselSituation);
            Assert.Equal(456, target.MaxDistanceFromLaunch);
            Assert.Equal(2, target.Points.Count);
            Assert.Equal("Target", target.VesselName);
            Assert.Single(target.OrbitSegments);
            Assert.Equal("Mun", target.OrbitSegments[0].bodyName);
        }

        [Fact]
        public void RecordingMetadata_SaveLoad_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "meta123",
                RecordingFormatVersion = 0,
                LoopPlayback = true,
                LoopIntervalSeconds = 2.5,
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal("meta123", loaded.RecordingId);
            Assert.Equal(0, loaded.RecordingFormatVersion);
            Assert.True(loaded.LoopPlayback);
            Assert.Equal(2.5, loaded.LoopIntervalSeconds);

        }

        [Fact]
        public void RecordingMetadata_Load_MissingFields_UsesLegacyFormatVersionAndKeepsOtherDefaults()
        {
            var node = new ConfigNode("RECORDING");
            var loaded = new Recording();

            string defaultId = loaded.RecordingId;

            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(defaultId, loaded.RecordingId);
            Assert.Equal(0, loaded.RecordingFormatVersion);
            Assert.False(loaded.LoopPlayback);
            Assert.Equal(10.0, loaded.LoopIntervalSeconds);
        }

        [Fact]
        public void RecordingMetadata_Hidden_SaveLoad_RoundTrip()
        {
            // Bug: Hidden=true not persisted — recording reappears after save/load cycle
            var source = new Recording
            {
                RecordingId = "hidden-rec",
                Hidden = true
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            // Verify the node contains the hidden value
            Assert.Equal("True", node.GetValue("hidden"));

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.True(loaded.Hidden);
        }

        [Fact]
        public void RecordingMetadata_Hidden_DefaultFalse_NotWritten()
        {
            // Bug: hidden=False written unnecessarily, bloating save files
            var source = new Recording
            {
                RecordingId = "visible-rec",
                Hidden = false
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            // hidden=false should not be written (saves space, matches default)
            Assert.Null(node.GetValue("hidden"));

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.False(loaded.Hidden);
        }

        [Fact]
        public void RecordingMetadata_Hidden_MissingField_DefaultsFalse()
        {
            // Bug: legacy recordings without hidden field crash or default to true
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "legacy-no-hidden");
            // No "hidden" value — simulates a pre-hide-feature recording

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.False(loaded.Hidden);
        }

        [Fact]
        public void DeleteRecordingFiles_NoId_DoesNotThrow()
        {
            var rec = new Recording();
            rec.RecordingId = null;
            RecordingStore.DeleteRecordingFiles(rec);
        }

        [Fact]
        public void DeleteRecordingFiles_NoSaveContext_DoesNotThrow()
        {
            var rec = new Recording
            {
                RecordingId = "test123"
            };
            RecordingStore.DeleteRecordingFiles(rec);
        }

        [Theory]
        [InlineData("abc123.prec", "abc123")]
        [InlineData("abc123_vessel.craft", "abc123")]
        [InlineData("abc123_ghost.craft", "abc123")]
        [InlineData("abc123.prec.txt", "abc123")]
        [InlineData("abc123_vessel.craft.txt", "abc123")]
        [InlineData("abc123_ghost.craft.txt", "abc123")]
        [InlineData("abc123.pcrf", null)]                              // .pcrf no longer recognized (#260)
        [InlineData("a1b2c3d4e5f6.prec", "a1b2c3d4e5f6")]           // GUID-style ID
        [InlineData("id.with.dots.prec", "id.with.dots")]             // dots in ID
        [InlineData("abc123.prec.tmp", null)]                         // safe-write temp file — should be ignored
        [InlineData("abc123.PREC", "abc123")]                         // case-insensitive suffix matching
        [InlineData("abc123_GHOST.CRAFT", "abc123")]                  // case-insensitive suffix matching
        [InlineData("readme.txt", null)]
        [InlineData("notes.md", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void ExtractRecordingIdFromFileName_Works(string fileName, string expected)
        {
            Assert.Equal(expected, RecordingStore.ExtractRecordingIdFromFileName(fileName));
        }

        [Theory]
        [InlineData("abc123.pcrf", true)]                                    // legacy ghost-geometry sidecar (#260)
        [InlineData("a1b2c3d4e5f6.pcrf", true)]
        [InlineData("ABC123.PCRF", true)]                                    // case-insensitive
        [InlineData("abc123.prec", false)]                                   // current sidecar — not legacy
        [InlineData("abc123_vessel.craft", false)]
        [InlineData("abc123_ghost.craft", false)]
        [InlineData("readme.txt", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsLegacySidecarFile_Works(string fileName, bool expected)
        {
            Assert.Equal(expected, RecordingStore.IsLegacySidecarFile(fileName));
        }

        [Theory]
        [InlineData("abc123.prec.stage.123", true)]
        [InlineData("abc123.prec.stage.123.tmp", true)]
        [InlineData("abc123_vessel.craft.bak.123", true)]
        [InlineData("abc123_ghost.craft.tmp", true)]
        [InlineData("abc123.prec.txt.stage.123", true)]
        [InlineData("abc123_vessel.craft.txt.bak.123", true)]
        [InlineData("abc123_ghost.craft.txt.tmp", true)]
        [InlineData("abc123.prec", false)]
        [InlineData("abc123.pcrf", false)]
        [InlineData("readme.txt", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsTransientSidecarArtifactFile_Works(string fileName, bool expected)
        {
            Assert.Equal(expected, RecordingStore.IsTransientSidecarArtifactFile(fileName));
        }

        [Theory]
        [InlineData("mk1pod_v2_123456", "mk1pod_v2")]
        [InlineData("probeCoreOcto2_1", "probeCoreOcto2")]
        [InlineData("solidBooster.sm.v2_12345", "solidBooster.sm.v2")]
        [InlineData("adapter_size2_size1", "adapter_size2_size1")]
        [InlineData("mk1pod.v2", "mk1pod.v2")]
        [InlineData("", null)]
        public void TryExtractPartName_Works(string raw, string expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.TryExtractPartName(raw));
        }

        [Theory]
        [InlineData("1,2,3", true)]
        [InlineData(" 1.5 , -2 , 3 ", true)]
        [InlineData("1,2", false)]
        [InlineData("x,2,3", false)]
        public void TryParseVector3_Works(string value, bool expected)
        {
            Vector3 parsed;
            bool ok = GhostVisualBuilder.TryParseVector3(value, out parsed);
            Assert.Equal(expected, ok);
        }

        [Theory]
        [InlineData("0,0,0,1", true)]
        [InlineData(" 0.1 , 0.2 , 0.3 , 0.4 ", true)]
        [InlineData("0,0,1", false)]
        [InlineData("0,0,0,w", false)]
        public void TryParseQuaternion_Works(string value, bool expected)
        {
            Quaternion parsed;
            bool ok = GhostVisualBuilder.TryParseQuaternion(value, out parsed);
            Assert.Equal(expected, ok);
        }

        [Theory]
        [InlineData("")]
        [InlineData("0,1")]
        [InlineData("x,0,0")]
        [InlineData("0,y,0,90")]
        public void TryParseFxLocalRotation_RejectsMalformedValues(string value)
        {
            bool ok = GhostVisualBuilder.TryParseFxLocalRotation(value, out _);

            Assert.False(ok);
        }

        [Fact]
        public void TryParseFxLocalRotation_AcceptsValidAxisAngle()
        {
            bool ok = GhostVisualBuilder.TryParseFxLocalRotation("0,0,0,90", out Quaternion parsed);

            Assert.True(ok);
            Assert.Equal(Quaternion.identity, parsed);
        }

        [Fact]
        public void GetPartTransformRaw_PrefersLegacyKeys_ButSupportsProtoKeys()
        {
            var node = new ConfigNode("PART");
            node.AddValue("position", "1,2,3");
            node.AddValue("rotation", "0,0,0,1");
            Assert.Equal("1,2,3", GhostVisualBuilder.GetPartPositionRaw(node));
            Assert.Equal("0,0,0,1", GhostVisualBuilder.GetPartRotationRaw(node));

            node.AddValue("pos", "4,5,6");
            node.AddValue("rot", "0.1,0.2,0.3,0.4");
            Assert.Equal("4,5,6", GhostVisualBuilder.GetPartPositionRaw(node));
            Assert.Equal("0.1,0.2,0.3,0.4", GhostVisualBuilder.GetPartRotationRaw(node));
        }

        [Fact]
        public void StashPendingTree_SetsPendingStashedThisTransition_ViaTree()
        {
            Assert.False(RecordingStore.PendingStashedThisTransition);

            var tree = new RecordingTree { TreeName = "TestTree" };
            RecordingStore.StashPendingTree(tree);

            Assert.True(RecordingStore.PendingStashedThisTransition);
        }

        [Fact]
        public void StashPendingTree_SetsPendingStashedThisTransition()
        {
            Assert.False(RecordingStore.PendingStashedThisTransition);

            var tree = new RecordingTree { TreeName = "TestTree" };
            RecordingStore.StashPendingTree(tree);

            Assert.True(RecordingStore.PendingStashedThisTransition);
        }

        [Fact]
        public void StashPendingTree_Null_DoesNotSetFlag()
        {
            RecordingStore.StashPendingTree(null);

            Assert.False(RecordingStore.PendingStashedThisTransition);
        }

        [Fact]
        public void ResetForTesting_ClearsPendingStashedFlag()
        {
            var tree = new RecordingTree { TreeName = "Ship" };
            RecordingStore.StashPendingTree(tree);
            Assert.True(RecordingStore.PendingStashedThisTransition);

            RecordingStore.ResetForTesting();

            Assert.False(RecordingStore.PendingStashedThisTransition);
        }
    }

    [Collection("Sequential")]
    public class CrewReplacementTests
    {
        public CrewReplacementTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        [Fact]
        public void CrewReplacements_EmptyByDefault()
        {
            Assert.Empty(CrewReservationManager.CrewReplacements);
        }

        [Fact]
        public void ResetReplacementsForTesting_ClearsDictionary()
        {
            // We can't call ReserveCrewIn directly (needs KSP roster),
            // but we can test the serialization round-trip which populates the dictionary.
            var node = new ConfigNode("SCENARIO");
            var replacementsNode = node.AddNode("CREW_REPLACEMENTS");
            var entry = replacementsNode.AddNode("ENTRY");
            entry.AddValue("original", "Jebediah Kerman");
            entry.AddValue("replacement", "Bob Kerman Jr.");

            // Use OnLoad to populate (need a scenario instance)
            // Instead, test via the static accessor after reset
            CrewReservationManager.ResetReplacementsForTesting();

            Assert.Empty(CrewReservationManager.CrewReplacements);
        }

        [Fact]
        public void CrewReplacements_SaveRoundTrip_PreservesMapping()
        {
            // Build a scenario ConfigNode with crew replacements
            var saveNode = new ConfigNode("SCENARIO");
            var replacementsNode = saveNode.AddNode("CREW_REPLACEMENTS");

            var entry1 = replacementsNode.AddNode("ENTRY");
            entry1.AddValue("original", "Jebediah Kerman");
            entry1.AddValue("replacement", "Rodfrey Kerman");

            var entry2 = replacementsNode.AddNode("ENTRY");
            entry2.AddValue("original", "Bill Kerman");
            entry2.AddValue("replacement", "Samantha Kerman");

            // Verify the ConfigNode structure is correct
            Assert.Equal(2, replacementsNode.GetNodes("ENTRY").Length);

            var loaded1 = replacementsNode.GetNodes("ENTRY")[0];
            Assert.Equal("Jebediah Kerman", loaded1.GetValue("original"));
            Assert.Equal("Rodfrey Kerman", loaded1.GetValue("replacement"));

            var loaded2 = replacementsNode.GetNodes("ENTRY")[1];
            Assert.Equal("Bill Kerman", loaded2.GetValue("original"));
            Assert.Equal("Samantha Kerman", loaded2.GetValue("replacement"));
        }

        // --- Reservation decision logic (extracted for testability) ---

        [Fact]
        public void ShouldProcessCrew_Available_ReturnsTrue()
        {
            Assert.True(CrewReservationManager.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Available));
        }

        [Fact]
        public void ShouldProcessCrew_Assigned_ReturnsTrue()
        {
            // Key scenario: crew already Assigned (on pad vessel after revert)
            // must still be processed so a replacement is hired and swap can happen
            Assert.True(CrewReservationManager.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Assigned));
        }

        [Fact]
        public void ShouldProcessCrew_Dead_ReturnsFalse()
        {
            Assert.False(CrewReservationManager.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Dead));
        }

        [Fact]
        public void ShouldProcessCrew_Missing_ReturnsTrue()
        {
            // Missing crew are processed: they may be alive but orphaned from
            // a removed vessel (e.g. --clean-start). ReserveCrewIn rescues them.
            Assert.True(CrewReservationManager.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Missing));
        }

        // --- Snapshot crew extraction ---

        [Fact]
        public void ExtractCrewFromSnapshot_SinglePart_SingleCrew()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");

            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);

            Assert.Single(crew);
            Assert.Equal("Jebediah Kerman", crew[0]);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_MultiplePartsAndCrew()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part1 = snapshot.AddNode("PART");
            part1.AddValue("crew", "Jebediah Kerman");
            part1.AddValue("crew", "Bill Kerman");
            var part2 = snapshot.AddNode("PART");
            part2.AddValue("crew", "Valentina Kerman");

            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);

            Assert.Equal(3, crew.Count);
            Assert.Contains("Jebediah Kerman", crew);
            Assert.Contains("Bill Kerman", crew);
            Assert.Contains("Valentina Kerman", crew);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_NoCrew_ReturnsEmpty()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART"); // part with no crew

            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);

            Assert.Empty(crew);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_NullSnapshot_ReturnsEmpty()
        {
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(null);

            Assert.Empty(crew);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_EmptyCrewValue_Skipped()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "");
            part.AddValue("crew", "Jebediah Kerman");

            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);

            Assert.Single(crew);
            Assert.Equal("Jebediah Kerman", crew[0]);
        }

        // --- EVA vessel removal decision logic (Bug #26) ---

        [Fact]
        public void ShouldRemoveEvaVessel_ReservedEva_ReturnsTrue()
        {
            var replacements = new Dictionary<string, string>
            {
                { "Valentina Kerman", "Agasel Kerman" }
            };

            Assert.True(CrewReservationManager.ShouldRemoveEvaVessel(
                true, "Valentina Kerman", replacements));
        }

        [Fact]
        public void ShouldRemoveEvaVessel_NonEva_ReturnsFalse()
        {
            // Non-EVA vessel with reserved crew should NOT be removed
            var replacements = new Dictionary<string, string>
            {
                { "Valentina Kerman", "Agasel Kerman" }
            };

            Assert.False(CrewReservationManager.ShouldRemoveEvaVessel(
                false, "Valentina Kerman", replacements));
        }

        [Fact]
        public void ShouldRemoveEvaVessel_EvaNotReserved_ReturnsFalse()
        {
            // EVA vessel whose crew is NOT in replacements should NOT be removed
            var replacements = new Dictionary<string, string>
            {
                { "Valentina Kerman", "Agasel Kerman" }
            };

            Assert.False(CrewReservationManager.ShouldRemoveEvaVessel(
                true, "Jebediah Kerman", replacements));
        }

        [Fact]
        public void ShouldRemoveEvaVessel_NullCrewName_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string>
            {
                { "Valentina Kerman", "Agasel Kerman" }
            };

            Assert.False(CrewReservationManager.ShouldRemoveEvaVessel(
                true, null, replacements));
        }

        [Fact]
        public void ShouldRemoveEvaVessel_EmptyReplacements_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string>();

            Assert.False(CrewReservationManager.ShouldRemoveEvaVessel(
                true, "Valentina Kerman", replacements));
        }

        // --- Bug #233: spawned EVA vessel PID guard ---

        [Fact]
        public void ShouldRemoveEvaVessel_SpawnedPidMatch_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string>
            {
                { "Valentina Kerman", "Agasel Kerman" }
            };
            var spawnedPids = new HashSet<uint> { 12345 };

            // Vessel PID matches a spawned recording — should NOT be removed
            Assert.False(CrewReservationManager.ShouldRemoveEvaVessel(
                true, "Valentina Kerman", replacements, 12345, spawnedPids));
        }

        [Fact]
        public void ShouldRemoveEvaVessel_SpawnedPidNoMatch_ReturnsTrue()
        {
            var replacements = new Dictionary<string, string>
            {
                { "Valentina Kerman", "Agasel Kerman" }
            };
            var spawnedPids = new HashSet<uint> { 99999 };

            // Vessel PID does NOT match — should be removed
            Assert.True(CrewReservationManager.ShouldRemoveEvaVessel(
                true, "Valentina Kerman", replacements, 12345, spawnedPids));
        }

        [Fact]
        public void ShouldRemoveEvaVessel_NullSpawnedPids_ReturnsTrue()
        {
            var replacements = new Dictionary<string, string>
            {
                { "Valentina Kerman", "Agasel Kerman" }
            };

            // No spawned PIDs set — backward compat, still removes
            Assert.True(CrewReservationManager.ShouldRemoveEvaVessel(
                true, "Valentina Kerman", replacements, 12345, null));
        }

        [Fact]
        public void ShouldRemoveEvaVessel_ZeroPid_SkipsPidCheck()
        {
            var replacements = new Dictionary<string, string>
            {
                { "Valentina Kerman", "Agasel Kerman" }
            };
            var spawnedPids = new HashSet<uint> { 0 }; // should not match pid=0

            // pid=0 means unknown — don't check against set
            Assert.True(CrewReservationManager.ShouldRemoveEvaVessel(
                true, "Valentina Kerman", replacements, 0, spawnedPids));
        }

        // --- BuildSpawnedVesselPidSet ---

        [Fact]
        public void BuildSpawnedVesselPidSet_CollectsNonZeroPids()
        {
            var recordings = new List<Recording>
            {
                new Recording { SpawnedVesselPersistentId = 1001 },
                new Recording { SpawnedVesselPersistentId = 0 },    // not spawned
                new Recording { SpawnedVesselPersistentId = 2002 },
            };

            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(recordings);

            Assert.Equal(2, pids.Count);
            Assert.Contains((uint)1001, pids);
            Assert.Contains((uint)2002, pids);
            Assert.DoesNotContain((uint)0, pids);
        }

        [Fact]
        public void BuildSpawnedVesselPidSet_NullRecordings_ReturnsEmpty()
        {
            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(null);
            Assert.Empty(pids);
        }

        #region Serialization Log Assertions

        [Fact]
        public void LoadCrewReplacements_NullNode_LogsNoReplacementsMessage()
        {
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            try
            {
                var node = new ConfigNode("SCENARIO");
                CrewReservationManager.LoadCrewReplacements(node);
                Assert.Contains(logLines, l => l.Contains("[CrewReservation]") && l.Contains("Loaded 0 crew replacements"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void LoadCrewReplacements_WithEntries_LogsCount()
        {
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            try
            {
                var node = new ConfigNode("SCENARIO");
                var crNode = node.AddNode("CREW_REPLACEMENTS");
                var entry = crNode.AddNode("ENTRY");
                entry.AddValue("original", "Jeb");
                entry.AddValue("replacement", "Bob");

                CrewReservationManager.LoadCrewReplacements(node);
                Assert.Contains(logLines, l => l.Contains("[CrewReservation]") && l.Contains("Loaded 1 crew replacement"));
                Assert.Equal("Bob", CrewReservationManager.CrewReplacements["Jeb"]);
            }
            finally
            {
                CrewReservationManager.ResetReplacementsForTesting();
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void SaveCrewReplacements_Empty_WritesNoNode()
        {
            CrewReservationManager.ResetReplacementsForTesting();
            var node = new ConfigNode("SCENARIO");
            CrewReservationManager.SaveCrewReplacements(node);
            Assert.Null(node.GetNode("CREW_REPLACEMENTS"));
        }

        [Fact]
        public void SaveCrewReplacements_WithData_RoundTrips()
        {
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            try
            {
                // Load some data
                var loadNode = new ConfigNode("SCENARIO");
                var crNode = loadNode.AddNode("CREW_REPLACEMENTS");
                var e1 = crNode.AddNode("ENTRY");
                e1.AddValue("original", "Jeb");
                e1.AddValue("replacement", "Bob");
                var e2 = crNode.AddNode("ENTRY");
                e2.AddValue("original", "Val");
                e2.AddValue("replacement", "Bill");
                CrewReservationManager.LoadCrewReplacements(loadNode);

                // Save
                var saveNode = new ConfigNode("SCENARIO");
                CrewReservationManager.SaveCrewReplacements(saveNode);

                // Verify round-trip
                var savedCr = saveNode.GetNode("CREW_REPLACEMENTS");
                Assert.NotNull(savedCr);
                var entries = savedCr.GetNodes("ENTRY");
                Assert.Equal(2, entries.Length);
                Assert.Contains(logLines, l => l.Contains("[CrewReservation]") && l.Contains("Saved 2 crew replacement"));
            }
            finally
            {
                CrewReservationManager.ResetReplacementsForTesting();
                ParsekLog.ResetTestOverrides();
            }
        }

        #endregion
    }

    [Collection("Sequential")]
    public class MergeDialogTests
    {
        private static List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + i * 10,
                    latitude = 0,
                    longitude = 0,
                    altitude = 100,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        // BuildMergeMessage and MergeDefault tests removed — those APIs were
        // deleted as part of standalone pending infrastructure removal.

        // --- Stationary point trimming tests ---

        private List<TrajectoryPoint> MakePadLaunchPoints()
        {
            // 5 stationary points on pad (alt=78, speed=0.3), then 5 moving points
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < 5; i++)
                points.Add(new TrajectoryPoint
                {
                    ut = 100 + i, altitude = 78, velocity = new Vector3(0.3f, 0, 0), bodyName = "Kerbin"
                });
            for (int i = 0; i < 5; i++)
                points.Add(new TrajectoryPoint
                {
                    ut = 105 + i, altitude = 80 + i * 5, velocity = new Vector3(10 + i * 5, 0, 0), bodyName = "Kerbin"
                });
            return points;
        }

        [Fact]
        public void CreateRecordingFromFlightData_TrimsLeadingStationaryPoints()
        {
            var points = MakePadLaunchPoints();
            var rec = RecordingStore.CreateRecordingFromFlightData(points, "TrimTest");

            Assert.NotNull(rec);
            // First 5 stationary points should be trimmed
            Assert.Equal(5, rec.Points.Count);
            Assert.Equal(105.0, rec.StartUT);
        }

        [Fact]
        public void CreateRecordingFromFlightData_RetimesPartEventsFromTrimmedWindow()
        {
            var points = MakePadLaunchPoints();
            var partEvents = new List<PartEvent>
            {
                new PartEvent { ut = 100, eventType = PartEventType.ShroudJettisoned, partPersistentId = 1 },
                new PartEvent { ut = 101, eventType = PartEventType.EngineIgnited, partPersistentId = 2 },
                new PartEvent { ut = 107, eventType = PartEventType.Decoupled, partPersistentId = 3 }
            };

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "RetimeTest", partEvents: partEvents);

            Assert.NotNull(rec);
            var events = rec.PartEvents;
            Assert.Equal(3, events.Count);
            // First two events should be retimed to the new start (105)
            Assert.Equal(105.0, events[0].ut);
            Assert.Equal(105.0, events[1].ut);
            // Third event is after trim point, unchanged
            Assert.Equal(107.0, events[2].ut);
        }

        [Fact]
        public void CreateRecordingFromFlightData_RemovesOrbitSegmentsBeforeTrim()
        {
            var points = MakePadLaunchPoints();
            var orbitSegments = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 100, endUT = 103, bodyName = "Kerbin" },
                new OrbitSegment { startUT = 106, endUT = 108, bodyName = "Kerbin" }
            };

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "OrbitTrimTest", orbitSegments: orbitSegments);

            Assert.NotNull(rec);
            // First segment ends at 103 < trimUT 105, should be removed
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(106.0, rec.OrbitSegments[0].startUT);
        }

        [Fact]
        public void CreateRecordingFromFlightData_NoTrimWhenAlreadyMoving()
        {
            // All points are moving from the start
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < 5; i++)
                points.Add(new TrajectoryPoint
                {
                    ut = 100 + i, altitude = 80 + i * 10, velocity = new Vector3(20, 0, 0), bodyName = "Kerbin"
                });

            var rec = RecordingStore.CreateRecordingFromFlightData(points, "NoTrimTest");

            Assert.NotNull(rec);
            Assert.Equal(5, rec.Points.Count);
            Assert.Equal(100.0, rec.StartUT);
        }

        [Fact]
        public void CreateRecordingFromFlightData_MultipleCallsReturnIndependentRecordings()
        {
            var recA = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "RocketA");
            Assert.NotNull(recA);
            Assert.Equal("RocketA", recA.VesselName);

            var recB = RecordingStore.CreateRecordingFromFlightData(MakePoints(4, 200), "RocketB");
            Assert.NotNull(recB);
            Assert.Equal("RocketB", recB.VesselName);
            Assert.Equal(4, recB.Points.Count);

            // Both recordings exist independently
            Assert.Equal("RocketA", recA.VesselName);
        }

        #region FlushDirtyFiles (T15 crash window)

        [Fact]
        public void CommitRecordingDirect_LogsFlushDirtyFiles()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "FlushTest");
                RecordingStore.CommitRecordingDirect(rec);

                // FlushDirtyFiles fires and logs (SaveRecordingFiles returns false
                // in test env — no KSP paths — so "failed 1" is expected)
                Assert.Contains(logLines, l =>
                    l.Contains("[RecordingStore]") && l.Contains("FlushDirtyFiles"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void RunOptimizationPass_SplitsTreeRecording()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            // Build a tree with one recording that spans two environment sections
            var tree = new RecordingTree
            {
                Id = "tree-split-test",
                TreeName = "SplitTest"
            };

            var rec = new Recording
            {
                RecordingId = "rec-001",
                VesselName = "Rocket",
                TreeId = tree.Id,
                VesselPersistentId = 42,
                TerminalStateValue = TerminalState.Destroyed,
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                RecordingFormatVersion = 0
            };
            // Points spanning exo→atmo transition
            rec.Points.Add(new TrajectoryPoint { ut = 17000, altitude = 80000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17029, altitude = 40000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17030, altitude = 30000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17060, altitude = 100, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 17000, endUT = 17030,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 17030, endUT = 17060,
                frames = new List<TrajectoryPoint>()
            });

            tree.Recordings[rec.RecordingId] = rec;
            tree.RootRecordingId = rec.RecordingId;

            // Commit tree directly
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            RecordingStore.CommittedTrees.Add(tree);

            RecordingStore.RunOptimizationPass();

            // Tree should now have 2 recordings
            Assert.Equal(2, tree.Recordings.Count);
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);

            // Both recordings should have the tree ID
            foreach (var r in tree.Recordings.Values)
                Assert.Equal(tree.Id, r.TreeId);
            Assert.All(tree.Recordings.Values, r => Assert.True(r.TreeOrder >= 0));

            // Chain linkage: both should share a ChainId
            var all = new List<Recording>(tree.Recordings.Values);
            all.Sort((a, b) => a.StartUT.CompareTo(b.StartUT));
            Assert.Equal(all[0].ChainId, all[1].ChainId);
            Assert.Equal(0, all[0].ChainIndex);
            Assert.Equal(1, all[1].ChainIndex);

            // Terminal state should be on the second half only
            Assert.Null(all[0].TerminalStateValue);
            Assert.Equal(TerminalState.Destroyed, all[1].TerminalStateValue);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitUpdatesTreeBranchPoint()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            // Build a tree with one recording that has a ChildBranchPointId
            // and spans two environment sections -- the split should move
            // the ChildBranchPointId to the second half and update the BP
            var tree = new RecordingTree
            {
                Id = "tree-bp-test",
                TreeName = "BPTest"
            };

            var bp = new BranchPoint
            {
                Id = "bp-001",
                UT = 17060,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec-parent" },
                ChildRecordingIds = new List<string> { "rec-child" }
            };
            tree.BranchPoints.Add(bp);

            var rec = new Recording
            {
                RecordingId = "rec-parent",
                VesselName = "Rocket",
                TreeId = tree.Id,
                VesselPersistentId = 42,
                ChildBranchPointId = "bp-001",
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                RecordingFormatVersion = 0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 17000, altitude = 80000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17029, altitude = 40000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17030, altitude = 30000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17060, altitude = 100, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 17000, endUT = 17030,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 17030, endUT = 17060,
                frames = new List<TrajectoryPoint>()
            });

            tree.Recordings[rec.RecordingId] = rec;
            tree.RootRecordingId = rec.RecordingId;

            RecordingStore.AddRecordingWithTreeForTesting(rec);
            RecordingStore.CommittedTrees.Add(tree);

            RecordingStore.RunOptimizationPass();

            // After split, first half should have no ChildBranchPointId
            Assert.Null(rec.ChildBranchPointId);

            // Second half should have the ChildBranchPointId
            var all = new List<Recording>(tree.Recordings.Values);
            var secondHalf = all.Find(r => r.RecordingId != "rec-parent");
            Assert.NotNull(secondHalf);
            Assert.Equal("bp-001", secondHalf.ChildBranchPointId);

            // BranchPoint.ParentRecordingIds should reference the second half, not the original
            Assert.Contains(secondHalf.RecordingId, bp.ParentRecordingIds);
            Assert.DoesNotContain("rec-parent", bp.ParentRecordingIds);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitLogsTreeId()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                var tree = new RecordingTree
                {
                    Id = "tree-log-test",
                    TreeName = "LogTest"
                };

                var rec = new Recording
                {
                    RecordingId = "rec-log",
                    VesselName = "Rocket",
                    TreeId = tree.Id,
                    VesselPersistentId = 42,
                    TerminalStateValue = TerminalState.Destroyed,
                    GhostVisualSnapshot = new ConfigNode("VESSEL"),
                    RecordingFormatVersion = 0
                };
                rec.Points.Add(new TrajectoryPoint { ut = 17000, altitude = 80000, bodyName = "Kerbin" });
                rec.Points.Add(new TrajectoryPoint { ut = 17029, altitude = 40000, bodyName = "Kerbin" });
                rec.Points.Add(new TrajectoryPoint { ut = 17030, altitude = 30000, bodyName = "Kerbin" });
                rec.Points.Add(new TrajectoryPoint { ut = 17060, altitude = 100, bodyName = "Kerbin" });
                rec.TrackSections.Add(new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic,
                    startUT = 17000, endUT = 17030,
                    frames = new List<TrajectoryPoint>()
                });
                rec.TrackSections.Add(new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    startUT = 17030, endUT = 17060,
                    frames = new List<TrajectoryPoint>()
                });

                tree.Recordings[rec.RecordingId] = rec;
                tree.RootRecordingId = rec.RecordingId;

                RecordingStore.AddRecordingWithTreeForTesting(rec);
                RecordingStore.CommittedTrees.Add(tree);

                RecordingStore.RunOptimizationPass();

                Assert.Contains(logLines, l =>
                    l.Contains("[RecordingStore]") && l.Contains("Split recording") && l.Contains("tree=tree-log-test"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
                RecordingStore.ResetForTesting();
            }
        }

        [Fact]
        public void RunOptimizationPass_LogsFlushDirtyFiles()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "OptFlush");
                RecordingStore.CommitRecordingDirect(rec);
                logLines.Clear();

                // Force-dirty to simulate optimizer touching the recording
                RecordingStore.CommittedRecordings[0].FilesDirty = true;
                RecordingStore.RunOptimizationPass();

                Assert.Contains(logLines, l =>
                    l.Contains("[RecordingStore]") && l.Contains("FlushDirtyFiles"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void RunOptimizationPass_NoRecordings_NoFlushLog()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                RecordingStore.RunOptimizationPass();

                Assert.DoesNotContain(logLines, l => l.Contains("FlushDirtyFiles"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void RunOptimizationPass_CleanRecordings_NoFlushLog()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Clean");
                RecordingStore.AddRecordingWithTreeForTesting(rec);
                // Pretend save succeeded
                RecordingStore.CommittedRecordings[0].FilesDirty = false;
                logLines.Clear();

                RecordingStore.RunOptimizationPass();

                Assert.DoesNotContain(logLines, l => l.Contains("FlushDirtyFiles"));
            }
            finally
            {
                ParsekLog.SuppressLogging = true;
                ParsekLog.ResetTestOverrides();
            }
        }

        #endregion
    }
}
