using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the B9 rewindable-tree fixture generator (RewindB9Fixture +
    /// the ScenarioWriter REWIND_POINTS / RP-sidecar additions). Each test names the
    /// re-fly prerequisite it guards so a fixture regression that would make
    /// CanInvoke decline in-game (missing sidecar, wrong pid, non-null session id)
    /// reds here headlessly instead.
    /// </summary>
    [Collection("Sequential")]
    public class RewindB9FixtureTests
    {
        // Minimal stock save skeleton with a FLIGHTSTATE anchor
        // (ScenarioWriter.InjectIntoSave inserts the ParsekScenario before it).
        private const string FakeSave =
            "GAME\n{\n" +
            "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
            "}\n";

        [Fact]
        public void BuildRewindPoint_HasFixedIdNullSessionAndQuicksavePath()
        {
            RewindPoint rp = RewindB9Fixture.BuildRewindPoint(splitUt: 1000.0);

            // Prereq 1: a fixed, known id a spec cites verbatim, with a quicksave
            // path under Parsek/RewindPoints/ (what CanInvoke probes on disk).
            Assert.Equal("rp_b9_root", rp.RewindPointId);
            Assert.Equal(
                RecordingPaths.BuildRewindPointRelativePath("rp_b9_root"),
                rp.QuicksaveFilename);

            // Prereq 2: CreatingSessionId null so LoadTimeSweep keeps it as a
            // durable split point rather than discarding a session-scoped RP.
            Assert.Null(rp.CreatingSessionId);
            Assert.False(rp.Corrupted);
            Assert.Equal(RewindB9Fixture.UpperSlotIndex, rp.FocusSlotIndex);
            Assert.Equal(1000.0, rp.UT);
        }

        [Fact]
        public void BuildRewindPoint_TwoControllableChildSlots()
        {
            RewindPoint rp = RewindB9Fixture.BuildRewindPoint(splitUt: 0.0);

            Assert.Equal(2, rp.ChildSlots.Count);

            ChildSlot upper = rp.ChildSlots.Single(s => s.SlotIndex == RewindB9Fixture.UpperSlotIndex);
            Assert.Equal(RewindB9Fixture.UpperRecordingId, upper.OriginChildRecordingId);
            Assert.True(upper.Controllable);

            ChildSlot booster = rp.ChildSlots.Single(s => s.SlotIndex == RewindB9Fixture.BoosterSlotIndex);
            Assert.Equal(RewindB9Fixture.BoosterRecordingId, booster.OriginChildRecordingId);
            Assert.True(booster.Controllable);

            // slot=1 (the InvokeRewind target) is the crashed booster.
            Assert.Equal(1, RewindB9Fixture.BoosterSlotIndex);
        }

        [Fact]
        public void BuildRewindPoint_PidSlotMapMatchesInjectedRecordingPids()
        {
            RewindPoint rp = RewindB9Fixture.BuildRewindPoint(splitUt: 0.0);

            // Prereq 3: the PidSlotMap keys are the SAME synthetic vessel pids the
            // injected recordings carry (ScenarioWriter derives both via the same
            // FNV-1a hash), so slot resolution finds the staged children.
            uint upperPid = ScenarioWriter.DeriveVesselPersistentId(RewindB9Fixture.UpperRecordingId);
            uint boosterPid = ScenarioWriter.DeriveVesselPersistentId(RewindB9Fixture.BoosterRecordingId);

            Assert.Equal(RewindB9Fixture.UpperSlotIndex, rp.PidSlotMap[upperPid]);
            Assert.Equal(RewindB9Fixture.BoosterSlotIndex, rp.PidSlotMap[boosterPid]);
            Assert.Equal(2, rp.PidSlotMap.Count);
        }

        [Fact]
        public void Inject_EmitsRewindPointsBlockAndRoundTrips()
        {
            var writer = new ScenarioWriter();
            RewindB9Fixture.PopulateWriter(writer, baseUT: 0.0);

            ConfigNode scenario = writer.BuildScenarioNode();
            ConfigNode rewindPoints = scenario.GetNode("REWIND_POINTS");
            Assert.NotNull(rewindPoints);

            ConfigNode[] points = rewindPoints.GetNodes("POINT");
            Assert.Single(points);

            // Round-trip through the SAME loader ParsekScenario uses, so the fixture
            // node shape is proven load-compatible with the live path.
            RewindPoint loaded = RewindPoint.LoadFrom(points[0]);
            Assert.Equal("rp_b9_root", loaded.RewindPointId);
            Assert.Null(loaded.CreatingSessionId);
            Assert.Equal(2, loaded.ChildSlots.Count);
            uint boosterPid = ScenarioWriter.DeriveVesselPersistentId(RewindB9Fixture.BoosterRecordingId);
            Assert.Equal(RewindB9Fixture.BoosterSlotIndex, loaded.PidSlotMap[boosterPid]);
        }

        [Fact]
        public void Inject_TreeCarriesCrashedBoosterSibling()
        {
            var writer = new ScenarioWriter();
            RewindB9Fixture.PopulateWriter(writer, baseUT: 0.0);

            string serialized = writer.SerializeConfigNode(writer.BuildScenarioNode(), "SCENARIO");

            // The crashed booster sibling carries TerminalState.Destroyed (=4).
            Assert.Contains("vesselName = B9 Booster A", serialized);
            Assert.Contains(
                "terminalState = " + ((int)TerminalState.Destroyed).ToString(CultureInfo.InvariantCulture),
                serialized);

            // Its serialized vesselPersistentId is exactly the pid the RP's
            // PidSlotMap keys on (proves the slot map matches the recording).
            uint boosterPid = ScenarioWriter.DeriveVesselPersistentId(RewindB9Fixture.BoosterRecordingId);
            Assert.Contains(
                "vesselPersistentId = " + boosterPid.ToString(CultureInfo.InvariantCulture),
                serialized);
        }

        [Fact]
        public void Inject_WritesRpQuicksaveSidecarAtRewindPointsPath()
        {
            string tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_rewind_b9_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string savePath = Path.Combine(tempDir, "persistent.sfs");
                string tempPath = savePath + ".tmp";
                File.WriteAllText(savePath, FakeSave);

                var writer = new ScenarioWriter().WithV3Format();
                RewindB9Fixture.PopulateWriter(writer, baseUT: 0.0);
                writer.InjectIntoSaveFile(savePath, tempPath);
                File.Copy(tempPath, savePath, overwrite: true);
                File.Delete(tempPath);

                // Prereq 1 on disk: the RP quicksave sidecar exists at the exact
                // path RewindInvoker.CanInvoke resolves and File.Exists-checks.
                string sidecar = Path.Combine(
                    tempDir, "Parsek", "RewindPoints", "rp_b9_root.sfs");
                Assert.True(File.Exists(sidecar), $"RP sidecar missing: {sidecar}");

                // B1 contract: the sidecar carries one controllable VESSEL per child
                // slot, each stamped with the persistentId the slot's PidSlotMap
                // references, so the re-fly pre-load scrub / post-load strip keep the
                // selected slot instead of stripping everything and failing Activate.
                ConfigNode loaded = ConfigNode.Load(sidecar);
                ConfigNode game = loaded.GetNode("GAME") ?? loaded;
                ConfigNode flightState = game.GetNode("FLIGHTSTATE");
                Assert.NotNull(flightState);

                ConfigNode[] vessels = flightState.GetNodes("VESSEL");
                Assert.Equal(2, vessels.Length); // one per controllable slot

                uint upperPid = ScenarioWriter.DeriveVesselPersistentId(RewindB9Fixture.UpperRecordingId);
                uint boosterPid = ScenarioWriter.DeriveVesselPersistentId(RewindB9Fixture.BoosterRecordingId);
                var sidecarPids = new System.Collections.Generic.HashSet<uint>();
                foreach (ConfigNode v in vessels)
                {
                    uint pid;
                    Assert.True(uint.TryParse(v.GetValue("persistentId"),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out pid));
                    sidecarPids.Add(pid);
                }
                // PidSlotMap pids == sidecar VESSEL pids == recording pids (triangle).
                Assert.Contains(upperPid, sidecarPids);
                Assert.Contains(boosterPid, sidecarPids);

                // activeVessel points at the focus slot's ordinal (slot 0 = upper).
                Assert.Equal("0", flightState.GetValue("activeVessel"));

                // The three recordings' sidecars also landed (tree is complete).
                string recDir = Path.Combine(tempDir, "Parsek", "Recordings");
                Assert.True(File.Exists(Path.Combine(recDir, RewindB9Fixture.BoosterRecordingId + ".prec")));
                Assert.Equal(3, writer.V3BuilderCount);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Sidecar_DeepParsePassesWhenPartsResolve()
        {
            string tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_rewind_b9_dp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
            try
            {
                string savePath = Path.Combine(tempDir, "persistent.sfs");
                string tempPath = savePath + ".tmp";
                File.WriteAllText(savePath, FakeSave);

                var writer = new ScenarioWriter().WithV3Format();
                RewindB9Fixture.PopulateWriter(writer, baseUT: 0.0);
                writer.InjectIntoSaveFile(savePath, tempPath);
                File.Copy(tempPath, savePath, overwrite: true);
                File.Delete(tempPath);

                string sidecar = Path.Combine(
                    tempDir, "Parsek", "RewindPoints", "rp_b9_root.sfs");
                RewindPoint rp = RewindB9Fixture.BuildRewindPoint(splitUt: 60.0);

                // Deep-parse (PartLoaderPrecondition-shape): with every PART resolving,
                // the precondition passes. This proves the sidecar carries real PART
                // nodes the CanInvoke deep-parse gate inspects (not a vessel-less save).
                RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = _ => true;
                RewindInvoker.PreconditionResult ok =
                    RewindInvoker.PartLoaderPrecondition.Check(rp, sidecar);
                Assert.True(ok.Passed, ok.Reason);
                Assert.False(rp.Corrupted);

                // And with NO part resolving, it declines with a missing-parts reason -
                // confirming there ARE PART nodes to reject (a vessel-less sidecar would
                // pass vacuously with nothing to check).
                RewindPoint rp2 = RewindB9Fixture.BuildRewindPoint(splitUt: 60.0);
                RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = _ => false;
                RewindInvoker.PreconditionResult bad =
                    RewindInvoker.PartLoaderPrecondition.Check(rp2, sidecar);
                Assert.False(bad.Passed);
                Assert.Contains("Missing parts", bad.Reason ?? "");
            }
            finally
            {
                RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
