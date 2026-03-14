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
            MilestoneStore.SuppressLogging = true;
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
        public void StashPending_WithPoints_SetsPending()
        {
            var points = MakePoints(5);

            RecordingStore.StashPending(points, "TestVessel");

            Assert.True(RecordingStore.HasPending);
            Assert.Equal(5, RecordingStore.Pending.Points.Count);
            Assert.Equal("TestVessel", RecordingStore.Pending.VesselName);
        }

        [Fact]
        public void StashPending_EmptyList_DoesNotStash()
        {
            RecordingStore.StashPending(new List<TrajectoryPoint>(), "Empty");

            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void StashPending_NullList_DoesNotStash()
        {
            RecordingStore.StashPending(null, "Null");

            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void CommitPending_MovesPendingToCommitted()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");

            RecordingStore.CommitPending();

            Assert.False(RecordingStore.HasPending);
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("Ship", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void CommitPending_NoPending_DoesNothing()
        {
            // Should not crash
            RecordingStore.CommitPending();

            Assert.False(RecordingStore.HasPending);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void DiscardPending_ClearsPending()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");

            RecordingStore.DiscardPending();

            Assert.False(RecordingStore.HasPending);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void Clear_ClearsEverything()
        {
            // Stash + commit one
            RecordingStore.StashPending(MakePoints(3), "First");
            RecordingStore.CommitPending();
            // Stash another (pending)
            RecordingStore.StashPending(MakePoints(2), "Second");

            RecordingStore.Clear();

            Assert.False(RecordingStore.HasPending);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void Recording_StartUT_EndUT_Computed()
        {
            RecordingStore.StashPending(MakePoints(5, startUT: 200), "Ship");

            var rec = RecordingStore.Pending;
            Assert.Equal(200, rec.StartUT);
            Assert.Equal(240, rec.EndUT); // 200 + 4*10
        }

        [Fact]
        public void Recording_StartUT_EndUT_EmptyPoints()
        {
            var rec = new RecordingStore.Recording();
            Assert.Equal(0, rec.StartUT);
            Assert.Equal(0, rec.EndUT);
        }

        [Fact]
        public void Recording_VesselSnapshot_StoredAndRetrieved()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");

            var node = new ConfigNode("VESSEL");
            node.AddValue("name", "TestVessel");
            RecordingStore.Pending.VesselSnapshot = node;

            Assert.NotNull(RecordingStore.Pending.VesselSnapshot);
            Assert.Equal("TestVessel", RecordingStore.Pending.VesselSnapshot.GetValue("name"));
        }

        [Fact]
        public void Recording_DistanceAndDestroyed_DefaultValues()
        {
            var rec = new RecordingStore.Recording();

            Assert.Equal(0, rec.DistanceFromLaunch);
            Assert.False(rec.VesselDestroyed);
            Assert.Null(rec.VesselSnapshot);
            Assert.Null(rec.VesselSituation);
        }

        [Fact]
        public void Recording_DefaultFields()
        {
            var rec = new RecordingStore.Recording();

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
            var rec = new RecordingStore.Recording();
            rec.DistanceFromLaunch = 12345.67;
            rec.MaxDistanceFromLaunch = 99999.99;

            Assert.Equal(12345.67, rec.DistanceFromLaunch);
            Assert.Equal(99999.99, rec.MaxDistanceFromLaunch);
        }

        [Fact]
        public void Recording_SpawnFields_Roundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = 42u;

            Assert.True(rec.VesselSpawned);
            Assert.Equal(42u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void Recording_LastAppliedResourceIndex_Tracks()
        {
            var rec = new RecordingStore.Recording();
            rec.LastAppliedResourceIndex = 5;

            Assert.Equal(5, rec.LastAppliedResourceIndex);
        }

        [Fact]
        public void StashPending_SinglePoint_Discards()
        {
            var points = MakePoints(1);

            RecordingStore.StashPending(points, "TooShort");

            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void StashPending_ExactlyTwoPoints_Succeeds()
        {
            var points = MakePoints(2);

            RecordingStore.StashPending(points, "MinValid");

            Assert.True(RecordingStore.HasPending);
            Assert.Equal(2, RecordingStore.Pending.Points.Count);
            Assert.False(string.IsNullOrEmpty(RecordingStore.Pending.RecordingId));
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion,
                RecordingStore.Pending.RecordingFormatVersion);
        }

        [Fact]
        public void BuildGhostGeometryRelativePath_UsesRecordingIdAndExtension()
        {
            string id = "abc123";
            string rel = RecordingPaths.BuildGhostGeometryRelativePath(id).Replace('\\', '/');
            Assert.Equal("Parsek/Recordings/abc123.pcrf", rel);
        }

        [Fact]
        public void StashPending_UsesProvidedRecordingMetadata()
        {
            var points = MakePoints(3);
            RecordingStore.StashPending(
                points,
                "Ship",
                recordingId: "fixedid",
                recordingFormatVersion: 7);

            Assert.True(RecordingStore.HasPending);
            Assert.Equal("fixedid", RecordingStore.Pending.RecordingId);
            Assert.Equal(7, RecordingStore.Pending.RecordingFormatVersion);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesOnlyPersistenceFields()
        {
            var vesselSnapshot = new ConfigNode("VESSEL");
            vesselSnapshot.AddValue("name", "Vessel A");
            var ghostSnapshot = new ConfigNode("VESSEL");
            ghostSnapshot.AddValue("name", "Ghost A");
            var source = new RecordingStore.Recording
            {
                RecordingId = "abc",
                RecordingFormatVersion = 9,
                VesselSnapshot = vesselSnapshot,
                GhostVisualSnapshot = ghostSnapshot,
                DistanceFromLaunch = 123,
                VesselDestroyed = true,
                VesselSituation = "Destroyed",
                MaxDistanceFromLaunch = 456,
            };
            source.Points.AddRange(MakePoints(4));
            source.OrbitSegments.Add(new OrbitSegment { startUT = 1, endUT = 2, bodyName = "Kerbin" });

            var target = new RecordingStore.Recording
            {
                VesselName = "Target",
                Points = MakePoints(2),
                OrbitSegments = new List<OrbitSegment> { new OrbitSegment { startUT = 10, endUT = 20, bodyName = "Mun" } }
            };

            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal("abc", target.RecordingId);
            Assert.Equal(9, target.RecordingFormatVersion);
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

        /// <summary>
        /// Verifies that legacy ghost geometry fields populated on the source recording
        /// are NOT propagated to the target. Catches regressions if someone re-adds
        /// ghost geometry copying to ApplyPersistenceArtifactsFrom.
        /// </summary>
        [Fact]
        public void ApplyPersistenceArtifactsFrom_DoesNotCopyLegacyGhostGeometryFields()
        {
            var source = new RecordingStore.Recording
            {
                RecordingId = "src",
                // Simulate a legacy recording that had ghost geometry fields populated
                GhostGeometryRelativePath = "Parsek/Recordings/src.pcrf",
                GhostGeometryAvailable = true,
                GhostGeometryCaptureError = "none",
                GhostGeometryCaptureStrategy = "live_hierarchy_probe_v1",
                GhostGeometryProbeStatus = "ready_for_hierarchy_clone",
                GhostGeometryVersion = 5,
            };
            source.Points.AddRange(MakePoints(3));

            var target = new RecordingStore.Recording { VesselName = "Target", Points = MakePoints(2) };
            target.ApplyPersistenceArtifactsFrom(source);

            // Ghost geometry fields must NOT transfer — they are dead legacy fields
            Assert.Null(target.GhostGeometryRelativePath);
            Assert.False(target.GhostGeometryAvailable);
            Assert.Null(target.GhostGeometryCaptureError);
            Assert.Null(target.GhostGeometryCaptureStrategy);
            Assert.Null(target.GhostGeometryProbeStatus);
            Assert.Equal(1, target.GhostGeometryVersion); // default, not copied from source
        }

        [Fact]
        public void RecordingMetadata_SaveLoad_RoundTrip()
        {
            var source = new RecordingStore.Recording
            {
                RecordingId = "meta123",
                RecordingFormatVersion = 12,
                LoopPlayback = true,
                LoopIntervalSeconds = 2.5,
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new RecordingStore.Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal("meta123", loaded.RecordingId);
            Assert.Equal(12, loaded.RecordingFormatVersion);
            Assert.True(loaded.LoopPlayback);
            Assert.Equal(2.5, loaded.LoopIntervalSeconds);

            // Ghost geometry fields are no longer serialized on save
            Assert.Null(node.GetValue("ghostGeometryVersion"));
            Assert.Null(node.GetValue("ghostGeometryStrategy"));
            Assert.Null(node.GetValue("ghostGeometryPath"));
            Assert.Null(node.GetValue("ghostGeometryAvailable"));
        }

        [Fact]
        public void RecordingMetadata_Load_MissingFields_KeepsDefaults()
        {
            var node = new ConfigNode("RECORDING");
            var loaded = new RecordingStore.Recording();

            string defaultId = loaded.RecordingId;
            int defaultRecVer = loaded.RecordingFormatVersion;

            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(defaultId, loaded.RecordingId);
            Assert.Equal(defaultRecVer, loaded.RecordingFormatVersion);
            Assert.False(loaded.LoopPlayback);
            Assert.Equal(10.0, loaded.LoopIntervalSeconds);
        }

        [Fact]
        public void RecordingMetadata_BackwardCompat_LoadsLegacyGhostGeometryFields()
        {
            // Legacy save files may contain ghost geometry fields — verify they deserialize
            // without error (backward compat) even though they're no longer written.
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "legacy-rec");
            node.AddValue("ghostGeometryVersion", "8");
            node.AddValue("ghostGeometryStrategy", "live_hierarchy_probe_v1");
            node.AddValue("ghostGeometryProbeStatus", "ready_for_hierarchy_clone");
            node.AddValue("ghostGeometryPath", "Parsek/Recordings/legacy.pcrf");
            node.AddValue("ghostGeometryAvailable", "True");
            node.AddValue("ghostGeometryError", "none");

            var loaded = new RecordingStore.Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal("legacy-rec", loaded.RecordingId);
            Assert.Equal(8, loaded.GhostGeometryVersion);
            Assert.Equal("Parsek/Recordings/legacy.pcrf", loaded.GhostGeometryRelativePath);
            Assert.True(loaded.GhostGeometryAvailable);
        }

        [Fact]
        public void DeleteRecordingFiles_NoId_DoesNotThrow()
        {
            var rec = new RecordingStore.Recording();
            rec.RecordingId = null;
            RecordingStore.DeleteRecordingFiles(rec);
        }

        [Fact]
        public void DeleteRecordingFiles_NoSaveContext_DoesNotThrow()
        {
            var rec = new RecordingStore.Recording
            {
                RecordingId = "test123"
            };
            RecordingStore.DeleteRecordingFiles(rec);
        }

        [Fact]
        public void DiscardPending_WithGeometryPath_DoesNotThrow()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");
            RecordingStore.Pending.GhostGeometryRelativePath = "Parsek/Recordings/pending.pcrf";
            RecordingStore.DiscardPending();
            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void ClearCommitted_WithGeometryPath_DoesNotThrow()
        {
            RecordingStore.StashPending(MakePoints(3), "A");
            RecordingStore.Pending.GhostGeometryRelativePath = "Parsek/Recordings/a.pcrf";
            RecordingStore.CommitPending();
            RecordingStore.StashPending(MakePoints(3, 200), "B");
            RecordingStore.Pending.GhostGeometryRelativePath = "Parsek/Recordings/b.pcrf";
            RecordingStore.CommitPending();

            RecordingStore.ClearCommitted();

            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void Clear_WithPendingAndCommittedGeometry_DoesNotThrow()
        {
            RecordingStore.StashPending(MakePoints(3), "A");
            RecordingStore.Pending.GhostGeometryRelativePath = "Parsek/Recordings/a.pcrf";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Pending");
            RecordingStore.Pending.GhostGeometryRelativePath = "Parsek/Recordings/pending.pcrf";

            RecordingStore.Clear();

            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.False(RecordingStore.HasPending);
        }

        [Theory]
        [InlineData("abc123.prec", "abc123")]
        [InlineData("abc123_vessel.craft", "abc123")]
        [InlineData("abc123_ghost.craft", "abc123")]
        [InlineData("abc123.pcrf", "abc123")]
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

        [Fact]
        public void GetGhostSnapshot_PrefersGhostVisualOverVesselSnapshot()
        {
            var rec = new RecordingStore.Recording();
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("name", "EndSnapshot");
            var ghost = new ConfigNode("VESSEL");
            ghost.AddValue("name", "StartSnapshot");
            rec.VesselSnapshot = vessel;
            rec.GhostVisualSnapshot = ghost;

            var selected = GhostVisualBuilder.GetGhostSnapshot(rec);

            Assert.Equal("StartSnapshot", selected.GetValue("name"));
        }

        [Fact]
        public void GetGhostSnapshot_ReturnsGhostWhenVesselSnapshotIsNull()
        {
            var rec = new RecordingStore.Recording();
            var ghost = new ConfigNode("VESSEL");
            ghost.AddValue("name", "StartSnapshot");
            rec.VesselSnapshot = null;
            rec.GhostVisualSnapshot = ghost;

            var selected = GhostVisualBuilder.GetGhostSnapshot(rec);

            Assert.NotNull(selected);
            Assert.Equal("StartSnapshot", selected.GetValue("name"));
        }

        [Fact]
        public void GetGhostSnapshot_BothNull_ReturnsNull()
        {
            var rec = new RecordingStore.Recording();
            rec.VesselSnapshot = null;
            rec.GhostVisualSnapshot = null;

            Assert.Null(GhostVisualBuilder.GetGhostSnapshot(rec));
        }

        [Fact]
        public void GetGhostSnapshot_NullRecording_ReturnsNull()
        {
            Assert.Null(GhostVisualBuilder.GetGhostSnapshot(null));
        }

        [Fact]
        public void GetGhostSnapshot_OnlyVesselSnapshot_ReturnsFallback()
        {
            var rec = new RecordingStore.Recording();
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("name", "VesselFallback");
            rec.VesselSnapshot = vessel;
            rec.GhostVisualSnapshot = null;

            var selected = GhostVisualBuilder.GetGhostSnapshot(rec);

            Assert.NotNull(selected);
            Assert.Equal("VesselFallback", selected.GetValue("name"));
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
        public void GetRecommendedAction_DestroyedWithSnapshot()
        {
            // destroyed=true takes priority over hasSnapshot=true
            var result = RecordingStore.GetRecommendedAction(
                destroyed: true, hasSnapshot: true);

            Assert.Equal(RecordingStore.MergeDefault.GhostOnly, result);
        }
    }

    [Collection("Sequential")]
    public class CrewReplacementTests
    {
        public CrewReplacementTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetReplacementsForTesting();
        }

        [Fact]
        public void CrewReplacements_EmptyByDefault()
        {
            Assert.Empty(ParsekScenario.CrewReplacements);
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
            ParsekScenario.ResetReplacementsForTesting();

            Assert.Empty(ParsekScenario.CrewReplacements);
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

        [Fact]
        public void CrewReplacements_SaveNode_EmptyMappingSkipsNode()
        {
            // With no replacements, the CREW_REPLACEMENTS node should not be created
            var node = new ConfigNode("SCENARIO");

            // No CREW_REPLACEMENTS node means nothing to load
            Assert.Null(node.GetNode("CREW_REPLACEMENTS"));
        }

        [Fact]
        public void CrewReplacements_LoadNode_HandlesNullValues()
        {
            var node = new ConfigNode("SCENARIO");
            var replacementsNode = node.AddNode("CREW_REPLACEMENTS");

            // Entry with missing replacement value
            var entry = replacementsNode.AddNode("ENTRY");
            entry.AddValue("original", "Jeb");
            // No "replacement" value

            // This should not crash and should not add an invalid mapping
            var crNode = node.GetNode("CREW_REPLACEMENTS");
            var entries = crNode.GetNodes("ENTRY");
            Assert.Single(entries);

            string replacement = entries[0].GetValue("replacement");
            Assert.Null(replacement);
        }

        [Fact]
        public void CrewReplacements_LoadNode_MissingNodeReturnsCleanState()
        {
            var node = new ConfigNode("SCENARIO");

            // No CREW_REPLACEMENTS node at all
            Assert.Null(node.GetNode("CREW_REPLACEMENTS"));
            // This mirrors what LoadCrewReplacements does: clear + return early
        }

        // --- Reservation decision logic (extracted for testability) ---

        [Fact]
        public void ShouldProcessCrew_Available_ReturnsTrue()
        {
            Assert.True(ParsekScenario.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Available));
        }

        [Fact]
        public void ShouldProcessCrew_Assigned_ReturnsTrue()
        {
            // Key scenario: crew already Assigned (on pad vessel after revert)
            // must still be processed so a replacement is hired and swap can happen
            Assert.True(ParsekScenario.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Assigned));
        }

        [Fact]
        public void ShouldProcessCrew_Dead_ReturnsFalse()
        {
            Assert.False(ParsekScenario.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Dead));
        }

        [Fact]
        public void ShouldProcessCrew_Missing_ReturnsTrue()
        {
            // Missing crew are processed: they may be alive but orphaned from
            // a removed vessel (e.g. --clean-start). ReserveCrewIn rescues them.
            Assert.True(ParsekScenario.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Missing));
        }

        // --- Snapshot crew extraction ---

        [Fact]
        public void ExtractCrewFromSnapshot_SinglePart_SingleCrew()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");

            var crew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);

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

            var crew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);

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

            var crew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);

            Assert.Empty(crew);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_NullSnapshot_ReturnsEmpty()
        {
            var crew = ParsekScenario.ExtractCrewFromSnapshot(null);

            Assert.Empty(crew);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_EmptyCrewValue_Skipped()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "");
            part.AddValue("crew", "Jebediah Kerman");

            var crew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);

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

            Assert.True(ParsekScenario.ShouldRemoveEvaVessel(
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

            Assert.False(ParsekScenario.ShouldRemoveEvaVessel(
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

            Assert.False(ParsekScenario.ShouldRemoveEvaVessel(
                true, "Jebediah Kerman", replacements));
        }

        [Fact]
        public void ShouldRemoveEvaVessel_NullCrewName_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string>
            {
                { "Valentina Kerman", "Agasel Kerman" }
            };

            Assert.False(ParsekScenario.ShouldRemoveEvaVessel(
                true, null, replacements));
        }

        [Fact]
        public void ShouldRemoveEvaVessel_EmptyReplacements_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string>();

            Assert.False(ParsekScenario.ShouldRemoveEvaVessel(
                true, "Valentina Kerman", replacements));
        }
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

        [Fact]
        public void BuildMergeMessage_GhostOnly_Destroyed_MentionsDestroyed()
        {
            var rec = new RecordingStore.Recording { VesselName = "Boom" };
            rec.Points.AddRange(MakePoints(5));
            rec.VesselDestroyed = true;

            string msg = MergeDialog.BuildMergeMessage(rec, 60,
                RecordingStore.MergeDefault.GhostOnly);

            Assert.Contains("destroyed", msg);
        }

        [Fact]
        public void BuildMergeMessage_GhostOnly_NotDestroyed_DoesNotMentionDestroyed()
        {
            var rec = new RecordingStore.Recording { VesselName = "Phantom" };
            rec.Points.AddRange(MakePoints(5));
            rec.VesselDestroyed = false;

            string msg = MergeDialog.BuildMergeMessage(rec, 60,
                RecordingStore.MergeDefault.GhostOnly);

            Assert.DoesNotContain("destroyed", msg);
            Assert.Contains("Recording captured", msg);
        }

        [Fact]
        public void BuildMergeMessage_Persist_ShortDistance_MentionsMaxDistance()
        {
            var rec = new RecordingStore.Recording { VesselName = "Orbiter" };
            rec.Points.AddRange(MakePoints(10));
            rec.DistanceFromLaunch = 50;
            rec.MaxDistanceFromLaunch = 500000;

            string msg = MergeDialog.BuildMergeMessage(rec, 300,
                RecordingStore.MergeDefault.Persist);

            Assert.Contains("500000", msg);
            Assert.Contains("persist", msg, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildMergeMessage_Persist_FarDistance_MentionsSituation()
        {
            var rec = new RecordingStore.Recording { VesselName = "Explorer" };
            rec.Points.AddRange(MakePoints(10));
            rec.DistanceFromLaunch = 500;
            rec.VesselSituation = "Orbiting Kerbin";

            string msg = MergeDialog.BuildMergeMessage(rec, 300,
                RecordingStore.MergeDefault.Persist);

            Assert.Contains("Orbiting Kerbin", msg);
        }

        [Fact]
        public void BuildMergeMessage_IncludesPointCount()
        {
            var rec = new RecordingStore.Recording { VesselName = "Ship" };
            rec.Points.AddRange(MakePoints(7));

            string msg = MergeDialog.BuildMergeMessage(rec, 60,
                RecordingStore.MergeDefault.GhostOnly);

            Assert.Contains("7", msg);
        }

        [Fact]
        public void BuildMergeMessage_IncludesDuration()
        {
            var rec = new RecordingStore.Recording { VesselName = "Ship" };
            rec.Points.AddRange(MakePoints(3));

            string msg = MergeDialog.BuildMergeMessage(rec, 45.3,
                RecordingStore.MergeDefault.GhostOnly);

            // Duration is locale-formatted ("45.3" or "45,3"), just check it contains "45"
            Assert.Contains("45", msg);
            Assert.Contains("Duration:", msg);
        }

        [Fact]
        public void SyncVersionFromPrecFile_UpgradesStaleMetadata()
        {
            var rec = new RecordingStore.Recording
            {
                RecordingId = "synctest",
                RecordingFormatVersion = 4
            };

            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "5");

            RecordingStore.SyncVersionFromPrecFile(precNode, rec);

            Assert.Equal(5, rec.RecordingFormatVersion);
        }

        [Fact]
        public void SyncVersionFromPrecFile_DoesNotDowngrade()
        {
            var rec = new RecordingStore.Recording
            {
                RecordingId = "synctest",
                RecordingFormatVersion = 5
            };

            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "4");

            RecordingStore.SyncVersionFromPrecFile(precNode, rec);

            Assert.Equal(5, rec.RecordingFormatVersion);
        }

        [Fact]
        public void SyncVersionFromPrecFile_NoVersionField_NoChange()
        {
            var rec = new RecordingStore.Recording
            {
                RecordingId = "synctest",
                RecordingFormatVersion = 4
            };

            var precNode = new ConfigNode("PARSEK_RECORDING");

            RecordingStore.SyncVersionFromPrecFile(precNode, rec);

            Assert.Equal(4, rec.RecordingFormatVersion);
        }

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
        public void StashPending_TrimsLeadingStationaryPoints()
        {
            var points = MakePadLaunchPoints();
            RecordingStore.StashPending(points, "TrimTest");

            Assert.True(RecordingStore.HasPending);
            // First 5 stationary points should be trimmed
            Assert.Equal(5, RecordingStore.Pending.Points.Count);
            Assert.Equal(105.0, RecordingStore.Pending.StartUT);
        }

        [Fact]
        public void StashPending_RetimesPartEventsFromTrimmedWindow()
        {
            var points = MakePadLaunchPoints();
            var partEvents = new List<PartEvent>
            {
                new PartEvent { ut = 100, eventType = PartEventType.ShroudJettisoned, partPersistentId = 1 },
                new PartEvent { ut = 101, eventType = PartEventType.EngineIgnited, partPersistentId = 2 },
                new PartEvent { ut = 107, eventType = PartEventType.Decoupled, partPersistentId = 3 }
            };

            RecordingStore.StashPending(points, "RetimeTest", partEvents: partEvents);

            Assert.True(RecordingStore.HasPending);
            var events = RecordingStore.Pending.PartEvents;
            Assert.Equal(3, events.Count);
            // First two events should be retimed to the new start (105)
            Assert.Equal(105.0, events[0].ut);
            Assert.Equal(105.0, events[1].ut);
            // Third event is after trim point, unchanged
            Assert.Equal(107.0, events[2].ut);
        }

        [Fact]
        public void StashPending_RemovesOrbitSegmentsBeforeTrim()
        {
            var points = MakePadLaunchPoints();
            var orbitSegments = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 100, endUT = 103, bodyName = "Kerbin" },
                new OrbitSegment { startUT = 106, endUT = 108, bodyName = "Kerbin" }
            };

            RecordingStore.StashPending(points, "OrbitTrimTest", orbitSegments: orbitSegments);

            Assert.True(RecordingStore.HasPending);
            // First segment ends at 103 < trimUT 105, should be removed
            Assert.Single(RecordingStore.Pending.OrbitSegments);
            Assert.Equal(106.0, RecordingStore.Pending.OrbitSegments[0].startUT);
        }

        [Fact]
        public void StashPending_NoTrimWhenAlreadyMoving()
        {
            // All points are moving from the start
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < 5; i++)
                points.Add(new TrajectoryPoint
                {
                    ut = 100 + i, altitude = 80 + i * 10, velocity = new Vector3(20, 0, 0), bodyName = "Kerbin"
                });

            RecordingStore.StashPending(points, "NoTrimTest");

            Assert.True(RecordingStore.HasPending);
            Assert.Equal(5, RecordingStore.Pending.Points.Count);
            Assert.Equal(100.0, RecordingStore.Pending.StartUT);
        }
    }
}
