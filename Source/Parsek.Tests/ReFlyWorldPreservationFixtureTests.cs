using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards the world-preservation rewind fixture (<see cref="ReFlyWorldPreservationFixture"/>)
    /// and the shared slot-affinity classifier the fixture is designed against.
    ///
    /// <para>
    /// Every cell names the re-fly prerequisite or in-game assertion it protects, so a
    /// fixture regression that would make the live <c>ReFlyWorldPreservation</c> batch
    /// SKIP (no unrelated fleet in the sidecar) or MISREAD (a fleet pid colliding with
    /// a slot pid) reds here headlessly instead of costing a KSP flight. The last cell
    /// is the end-to-end one: it runs the REAL production scrub over the generated
    /// quicksave, which is the only way to prove the fixture and the code under test
    /// agree.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyWorldPreservationFixtureTests : IDisposable
    {
        // Minimal stock save skeleton carrying ONE command VESSEL (the donor the RP
        // sidecar clones per slot and per unrelated fleet member) plus ONE
        // SpaceObject asteroid, so the re-admit path has something real to re-admit.
        private const string FakeSave =
            "GAME\n{\n" +
            "\tFLIGHTSTATE\n\t{\n" +
            "\t\tversion = 1.12.5\n" +
            "\t\tUT = 100\n" +
            "\t\tactiveVessel = 0\n" +
            "\t\tVESSEL\n\t\t{\n" +
            "\t\t\tpid = 76878120df5742eb885d8272d59dc892\n" +
            "\t\t\tpersistentId = 2871960053\n" +
            "\t\t\tname = Donor Pod\n" +
            "\t\t\ttype = Ship\n" +
            "\t\t\tsit = PRELAUNCH\n" +
            "\t\t\tlanded = True\n" +
            "\t\t\troot = 0\n" +
            "\t\t\tORBIT\n\t\t\t{\n\t\t\t\tSMA = 600000\n\t\t\t\tREF = 1\n\t\t\t}\n" +
            "\t\t\tPART\n\t\t\t{\n\t\t\t\tname = mk1pod.v2\n" +
            "\t\t\t\tpersistentId = 111222333\n\t\t\t}\n" +
            "\t\t}\n" +
            "\t\tVESSEL\n\t\t{\n" +
            "\t\t\tpid = 60e7fd84291d4c3ea5e74fe1fc93f9b2\n" +
            "\t\t\tpersistentId = 1490391000\n" +
            "\t\t\tname = Ast. VPA-167\n" +
            "\t\t\ttype = SpaceObject\n" +
            "\t\t\tsit = ORBITING\n" +
            "\t\t\tlanded = False\n" +
            "\t\t\troot = 0\n" +
            "\t\t\tPART\n\t\t\t{\n\t\t\t\tname = PotatoRoid\n" +
            "\t\t\t\tpersistentId = 660576410\n\t\t\t}\n" +
            "\t\t\tDISCOVERY\n\t\t\t{\n\t\t\t\tstate = 1\n\t\t\t\tsize = 2\n\t\t\t}\n" +
            "\t\t}\n" +
            "\t}\n" +
            "}\n";

        private readonly string tempDir;
        private readonly bool priorParsekLogSuppress;

        public ReFlyWorldPreservationFixtureTests()
        {
            tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_refly_wp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }

        // ------------------------------------------------------------------
        // The shared classifier (RewindInvoker.BuildSlotPidSets +
        // ClassifySlotAffinity) - one predicate, two consumers.
        // ------------------------------------------------------------------

        [Fact]
        public void ClassifySlotAffinity_SortsSelectedOtherAndUnrelatedOnEitherKey()
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_affinity",
                PidSlotMap = new Dictionary<uint, int> { { 11u, 0 }, { 22u, 1 } },
                RootPartPidMap = new Dictionary<uint, int> { { 110u, 0 }, { 220u, 1 } },
            };
            RewindInvoker.ReFlySlotPidSets sets = RewindInvoker.BuildSlotPidSets(rp, 1);

            // Vessel-level pid hit, both directions.
            Assert.Equal(RewindInvoker.ReFlySlotAffinity.SelectedSlot,
                RewindInvoker.ClassifySlotAffinity(MakeVessel(22u, "sel", 999u), sets));
            Assert.Equal(RewindInvoker.ReFlySlotAffinity.OtherSlot,
                RewindInvoker.ClassifySlotAffinity(MakeVessel(11u, "sib", 999u), sets));

            // ROOT-PART pid fallback, both directions (the strip's own fallback key).
            Assert.Equal(RewindInvoker.ReFlySlotAffinity.SelectedSlot,
                RewindInvoker.ClassifySlotAffinity(MakeVessel(0u, "sel-by-root", 220u), sets));
            Assert.Equal(RewindInvoker.ReFlySlotAffinity.OtherSlot,
                RewindInvoker.ClassifySlotAffinity(MakeVessel(0u, "sib-by-root", 110u), sets));

            // In NEITHER map on EITHER key = the preserved population.
            Assert.Equal(RewindInvoker.ReFlySlotAffinity.Unrelated,
                RewindInvoker.ClassifySlotAffinity(MakeVessel(77u, "station", 770u), sets));
            Assert.Equal(RewindInvoker.ReFlySlotAffinity.Unrelated,
                RewindInvoker.ClassifySlotAffinity(null, sets));
        }

        [Fact]
        public void ClassifySlotAffinity_SelectedWinsWhenAVesselMatchesBothMaps()
        {
            // A pid the maps disagree about is the RE-FLY TARGET, never a sibling to
            // remove: classifying it OtherSlot would delete the craft the player is
            // about to fly. Mirrors the scrub's `!isSelectedSlot &&` ordering.
            var rp = new RewindPoint
            {
                RewindPointId = "rp_both",
                PidSlotMap = new Dictionary<uint, int> { { 42u, 1 } },
                RootPartPidMap = new Dictionary<uint, int> { { 420u, 0 } },
            };
            RewindInvoker.ReFlySlotPidSets sets = RewindInvoker.BuildSlotPidSets(rp, 1);

            Assert.Equal(RewindInvoker.ReFlySlotAffinity.SelectedSlot,
                RewindInvoker.ClassifySlotAffinity(MakeVessel(42u, "both", 420u), sets));
        }

        [Fact]
        public void BuildSlotPidSets_ReportsAnUnmappedSelectedSlot()
        {
            // The refusal the scrub's own guard rests on: with nothing mapped to the
            // selected slot there is no vessel to keep and no activeVessel to point
            // at, so the scrub must decline rather than pick a survivor.
            var rp = new RewindPoint
            {
                RewindPointId = "rp_unmapped",
                PidSlotMap = new Dictionary<uint, int> { { 11u, 0 } },
                RootPartPidMap = new Dictionary<uint, int> { { 110u, 0 } },
            };

            Assert.True(RewindInvoker.BuildSlotPidSets(rp, 1).SelectedSlotIsUnmapped);
            Assert.False(RewindInvoker.BuildSlotPidSets(rp, 0).SelectedSlotIsUnmapped);
            Assert.True(RewindInvoker.BuildSlotPidSets(null, 0).SelectedSlotIsUnmapped);
        }

        // ------------------------------------------------------------------
        // Fixture shape
        // ------------------------------------------------------------------

        [Fact]
        public void BuildRewindPoint_CarriesTheB9UsabilityPrerequisites()
        {
            RewindPoint rp = ReFlyWorldPreservationFixture.BuildRewindPoint(splitUt: 160.0);

            Assert.Equal("rp_wp_root", rp.RewindPointId);
            Assert.Equal(160.0, rp.UT);
            // Prereq: an on-disk quicksave path CanInvoke can probe.
            Assert.Equal(
                RecordingPaths.BuildRewindPointRelativePath("rp_wp_root"), rp.QuicksaveFilename);
            // Prereq: null CreatingSessionId so LoadTimeSweep keeps it as a durable
            // split point instead of discarding it as a session-scoped provisional.
            Assert.Null(rp.CreatingSessionId);
            Assert.False(rp.Corrupted);
            Assert.Equal(2, rp.ChildSlots.Count);
            Assert.Equal(ReFlyWorldPreservationFixture.UpperSlotIndex, rp.FocusSlotIndex);

            // Prereq: both pid maps keyed to the pids the injected recordings carry,
            // so `slot=1` resolves to the crashed booster on either key.
            Assert.Equal(
                ReFlyWorldPreservationFixture.BoosterSlotIndex,
                rp.PidSlotMap[ScenarioWriter.DeriveVesselPersistentId(
                    ReFlyWorldPreservationFixture.BoosterRecordingId)]);
            Assert.Equal(
                ReFlyWorldPreservationFixture.BoosterSlotIndex,
                rp.RootPartPidMap[ScenarioWriter.DeriveRootPartPersistentId(
                    ReFlyWorldPreservationFixture.BoosterRecordingId)]);
        }

        [Fact]
        public void UnrelatedFleetPids_CanNeverCollideWithASlotPid()
        {
            // THE cell that keeps the whole in-game category honest. A fleet pid that
            // landed in a slot map would be classified SelectedSlot / OtherSlot, and
            // the preservation assertion would then be checking the re-fly's own
            // vessels - green, and about nothing.
            RewindPoint rp = ReFlyWorldPreservationFixture.BuildRewindPoint(splitUt: 160.0);
            string[] roles =
            {
                ReFlyWorldPreservationFixture.StationRole,
                ReFlyWorldPreservationFixture.FlagRole,
                ReFlyWorldPreservationFixture.CollidingProbeRole,
                ReFlyWorldPreservationFixture.CollidingDebrisRole,
            };

            var seen = new HashSet<uint>();
            foreach (string role in roles)
            {
                uint vesselPid = ReFlyWorldPreservationFixture.UnrelatedVesselPid(role);
                uint rootPid = ReFlyWorldPreservationFixture.UnrelatedRootPartPid(role);

                Assert.NotEqual(0u, vesselPid);
                Assert.NotEqual(0u, rootPid);
                Assert.False(rp.PidSlotMap.ContainsKey(vesselPid), role + " vessel pid is a slot pid");
                Assert.False(rp.RootPartPidMap.ContainsKey(rootPid), role + " root pid is a slot pid");
                // And distinct per role, or two fleet members would be one vessel.
                Assert.True(seen.Add(vesselPid), role + " vessel pid duplicates another role's");
                Assert.True(seen.Add(rootPid), role + " root pid duplicates another role's");
            }
        }

        [Fact]
        public void RpSidecar_CarriesBothSlotsPlusTheWholeUnrelatedFleet()
        {
            string sidecar = InjectAndReturnSidecarPath();
            ConfigNode flightState = LoadFlightState(sidecar);

            ConfigNode[] vessels = flightState.GetNodes("VESSEL");
            // 2 slots + re-admitted asteroid + Station + Flag + colliding Probe +
            // colliding Debris.
            Assert.Equal(7, vessels.Length);

            var byName = new List<string>();
            var types = new Dictionary<uint, string>();
            foreach (ConfigNode v in vessels)
            {
                byName.Add(v.GetValue("name"));
                types[ParsePid(v)] = v.GetValue("type");
            }

            // The two slot vessels carry THIS fixture's prefix, not B9's - a shared
            // "B9 Slot N" name is a diagnosis trap when two fixtures' logs sit side
            // by side.
            Assert.Contains("WP Slot 0", byName);
            Assert.Contains("WP Slot 1", byName);

            // The re-admitted asteroid arrives VERBATIM, DISCOVERY block included -
            // the whole reason it is re-admitted rather than synthesised.
            ConfigNode asteroid = FindByPid(vessels, 1490391000u);
            Assert.NotNull(asteroid);
            Assert.Equal("SpaceObject", asteroid.GetValue("type"));
            Assert.NotNull(asteroid.GetNode("DISCOVERY"));
            Assert.Equal("PotatoRoid", asteroid.GetNodes("PART")[0].GetValue("name"));

            // The four cloned fleet members, each under its declared type.
            Assert.Equal("Station",
                types[ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.StationRole)]);
            Assert.Equal("Flag",
                types[ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.FlagRole)]);
            Assert.Equal("Probe",
                types[ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.CollidingProbeRole)]);
            Assert.Equal("Debris",
                types[ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.CollidingDebrisRole)]);
        }

        [Fact]
        public void RpSidecar_NameCollisionPairSharesTheReFlownCraftName()
        {
            // The #587 regression case, and the DISCRIMINATION the positive control
            // rests on: one Probe and one Debris under the SAME name as the
            // Destroyed-terminal recording the re-fly targets, differing only by type
            // and pid. If the names ever drift apart the in-game cells skip.
            string sidecar = InjectAndReturnSidecarPath();
            ConfigNode[] vessels = LoadFlightState(sidecar).GetNodes("VESSEL");

            ConfigNode probe = FindByPid(vessels,
                ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.CollidingProbeRole));
            ConfigNode debris = FindByPid(vessels,
                ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.CollidingDebrisRole));

            Assert.NotNull(probe);
            Assert.NotNull(debris);
            Assert.Equal(ReFlyWorldPreservationFixture.BoosterVesselName, probe.GetValue("name"));
            Assert.Equal(ReFlyWorldPreservationFixture.BoosterVesselName, debris.GetValue("name"));
            Assert.NotEqual(probe.GetValue("type"), debris.GetValue("type"));
        }

        [Fact]
        public void RpSidecar_EveryVesselCarriesADistinctPidAndLaunchGuid()
        {
            // Caught while reviewing, not by a flight: the first cut seeded each
            // cloned vessel's KSP launch guid from its DISPLAY NAME, and the
            // name-collision pair shares a name BY DESIGN - so the Probe and the
            // Debris were emitted with one `pid` between them. Two VESSEL nodes under
            // one guid is a save KSP resolves by regenerating one of them, which
            // would have made the #587 discrimination pair unidentifiable in the very
            // scene it exists to be read in. Identity now seeds from the ROLE key;
            // this cell is what keeps it that way.
            string sidecar = InjectAndReturnSidecarPath();
            ConfigNode[] vessels = LoadFlightState(sidecar).GetNodes("VESSEL");

            var pids = new HashSet<uint>();
            var guids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigNode v in vessels)
            {
                uint pid = ParsePid(v);
                Assert.NotEqual(0u, pid);
                Assert.True(pids.Add(pid),
                    "duplicate persistentId " + pid + " on '" + v.GetValue("name") + "'");

                string guid = v.GetValue("pid");
                Assert.False(string.IsNullOrEmpty(guid));
                Assert.True(guids.Add(guid),
                    "duplicate vessel pid guid " + guid + " on '" + v.GetValue("name") + "'");
            }
            Assert.Equal(7, pids.Count);
            Assert.Equal(7, guids.Count);
        }

        [Fact]
        public void RpSidecar_OrbitsTheClonedFleetSoNothingPilesUpOnThePad()
        {
            // A landed pile of clones at one lat/lon is a kraken waiting for the very
            // scene the in-game cells read. Only the Flag stays landed (a flag is a
            // surface marker); the other three are parked in distinct circular orbits
            // well outside the physics bubble.
            string sidecar = InjectAndReturnSidecarPath();
            ConfigNode[] vessels = LoadFlightState(sidecar).GetNodes("VESSEL");

            AssertOrbiting(FindByPid(vessels,
                ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.StationRole)), 800000.0);
            AssertOrbiting(FindByPid(vessels,
                ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.CollidingProbeRole)), 900000.0);
            AssertOrbiting(FindByPid(vessels,
                ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.CollidingDebrisRole)), 950000.0);

            ConfigNode flag = FindByPid(vessels,
                ReFlyWorldPreservationFixture.UnrelatedVesselPid(
                    ReFlyWorldPreservationFixture.FlagRole));
            Assert.NotNull(flag);
            Assert.Equal("PRELAUNCH", flag.GetValue("sit"));
            Assert.Equal("True", flag.GetValue("landed"));
        }

        [Fact]
        public void RpSidecar_ActiveVesselStillIndexesTheFocusSlotPastTheAppendedFleet()
        {
            // The fleet is APPENDED after activeVessel is set, so the focus slot's
            // ordinal must be untouched. Prepending would silently repoint the
            // re-fly's own resume at an unrelated survivor.
            string sidecar = InjectAndReturnSidecarPath();
            ConfigNode flightState = LoadFlightState(sidecar);

            Assert.Equal("0", flightState.GetValue("activeVessel"));
            Assert.Equal("WP Slot 0", flightState.GetNodes("VESSEL")[0].GetValue("name"));
        }

        [Fact]
        public void RpSidecar_CarriesTheTreeAsAnActiveResumeNodeAndAgreeingLaunchGuids()
        {
            // The two production-shape rules CLAUDE.md pins for an RP fixture:
            // isActive=True (what TryRestoreActiveTreeNode reads back, and what a
            // real GamePersistence.SaveGame always writes) and each slot's sidecar
            // VESSEL pid derived from the origin recording id so it agrees with the
            // recording's recordedVesselGuid (else QuickloadResumeMatchGuard rejects
            // the candidate and a FIXTURE divergence reads as a product defect).
            string sidecar = InjectAndReturnSidecarPath();
            ConfigNode loaded = ConfigNode.Load(sidecar);
            ConfigNode game = loaded.GetNode("GAME") ?? loaded;

            ConfigNode parsek = null;
            foreach (ConfigNode scenario in game.GetNodes("SCENARIO"))
            {
                if (string.Equals(scenario.GetValue("name"), "ParsekScenario", StringComparison.Ordinal))
                    parsek = scenario;
            }
            Assert.NotNull(parsek);

            ConfigNode[] treeNodes = parsek.GetNodes("RECORDING_TREE");
            Assert.NotEmpty(treeNodes);
            ConfigNode active = null;
            foreach (ConfigNode tree in treeNodes)
            {
                if (string.Equals(tree.GetValue("isActive"), "True", StringComparison.Ordinal))
                    active = tree;
            }
            Assert.NotNull(active);
            Assert.Equal(ReFlyWorldPreservationFixture.UpperRecordingId,
                active.GetValue("activeRecordingId"));

            ConfigNode[] vessels = LoadFlightState(sidecar).GetNodes("VESSEL");
            ConfigNode booster = FindByPid(vessels,
                ScenarioWriter.DeriveVesselPersistentId(
                    ReFlyWorldPreservationFixture.BoosterRecordingId));
            Assert.NotNull(booster);
            Assert.Equal(
                ScenarioWriter.DeriveVesselLaunchGuid(
                    ReFlyWorldPreservationFixture.BoosterRecordingId),
                booster.GetValue("pid"));
        }

        // ------------------------------------------------------------------
        // End-to-end: the REAL scrub over the generated quicksave
        // ------------------------------------------------------------------

        [Fact]
        public void RealScrubOverTheGeneratedSidecar_PreservesTheFleetAndRemovesTheSibling()
        {
            // The cell that ties fixture to product. Everything above asserts what
            // the generator WROTE; this drives the production
            // ScrubQuicksaveToSelectedSlotForReFly over it and asserts the world the
            // live re-fly would actually load - which is exactly what the in-game
            // ReFlyWorldPreservation cells then read back out of FlightGlobals.
            string sidecar = InjectAndReturnSidecarPath();
            string temp = Path.Combine(tempDir, "Parsek_Rewind_test.sfs");
            File.Copy(sidecar, temp, overwrite: true);

            RewindPoint rp = ReFlyWorldPreservationFixture.BuildRewindPoint(splitUt: 160.0);
            RewindInvoker.ReFlySaveScrubResult result =
                RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                    temp, rp, ReFlyWorldPreservationFixture.BoosterSlotIndex);

            Assert.True(result.Applied);
            Assert.Equal(7, result.VesselCountBefore);
            // Exactly the upper-stage sibling goes; the five unrelated vessels and
            // the selected booster stay.
            Assert.Equal(1, result.VesselsRemoved);
            Assert.Equal(6, result.VesselsKept);
            Assert.Equal(5, result.VesselsPreserved);

            ConfigNode[] after = LoadFlightState(temp).GetNodes("VESSEL");
            Assert.Equal(6, after.Length);

            // The sibling slot is gone by pid.
            Assert.Null(FindByPid(after, ScenarioWriter.DeriveVesselPersistentId(
                ReFlyWorldPreservationFixture.UpperRecordingId)));
            // The selected slot is present AND is what activeVessel indexes.
            Assert.NotNull(FindByPid(after, ScenarioWriter.DeriveVesselPersistentId(
                ReFlyWorldPreservationFixture.BoosterRecordingId)));
            int activeIndex = int.Parse(
                LoadFlightState(temp).GetValue("activeVessel"), CultureInfo.InvariantCulture);
            Assert.Equal(
                ScenarioWriter.DeriveVesselPersistentId(
                    ReFlyWorldPreservationFixture.BoosterRecordingId),
                ParsePid(after[activeIndex]));

            // Every fleet member survived, asteroid included.
            Assert.NotNull(FindByPid(after, 1490391000u));
            foreach (string role in new[]
            {
                ReFlyWorldPreservationFixture.StationRole,
                ReFlyWorldPreservationFixture.FlagRole,
                ReFlyWorldPreservationFixture.CollidingProbeRole,
                ReFlyWorldPreservationFixture.CollidingDebrisRole,
            })
            {
                Assert.NotNull(FindByPid(
                    after, ReFlyWorldPreservationFixture.UnrelatedVesselPid(role)));
            }
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private string InjectAndReturnSidecarPath()
        {
            string savePath = Path.Combine(tempDir, "persistent.sfs");
            string tempPath = savePath + ".tmp";
            File.WriteAllText(savePath, FakeSave);

            var writer = new ScenarioWriter().WithV3Format();
            ReFlyWorldPreservationFixture.PopulateWriter(writer, baseUT: 100.0);
            writer.InjectIntoSaveFile(savePath, tempPath);
            File.Copy(tempPath, savePath, overwrite: true);
            File.Delete(tempPath);

            string sidecar = Path.Combine(
                tempDir, "Parsek", "RewindPoints",
                ReFlyWorldPreservationFixture.RewindPointId + ".sfs");
            Assert.True(File.Exists(sidecar), "RP sidecar missing: " + sidecar);
            return sidecar;
        }

        private static ConfigNode LoadFlightState(string sfsPath)
        {
            ConfigNode loaded = ConfigNode.Load(sfsPath);
            ConfigNode game = loaded.GetNode("GAME") ?? loaded;
            ConfigNode flightState = game.GetNode("FLIGHTSTATE");
            Assert.NotNull(flightState);
            return flightState;
        }

        private static uint ParsePid(ConfigNode vessel)
        {
            uint pid;
            uint.TryParse(vessel?.GetValue("persistentId"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out pid);
            return pid;
        }

        private static ConfigNode FindByPid(ConfigNode[] vessels, uint pid)
        {
            for (int i = 0; i < vessels.Length; i++)
            {
                if (ParsePid(vessels[i]) == pid) return vessels[i];
            }
            return null;
        }

        private static void AssertOrbiting(ConfigNode vessel, double expectedSma)
        {
            Assert.NotNull(vessel);
            Assert.Equal("ORBITING", vessel.GetValue("sit"));
            Assert.Equal("False", vessel.GetValue("landed"));
            ConfigNode orbit = vessel.GetNode("ORBIT");
            Assert.NotNull(orbit);
            Assert.Equal(expectedSma,
                double.Parse(orbit.GetValue("SMA"), CultureInfo.InvariantCulture), 3);
        }

        private static ConfigNode MakeVessel(uint vesselPid, string name, uint rootPartPid)
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("persistentId", vesselPid.ToString(CultureInfo.InvariantCulture));
            v.AddValue("name", name);
            v.AddValue("root", "0");
            ConfigNode part = v.AddNode("PART");
            part.AddValue("name", "mk1pod.v2");
            part.AddValue("persistentId", rootPartPid.ToString(CultureInfo.InvariantCulture));
            return v;
        }
    }
}
