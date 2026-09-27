using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REWIND-NAME-STRIP-TAKES-EARLIER-SAME-CRAFT-VESSEL: the plain rewind's pre-load strip
    /// removed the rewind owner's vessel by NAME alone, so rewinding a relaunch of a craft also
    /// removed an earlier launch of the same craft still standing in the rewind quicksave, and
    /// nothing re-spawned it (it is committed history). The owner strip now matches the owner's
    /// launch guid (the quicksave VESSEL <c>pid</c> value against the owner's
    /// <c>RecordedVesselGuid</c>, both read from the same live vessel at the recording's start).
    /// </summary>
    [Collection("Sequential")]
    public class RewindOwnerStripTests : IDisposable
    {
        // Flight 1: Kerbal X launched, landed, committed (the in-flight commit adopts the live
        // vessel, so its spawned pid is the source pid).
        private const uint FirstPid = 1111000001u;
        private const string FirstGuid = "aaaa1111aaaa1111aaaa1111aaaa1111";
        // Flight 2: the same craft relaunched. KSP regenerates the craft-baked persistentId
        // because the first vessel is live, but the craft name is the same.
        private const uint SecondPid = 2222000002u;
        private const string SecondGuid = "bbbb2222bbbb2222bbbb2222bbbb2222";
        private const uint OtherPid = 3333000003u;
        private const double SecondLaunchUT = 1000.0;

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public RewindOwnerStripTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek_rwos_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private static Recording Rec(
            string id, string name, string treeId, double startUT, double endUT,
            uint vesselPid, string guid)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = name,
                TreeId = treeId,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselPersistentId = vesselPid,
                RecordedVesselGuid = guid,
            };
        }

        private static void Commit(string treeId, params Recording[] recs)
        {
            var tree = new RecordingTree { Id = treeId, TreeName = recs[0].VesselName, RootRecordingId = recs[0].RecordingId };
            foreach (var r in recs)
                tree.AddOrReplaceRecording(r);
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (var r in recs)
                RecordingStore.AddRecordingWithTreeForTesting(r);
        }

        /// <summary>Two committed flights of the same craft; returns (first, second).</summary>
        private (Recording first, Recording second) BuildSameCraftRelaunch()
        {
            var first = Rec("first00001", "Kerbal X", "treeA", 10, 400, FirstPid, FirstGuid);
            first.TerminalStateValue = TerminalState.Landed;
            first.SpawnedVesselPersistentId = FirstPid;
            first.VesselSpawned = true;
            Commit("treeA", first);

            var second = Rec("second0002", "Kerbal X", "treeB", SecondLaunchUT, 1500, SecondPid, SecondGuid);
            second.TerminalStateValue = TerminalState.Landed;
            second.SpawnedVesselPersistentId = SecondPid;
            second.VesselSpawned = true;
            second.RewindSaveFileName = "parsek_rw_second";
            Commit("treeB", second);
            return (first, second);
        }

        private static string VesselNode(string name, uint pid, string guid, string situation)
        {
            return "  VESSEL\n  {\n" +
                (guid != null ? "    pid = " + guid + "\n" : "") +
                "    name = " + name + "\n" +
                "    persistentId = " + pid.ToString(CultureInfo.InvariantCulture) + "\n" +
                "    sit = " + situation + "\n  }\n";
        }

        private string WriteSave(params string[] vessels)
        {
            string content = "FLIGHTSTATE\n{\n  UT = " +
                SecondLaunchUT.ToString("R", CultureInfo.InvariantCulture) + "\n" +
                string.Concat(vessels) + "}\n";
            string path = Path.Combine(tempDir, "parsek_rw_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".sfs");
            File.WriteAllText(path, content);
            return path;
        }

        private static List<uint> SurvivingPids(string path)
        {
            var pids = new List<uint>();
            foreach (var v in ConfigNode.Load(path).GetNode("FLIGHTSTATE").GetNodes("VESSEL"))
                pids.Add(uint.Parse(v.GetValue("persistentId"), CultureInfo.InvariantCulture));
            return pids;
        }

        private static List<(uint pid, string guid)> SurvivingIdentities(string path)
        {
            var ids = new List<(uint, string)>();
            foreach (var v in ConfigNode.Load(path).GetNode("FLIGHTSTATE").GetNodes("VESSEL"))
                ids.Add((uint.Parse(v.GetValue("persistentId"), CultureInfo.InvariantCulture),
                    VesselLaunchIdentity.TryReadVesselGuid(v)));
            return ids;
        }

        private static void PreProcess(string path, Recording owner)
        {
            RecordingStore.PreProcessRewindSave(
                path, RecordingStore.BuildRewindOwnerStrip(owner),
                RecordingStore.ResolveRewindStripSpawnedPids,
                RecordingStore.RewindToLaunchLeadTimeSeconds);
        }

        // ---- The repro: ordinary play, the whole plain-rewind strip chain ----

        [Fact]
        public void SameCraftRelaunch_RewindOfSecondFlight_KeepsFirstLandedVessel()
        {
            var shape = BuildSameCraftRelaunch();
            // The rewind save captured at the second launch: the first Kerbal X landed
            // elsewhere, the second on the pad.
            string sfs = WriteSave(
                VesselNode("Kerbal X", FirstPid, FirstGuid, "LANDED"),
                VesselNode("Kerbal X", SecondPid, SecondGuid, "PRELAUNCH"));

            PreProcess(sfs, shape.second);

            var left = SurvivingPids(sfs);
            Assert.Contains(FirstPid, left);
            Assert.DoesNotContain(SecondPid, left);
            Assert.Contains(FirstPid, RewindContext.RewindQuicksaveVesselPids);

            // OnLoad: the launch-identity strip keeps the first vessel as pre-existing, and the
            // future-prelaunch pass keeps it too.
            var all = RecordingStore.CollectAllCommittedRecordings();
            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                FirstPid, FirstGuid, Vessel.Situations.LANDED, all,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: RewindContext.RewindQuicksaveVesselPids,
                out _, out string reason);
            Assert.False(strip);
            Assert.StartsWith("pre-existing", reason);
            Assert.False(ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.LANDED, FirstPid, RewindContext.RewindQuicksaveVesselPids));

            // Playback reset + the guid-aware post-strip reconcile keep the first flight linked
            // to its vessel; the rewound flight is reset and re-spawns at its end.
            RecordingStore.ResetAllPlaybackState(RewindContext.RewindHistoricalSpawnKeepRecordingIds);
            ParsekScenario.ReconcileSpawnStateAfterStrip(SurvivingIdentities(sfs), all);
            Assert.True(shape.first.VesselSpawned);
            Assert.Equal(FirstPid, shape.first.SpawnedVesselPersistentId);
            Assert.False(shape.second.VesselSpawned);
            Assert.Equal(0u, shape.second.SpawnedVesselPersistentId);

            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Rewind owner strip: keeping vessel 'Kerbal X' (pid=" + FirstPid)
                && l.Contains("different launch"));
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Stripped 1 vessel(s) from save (1 by name [Kerbal X]")
                && l.Contains("kept 1 other launch(es)"));
        }

        // ---- Mirror direction: the owner's own vessel is still stripped ----

        [Fact]
        public void OwnerGuidUnknown_NameDecides_BothStripped()
        {
            // An owner recording with no launch guid cannot tell the two apart: the name
            // decides, exactly as before.
            var shape = BuildSameCraftRelaunch();
            shape.second.RecordedVesselGuid = null;
            string sfs = WriteSave(
                VesselNode("Kerbal X", FirstPid, FirstGuid, "LANDED"),
                VesselNode("Kerbal X", SecondPid, SecondGuid, "PRELAUNCH"));

            PreProcess(sfs, shape.second);

            Assert.Empty(SurvivingPids(sfs));
        }

        [Fact]
        public void OwnerNotAnchoredInSave_NameDecides_BothStripped()
        {
            // The owner's guid is known but no vessel in the save carries it: the guid cannot
            // identify the owner's vessel here, so a mismatch proves nothing and the name decides.
            var shape = BuildSameCraftRelaunch();
            string sfs = WriteSave(
                VesselNode("Kerbal X", FirstPid, FirstGuid, "LANDED"),
                VesselNode("Kerbal X", SecondPid, "cccc3333cccc3333cccc3333cccc3333", "PRELAUNCH"));

            PreProcess(sfs, shape.second);

            Assert.Empty(SurvivingPids(sfs));
        }

        [Fact]
        public void SaveVesselWithoutGuid_NotConclusive_Stripped()
        {
            var shape = BuildSameCraftRelaunch();
            string sfs = WriteSave(
                VesselNode("Kerbal X", FirstPid, null, "LANDED"),
                VesselNode("Kerbal X", SecondPid, SecondGuid, "PRELAUNCH"));

            PreProcess(sfs, shape.second);

            Assert.Empty(SurvivingPids(sfs));
        }

        [Fact]
        public void RenamedOwnerRecording_OwnerVesselStrippedByGuid()
        {
            // The recording was renamed after the flight: the name no longer links it to its
            // vessel, the launch guid still does.
            var shape = BuildSameCraftRelaunch();
            shape.second.VesselName = "Second Kerbal X";
            string sfs = WriteSave(
                VesselNode("Kerbal X", FirstPid, FirstGuid, "LANDED"),
                VesselNode("Kerbal X", SecondPid, SecondGuid, "PRELAUNCH"),
                VesselNode("Station", OtherPid, "dddd4444dddd4444dddd4444dddd4444", "ORBITING"));

            PreProcess(sfs, shape.second);

            var left = SurvivingPids(sfs);
            Assert.Contains(FirstPid, left);
            Assert.Contains(OtherPid, left);
            Assert.DoesNotContain(SecondPid, left);
            Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("1 by owner guid"));
        }

        [Fact]
        public void EvaOwner_KerbalStrippedByName_Eva6Shape()
        {
            // EVA-6: the rewind owner is the kerbal's own recording; his EVA vessel in the save
            // carries his launch guid and is stripped with the same "by name" log as before.
            const uint jebPid = 1310464284u;
            const string jebGuid = "d7c7968d91e6439a826a490a48666942";
            const uint capsulePid = 2708531065u;
            var capsule = Rec("28b6e543", "Kerbal X", "treeA", 35, 1276.64, capsulePid,
                "5493223fe49b42b181998849a9a2aefa");
            capsule.TerminalStateValue = TerminalState.Landed;
            capsule.SpawnedVesselPersistentId = capsulePid;
            capsule.VesselSpawned = true;
            Commit("treeA", capsule);
            var jeb = Rec("5092f4cb", "Jebediah Kerman", "treeB", SecondLaunchUT, 1281.9, jebPid, jebGuid);
            jeb.EvaCrewName = "Jebediah Kerman";
            jeb.SpawnedVesselPersistentId = jebPid;
            jeb.VesselSpawned = true;
            jeb.RewindSaveFileName = "parsek_rw_jeb";
            Commit("treeB", jeb);
            string sfs = WriteSave(
                VesselNode("#autoLOC_501232", capsulePid, "5493223fe49b42b181998849a9a2aefa", "LANDED"),
                VesselNode("Jebediah Kerman", jebPid, jebGuid, "LANDED"));

            PreProcess(sfs, jeb);

            var left = SurvivingPids(sfs);
            Assert.Contains(capsulePid, left);
            Assert.DoesNotContain(jebPid, left);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("(1 by name [Jebediah Kerman], 0 by owner guid"));
        }

        [Fact]
        public void ChainEvaChildName_StaysNameOnly()
        {
            // A chain EVA kerbal standing outside at the save boards the vessel later and
            // re-exits (a new EVA guid); the replay re-produces him, so his quicksave EVA
            // vessel is stripped by name whatever its guid.
            var owner = Rec("rover00001", "Rover", "treeR", SecondLaunchUT, 1500, SecondPid, SecondGuid);
            owner.ChainId = "chainR";
            owner.ChainIndex = 0;
            owner.RewindSaveFileName = "parsek_rw_rover";
            var eva = Rec("evabill001", "Bill Kerman", "treeR", 1200, 1300, OtherPid, "eeee5555eeee5555eeee5555eeee5555");
            eva.ChainId = "chainR";
            eva.ChainIndex = 1;
            eva.EvaCrewName = "Bill Kerman";
            Commit("treeR", owner, eva);
            string sfs = WriteSave(
                VesselNode("Rover", SecondPid, SecondGuid, "LANDED"),
                VesselNode("Bill Kerman", 4044000004u, "ffff6666ffff6666ffff6666ffff6666", "LANDED"));

            PreProcess(sfs, owner);

            Assert.Empty(SurvivingPids(sfs));
        }

        // ---- The pure decision ----

        [Fact]
        public void Classify_Table()
        {
            var strip = new RecordingStore.RewindOwnerStrip { OwnerName = "Kerbal X", OwnerGuid = SecondGuid };
            strip.NameOnly.Add("Bill Kerman");

            Assert.Equal(RecordingStore.RewindOwnerStripDecision.StripName,
                RecordingStore.ClassifyRewindOwnerStripVessel("Kerbal X", SecondGuid, strip, true));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.KeepOtherLaunch,
                RecordingStore.ClassifyRewindOwnerStripVessel("Kerbal X", FirstGuid, strip, true));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.StripName,
                RecordingStore.ClassifyRewindOwnerStripVessel("Kerbal X", FirstGuid, strip, false));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.StripName,
                RecordingStore.ClassifyRewindOwnerStripVessel("Kerbal X", null, strip, true));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.StripOwnerGuid,
                RecordingStore.ClassifyRewindOwnerStripVessel("Renamed", SecondGuid.ToUpperInvariant(), strip, true));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.StripName,
                RecordingStore.ClassifyRewindOwnerStripVessel("Bill Kerman", FirstGuid, strip, true));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.NotMatched,
                RecordingStore.ClassifyRewindOwnerStripVessel("Station", FirstGuid, strip, true));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.NotMatched,
                RecordingStore.ClassifyRewindOwnerStripVessel("Kerbal X", SecondGuid, null, true));

            var legacy = RecordingStore.RewindOwnerStrip.FromNames(new[] { "Kerbal X" });
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.StripName,
                RecordingStore.ClassifyRewindOwnerStripVessel("Kerbal X", FirstGuid, legacy, false));
        }

        [Fact]
        public void Anchored_OnlyWhenASaveVesselCarriesTheOwnerGuid()
        {
            var strip = new RecordingStore.RewindOwnerStrip { OwnerName = "Kerbal X", OwnerGuid = SecondGuid };
            Assert.True(RecordingStore.RewindOwnerStripIsAnchored(strip, new[] { FirstGuid, null, SecondGuid }));
            Assert.False(RecordingStore.RewindOwnerStripIsAnchored(strip, new[] { FirstGuid, null }));
            Assert.False(RecordingStore.RewindOwnerStripIsAnchored(
                new RecordingStore.RewindOwnerStrip { OwnerName = "Kerbal X" }, new[] { SecondGuid }));
        }
    }
}
