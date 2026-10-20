using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// design-load-restores-the-save.md 4.5: the reference scanner over a save file and the
    /// collector's decision table, fail-closed rule, referrer cache and load-time skips.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekFileCollectorTests : IDisposable
    {
        private const string HashA1 = "0123456789abcdef";
        private const string HashA2 = "fedcba9876543210";
        private const string HashB = "00112233aabbccdd";
        private const string HashM = "1111222233334444";

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> cleanupRoots = new List<string>();

        public ParsekFileCollectorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekFileCollector.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            RewindContext.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekFileCollector.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            foreach (string root in cleanupRoots)
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        // ------------------------------------------------------------- scanner

        private static SaveReferences Scan(string text)
        {
            Assert.True(SaveReferenceScanner.TryScan(new StringReader(text), out var refs, out string failure),
                "scan failed: " + failure);
            return refs;
        }

        private static string ParsekScenarioText(string body)
        {
            return "GAME\n{\n\tversion = 1.12.5\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n\t\tscene = 5, 7, 8\n"
                + body + "\n\t}\n}\n";
        }

        [Fact]
        public void Scanner_CollectsNestedRecordingsPointsAndRewindSaves()
        {
            string text = ParsekScenarioText(@"
        RECORDING_TREE
        {
            id = tree-1
            resumeRewindSave = parsek_rw_resume
            RECORDING
            {
                recordingId = rec-a
                precHash = " + HashA1 + @"
                vesselHash = " + HashB + @"
                rewindSave = parsek_rw_aaa
                BRANCH
                {
                    recordingId = not-a-recording-node
                }
            }
            RECORDING
            {
                recordingId = rec-b // trailing comment
            }
        }
        REWIND_POINTS
        {
            POINT
            {
                rewindPointId = rp-1
                quicksaveFilename = rp-1.sfs
                CHILD_SLOT
                {
                    originChildRecordingId = rec-a
                }
            }
        }");

            var refs = Scan(text);

            Assert.Equal(new[] { "rec-a", "rec-b" }, refs.RecordingIds.OrderBy(x => x).ToArray());
            Assert.True(refs.NamesVersion("rec-a", HashA1));
            Assert.True(refs.NamesVersion("rec-a", HashB));
            Assert.False(refs.NamesVersion("rec-b", HashA1));
            Assert.Equal(new[] { "rp-1" }, refs.RewindPointIds.ToArray());
            Assert.Contains("parsek_rw_aaa", refs.RewindSaveNames);
            Assert.Contains("parsek_rw_resume", refs.RewindSaveNames);
            Assert.DoesNotContain("not-a-recording-node", refs.RecordingIds);
        }

        [Fact]
        public void Scanner_IgnoresRecordingsOutsideTheParsekScenario()
        {
            string text = "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = SomeOtherMod\n\t\tRECORDING\n\t\t{\n"
                + "\t\t\trecordingId = foreign\n\t\t\trewindSave = parsek_rw_foreign\n\t\t}\n\t}\n"
                + "\tFLIGHTSTATE\n\t{\n\t\tRECORDING\n\t\t{\n\t\t\trecordingId = flightstate\n\t\t}\n\t}\n"
                + "\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n\t\tRECORDING\n\t\t{\n"
                + "\t\t\trecordingId = mine\n\t\t}\n\t}\n}\n";

            var refs = Scan(text);

            Assert.Equal(new[] { "mine" }, refs.RecordingIds.ToArray());
            Assert.Empty(refs.RewindSaveNames);
        }

        [Fact]
        public void Scanner_ParsekNameAfterChildren_StillCounts()
        {
            // The scenario's name line can follow its child nodes; the buffer is judged at close.
            string text = "GAME\n{\n\tSCENARIO\n\t{\n\t\tRECORDING\n\t\t{\n\t\t\trecordingId = late\n\t\t}\n"
                + "\t\tname = ParsekScenario\n\t}\n}\n";
            Assert.Equal(new[] { "late" }, Scan(text).RecordingIds.ToArray());
        }

        [Fact]
        public void Scanner_TruncatedFile_Fails()
        {
            string text = ParsekScenarioText("\t\tRECORDING\n\t\t{\n\t\t\trecordingId = rec-a\n\t\t}");
            string truncated = text.Substring(0, text.IndexOf("recordingId", StringComparison.Ordinal));
            Assert.False(SaveReferenceScanner.TryScan(new StringReader(truncated), out _, out string failure));
            Assert.StartsWith("truncated", failure);
        }

        [Fact]
        public void Scanner_UnbalancedClose_Fails()
        {
            Assert.False(SaveReferenceScanner.TryScan(new StringReader("GAME\n{\n}\n}\n"), out _, out string failure));
            Assert.StartsWith("unbalanced-close", failure);
        }

        [Fact]
        public void Scanner_EmptyFile_Fails()
        {
            Assert.False(SaveReferenceScanner.TryScan(new StringReader(""), out _, out string failure));
            Assert.Equal("no-nodes", failure);
        }

        [Fact]
        public void Scanner_SaveWithoutParsekNode_NamesNothing()
        {
            var refs = Scan("GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ResearchAndDevelopment\n\t}\n}\n");
            Assert.Empty(refs.RecordingIds);
            Assert.Empty(refs.RewindPointIds);
            Assert.Empty(refs.RewindSaveNames);
        }

        [Fact]
        public void Scanner_ReadsWhatConfigNodeSaveWrites()
        {
            // KSP's own writer is the format the scanner must follow.
            var root = new ConfigNode();
            var game = root.AddNode("GAME");
            var scenario = game.AddNode("SCENARIO");
            scenario.AddValue("name", "ParsekScenario");
            var tree = scenario.AddNode("RECORDING_TREE");
            tree.AddValue("resumeRewindSave", "parsek_rw_written");
            var rec = tree.AddNode("RECORDING");
            rec.AddValue("recordingId", "rec-written");
            rec.AddValue("precHash", HashA1);
            var point = scenario.AddNode("REWIND_POINTS").AddNode("POINT");
            point.AddValue("rewindPointId", "rp-written");

            string dir = NewSaveDir("writer");
            string path = Path.Combine(dir, "persistent.sfs");
            root.Save(path);

            SaveReferences refs;
            using (var reader = new StreamReader(path))
                Assert.True(SaveReferenceScanner.TryScan(reader, out refs, out _));
            Assert.Contains("rec-written", refs.RecordingIds);
            Assert.True(refs.NamesVersion("rec-written", HashA1));
            Assert.Contains("rp-written", refs.RewindPointIds);
            Assert.Contains("parsek_rw_written", refs.RewindSaveNames);
        }

        // ------------------------------------------------------------- collector

        private string NewSaveDir(string label)
        {
            string dir = Path.Combine(Path.GetTempPath(), "parsek-collector-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            cleanupRoots.Add(dir);
            ParsekFileCollector.SaveDirectoryOverrideForTesting = dir;
            return dir;
        }

        private static string Write(string dir, string relative, string content = "x")
        {
            string path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        private static string RecordingNode(string id, string precHash = null, string vesselHash = null, string rewindSave = null)
        {
            string body = "\t\tRECORDING\n\t\t{\n\t\t\trecordingId = " + id + "\n";
            if (precHash != null) body += "\t\t\tprecHash = " + precHash + "\n";
            if (vesselHash != null) body += "\t\t\tvesselHash = " + vesselHash + "\n";
            if (rewindSave != null) body += "\t\t\trewindSave = " + rewindSave + "\n";
            return body + "\t\t}\n";
        }

        private static string PointsNode(params string[] rpIds)
        {
            string body = "\t\tREWIND_POINTS\n\t\t{\n";
            foreach (string rp in rpIds)
                body += "\t\t\tPOINT\n\t\t\t{\n\t\t\t\trewindPointId = " + rp + "\n\t\t\t}\n";
            return body + "\t\t}\n";
        }

        private static string VersionName(string id, string hash, SidecarKind kind)
        {
            return "Parsek/Recordings/_versions/" + SidecarVersions.VersionFileName(id, hash, kind);
        }

        private void AddMemoryRecording(string id, string precHash = null)
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = id,
                VesselName = id,
                PrecHash = precHash,
            });
        }

        [Fact]
        public void Collect_DecisionTable()
        {
            string dir = NewSaveDir("table");
            Write(dir, "persistent.sfs", ParsekScenarioText(
                RecordingNode("rec-a", precHash: HashA1, rewindSave: "parsek_rw_aaa") + PointsNode("rp-1")));
            AddMemoryRecording("rec-m", HashM);
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { new RewindPoint { RewindPointId = "rp-3" } },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            ParsekScenario.pendingActiveTreeResumeRewindSave = "parsek_rw_ccc";

            string headA = Write(dir, "Parsek/Recordings/rec-a.prec");
            string headM = Write(dir, "Parsek/Recordings/rec-m.prec");
            string headOrphan = Write(dir, "Parsek/Recordings/rec-orphan_vessel.craft", "orphan");
            string transient = Write(dir, "Parsek/Recordings/rec-a.prec.tmp");
            string versionA1 = Write(dir, VersionName("rec-a", HashA1, SidecarKind.Prec));
            string versionA2 = Write(dir, VersionName("rec-a", HashA2, SidecarKind.Prec), "stale");
            string versionM = Write(dir, VersionName("rec-m", HashM, SidecarKind.Prec));
            string versionB = Write(dir, VersionName("rec-b", HashB, SidecarKind.Vessel));
            string unknownInVersions = Write(dir, "Parsek/Recordings/_versions/readme.txt");
            string rp1 = Write(dir, "Parsek/RewindPoints/rp-1.sfs");
            string rp2 = Write(dir, "Parsek/RewindPoints/rp-2.sfs");
            string rp3 = Write(dir, "Parsek/RewindPoints/rp-3.sfs");
            string rwA = Write(dir, "Parsek/Saves/parsek_rw_aaa.sfs");
            string rwB = Write(dir, "Parsek/Saves/parsek_rw_bbb.sfs");
            string rwC = Write(dir, "Parsek/Saves/parsek_rw_ccc.sfs");
            string careerStart = Write(dir, "Parsek/Saves/parsek_career_start.sfs");
            string unknownSave = Write(dir, "Parsek/Saves/someone_elses.sfs");

            var result = ParsekFileCollector.Run("test", scenario);

            Assert.True(result.Ran);
            Assert.True(File.Exists(headA), "expected kept: " + headA);
            Assert.True(File.Exists(headM), "expected kept: " + headM);
            Assert.False(File.Exists(headOrphan), "expected collected: " + headOrphan);
            Assert.Equal("orphan", File.ReadAllText(Path.Combine(dir, "Parsek", "Recordings",
                RecordingStore.OrphanQuarantineDirName, "rec-orphan_vessel.craft")));
            Assert.False(File.Exists(transient), "expected collected: " + transient);
            Assert.True(File.Exists(versionA1), "expected kept: " + versionA1);
            Assert.False(File.Exists(versionA2), "expected collected: " + versionA2);
            Assert.True(File.Exists(versionM), "expected kept: " + versionM);
            Assert.False(File.Exists(versionB), "expected collected: " + versionB);
            Assert.True(File.Exists(unknownInVersions), "expected kept: " + unknownInVersions);
            Assert.True(File.Exists(rp1), "expected kept: " + rp1);
            Assert.False(File.Exists(rp2), "expected collected: " + rp2);
            Assert.True(File.Exists(rp3), "expected kept: " + rp3);
            Assert.True(File.Exists(rwA), "expected kept: " + rwA);
            Assert.False(File.Exists(rwB), "expected collected: " + rwB);
            Assert.True(File.Exists(rwC), "expected kept: " + rwC);
            Assert.True(File.Exists(careerStart), "expected kept: " + careerStart);
            Assert.True(File.Exists(unknownSave), "expected kept: " + unknownSave);

            Assert.Equal(2, result.VersionsDeleted);
            Assert.Equal(1, result.HeadsQuarantined);
            Assert.Equal(1, result.RewindPointFilesDeleted);
            Assert.Equal(1, result.RewindSavesDeleted);
            Assert.Contains(logLines, l => l.Contains("[FileCollector]") && l.Contains("Collection:")
                && l.Contains("referrers=1") && l.Contains("versionsDeleted=2")
                && l.Contains("headsQuarantined=1") && l.Contains("rewindPointFilesDeleted=1")
                && l.Contains("rewindSavesDeleted=1") && l.Contains("reason=test"));
        }

        [Fact]
        public void Collect_FilesIncomingFirst_ThenJudgesIt()
        {
            string dir = NewSaveDir("incoming");
            byte[] named = { 1, 2, 3 };
            byte[] unnamed = { 4, 5, 6 };
            string namedHash = SidecarContentHash.Compute(named);
            Write(dir, "persistent.sfs", ParsekScenarioText(RecordingNode("rec-a", precHash: namedHash)));
            AddMemoryRecording("rec-a");
            string versions = Path.Combine(dir, "Parsek", "Recordings", "_versions");
            Directory.CreateDirectory(versions);
            File.WriteAllBytes(Path.Combine(versions, SidecarVersions.IncomingFileName("rec-a", SidecarKind.Prec)), named);
            File.WriteAllBytes(Path.Combine(versions, SidecarVersions.IncomingFileName("rec-a", SidecarKind.Prec)), unnamed);

            var result = ParsekFileCollector.Run("test", null);

            Assert.Equal(2, result.IncomingFiled);
            Assert.Equal(1, result.VersionsDeleted);
            string[] left = Directory.GetFiles(versions).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { SidecarVersions.VersionFileName("rec-a", namedHash, SidecarKind.Prec) }, left);
        }

        [Fact]
        public void Collect_BackupSaveIsAReferrer()
        {
            string dir = NewSaveDir("backup");
            Write(dir, "persistent.sfs", ParsekScenarioText(RecordingNode("rec-a")));
            Write(dir, "Backup/persistent (2026-10-01).sfs",
                ParsekScenarioText(RecordingNode("rec-old", precHash: HashA2) + PointsNode("rp-old")));
            AddMemoryRecording("rec-a");
            string head = Write(dir, "Parsek/Recordings/rec-old.prec");
            string version = Write(dir, VersionName("rec-old", HashA2, SidecarKind.Prec));
            string rp = Write(dir, "Parsek/RewindPoints/rp-old.sfs");

            var result = ParsekFileCollector.Run("test", null);

            Assert.Equal(2, result.Referrers);
            Assert.True(File.Exists(head), "expected kept: " + head);
            Assert.True(File.Exists(version), "expected kept: " + version);
            Assert.True(File.Exists(rp), "expected kept: " + rp);
        }

        [Fact]
        public void Collect_TwoSavesInOneFolder_TheOtherSavesFilesSurvive()
        {
            // S6: before.sfs is loaded (memory names its hash); after.sfs, written later,
            // names rec-a at another hash and a recording before.sfs never knew.
            string dir = NewSaveDir("s6");
            Write(dir, "before.sfs", ParsekScenarioText(RecordingNode("rec-a", precHash: HashA1)));
            Write(dir, "after.sfs", ParsekScenarioText(
                RecordingNode("rec-a", precHash: HashA2) + RecordingNode("rec-c", precHash: HashB)));
            AddMemoryRecording("rec-a", HashA1);
            string headA = Write(dir, "Parsek/Recordings/rec-a.prec");
            string afterVersion = Write(dir, VersionName("rec-a", HashA2, SidecarKind.Prec));
            string headC = Write(dir, "Parsek/Recordings/rec-c.prec");
            string headCVessel = Write(dir, "Parsek/Recordings/rec-c_vessel.craft");

            var result = ParsekFileCollector.Run("test", null);

            Assert.True(result.Ran);
            Assert.True(File.Exists(headA), "expected kept: " + headA);
            Assert.True(File.Exists(afterVersion), "expected kept: " + afterVersion);
            Assert.True(File.Exists(headC), "expected kept: " + headC);
            Assert.True(File.Exists(headCVessel), "expected kept: " + headCVessel);
            Assert.Equal(0, result.HeadsQuarantined);
            Assert.Equal(0, result.VersionsDeleted);
        }

        [Fact]
        public void Collect_TruncatedReferrer_FailsClosed_WarnsOncePerPath()
        {
            string dir = NewSaveDir("truncated");
            Write(dir, "persistent.sfs", ParsekScenarioText(RecordingNode("rec-a")));
            string broken = Write(dir, "quicksave.sfs", "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n");
            AddMemoryRecording("rec-a");
            string orphanVersion = Write(dir, VersionName("rec-z", HashB, SidecarKind.Prec));
            string orphanHead = Write(dir, "Parsek/Recordings/rec-z.prec");
            string orphanRp = Write(dir, "Parsek/RewindPoints/rp-z.sfs");

            var first = ParsekFileCollector.Run("test", null);
            var second = ParsekFileCollector.Run("test", null);

            Assert.False(first.Ran);
            Assert.Equal("unreadable-referrer", first.SkipReason);
            Assert.Equal("unreadable-referrer", second.SkipReason);
            Assert.True(File.Exists(orphanVersion), "expected kept: " + orphanVersion);
            Assert.True(File.Exists(orphanHead), "expected kept: " + orphanHead);
            Assert.True(File.Exists(orphanRp), "expected kept: " + orphanRp);
            Assert.Single(logLines.Where(l => l.Contains("[WARN][FileCollector]")
                && l.Contains("reason=unreadable-referrer") && l.Contains(Path.GetFileName(broken))));
        }

        [Fact]
        public void Collect_LockedReferrer_FailsClosed()
        {
            string dir = NewSaveDir("locked");
            Write(dir, "persistent.sfs", ParsekScenarioText(RecordingNode("rec-a")));
            string locked = Write(dir, "quicksave.sfs", ParsekScenarioText(RecordingNode("rec-z")));
            AddMemoryRecording("rec-a");
            string orphanVersion = Write(dir, VersionName("rec-y", HashB, SidecarKind.Prec));
            ParsekFileCollector.OpenReferrerForTesting = path =>
            {
                if (string.Equals(Path.GetFileName(path), "quicksave.sfs", StringComparison.Ordinal))
                    throw new IOException("simulated sharing violation");
                return new StreamReader(path);
            };

            var result = ParsekFileCollector.Run("test", null);

            Assert.Equal("unreadable-referrer", result.SkipReason);
            Assert.True(File.Exists(orphanVersion), "expected kept: " + orphanVersion);
            Assert.Contains(logLines, l => l.Contains("[FileCollector]") && l.Contains("open-failed")
                && l.Contains("simulated sharing violation") && l.Contains(Path.GetFileName(locked)));
        }

        [Fact]
        public void Collect_ReferrerCache_RescansOnlyWhenTheFileChanges()
        {
            string dir = NewSaveDir("cache");
            string persistent = Write(dir, "persistent.sfs", ParsekScenarioText(RecordingNode("rec-a", precHash: HashA1)));
            AddMemoryRecording("rec-a");

            var first = ParsekFileCollector.Run("test", null);
            Assert.Equal(1, first.Scanned);
            Assert.Equal(0, first.Cached);

            var second = ParsekFileCollector.Run("test", null);
            Assert.Equal(0, second.Scanned);
            Assert.Equal(1, second.Cached);

            // The save now names a second version; a stale cache would collect it.
            File.WriteAllText(persistent, ParsekScenarioText(
                RecordingNode("rec-a", precHash: HashA1) + RecordingNode("rec-a2", precHash: HashA2)));
            File.SetLastWriteTimeUtc(persistent, DateTime.UtcNow.AddMinutes(5));
            string version = Write(dir, VersionName("rec-a2", HashA2, SidecarKind.Prec));

            var third = ParsekFileCollector.Run("test", null);
            Assert.Equal(1, third.Scanned);
            Assert.True(File.Exists(version), "expected kept: " + version);
        }

        [Fact]
        public void Collect_SaveWithoutParsekNode_IsAValidReferrerNamingNothing()
        {
            string dir = NewSaveDir("noparsek");
            Write(dir, "persistent.sfs", ParsekScenarioText(RecordingNode("rec-a")));
            Write(dir, "stock-only.sfs", "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ProgressTracking\n\t}\n}\n");
            AddMemoryRecording("rec-a");
            string version = Write(dir, VersionName("rec-z", HashB, SidecarKind.Ghost));

            var result = ParsekFileCollector.Run("test", null);

            Assert.True(result.Ran);
            Assert.Equal(2, result.Referrers);
            Assert.False(File.Exists(version), "expected collected: " + version);
        }

        [Fact]
        public void Collect_ZeroKnownIds_WithHeadsOnDisk_TouchesNothing()
        {
            string dir = NewSaveDir("zero");
            Write(dir, "persistent.sfs", ParsekScenarioText(""));
            string head = Write(dir, "Parsek/Recordings/rec-a.prec");
            string version = Write(dir, VersionName("rec-a", HashA1, SidecarKind.Prec));
            string rp = Write(dir, "Parsek/RewindPoints/rp-1.sfs");

            var result = ParsekFileCollector.Run("test", null);

            Assert.Equal("zero-known-ids", result.SkipReason);
            Assert.True(File.Exists(head), "expected kept: " + head);
            Assert.True(File.Exists(version), "expected kept: " + version);
            Assert.True(File.Exists(rp), "expected kept: " + rp);
            Assert.Contains(logLines, l => l.Contains("[WARN][FileCollector]") && l.Contains("REFUSING to delete"));
        }

        [Fact]
        public void Collect_EmptyMemory_ReferrerNamesTheHead_RunsWithoutWarn_AndKeepsTheHead()
        {
            // An F9 (a cold load) to a save made before any flight was recorded: memory holds
            // no recording, but persistent.sfs names the head on disk. Not lost tree state.
            string dir = NewSaveDir("empty-memory");
            Write(dir, "persistent.sfs", ParsekScenarioText(RecordingNode("r1")));
            string head = Write(dir, "Parsek/Recordings/r1.prec");

            var result = ParsekFileCollector.Run("test", null);

            Assert.True(result.Ran);
            Assert.Null(result.SkipReason);
            Assert.True(File.Exists(head), "expected kept: " + head);
            Assert.Equal(0, result.HeadsQuarantined);
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][FileCollector]"));
        }

        [Fact]
        public void Collect_MemoryOnlyRecording_KeepsItsVersionAndRewindSave()
        {
            // A recording committed after the last save is named only by memory.
            string dir = NewSaveDir("memory");
            Write(dir, "persistent.sfs", ParsekScenarioText(""));
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = "rec-new",
                VesselName = "New",
                PrecHash = HashM,
                RewindSaveFileName = "parsek_rw_new",
            });
            string version = Write(dir, VersionName("rec-new", HashM, SidecarKind.Prec));
            string rewindSave = Write(dir, "Parsek/Saves/parsek_rw_new.sfs");

            var result = ParsekFileCollector.Run("test", null);

            Assert.True(result.Ran);
            Assert.True(File.Exists(version), "expected kept: " + version);
            Assert.True(File.Exists(rewindSave), "expected kept: " + rewindSave);
        }

        [Fact]
        public void IsCollectableRewindSaveName_OnlyParsekRwSaves()
        {
            Assert.True(ParsekFileCollector.IsCollectableRewindSaveName("parsek_rw_abc123.sfs"));
            Assert.False(ParsekFileCollector.IsCollectableRewindSaveName("parsek_career_start.sfs"));
            Assert.False(ParsekFileCollector.IsCollectableRewindSaveName("parsek_rw_.sfs"));
            Assert.False(ParsekFileCollector.IsCollectableRewindSaveName("parsek_rw_abc.sfs.tmp"));
            Assert.False(ParsekFileCollector.IsCollectableRewindSaveName("quicksave.sfs"));
            Assert.False(ParsekFileCollector.IsCollectableRewindSaveName(null));
        }

        // ------------------------------------------------------------- when it runs

        [Theory]
        [InlineData((int)EarlyLoadKind.Cold, true, false, false, 0.0, double.NaN, null)]
        [InlineData((int)EarlyLoadKind.Cold, true, false, false, 5.0, 1.0, null)]
        [InlineData((int)EarlyLoadKind.InSession, false, false, false, 5.0, double.NaN, null)]
        [InlineData((int)EarlyLoadKind.InSession, false, false, false, 500.0, 1.0, "rate-limited")]
        [InlineData((int)EarlyLoadKind.InSession, false, false, false, 601.0, 1.0, null)]
        [InlineData((int)EarlyLoadKind.InSession, false, true, false, 5.0, double.NaN, "rewind")]
        [InlineData((int)EarlyLoadKind.PlainRewind, false, false, false, 5.0, double.NaN, "rewind")]
        [InlineData((int)EarlyLoadKind.ReFlyStart, false, false, false, 5.0, double.NaN, "refly-start")]
        [InlineData((int)EarlyLoadKind.DiscardReFly, false, false, false, 5.0, double.NaN, "discard-refly")]
        [InlineData((int)EarlyLoadKind.InSession, false, false, true, 5.0, double.NaN, "journal-open")]
        [InlineData((int)EarlyLoadKind.Cold, true, false, true, 5.0, double.NaN, "journal-open")]
        public void DecideSkipAtLoad_TruthTable(
            int kind, bool cold, bool rewinding, bool journalOpen,
            double now, double lastRun, string expected)
        {
            // xUnit theories must be public; EarlyLoadKind is internal, so the row carries its value.
            Assert.Equal(expected,
                ParsekFileCollector.DecideSkipAtLoad((EarlyLoadKind)kind, cold, rewinding, journalOpen, now, lastRun));
        }

        [Fact]
        public void RunAtLoad_InSession_RateLimitedTenMinutesAfterAnyRun()
        {
            string dir = NewSaveDir("rate");
            Write(dir, "persistent.sfs", ParsekScenarioText(RecordingNode("rec-a")));
            AddMemoryRecording("rec-a");
            double now = 100.0;
            ParsekFileCollector.RealtimeClockForTesting = () => now;

            Assert.True(ParsekFileCollector.RunAtLoad(EarlyLoadKind.Cold, coldBranch: true, scenario: null).Ran);
            now = 200.0;
            var limited = ParsekFileCollector.RunAtLoad(EarlyLoadKind.InSession, coldBranch: false, scenario: null);
            Assert.Equal("rate-limited", limited.SkipReason);
            now = 701.0;
            Assert.True(ParsekFileCollector.RunAtLoad(EarlyLoadKind.InSession, coldBranch: false, scenario: null).Ran);
            Assert.Contains(logLines, l => l.Contains("[FileCollector]") && l.Contains("Collection skipped reason=rate-limited"));
        }
    }
}
