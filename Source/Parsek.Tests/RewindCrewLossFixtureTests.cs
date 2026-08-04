using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the crew-loss rewindable-tree fixture (CL stage B).
    ///
    /// <para>
    /// The fixture's whole reason to exist is that its re-fly target carries a kerbal
    /// who DIES, so the load-bearing cell here is
    /// <see cref="TheFixturesPodShapeMakesTheProductInferDead"/>: it feeds the exact
    /// recording shape the fixture authors through Parsek's OWN inference and asserts
    /// the derived end state is <see cref="KerbalEndState.Dead"/>. If that ever stops
    /// holding, the scenario built on this fixture would still fly green while
    /// silently tombstoning nothing, and the D9 / D12 claims behind it would be
    /// false - so it reds here, headlessly, instead.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RewindCrewLossFixtureTests : System.IDisposable
    {
        private const string FakeSave =
            "GAME\n{\n" +
            "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
            "}\n";

        private readonly List<string> logLines = new List<string>();

        public RewindCrewLossFixtureTests()
        {
            // Shared static state (ParsekLog): reset on the way in AND out, per the
            // house pattern, so suite ordering cannot leak a sink or a verbosity
            // setting into these cells.
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ---- the load-bearing cell ----------------------------------------

        [Fact]
        public void TheFixturesPodShapeMakesTheProductInferDead()
        {
            // The shape the fixture authors for slot 1: crewed GHOST-VISUAL snapshot,
            // NO vessel snapshot, terminal Destroyed. Built here as a Recording so the
            // real product path runs over it rather than a paraphrase of it.
            var ghost = new ConfigNode("VESSEL");
            ghost.AddNode("PART").AddValue("crew", RewindCrewLossFixture.DeadCrewName);

            var rec = new Recording
            {
                RecordingId = RewindCrewLossFixture.PodRecordingId,
                GhostVisualSnapshot = ghost,
                VesselSnapshot = null,
                TerminalStateValue = TerminalState.Destroyed
            };

            // 1. The gate admits it - via the ghost-visual branch PR #1395 added,
            //    which is the ONLY branch a destroyed vessel can pass (it has no
            //    end-of-recording VesselSnapshot to admit on). Asserted on the
            //    RETURN VALUE, not on the log line: the admission logs at Info or
            //    Verbose depending on whether the ghost snapshot carries crew, and
            //    routing is a global ParsekLog setting other suites mutate - so
            //    gating on the line makes this cell order-dependent. The dedicated
            //    log-routing cells live in LedgerOrchestratorTests.
            Assert.True(LedgerOrchestrator.NeedsCrewEndStatePopulation(rec),
                "a crewed, ghost-visual-only, Destroyed recording must be admitted for "
                + "crew-end-state population, or the fixture produces no death row");

            // 2. Population resolves the crew member to Dead.
            KerbalsModule.PopulateCrewEndStates(rec);
            Assert.NotNull(rec.CrewEndStates);
            Assert.True(rec.CrewEndStates.TryGetValue(
                RewindCrewLossFixture.DeadCrewName, out var endState));
            Assert.Equal(KerbalEndState.Dead, endState);
        }

        [Fact]
        public void ThePodCarriesNoVesselSnapshotSoTheGateCannotShortCircuit()
        {
            // Regression guard on the fixture, not the product. Giving the pod a
            // VesselSnapshot would make NeedsCrewEndStatePopulation return true on its
            // FIRST branch, so the fixture would still produce a Dead row while no
            // longer exercising the destroyed-vessel path it exists to cover - green,
            // and meaningless. Asserted against the SIDECAR FILES the game actually
            // reads (v3 keeps snapshots out of the .sfs), not the metadata node.
            WithInjectedSave(path =>
            {
                string recordings = Path.Combine(
                    Path.GetDirectoryName(path), "Parsek", "Recordings");
                string ghost = Path.Combine(recordings,
                    RewindCrewLossFixture.PodRecordingId + "_ghost.craft");
                string vessel = Path.Combine(recordings,
                    RewindCrewLossFixture.PodRecordingId + "_vessel.craft");

                Assert.True(File.Exists(ghost),
                    "the crew-loss pod must carry a ghost-visual snapshot as its crew source");
                Assert.False(File.Exists(vessel),
                    "the crew-loss pod must carry NO vessel snapshot (see RewindCrewLossFixture): "
                    + "a destroyed vessel has none, and authoring one would let "
                    + "NeedsCrewEndStatePopulation short-circuit on its first branch");
                return 0;
            });
        }

        [Fact]
        public void ThePodIsDestroyedAndCarriesTheCrewMember()
        {
            Assert.Equal(((int)TerminalState.Destroyed).ToString(),
                PodRecordingNode().GetValue("terminalState"));

            // The crew member must reach DISK somewhere in the pod's authored
            // artifacts, or the crew source the gate admits on is empty. Searched
            // across the pod's sidecar set rather than pinned to one file, because
            // which artifact carries the roster name is a codec detail this fixture
            // does not own; the end-to-end proof that the shape yields Dead is
            // TheFixturesPodShapeMakesTheProductInferDead.
            WithInjectedSave(path =>
            {
                string recordings = Path.Combine(
                    Path.GetDirectoryName(path), "Parsek", "Recordings");
                bool found = Directory
                    .GetFiles(recordings, RewindCrewLossFixture.PodRecordingId + "*")
                    .Any(f => DecodedSidecarText(f)
                        .Contains(RewindCrewLossFixture.DeadCrewName));
                Assert.True(found,
                    "the crew-loss pod's sidecar set must carry "
                    + RewindCrewLossFixture.DeadCrewName);
                return 0;
            });
        }

        [Fact]
        public void TheRootStaysCrewlessSoThePodIsTheOnlyCrewSourceInTheTree()
        {
            // THE HALF-(ii) GUARD. The registry pins `dead-crew-strip` as (i) the death
            // action tombstoned AND (ii) the ELS recompute leaving NO surviving
            // reservation or stand-in for the kerbal. (ii) is observable only when the
            // pod is the tree's ONLY crew source: a crewed root carries no terminal
            // state, so InferCrewEndState returns Unknown and the root holds an
            // indefinite reservation from OUTSIDE the supersede subtree - which is
            // exactly what CL-3's 2026-08-03_1834 flight measured (`Recomputed after
            // tombstones: 1 reservations remain`, sourced from 'cl-stack-root').
            // Asserted against the SIDECAR the game reads, existence-checked first so
            // the cell cannot pass vacuously against a fixture that stopped writing a
            // root snapshot at all.
            WithInjectedSave(path =>
            {
                string vessel = Path.Combine(
                    Path.GetDirectoryName(path), "Parsek", "Recordings",
                    RewindCrewLossFixture.RootRecordingId + "_vessel.craft");
                Assert.True(File.Exists(vessel),
                    "the crew-loss root must still carry a vessel snapshot: crewless "
                    + "is the point, snapshot-less would be a different fixture");

                string text = DecodedSidecarText(vessel);
                // ANTI-VACUITY ANCHOR, and it is not decoration: the sidecar the game
                // reads is DEFLATE-compressed binary, so a raw ReadAllText scan over it
                // can neither find a name that IS there nor honestly miss one - the
                // absence assertion below is worthless unless the decode produced the
                // real node first.
                Assert.Contains("CL Stack", text);
                Assert.DoesNotContain(RewindCrewLossFixture.DeadCrewName, text);
                return 0;
            });

            // ...and the root still carries NO terminal state, which is the other half
            // of why a crewed root reserved indefinitely. Pinned so a future author
            // cannot "fix" a surviving reservation by stamping a terminal state on the
            // root instead of keeping it crewless - that would leave a second crew
            // source in the tree and make the recompute half unreadable again.
            Assert.Null(RecordingNode(RewindCrewLossFixture.RootRecordingId)
                .GetValue("terminalState"));
        }

        [Fact]
        public void TheProbeSiblingStaysCrewlessSoTheTombstoneTallyIsAttributable()
        {
            // Only ONE recording in the tree may contribute a kerbal-death row, or a
            // `Kerbal=N` tombstone tally cannot be attributed to the pod.
            Assert.Equal(((int)TerminalState.Orbiting).ToString(),
                RecordingNode(RewindCrewLossFixture.ProbeRecordingId).GetValue("terminalState"));

            WithInjectedSave(path =>
            {
                string vessel = Path.Combine(
                    Path.GetDirectoryName(path), "Parsek", "Recordings",
                    RewindCrewLossFixture.ProbeRecordingId + "_vessel.craft");
                // Decoded, with the same anti-vacuity anchor as the root cell: this
                // assertion used to run over the RAW compressed bytes and could not
                // have caught a crewed probe.
                string text = DecodedSidecarText(vessel);
                Assert.Contains("CL Probe B", text);
                Assert.DoesNotContain(RewindCrewLossFixture.DeadCrewName, text);
                return 0;
            });
        }

        // ---- RP usability prerequisites (mirrors RewindB9FixtureTests) ------

        [Fact]
        public void BuildRewindPoint_HasFixedIdNullSessionAndTwoControllableSlots()
        {
            var rp = RewindCrewLossFixture.BuildRewindPoint(1000.0);

            Assert.Equal("rp_cl_root", rp.RewindPointId);
            Assert.Equal(RewindCrewLossFixture.RewindPointId, rp.RewindPointId);
            // Null CreatingSessionId keeps LoadTimeSweep from discarding it as a
            // session-scoped provisional.
            Assert.Null(rp.CreatingSessionId);
            Assert.False(rp.Corrupted);
            Assert.Equal(RecordingPaths.BuildRewindPointRelativePath(
                RewindCrewLossFixture.RewindPointId), rp.QuicksaveFilename);

            Assert.Equal(2, rp.ChildSlots.Count(s => s.Controllable));
            var pod = rp.ChildSlots.Single(
                s => s.SlotIndex == RewindCrewLossFixture.PodSlotIndex);
            Assert.Equal(RewindCrewLossFixture.PodRecordingId, pod.OriginChildRecordingId);
            Assert.True(pod.Controllable);
        }

        [Fact]
        public void TheRewindTargetIsTheCrewedSlotNotTheFocusSlot()
        {
            var rp = RewindCrewLossFixture.BuildRewindPoint(1000.0);

            // The spec drives `InvokeRewind slot=1`. Focus is the SURVIVING sibling,
            // so a re-fly genuinely switches away from the focus slot - the same
            // arrangement B9 proved.
            Assert.Equal(RewindCrewLossFixture.ProbeSlotIndex, rp.FocusSlotIndex);
            Assert.NotEqual(rp.FocusSlotIndex, RewindCrewLossFixture.PodSlotIndex);
        }

        [Fact]
        public void PidMapsAgreeWithTheDerivedSlotPids()
        {
            var rp = RewindCrewLossFixture.BuildRewindPoint(1000.0);

            Assert.Equal(RewindCrewLossFixture.PodSlotIndex,
                rp.PidSlotMap[ScenarioWriter.DeriveVesselPersistentId(
                    RewindCrewLossFixture.PodRecordingId)]);
            Assert.Equal(RewindCrewLossFixture.PodSlotIndex,
                rp.RootPartPidMap[ScenarioWriter.DeriveRootPartPersistentId(
                    RewindCrewLossFixture.PodRecordingId)]);

            // Vessel-level and root-part pids must never collide, or the strip's
            // fallback path is not actually distinguishable.
            Assert.NotEqual(
                ScenarioWriter.DeriveVesselPersistentId(RewindCrewLossFixture.PodRecordingId),
                ScenarioWriter.DeriveRootPartPersistentId(RewindCrewLossFixture.PodRecordingId));
        }

        [Fact]
        public void ThePodsRecordedGuidMatchesTheSidecarDerivedLaunchGuid()
        {
            // The documented R1 trap: without an explicit recordedVesselGuid the
            // recording's guid is backfilled from the snapshot pid, conclusively
            // differs from the RP-stamped VESSEL pid, and QuickloadResumeMatchGuard
            // rejects the re-fly candidate - presenting exactly like a product defect.
            var pod = RecordingNode(RewindCrewLossFixture.PodRecordingId);
            Assert.Equal(
                ScenarioWriter.DeriveVesselLaunchGuid(RewindCrewLossFixture.PodRecordingId),
                pod.GetValue("recordedVesselGuid"));
        }

        [Fact]
        public void InjectedSaveCarriesTheTreeAndTheRewindPoint()
        {
            string text = InjectedSaveText();

            Assert.Contains("name = ParsekScenario", text);
            Assert.Contains("vesselName = CL Stack", text);
            Assert.Contains("vesselName = CL Probe B", text);
            Assert.Contains("vesselName = CL Pod A", text);
            Assert.Contains("REWIND_POINTS", text);
            Assert.Contains("rewindPointId = " + RewindCrewLossFixture.RewindPointId, text);
        }

        [Fact]
        public void TheRewindPointIdIsDistinctFromB9sSoTheTwoFixturesCannotCollide()
        {
            // Both presets can be injected into the same instance across runs; a
            // shared id would let a stale sidecar satisfy the other spec's
            // InvokeRewind and the run would pass against the wrong fixture.
            Assert.NotEqual(RewindB9Fixture.RewindPointId,
                RewindCrewLossFixture.RewindPointId);
            Assert.NotEqual(RewindB9Fixture.BoosterRecordingId,
                RewindCrewLossFixture.PodRecordingId);
        }

        // ---- helpers -------------------------------------------------------

        // Snapshot sidecars are DEFLATE-compressed binary (SnapshotSidecarCodec), so a
        // raw File.ReadAllText scan over one proves nothing in either direction: a
        // crew-name assertion passes vacuously against a crewed snapshot and an
        // absence assertion passes vacuously against anything. Decode through the
        // product's own codec, and fall back to plain text only for the sidecars that
        // genuinely are text (.prec, and the settings-gated .txt mirrors - which are a
        // DEBUG artifact and must never be the surface a gate depends on).
        private static string DecodedSidecarText(string path)
        {
            if (SnapshotSidecarCodec.HasBinaryMagic(path)
                && SnapshotSidecarCodec.TryLoad(path, out ConfigNode node, out _)
                && node != null)
            {
                return node.ToString();
            }
            return File.ReadAllText(path);
        }

        // Injects the fixture into a throwaway save and hands the caller the result.
        // Takes a callback rather than returning a path so the temp dir is always
        // reaped, including the RP sidecar the writer emits alongside the save.
        private static T WithInjectedSave<T>(System.Func<string, T> read)
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "parsek-cl-fixture-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string savePath = Path.Combine(dir, "persistent.sfs");
                File.WriteAllText(savePath, FakeSave);
                string tempPath = savePath + ".tmp";

                var writer = new ScenarioWriter().WithV3Format();
                writer.RewindSlotVesselNamePrefix = "CL Slot ";
                RewindCrewLossFixture.PopulateWriter(writer, 1000.0);
                writer.InjectIntoSaveFile(savePath, tempPath);
                return read(tempPath);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }

        private static string InjectedSaveText()
            => WithInjectedSave(File.ReadAllText);

        private static ConfigNode PodRecordingNode()
            => RecordingNode(RewindCrewLossFixture.PodRecordingId);

        private static ConfigNode RecordingNode(string recordingId)
        {
            return WithInjectedSave(path =>
            {
                ConfigNode loaded = ConfigNode.Load(path);
                ConfigNode game = loaded.GetNode("GAME") ?? loaded;
                ConfigNode scenario = game.GetNodes("SCENARIO")
                    .FirstOrDefault(n => n.GetValue("name") == "ParsekScenario");
                Assert.NotNull(scenario);

                ConfigNode match = scenario.GetNodes("RECORDING_TREE")
                    .SelectMany(t => t.GetNodes("RECORDING"))
                    .FirstOrDefault(r => r.GetValue("recordingId") == recordingId);
                Assert.True(match != null,
                    "recording '" + recordingId + "' not authored by RewindCrewLossFixture");
                return match;
            });
        }
    }
}
