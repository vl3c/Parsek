using System;
using System.Collections.Generic;
using System.IO;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Crew inventory capture and restore (<see cref="CrewInventorySnapshot"/>): a crewed
    /// kerbal's inventory lives on his roster entry, not in the vessel's PART nodes, so the
    /// recorded snapshot carries a <c>PARSEK_CREW_INVENTORY</c> child and the spawn
    /// primitives restore it onto the seated kerbals. The live halves (the Vessel capture
    /// and the roster applier) need KSP; everything they feed and consume is pinned here.
    /// </summary>
    [Collection("Sequential")]
    public class CrewInventorySnapshotTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public CrewInventorySnapshotTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-crewinv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            try { Directory.Delete(tempDir, true); } catch { }
        }

        private static ConfigNode Inventory(params string[] partNames)
        {
            var inv = new ConfigNode("INVENTORY");
            inv.AddValue("name", "ModuleInventoryPart");
            if (partNames.Length > 0)
            {
                var stored = inv.AddNode("STOREDPARTS");
                for (int i = 0; i < partNames.Length; i++)
                {
                    var sp = stored.AddNode("STOREDPART");
                    sp.AddValue("slotIndex", i.ToString());
                    sp.AddValue("partName", partNames[i]);
                }
            }
            return inv;
        }

        private static List<string> StoredPartNames(ConfigNode inventory)
        {
            var names = new List<string>();
            ConfigNode stored = inventory?.GetNode("STOREDPARTS");
            if (stored == null) return names;
            foreach (ConfigNode sp in stored.GetNodes("STOREDPART"))
                names.Add(sp.GetValue("partName"));
            return names;
        }

        private static ConfigNode CrewedSnapshotWithInventory()
        {
            return new VesselSnapshotBuilder()
                .WithName("Inventory Lander")
                .AddPart("mk1-3pod", crew: "Jebediah Kerman")
                .AddPart("landerCabinSmall", crew: "Bill Kerman")
                .WithCrewInventory("Jebediah Kerman", "evaChute", "groundExperimentPack")
                .WithCrewInventory("Bill Kerman")
                .Build();
        }

        #region Codec: write / read / rename / strip

        [Fact]
        public void WriteThenRead_RoundTripsEveryEntryInStockShape()
        {
            var snap = new ConfigNode("VESSEL");
            var entries = new List<KeyValuePair<string, ConfigNode>>
            {
                new KeyValuePair<string, ConfigNode>("Jebediah Kerman", Inventory("evaChute", "cargoContainer")),
                new KeyValuePair<string, ConfigNode>("Bill Kerman", Inventory()),
                new KeyValuePair<string, ConfigNode>("", Inventory("skipped")),
                new KeyValuePair<string, ConfigNode>("Val Kerman", null),
            };

            int written = CrewInventorySnapshot.WriteToSnapshot(snap, entries);

            Assert.Equal(2, written);
            Assert.True(CrewInventorySnapshot.HasCapture(snap));
            var read = CrewInventorySnapshot.ReadFromSnapshot(snap);
            Assert.Equal(2, read.Count);
            Assert.Equal(new[] { "evaChute", "cargoContainer" }, StoredPartNames(read["Jebediah Kerman"]));
            Assert.Empty(StoredPartNames(read["Bill Kerman"]));
            Assert.Equal("INVENTORY", read["Jebediah Kerman"].name);
            Assert.Equal("ModuleInventoryPart", read["Jebediah Kerman"].GetValue("name"));
        }

        [Fact]
        public void Write_DeepCopiesTheSource_AndReplacesAnEarlierCapture()
        {
            var snap = new ConfigNode("VESSEL");
            var source = Inventory("evaChute");
            CrewInventorySnapshot.WriteToSnapshot(snap, new List<KeyValuePair<string, ConfigNode>>
            {
                new KeyValuePair<string, ConfigNode>("Jebediah Kerman", source),
            });
            source.GetNode("STOREDPARTS").AddNode("STOREDPART").AddValue("partName", "late");

            CrewInventorySnapshot.WriteToSnapshot(snap, new List<KeyValuePair<string, ConfigNode>>
            {
                new KeyValuePair<string, ConfigNode>("Bill Kerman", Inventory("cargoContainer")),
            });

            Assert.Single(snap.GetNodes(CrewInventorySnapshot.NodeName));
            var read = CrewInventorySnapshot.ReadFromSnapshot(snap);
            Assert.False(read.ContainsKey("Jebediah Kerman"));
            Assert.Equal(new[] { "cargoContainer" }, StoredPartNames(read["Bill Kerman"]));
        }

        [Fact]
        public void Read_NoCaptureNode_ReturnsNull()
        {
            ConfigNode snap = VesselSnapshotBuilder.CrewedShip("Old", "Jebediah Kerman").Build();

            Assert.False(CrewInventorySnapshot.HasCapture(snap));
            Assert.Null(CrewInventorySnapshot.ReadFromSnapshot(snap));
            Assert.Null(CrewInventorySnapshot.ReadFromSnapshot(null));
        }

        [Fact]
        public void Builder_WithCrewInventory_EmitsTheProductionShape()
        {
            ConfigNode snap = CrewedSnapshotWithInventory();

            var read = CrewInventorySnapshot.ReadFromSnapshot(snap);
            Assert.Equal(2, CrewInventorySnapshot.CountStoredParts(read["Jebediah Kerman"]));
            Assert.Equal(0, CrewInventorySnapshot.CountStoredParts(read["Bill Kerman"]));
            Assert.Equal(new[] { "Jebediah Kerman", "Bill Kerman" }, CrewInventorySnapshot.CollectSeatedCrew(snap));
            // The capture is a VESSEL child, never inside a PART, so part walks never see it.
            foreach (ConfigNode part in snap.GetNodes("PART"))
                Assert.False(part.HasNode(CrewInventorySnapshot.NodeName));
        }

        [Fact]
        public void RenameKerbals_RenamesOnlyResolvedKeys()
        {
            ConfigNode snap = CrewedSnapshotWithInventory();

            int renamed = CrewInventorySnapshot.RenameKerbals(snap,
                n => n == "Bill Kerman" ? "Bob Kerman" : (n == "Jebediah Kerman" ? "Jebediah Kerman" : null));

            Assert.Equal(1, renamed);
            var read = CrewInventorySnapshot.ReadFromSnapshot(snap);
            Assert.True(read.ContainsKey("Bob Kerman"));
            Assert.True(read.ContainsKey("Jebediah Kerman"));
            Assert.False(read.ContainsKey("Bill Kerman"));
        }

        #endregion

        #region Persistence and carry-over

        [Fact]
        public void SnapshotSidecar_RoundTripsTheCapture()
        {
            ConfigNode snap = CrewedSnapshotWithInventory();
            string path = Path.Combine(tempDir, "crewinv_vessel.craft");

            RecordingStore.WriteSnapshotSidecarForTesting(path, snap);
            Assert.True(RecordingStore.LoadSnapshotSidecarForTesting(path, out ConfigNode loaded));

            var read = CrewInventorySnapshot.ReadFromSnapshot(loaded);
            Assert.NotNull(read);
            Assert.Equal(new[] { "evaChute", "groundExperimentPack" }, StoredPartNames(read["Jebediah Kerman"]));
            Assert.True(read.ContainsKey("Bill Kerman"));
        }

        [Fact]
        public void DeepClone_CarriesTheCapture_AsAnIndependentCopy()
        {
            var rec = new Recording { RecordingId = "crewinv-clone", VesselSnapshot = CrewedSnapshotWithInventory() };

            Recording clone = Recording.DeepClone(rec);
            CrewInventorySnapshot.StripFromSnapshot(rec.VesselSnapshot);

            var read = CrewInventorySnapshot.ReadFromSnapshot(clone.VesselSnapshot);
            Assert.NotNull(read);
            Assert.Equal(2, CrewInventorySnapshot.CountStoredParts(read["Jebediah Kerman"]));
        }

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, altitude = 50000.0, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero,
            };
        }

        [Fact]
        public void OptimizerSplit_MovesTheCaptureToTheEndHalf()
        {
            var rec = new Recording { RecordingId = "crewinv-split", VesselSnapshot = CrewedSnapshotWithInventory() };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(34.0));
            rec.Points.Add(PointAt(53.0));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 8.0, endUT = 53.0, sampleRateHz = 1f,
                minAltitude = float.NaN, maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint> { PointAt(8.0), PointAt(34.0), PointAt(53.0) },
            });

            Recording tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.False(CrewInventorySnapshot.HasCapture(rec.VesselSnapshot));
            var read = CrewInventorySnapshot.ReadFromSnapshot(tip.VesselSnapshot);
            Assert.NotNull(read);
            Assert.Equal(2, CrewInventorySnapshot.CountStoredParts(read["Jebediah Kerman"]));
        }

        [Fact]
        public void OptimizerMerge_TargetInheritsTheAbsorbedEndCapture()
        {
            var target = new Recording
            {
                RecordingId = "crewinv-merge-target",
                VesselSnapshot = new VesselSnapshotBuilder()
                    .AddPart("mk1-3pod", crew: "Jebediah Kerman")
                    .WithCrewInventory("Jebediah Kerman", "evaChute")
                    .Build(),
            };
            target.Points.Add(PointAt(10.0));
            target.Points.Add(PointAt(20.0));
            var absorbed = new Recording
            {
                RecordingId = "crewinv-merge-absorbed",
                VesselSnapshot = new VesselSnapshotBuilder()
                    .AddPart("mk1-3pod", crew: "Jebediah Kerman")
                    .WithCrewInventory("Jebediah Kerman")
                    .Build(),
            };
            absorbed.Points.Add(PointAt(20.0));
            absorbed.Points.Add(PointAt(30.0));

            RecordingOptimizer.MergeInto(target, absorbed);

            var read = CrewInventorySnapshot.ReadFromSnapshot(target.VesselSnapshot);
            Assert.NotNull(read);
            Assert.Equal(0, CrewInventorySnapshot.CountStoredParts(read["Jebediah Kerman"]));
        }

        #endregion

        #region Restore at spawn

        [Fact]
        public void RestoreForSpawn_ReplacesEachSeatedKerbalsInventory_AndStripsTheNode()
        {
            ConfigNode spawnNode = CrewedSnapshotWithInventory();
            var roster = new Dictionary<string, ConfigNode>
            {
                // Current roster state: Jeb still holds the launch-day cargo he stowed in
                // flight (the duplication case); Bill holds nothing.
                { "Jebediah Kerman", Inventory("groundExperimentPack", "cargoContainer") },
                { "Bill Kerman", Inventory("evaRepairKit") },
            };

            CrewInventoryRestoreResult result = CrewInventorySnapshot.RestoreForSpawn(
                spawnNode, (name, inv) => { roster[name] = inv; return true; }, "test");

            Assert.True(result.HadCapture);
            Assert.Equal(2, result.SeatedCrew);
            Assert.Equal(2, result.Restored);
            Assert.Equal(0, result.NoEntry);
            Assert.Equal(new[] { "evaChute", "groundExperimentPack" }, StoredPartNames(roster["Jebediah Kerman"]));
            Assert.Empty(StoredPartNames(roster["Bill Kerman"]));
            Assert.False(CrewInventorySnapshot.HasCapture(spawnNode));
            Assert.Contains(logLines, l => l.Contains("[CrewInventory]")
                && l.Contains("RestoreForSpawn: 'Inventory Lander' seated=2 restored=2"));
        }

        [Fact]
        public void RestoreForSpawn_AppliesACopy_SoTheRecordedNodeStaysPristine()
        {
            var rec = new Recording { VesselSnapshot = CrewedSnapshotWithInventory() };
            ConfigNode spawnNode = rec.VesselSnapshot.CreateCopy();
            ConfigNode applied = null;

            CrewInventorySnapshot.RestoreForSpawn(spawnNode,
                (name, inv) => { if (name == "Jebediah Kerman") applied = inv; return true; }, "test");
            applied.GetNode("STOREDPARTS").AddNode("STOREDPART").AddValue("partName", "mutated");

            var recorded = CrewInventorySnapshot.ReadFromSnapshot(rec.VesselSnapshot);
            Assert.Equal(2, CrewInventorySnapshot.CountStoredParts(recorded["Jebediah Kerman"]));
        }

        [Fact]
        public void RestoreForSpawn_NoCapture_LeavesRosterUnchanged_AndLogsOnce()
        {
            ConfigNode spawnNode = VesselSnapshotBuilder.CrewedShip("Legacy Pod", "Jebediah Kerman").Build();
            int calls = 0;

            CrewInventoryRestoreResult result = CrewInventorySnapshot.RestoreForSpawn(
                spawnNode, (name, inv) => { calls++; return true; }, "RespawnVessel");

            Assert.False(result.HadCapture);
            Assert.Equal(0, result.Restored);
            Assert.Equal(0, calls);
            Assert.Single(logLines.FindAll(l => l.Contains("[CrewInventory]")
                && l.Contains("no recorded crew inventory on 'Legacy Pod'")
                && l.Contains("roster inventory kept for 1 kerbal(s)")));
        }

        [Fact]
        public void RestoreForSpawn_OnlySeatedCrew_RemovedCrewAreNotTouched()
        {
            // Bill was removed from the spawn copy (dead / EVA'd / on another vessel):
            // his recorded entry is reported unseated and never applied.
            ConfigNode spawnNode = CrewedSnapshotWithInventory();
            VesselSpawner.RemoveSpecificCrewFromSnapshot(spawnNode, new HashSet<string> { "Bill Kerman" });
            var appliedNames = new List<string>();

            CrewInventoryRestoreResult result = CrewInventorySnapshot.RestoreForSpawn(
                spawnNode, (name, inv) => { appliedNames.Add(name); return true; }, "test");

            Assert.Equal(new[] { "Jebediah Kerman" }, appliedNames);
            Assert.Equal(1, result.Unseated);
        }

        [Fact]
        public void RestoreForSpawn_SeatedKerbalWithoutEntry_KeepsRoster_AndApplyFailureIsCounted()
        {
            ConfigNode spawnNode = new VesselSnapshotBuilder()
                .AddPart("mk1-3pod", crew: "Jebediah Kerman")
                .AddPart("mk1-3pod", crew: "Valentina Kerman")
                .WithCrewInventory("Jebediah Kerman", "evaChute")
                .Build();

            CrewInventoryRestoreResult result = CrewInventorySnapshot.RestoreForSpawn(
                spawnNode, (name, inv) => false, "test");

            Assert.Equal(1, result.NoEntry);
            Assert.Equal(1, result.ApplyFailed);
            Assert.Equal(0, result.Restored);
        }

        #endregion

        #region Capture decision (no roster mutation)

        [Fact]
        public void SelectCapturedInventory_NullBackingField_NoEntry()
        {
            // The kerbal never had an inventory materialized: the capture must record
            // nothing (never the InventoryNode getter's lazy default).
            Assert.Null(CrewInventorySnapshot.SelectCapturedInventory(null, null));
        }

        [Fact]
        public void SelectCapturedInventory_BackingField_IsCopied_SourceUntouched()
        {
            ConfigNode backing = Inventory("evaChute");

            ConfigNode captured = CrewInventorySnapshot.SelectCapturedInventory(null, backing);
            captured.GetNode("STOREDPARTS").AddNode("STOREDPART").AddValue("partName", "mutated");

            Assert.NotSame(backing, captured);
            Assert.Equal(new[] { "evaChute" }, StoredPartNames(backing));
        }

        [Fact]
        public void SelectCapturedInventory_LiveModuleWinsOverBackingField()
        {
            ConfigNode live = Inventory("cargoContainer");

            ConfigNode captured = CrewInventorySnapshot.SelectCapturedInventory(live, Inventory("evaChute"));

            Assert.Equal(new[] { "cargoContainer" }, StoredPartNames(captured));
        }

        [Fact]
        public void CaptureWithANullBackingKerbal_RecordsNoEntry_AndTheRestoreLeavesHimAlone()
        {
            // Seam walk of the capture: Jeb has a stored inventory, Bill's backing field is null.
            var backingByKerbal = new Dictionary<string, ConfigNode>
            {
                { "Jebediah Kerman", Inventory("evaChute") },
                { "Bill Kerman", null },
            };
            var entries = new List<KeyValuePair<string, ConfigNode>>();
            foreach (var kvp in backingByKerbal)
            {
                ConfigNode inv = CrewInventorySnapshot.SelectCapturedInventory(null, kvp.Value);
                if (inv != null)
                    entries.Add(new KeyValuePair<string, ConfigNode>(kvp.Key, inv));
            }
            ConfigNode snap = new VesselSnapshotBuilder()
                .AddPart("mk1-3pod", crew: "Jebediah Kerman")
                .AddPart("mk1-3pod", crew: "Bill Kerman")
                .Build();
            CrewInventorySnapshot.WriteToSnapshot(snap, entries);

            var applied = new List<string>();
            CrewInventoryRestoreResult result = CrewInventorySnapshot.RestoreForSpawn(
                snap, (name, inv) => { applied.Add(name); return true; }, "test");

            Assert.Null(backingByKerbal["Bill Kerman"]);
            Assert.Equal(new[] { "Jebediah Kerman" }, applied);
            Assert.Equal(1, result.NoEntry);
        }

        #endregion

        #region Rollback on a failed spawn

        [Fact]
        public void RollbackRestore_WritesEachPriorBack_IncludingANullPrior()
        {
            ConfigNode spawnNode = CrewedSnapshotWithInventory();
            var roster = new Dictionary<string, ConfigNode>
            {
                { "Jebediah Kerman", Inventory("cargoContainer") },
                { "Bill Kerman", null }, // no stored inventory before the spawn
            };

            CrewInventoryRestoreResult result = CrewInventorySnapshot.RestoreForSpawn(
                spawnNode,
                name => roster[name] != null ? roster[name].CreateCopy() : null,
                (name, inv) => { roster[name] = inv; return true; },
                "test");
            Assert.Equal(2, result.Priors.Count);
            Assert.Equal(2, CrewInventorySnapshot.CountStoredParts(roster["Jebediah Kerman"]));

            int rolledBack = CrewInventorySnapshot.RollbackRestore(result,
                (name, prior) => { roster[name] = prior; return true; }, "RespawnVessel null vesselRef");

            Assert.Equal(2, rolledBack);
            Assert.Equal(new[] { "cargoContainer" }, StoredPartNames(roster["Jebediah Kerman"]));
            Assert.Null(roster["Bill Kerman"]);
            Assert.Contains(logLines, l => l.Contains("[CrewInventory]")
                && l.Contains("RollbackRestore: spawn failed after the crew inventory restore; rolled back 2 kerbal(s), failed=0")
                && l.Contains("RespawnVessel null vesselRef"));
        }

        [Fact]
        public void RollbackRestore_OnlyKerbalsThatWereApplied()
        {
            ConfigNode spawnNode = CrewedSnapshotWithInventory();
            CrewInventoryRestoreResult result = CrewInventorySnapshot.RestoreForSpawn(
                spawnNode, name => Inventory(), (name, inv) => name == "Bill Kerman", "test");
            var rolled = new List<string>();

            CrewInventorySnapshot.RollbackRestore(result, (name, prior) => { rolled.Add(name); return true; }, "test");

            Assert.Equal(new[] { "Bill Kerman" }, rolled);
        }

        [Fact]
        public void RollbackRestore_NoRestoreRan_IsANoOp()
        {
            int calls = 0;

            int rolledBack = CrewInventorySnapshot.RollbackRestore(default(CrewInventoryRestoreResult),
                (name, prior) => { calls++; return true; }, "test");

            Assert.Equal(0, rolledBack);
            Assert.Equal(0, calls);
            Assert.DoesNotContain(logLines, l => l.Contains("RollbackRestore"));
        }

        #endregion

        #region Stand-ins

        [Fact]
        public void ReverseMap_MovesTheStandInsInventoryKeyToTheOriginal()
        {
            // Capture keys by the live (stand-in) name; the capture-time reverse map moves
            // the key with the PART crew value, so the seat's inventory belongs to the original.
            ConfigNode snap = new VesselSnapshotBuilder()
                .AddPart("mk1-3pod", crew: "Kirrim Kerman")
                .WithCrewInventory("Kirrim Kerman", "evaChute")
                .Build();
            var replacements = new Dictionary<string, string> { { "Jebediah Kerman", "Kirrim Kerman" } };

            int rewritten = KerbalsModule.ReverseMapCrewNamesInSnapshot(snap, replacements, "test");

            Assert.Equal(1, rewritten);
            var read = CrewInventorySnapshot.ReadFromSnapshot(snap);
            Assert.True(read.ContainsKey("Jebediah Kerman"));
            Assert.False(read.ContainsKey("Kirrim Kerman"));
        }

        [Fact]
        public void KscStandInSwap_TheStandInGetsTheSeatsRecordedInventory()
        {
            ConfigNode snap = CrewedSnapshotWithInventory();
            var replacements = new Dictionary<string, string> { { "Jebediah Kerman", "Kirrim Kerman" } };

            int swapped = CrewReservationManager.SwapReservedCrewInSnapshot(
                snap, replacements, name => ProtoCrewMember.RosterStatus.Available, out int cleared);
            var appliedNames = new Dictionary<string, int>();
            CrewInventorySnapshot.RestoreForSpawn(snap,
                (name, inv) => { appliedNames[name] = CrewInventorySnapshot.CountStoredParts(inv); return true; },
                "test");

            Assert.Equal(1, swapped);
            Assert.Equal(0, cleared);
            Assert.Equal(2, appliedNames["Kirrim Kerman"]);
            Assert.False(appliedNames.ContainsKey("Jebediah Kerman"));
            Assert.Equal(0, appliedNames["Bill Kerman"]);
        }

        [Fact]
        public void KscStandInSwap_ClearedSeat_KeepsTheOriginalKey_ReportedUnseated()
        {
            ConfigNode snap = CrewedSnapshotWithInventory();
            var replacements = new Dictionary<string, string> { { "Jebediah Kerman", "Kirrim Kerman" } };

            CrewReservationManager.SwapReservedCrewInSnapshot(
                snap, replacements, name => ProtoCrewMember.RosterStatus.Assigned, out int cleared);
            var appliedNames = new List<string>();
            CrewInventoryRestoreResult result = CrewInventorySnapshot.RestoreForSpawn(snap,
                (name, inv) => { appliedNames.Add(name); return true; }, "test");

            Assert.Equal(1, cleared);
            Assert.Equal(new[] { "Bill Kerman" }, appliedNames);
            Assert.Equal(1, result.Unseated);
        }

        #endregion
    }
}
