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
    /// Headless guards for the two injection presets behind the OC-1 and RB-1 / RB-2
    /// lanes (<see cref="OverlapCapFixture"/>, <see cref="RewindReadbackFixture"/>). Each
    /// cell names the lane property it protects, so a fixture drift that would make a
    /// flight vacuous (a loop the cap never touches, an RP that resurrects nothing) reds
    /// here instead of costing a KSP boot.
    /// </summary>
    [Collection("Sequential")]
    public class CapReadbackFixtureTests : IDisposable
    {
        // career-science-pad's Flea, cut to the keys the fixture reads: its identity,
        // its pad situation and its crewed pod.
        private static readonly string FakeCareerSave =
            "GAME\n{\n" +
            "\tFLIGHTSTATE\n\t{\n" +
            "\t\tversion = 1.12.5\n" +
            "\t\tUT = 9.0599999999998957\n" +
            "\t\tactiveVessel = 0\n" +
            "\t\tVESSEL\n\t\t{\n" +
            "\t\t\tpid = " + RewindReadbackFixture.HostFleaLaunchGuid + "\n" +
            "\t\t\tpersistentId = " +
                RewindReadbackFixture.HostFleaPersistentId.ToString(CultureInfo.InvariantCulture) + "\n" +
            "\t\t\tname = #autoLOC_501224\n" +
            "\t\t\ttype = Ship\n" +
            "\t\t\tsit = PRELAUNCH\n" +
            "\t\t\tlanded = True\n" +
            "\t\t\tlandedAt = LaunchPad\n" +
            "\t\t\tlaunchedFrom = LaunchPad\n" +
            "\t\t\troot = 0\n" +
            "\t\t\tORBIT\n\t\t\t{\n\t\t\t\tSMA = 300819.3\n\t\t\t\tREF = 1\n\t\t\t}\n" +
            "\t\t\tref = 4000001\n" +
            "\t\t\tPART\n\t\t\t{\n\t\t\t\tname = mk1pod.v2\n" +
            "\t\t\t\tuid = 4000001\n" +
            "\t\t\t\tpersistentId = 111222333\n" +
            "\t\t\t\tcrew = Jebediah Kerman\n\t\t\t}\n" +
            "\t\t\tPART\n\t\t\t{\n\t\t\t\tname = parachuteSingle\n" +
            "\t\t\t\tuid = 4000002\n" +
            "\t\t\t\tpersistentId = 1253190902\n\t\t\t}\n" +
            "\t\t}\n" +
            "\t}\n" +
            "}\n";

        private readonly string tempDir;
        private readonly bool priorSuppress;

        public CapReadbackFixtureTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "parsek_capreadback_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            priorSuppress = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorSuppress;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        // ------------------------------------------------------------------
        // overlap-cap (OC-1)
        // ------------------------------------------------------------------

        [Fact]
        public void OverlapCap_PeriodIsBelowSpanOver20_SoTheEngineCapMustRaiseIt()
        {
            RecordingBuilder b = OverlapCapFixture.BuildRecording(OverlapCapFixture.HostSaveUT);
            double duration = b.GetEndUT() - b.GetStartUT();
            Assert.Equal(OverlapCapFixture.DurationSeconds, duration, 6);
            Assert.True(b.GetLoopPlayback());
            double period = b.GetLoopIntervalSeconds();
            Assert.Equal(OverlapCapFixture.LoopPeriodSeconds, period, 6);

            // Not clamped by ResolveLoopInterval (at the floor, not below it)...
            Assert.True(period >= LoopTiming.MinCycleDuration);
            // ...an overlap loop...
            Assert.True(GhostPlaybackLogic.IsOverlapLoop(period, duration));
            // ...and the cap raises it to exactly span/20: the verdict OC-1 pins.
            double effective = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                period, duration, GhostPlayback.MaxOverlapGhostsPerRecording);
            Assert.Equal(OverlapCapFixture.ExpectedEffectiveCadenceSeconds, effective, 9);
            Assert.True(Math.Abs(effective - period) > 1e-6, "the cap must adjust the period");
            Assert.Equal(20L, (long)Math.Ceiling(duration / effective));
        }

        [Fact]
        public void OverlapCap_StartsAfterTheSaveClock_SoEveryCopySpawnsUnderTheTracer()
        {
            RecordingBuilder b = OverlapCapFixture.BuildRecording(OverlapCapFixture.HostSaveUT);
            Assert.True(b.GetStartUT() - OverlapCapFixture.HostSaveUT >= 30.0);
        }

        [Fact]
        public void OverlapCap_WritesALoopableAtmosphericPhaseAndOwnLoopFields()
        {
            ConfigNode meta = OverlapCapFixture.BuildRecording(OverlapCapFixture.HostSaveUT)
                .BuildV3Metadata();
            Assert.Equal("True", meta.GetValue("loopPlayback"));
            Assert.Equal("atmo", meta.GetValue("segmentPhase"));
            Assert.Equal(OverlapCapFixture.LoopPeriodSeconds,
                double.Parse(meta.GetValue("loopIntervalSeconds"), CultureInfo.InvariantCulture), 9);
            // The phase is what keeps SanitizeNonLoopableLoopPlayback off the toggle.
            Assert.True(Recording.IsLoopableRecording(new Recording { SegmentPhase = "atmo" }));
            Assert.False(Recording.IsLoopableRecording(new Recording()));
        }

        // ------------------------------------------------------------------
        // rewind-readback (RB-1 / RB-2)
        // ------------------------------------------------------------------

        [Fact]
        public void RewindReadback_RpIsInThePastAndSlot1IsTheOpenUnfinishedFlight()
        {
            double split = RewindReadbackFixture.SplitUTFor(RewindReadbackFixture.HostSaveUT);
            Assert.True(split > 0.0);
            Assert.True(split < RewindReadbackFixture.HostSaveUT);
            RewindPoint rp = RewindReadbackFixture.BuildRewindPoint(split);
            Assert.Null(rp.CreatingSessionId);
            Assert.Equal(RewindReadbackFixture.BoosterSlotIndex,
                rp.PidSlotMap[ScenarioWriter.DeriveVesselPersistentId(
                    RewindReadbackFixture.BoosterRecordingId)]);
            // The Flea is UNRELATED to the RP: neither map may name its pid.
            Assert.False(rp.PidSlotMap.ContainsKey(RewindReadbackFixture.HostFleaPersistentId));
            Assert.False(rp.RootPartPidMap.ContainsKey(RewindReadbackFixture.HostFleaPersistentId));
        }

        [Fact]
        public void RewindReadback_SidecarLandsCrewlessSlotsOnTheRunwayAndReadmitsTheFleaVerbatim()
        {
            string sidecar = InjectReadbackAndReturnSidecar();
            ConfigNode[] vessels = LoadFlightState(sidecar).GetNodes("VESSEL");
            Assert.Equal(3, vessels.Length);

            foreach (string recId in new[]
            {
                RewindReadbackFixture.UpperRecordingId, RewindReadbackFixture.BoosterRecordingId,
            })
            {
                ConfigNode slot = FindByPid(vessels, ScenarioWriter.DeriveVesselPersistentId(recId));
                Assert.NotNull(slot);
                Assert.Equal("LANDED", slot.GetValue("sit"));
                Assert.Equal("Runway", slot.GetValue("landedAt"));
                foreach (ConfigNode part in slot.GetNodes("PART"))
                    Assert.Null(part.GetValue("crew"));
                Assert.Equal(ScenarioWriter.DeriveVesselLaunchGuid(recId), slot.GetValue("pid"));
            }
            double lonUpper = double.Parse(FindByPid(vessels, ScenarioWriter.DeriveVesselPersistentId(
                RewindReadbackFixture.UpperRecordingId)).GetValue("lon"), CultureInfo.InvariantCulture);
            double lonBooster = double.Parse(FindByPid(vessels, ScenarioWriter.DeriveVesselPersistentId(
                RewindReadbackFixture.BoosterRecordingId)).GetValue("lon"), CultureInfo.InvariantCulture);
            // Two stations, never one pile.
            Assert.True(Math.Abs(lonUpper - lonBooster) > 0.005);

            ConfigNode flea = FindByPid(vessels, RewindReadbackFixture.HostFleaPersistentId);
            Assert.NotNull(flea);
            // The Flea is readmitted verbatim, so its part ids are the host save's own.
            Assert.Equal("4000002", flea.GetNodes("PART")[1].GetValue("uid"));
            Assert.Equal("1253190902", flea.GetNodes("PART")[1].GetValue("persistentId"));
            foreach (string recId in new[]
            {
                RewindReadbackFixture.UpperRecordingId, RewindReadbackFixture.BoosterRecordingId,
            })
            {
                ConfigNode slotClone = FindByPid(vessels, ScenarioWriter.DeriveVesselPersistentId(recId));
                AssertSharesNoPartIdentityWith(flea, slotClone);
                // The slot map keys on the root part pid, which must survive the re-roll.
                Assert.Equal(
                    ScenarioWriter.DeriveRootPartPersistentId(recId).ToString(CultureInfo.InvariantCulture),
                    slotClone.GetNodes("PART")[0].GetValue("persistentId"));
            }
            Assert.Equal(RewindReadbackFixture.HostFleaLaunchGuid, flea.GetValue("pid"));
            Assert.Equal("PRELAUNCH", flea.GetValue("sit"));
            Assert.Equal("Jebediah Kerman", flea.GetNodes("PART")[0].GetValue("crew"));
        }

        [Fact]
        public void RewindReadback_RealScrubKeepsTheFleaAndTheSelectedSlotOnly()
        {
            string sidecar = InjectReadbackAndReturnSidecar();
            string temp = Path.Combine(tempDir, "Parsek_Rewind_test.sfs");
            File.Copy(sidecar, temp, overwrite: true);

            RewindPoint rp = RewindReadbackFixture.BuildRewindPoint(
                RewindReadbackFixture.SplitUTFor(RewindReadbackFixture.HostSaveUT));
            RewindInvoker.ReFlySaveScrubResult result =
                RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                    temp, rp, RewindReadbackFixture.BoosterSlotIndex);

            Assert.True(result.Applied);
            Assert.Equal(1, result.VesselsRemoved);
            Assert.Equal(1, result.VesselsPreserved);

            ConfigNode fs = LoadFlightState(temp);
            ConfigNode[] after = fs.GetNodes("VESSEL");
            Assert.Equal(2, after.Length);
            Assert.NotNull(FindByPid(after, RewindReadbackFixture.HostFleaPersistentId));
            Assert.Null(FindByPid(after, ScenarioWriter.DeriveVesselPersistentId(
                RewindReadbackFixture.UpperRecordingId)));
            int active = int.Parse(fs.GetValue("activeVessel"), CultureInfo.InvariantCulture);
            Assert.Equal(ScenarioWriter.DeriveVesselPersistentId(
                RewindReadbackFixture.BoosterRecordingId), ParsePid(after[active]));
        }

        [Fact]
        public void RewindReadback_RelayIsAppendedToTheLiveSaveOnlyAndNeverToTheRp()
        {
            string sidecar = InjectReadbackAndReturnSidecar();
            string savePath = Path.Combine(tempDir, "persistent.sfs");
            Assert.True(RewindReadbackFixture.AppendParkedRelayVessel(savePath));

            ConfigNode fs = LoadFlightState(savePath);
            ConfigNode[] live = fs.GetNodes("VESSEL");
            Assert.Equal(2, live.Length);
            // The Flea keeps index 0, so activeVessel = 0 still focuses it on the first load.
            Assert.Equal(RewindReadbackFixture.HostFleaPersistentId, ParsePid(live[0]));
            Assert.Equal("0", fs.GetValue("activeVessel"));
            ConfigNode relay = live[1];
            Assert.Equal(RewindReadbackFixture.RelayVesselPid, ParsePid(relay));
            Assert.Equal(RewindReadbackFixture.RelayVesselName, relay.GetValue("name"));
            Assert.Equal("LANDED", relay.GetValue("sit"));
            Assert.Equal("Runway", relay.GetValue("landedAt"));
            foreach (ConfigNode part in relay.GetNodes("PART"))
                Assert.Null(part.GetValue("crew"));
            Assert.NotEqual(RewindReadbackFixture.HostFleaLaunchGuid, relay.GetValue("pid"));
            AssertSharesNoPartIdentityWith(live[0], relay);
            // The injected Parsek scenario survives the round trip.
            Assert.Contains("rewindPointId = " + RewindReadbackFixture.RewindPointId,
                File.ReadAllText(savePath));

            // The RP quicksave was written before the relay existed.
            Assert.Null(FindByPid(LoadFlightState(sidecar).GetNodes("VESSEL"),
                RewindReadbackFixture.RelayVesselPid));
        }

        /// <summary>
        /// A Flea clone must not share a part flight id (or a non-root persistentId) with the
        /// Flea: kRPC resolves parts by flight id, and a loaded clone on the runway made the
        /// flown Flea read no thrust and then no parachute (RB-1 2026-09-27_1353, RB-2
        /// 2026-09-27_1417). Fails if the fixture copies the Flea's PART ids again.
        /// </summary>
        private static void AssertSharesNoPartIdentityWith(ConfigNode flea, ConfigNode clone)
        {
            var fleaUids = new HashSet<string>();
            var fleaPids = new HashSet<string>();
            foreach (ConfigNode part in flea.GetNodes("PART"))
            {
                fleaUids.Add(part.GetValue("uid"));
                fleaPids.Add(part.GetValue("persistentId"));
            }
            ConfigNode[] cloneParts = clone.GetNodes("PART");
            Assert.Equal(flea.GetNodes("PART").Length, cloneParts.Length);
            foreach (ConfigNode part in cloneParts)
            {
                Assert.False(string.IsNullOrEmpty(part.GetValue("uid")));
                Assert.DoesNotContain(part.GetValue("uid"), fleaUids);
                Assert.DoesNotContain(part.GetValue("persistentId"), fleaPids);
            }
            Assert.Equal(cloneParts[0].GetValue("uid"), clone.GetValue("ref"));
        }

        private string InjectReadbackAndReturnSidecar()
        {
            string savePath = Path.Combine(tempDir, "persistent.sfs");
            File.WriteAllText(savePath, FakeCareerSave);
            string tempPath = savePath + ".tmp";
            var writer = new ScenarioWriter().WithV3Format();
            RewindReadbackFixture.PopulateWriter(writer, RewindReadbackFixture.HostSaveUT);
            writer.InjectIntoSaveFile(savePath, tempPath);
            File.Copy(tempPath, savePath, overwrite: true);
            File.Delete(tempPath);

            string content = File.ReadAllText(savePath);
            Assert.Contains("rewindPointId = " + RewindReadbackFixture.RewindPointId, content);
            string sidecar = Path.Combine(tempDir, "Parsek", "RewindPoints",
                RewindReadbackFixture.RewindPointId + ".sfs");
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
                if (ParsePid(vessels[i]) == pid) return vessels[i];
            return null;
        }
    }
}
