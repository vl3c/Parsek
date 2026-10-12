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

        // ---- Vessel name decisions + link rows (vessel names design 7.9, phase 2b) ----

        // catches: a Parsek rename moving the owner's leg names so the owner-name fallback
        // compares the NEW name against the quicksave's OLD one, leaving a re-keyed owner copy
        // (fresh guid; the resume path cleared its spawn pid) unstripped - a duplicate after
        // the rewind. Phase 2b: the copy's Resume link guid is part of the owner's identity.
        [Fact]
        public void RenamedOwner_RekeyedCopyUnderTheOldName_IsStillStripped()
        {
            var owner = Rec("owner0001", "Kerbal X", "treeR", 10, 400, SecondPid, SecondGuid);
            owner.TerminalStateValue = TerminalState.Landed;
            Commit("treeR", owner);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(owner, "Relay-2"));
            Assert.Equal("Relay-2", owner.VesselName);
            Assert.True(VesselNameStore.AddLink("cccc3333cccc3333cccc3333cccc3333", SecondGuid,
                VesselIdentityLinkKind.Resume, "test"));

            string sfs = WriteSave(VesselNode("Kerbal X", OtherPid, "cccc3333cccc3333cccc3333cccc3333", "LANDED"));
            PreProcess(sfs, owner);

            Assert.DoesNotContain(OtherPid, SurvivingPids(sfs));
            Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("1 by owner guid"));
        }

        // The Phase 1 gap the links close: a re-keyed copy under a name none of the owner's
        // legs ever carried (renamed in the Tracking Station after its spawn) is found by its
        // Spawn link guid.
        [Fact]
        public void RenamedOwner_RekeyedCopyUnderAnyName_StrippedByLinkGuid()
        {
            var owner = Rec("owner0001", "Kerbal X", "treeR", 10, 400, SecondPid, SecondGuid);
            owner.TerminalStateValue = TerminalState.Landed;
            Commit("treeR", owner);
            Assert.True(VesselNameStore.AddLink("cccc3333cccc3333cccc3333cccc3333", SecondGuid,
                VesselIdentityLinkKind.Spawn, "test"));

            string sfs = WriteSave(
                VesselNode("Somebody Else", OtherPid, "cccc3333cccc3333cccc3333cccc3333", "LANDED"),
                VesselNode("Station", 3555000005u, "dddd4444dddd4444dddd4444dddd4444", "ORBITING"));
            PreProcess(sfs, owner);

            List<uint> left = SurvivingPids(sfs);
            Assert.DoesNotContain(OtherPid, left);
            Assert.Contains(3555000005u, left);
            Assert.Contains("cccc3333cccc3333cccc3333cccc3333",
                RecordingStore.BuildRewindOwnerStrip(owner).OwnerIdentityGuids);
        }

        // catches: dropping the Phase 1 alias pair once links exist. A copy resumed BEFORE the
        // upgrade has no Resume link (the resume cleared its spawn pid, so the load backfill
        // cannot link it either); after a rename only the unanchored alias pair still finds it
        // under its old name.
        [Fact]
        public void RenamedOwner_PreUpgradeResumedCopyWithoutLink_UnderTheOldName_IsStillStripped()
        {
            var owner = Rec("owner0001", "Kerbal X", "treeR", 10, 400, SecondPid, SecondGuid);
            owner.TerminalStateValue = TerminalState.Landed;
            owner.VesselSpawned = false;
            owner.SpawnedVesselPersistentId = 0u;
            Commit("treeR", owner);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(owner, "Relay-2"));
            Assert.Equal(0, VesselNameStore.LinkCount);

            string sfs = WriteSave(VesselNode("Kerbal X", OtherPid, "cccc3333cccc3333cccc3333cccc3333", "LANDED"));
            PreProcess(sfs, owner);

            Assert.DoesNotContain(OtherPid, SurvivingPids(sfs));
            RecordingStore.RewindOwnerStrip strip = RecordingStore.BuildRewindOwnerStrip(owner);
            Assert.True(strip.OwnerAliasNames.SetEquals(new[] { "Kerbal X" }));
            Assert.DoesNotContain("cccc3333cccc3333cccc3333cccc3333", strip.OwnerIdentityGuids);
        }

        // catches: the alias set taking the identity's OTHER leg names. A naming part took over
        // the merged leg's name at a dock ("Station Alpha"), then the player renamed the ship:
        // a rewind to before the launch must not strip the real Station Alpha in the save.
        [Fact]
        public void RenamedOwner_DockNamingPartName_DoesNotStripTheRealStation()
        {
            var owner = Rec("owner0001", "Kerbal X", "treeR", 10, 400, SecondPid, SecondGuid);
            owner.TerminalStateValue = TerminalState.Docked;
            var merged = Rec("merged001", "Station Alpha", "treeR", 400, 900, SecondPid, SecondGuid);
            merged.TerminalStateValue = TerminalState.Orbiting;
            Commit("treeR", owner, merged);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(merged, "Relay"));
            Assert.Equal("Relay", owner.VesselName);
            Assert.Contains("Station Alpha", VesselNameStore.FindByGuid(SecondGuid).PriorNames);
            Assert.True(VesselNameStore.AddLink("dddd4444dddd4444dddd4444dddd4444", SecondGuid,
                VesselIdentityLinkKind.Spawn, "test"));

            const uint CopyPid = 3444000004u;
            string sfs = WriteSave(
                VesselNode("Station Alpha", OtherPid, "cccc3333cccc3333cccc3333cccc3333", "ORBITING"),
                VesselNode("Kerbal X", CopyPid, "dddd4444dddd4444dddd4444dddd4444", "LANDED"));
            PreProcess(sfs, owner);

            List<uint> left = SurvivingPids(sfs);
            Assert.Contains(OtherPid, left);
            Assert.DoesNotContain(CopyPid, left);
            RecordingStore.RewindOwnerStrip dockStrip = RecordingStore.BuildRewindOwnerStrip(owner);
            Assert.DoesNotContain("cccc3333cccc3333cccc3333cccc3333", dockStrip.OwnerIdentityGuids);
            Assert.DoesNotContain("Station Alpha", dockStrip.OwnerAliasNames);
        }

        // catches: design 8.17 staging, where the launch guid stays on a debris half: its name
        // must not become an alias, or another launch's debris of that name is stripped.
        [Fact]
        public void RenamedOwner_DebrisHalfName_DoesNotStripAnotherLaunchsDebris()
        {
            var owner = Rec("owner0001", "Kerbal X", "treeR", 10, 400, SecondPid, SecondGuid);
            owner.TerminalStateValue = TerminalState.Landed;
            var debris = Rec("debris001", "Kerbal X Debris", "treeR", 400, 700, SecondPid, SecondGuid);
            debris.IsDebris = true;
            debris.TerminalStateValue = TerminalState.Destroyed;
            Commit("treeR", owner, debris);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(owner, "Relay"));
            Assert.Contains("Kerbal X Debris", VesselNameStore.FindByGuid(SecondGuid).PriorNames);

            string sfs = WriteSave(
                VesselNode("Kerbal X Debris", OtherPid, "cccc3333cccc3333cccc3333cccc3333", "ORBITING"));
            PreProcess(sfs, owner);

            Assert.Contains(OtherPid, SurvivingPids(sfs));
        }

        // With the owner's own vessel in the save, a different launch under the old name is
        // committed history and stays.
        [Fact]
        public void RenamedOwner_AnchoredOwner_KeepsAnotherLaunchUnderTheOldName()
        {
            var owner = Rec("owner0001", "Kerbal X", "treeR", 10, 400, SecondPid, SecondGuid);
            owner.TerminalStateValue = TerminalState.Landed;
            Commit("treeR", owner);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(owner, "Relay-2"));

            string sfs = WriteSave(
                VesselNode("Kerbal X", SecondPid, SecondGuid, "PRELAUNCH"),
                VesselNode("Kerbal X", OtherPid, "cccc3333cccc3333cccc3333cccc3333", "LANDED"));
            PreProcess(sfs, owner);

            List<uint> left = SurvivingPids(sfs);
            Assert.DoesNotContain(SecondPid, left);
            Assert.Contains(OtherPid, left);
            RecordingStore.RewindOwnerStrip strip = RecordingStore.BuildRewindOwnerStrip(owner);
            Assert.Contains("Kerbal X", strip.OwnerAliasNames);
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.NotMatched,
                RecordingStore.ClassifyRewindOwnerStripVessel(
                    "Kerbal X", "cccc3333cccc3333cccc3333cccc3333", strip, ownerAnchored: true));
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
        public void Classify_IdentityGuidSet_LinkGuidsCountAsTheOwner()
        {
            var strip = new RecordingStore.RewindOwnerStrip { OwnerName = "Relay", OwnerGuid = SecondGuid };
            strip.OwnerIdentityGuids.Add(SecondGuid);
            strip.OwnerIdentityGuids.Add("cccc3333cccc3333cccc3333cccc3333");

            Assert.Equal(RecordingStore.RewindOwnerStripDecision.StripOwnerGuid,
                RecordingStore.ClassifyRewindOwnerStripVessel("Kerbal X", "CCCC3333CCCC3333CCCC3333CCCC3333", strip, true));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.StripName,
                RecordingStore.ClassifyRewindOwnerStripVessel("Relay", "cccc3333cccc3333cccc3333cccc3333", strip, true));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.KeepOtherLaunch,
                RecordingStore.ClassifyRewindOwnerStripVessel("Relay", FirstGuid, strip, true));
            Assert.Equal(RecordingStore.RewindOwnerStripDecision.NotMatched,
                RecordingStore.ClassifyRewindOwnerStripVessel("Kerbal X", FirstGuid, strip, false));
            Assert.True(RecordingStore.RewindOwnerStripIsAnchored(strip, new[] { "cccc3333-cccc-3333-cccc-3333cccc3333" }));
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
