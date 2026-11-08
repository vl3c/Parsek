using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the decision, formatting and currency units of
    /// <see cref="RecordingSidecarStore"/> that had no direct test: the loaded-snapshot hash
    /// choice, the snapshot load failure reason, the log formatters, the readable-mirror path
    /// and switch, the trajectory head byte check, the named-version currency refusals, the
    /// shrinkage diagnostic's thresholds, the node equivalence the ghost snapshot mode rests
    /// on, the ghost snapshot mode parse and expectation, the predicted segment count and the
    /// encoding label.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingSidecarStoreUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public RecordingSidecarStoreUnitTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-sidecar-unit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            RecordingPaths.SaveRootOverrideForTesting = null;
            ParsekSettings.CurrentOverrideForTesting = null;
            RecordingStore.ResetForTesting();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }

        private string WriteBytes(string name, byte[] bytes)
        {
            string path = Path.Combine(tempDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            return path;
        }

        // ---- HashOfLoadedSnapshot ----

        [Fact]
        public void LoadedSnapshotHash_KeepsTheCurrentHashUnlessAFreshLoadNeedsOne()
        {
            string path = WriteBytes("v.craft", new byte[] { 1, 2, 3 });
            string fileHash = SidecarContentHash.ComputeFile(path);

            // Not loaded, or no path: the current hash, whatever it is.
            Assert.Equal("cur", RecordingSidecarStore.HashOfLoadedSnapshot(
                RecordingSidecarStore.SnapshotSidecarLoadState.Missing, path, "cur", false));
            Assert.Equal("cur", RecordingSidecarStore.HashOfLoadedSnapshot(
                RecordingSidecarStore.SnapshotSidecarLoadState.Invalid, path, "cur", true));
            Assert.Null(RecordingSidecarStore.HashOfLoadedSnapshot(
                RecordingSidecarStore.SnapshotSidecarLoadState.Loaded, "", null, false));

            // Loaded through a resolved version with a known hash: that hash, unread.
            Assert.Equal("cur", RecordingSidecarStore.HashOfLoadedSnapshot(
                RecordingSidecarStore.SnapshotSidecarLoadState.Loaded, path, "cur", true));
            // Loaded, not resolved: the file's own hash, even over a known one.
            Assert.Equal(fileHash, RecordingSidecarStore.HashOfLoadedSnapshot(
                RecordingSidecarStore.SnapshotSidecarLoadState.Loaded, path, "cur", false));
            // Loaded and resolved but no hash yet: the file's hash.
            Assert.Equal(fileHash, RecordingSidecarStore.HashOfLoadedSnapshot(
                RecordingSidecarStore.SnapshotSidecarLoadState.Loaded, path, null, true));
        }

        // ---- DetermineSnapshotLoadFailureReason ----

        private static RecordingSidecarStore.SnapshotSidecarLoadSummary Summary(
            RecordingSidecarStore.SnapshotSidecarLoadState vessel,
            RecordingSidecarStore.SnapshotSidecarLoadState ghost)
        {
            return new RecordingSidecarStore.SnapshotSidecarLoadSummary { VesselState = vessel, GhostState = ghost };
        }

        [Fact]
        public void SnapshotLoadFailureReason_NamesTheFirstMissingSnapshotsBadState()
        {
            var invalid = RecordingSidecarStore.SnapshotSidecarLoadState.Invalid;
            var unsupported = RecordingSidecarStore.SnapshotSidecarLoadState.Unsupported;
            var missing = RecordingSidecarStore.SnapshotSidecarLoadState.Missing;

            Assert.Null(RecordingSidecarStore.DetermineSnapshotLoadFailureReason(Summary(invalid, invalid), null));

            var bare = new Recording();
            Assert.Equal("snapshot-vessel-invalid",
                RecordingSidecarStore.DetermineSnapshotLoadFailureReason(Summary(invalid, unsupported), bare));
            Assert.Equal("snapshot-vessel-unsupported",
                RecordingSidecarStore.DetermineSnapshotLoadFailureReason(Summary(unsupported, invalid), bare));
            Assert.Equal("snapshot-ghost-invalid",
                RecordingSidecarStore.DetermineSnapshotLoadFailureReason(Summary(missing, invalid), bare));
            Assert.Equal("snapshot-ghost-unsupported",
                RecordingSidecarStore.DetermineSnapshotLoadFailureReason(Summary(missing, unsupported), bare));
            // A missing sidecar is not a failure.
            Assert.Null(RecordingSidecarStore.DetermineSnapshotLoadFailureReason(Summary(missing, missing), bare));

            // A snapshot already in memory masks its own sidecar's state.
            var withVessel = new Recording { VesselSnapshot = new ConfigNode("VESSEL") };
            Assert.Equal("snapshot-ghost-invalid",
                RecordingSidecarStore.DetermineSnapshotLoadFailureReason(Summary(invalid, invalid), withVessel));
            var withBoth = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
            };
            Assert.Null(RecordingSidecarStore.DetermineSnapshotLoadFailureReason(Summary(invalid, unsupported), withBoth));
        }

        // ---- log formatters / readable mirrors ----

        [Fact]
        public void LogFormatters_PlaceholderForNullAndTypeColonMessage()
        {
            Assert.Equal("<null>", RecordingSidecarStore.FormatPathForSidecarLog(null));
            Assert.Equal("<null>", RecordingSidecarStore.FormatPathForSidecarLog(""));
            Assert.Equal("a/b.prec", RecordingSidecarStore.FormatPathForSidecarLog("a/b.prec"));

            Assert.Equal("<none>", RecordingSidecarStore.FormatExceptionForSidecarLog(null));
            Assert.Equal("IOException:disk gone",
                RecordingSidecarStore.FormatExceptionForSidecarLog(new IOException("disk gone")));
        }

        [Fact]
        public void SidecarContext_NamesIdEpochAndMode()
        {
            string folder = RecordingSidecarStore.SafeSaveFolderForSidecarLog();
            Assert.False(string.IsNullOrEmpty(folder));

            var rec = new Recording
            {
                RecordingId = "rec-3",
                SidecarEpoch = 7,
                GhostSnapshotMode = GhostSnapshotMode.Separate,
            };
            Assert.Equal(
                "id=rec-3 saveFolder='" + folder + "' epoch=7 ghostSnapshotMode=Separate",
                RecordingSidecarStore.FormatSidecarContext(rec));
            // The explicit-mode overload reports the mode it is given, not the recording's.
            Assert.Equal(
                "id=rec-3 saveFolder='" + folder + "' epoch=7 ghostSnapshotMode=AliasVessel",
                RecordingSidecarStore.FormatSidecarContext(rec, GhostSnapshotMode.AliasVessel));
            Assert.Equal(
                "id=<null> saveFolder='" + folder + "' epoch=0 ghostSnapshotMode=Unspecified",
                RecordingSidecarStore.FormatSidecarContext(null));
            Assert.Equal(
                "id=<null> saveFolder='" + folder + "' epoch=0 ghostSnapshotMode=Unspecified",
                RecordingSidecarStore.FormatSidecarContext(new Recording { RecordingId = "" }));
        }

        [Fact]
        public void ReadableMirror_PathAppendsTxtAndTheSwitchPrefersTheOverride()
        {
            Assert.Null(RecordingSidecarStore.GetReadableMirrorPath(null));
            Assert.Null(RecordingSidecarStore.GetReadableMirrorPath(""));
            Assert.Equal("x/r.prec.txt", RecordingSidecarStore.GetReadableMirrorPath("x/r.prec"));

            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = true;
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { writeReadableSidecarMirrors = false };
            Assert.True(RecordingSidecarStore.ShouldWriteReadableSidecarMirrors());
            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { writeReadableSidecarMirrors = true };
            Assert.False(RecordingSidecarStore.ShouldWriteReadableSidecarMirrors());

            // No override: the setting decides.
            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            Assert.True(RecordingSidecarStore.ShouldWriteReadableSidecarMirrors());
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { writeReadableSidecarMirrors = false };
            Assert.False(RecordingSidecarStore.ShouldWriteReadableSidecarMirrors());
        }

        // ---- currency checks ----

        [Fact]
        public void TrajectoryHead_HoldsTheNamedBytesOrSaysWhy()
        {
            string reason;
            Assert.True(RecordingSidecarStore.TrajectoryHeadHoldsNamedBytes(null, out reason));
            Assert.Null(reason);
            Assert.True(RecordingSidecarStore.TrajectoryHeadHoldsNamedBytes(
                new Recording { RecordingId = "r1" }, out reason));
            // An invalid id is not checked at all.
            Assert.True(RecordingSidecarStore.TrajectoryHeadHoldsNamedBytes(
                new Recording { RecordingId = "../r1", PrecHash = "h" }, out reason));
            Assert.Null(reason);

            RecordingPaths.SaveRootOverrideForTesting = tempDir;
            var rec = new Recording { RecordingId = "r1", PrecHash = "h" };
            Assert.False(RecordingSidecarStore.TrajectoryHeadHoldsNamedBytes(rec, out reason));
            Assert.Equal("trajectory-missing", reason);

            string head = WriteBytes(RecordingPaths.BuildTrajectoryRelativePath("r1"), new byte[] { 9, 8, 7 });
            Assert.False(RecordingSidecarStore.TrajectoryHeadHoldsNamedBytes(rec, out reason));
            Assert.Equal("head-holds-other-bytes", reason);

            rec.PrecHash = SidecarContentHash.ComputeFile(head);
            Assert.True(RecordingSidecarStore.TrajectoryHeadHoldsNamedBytes(rec, out reason));
            Assert.Null(reason);
        }

        [Fact]
        public void NamedVersionCurrency_RefusesWithoutARecordingAHashOrAValidId()
        {
            string reason;
            Assert.False(RecordingSidecarStore.AreRecordingFilesCurrentThroughNamedVersions(null, out reason));
            Assert.Equal("recording-null", reason);
            Assert.False(RecordingSidecarStore.AreRecordingFilesCurrentThroughNamedVersions(
                new Recording { RecordingId = "r1" }, out reason));
            Assert.Equal("no-named-prec-hash", reason);
            Assert.False(RecordingSidecarStore.AreRecordingFilesCurrentThroughNamedVersions(
                new Recording { RecordingId = "a/b", PrecHash = "h" }, out reason));
            Assert.Equal("recording-id-invalid", reason);
        }

        [Fact]
        public void LazyVesselSnapshotPath_WithoutAHashIsTheHead()
        {
            Assert.Equal("head", RecordingSidecarStore.ResolveLazyVesselSnapshotPath(null, "head"));
            Assert.Equal("head", RecordingSidecarStore.ResolveLazyVesselSnapshotPath(new Recording(), "head"));
            Assert.Null(RecordingSidecarStore.ResolveLazyVesselSnapshotPath(new Recording { VesselSnapshotHash = "" }, null));
        }

        // ---- TryBuildTrajectoryShrinkageWarningForSave ----

        private static TrajectoryPoint Pt(double ut) => new TrajectoryPoint
        {
            ut = ut, latitude = -0.05, longitude = -74.5, altitude = 1000.0, bodyName = "Kerbin",
        };

        private string WriteRicherSidecar(string id, int points, int epoch)
        {
            var original = new Recording { RecordingId = id, SidecarEpoch = epoch };
            for (int i = 0; i < points; i++)
                original.Points.Add(Pt(100.0 + i));
            string path = Path.Combine(tempDir, id + ".prec");
            RecordingSidecarStore.WriteTrajectorySidecar(path, original, epoch);
            return path;
        }

        private static Recording SectionAuthoritative(string id, int epoch, params double[] uts)
        {
            var rec = new Recording { RecordingId = id, SidecarEpoch = epoch };
            var frames = new List<TrajectoryPoint>();
            foreach (double ut in uts)
            {
                rec.Points.Add(Pt(ut));
                frames.Add(Pt(ut));
            }
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = uts[0],
                endUT = uts[uts.Length - 1],
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
            });
            return rec;
        }

        [Fact]
        public void ShrinkageWarning_NeedsAFileANearEmptySectionPayloadAndTenExistingPoints()
        {
            string warning;
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(
                null, "x", true, out warning));
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(
                new Recording(), Path.Combine(tempDir, "absent.prec"), true, out warning));
            Assert.Null(warning);

            string rich = WriteRicherSidecar("shrink-a", 10, 4);

            // A flat (not section-authoritative) incoming trajectory never warns.
            var flat = new Recording { RecordingId = "shrink-a", SidecarEpoch = 4 };
            flat.Points.Add(Pt(100.0));
            Assert.False(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(flat));
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(flat, rich, true, out warning));

            // Two section points is not near-empty.
            Recording two = SectionAuthoritative("shrink-a", 4, 100.0, 101.0);
            Assert.True(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(two));
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(two, rich, true, out warning));

            // One section point over ten existing points warns; the next epoch follows incrementEpoch.
            Recording one = SectionAuthoritative("shrink-a", 4, 100.0);
            Assert.True(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(one, rich, true, out warning));
            Assert.Contains("recId=shrink-a currentEpoch=4 nextEpoch=5 existingProbeRecordingId=shrink-a", warning);
            Assert.Contains("existingProbeEpoch=4", warning);
            Assert.Contains("existingProbeEncoding=BinaryV0", warning);
            Assert.Contains("existingPoints=10 incomingPoints=1 existingSections=0 incomingSections=1", warning);
            Assert.Contains("sectionAuthoritative=True path='" + rich + "'", warning);
            Assert.EndsWith("- continuing normal save (diagnostic only)", warning);

            Assert.True(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(one, rich, false, out warning));
            Assert.Contains("currentEpoch=4 nextEpoch=4 ", warning);
        }

        [Fact]
        public void ShrinkageWarning_NineExistingPointsIsBelowTheThreshold()
        {
            string rich = WriteRicherSidecar("shrink-b", 9, 2);
            Recording one = SectionAuthoritative("shrink-b", 2, 100.0);
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(
                one, rich, true, out string warning));
            Assert.Null(warning);
        }

        // ---- ConfigNodesEquivalent / ghost snapshot mode ----

        [Fact]
        public void NodesEquivalent_OrderedOrdinalCompareOfNamesValuesAndChildren()
        {
            ConfigNode Make(string v)
            {
                var n = new ConfigNode("VESSEL");
                n.AddValue("name", "Probe");
                n.AddValue("sit", v);
                var part = n.AddNode("PART");
                part.AddValue("name", "probeCore");
                return n;
            }

            var a = Make("ORBITING");
            Assert.True(RecordingSidecarStore.ConfigNodesEquivalent(a, a));
            Assert.True(RecordingSidecarStore.ConfigNodesEquivalent(null, null));
            Assert.False(RecordingSidecarStore.ConfigNodesEquivalent(a, null));
            Assert.False(RecordingSidecarStore.ConfigNodesEquivalent(null, a));
            Assert.True(RecordingSidecarStore.ConfigNodesEquivalent(a, Make("ORBITING")));
            Assert.False(RecordingSidecarStore.ConfigNodesEquivalent(a, Make("orbiting")));

            var renamed = Make("ORBITING");
            renamed.name = "vessel";
            Assert.False(RecordingSidecarStore.ConfigNodesEquivalent(a, renamed));

            var extraValue = Make("ORBITING");
            extraValue.AddValue("lat", "0");
            Assert.False(RecordingSidecarStore.ConfigNodesEquivalent(a, extraValue));

            var extraNode = Make("ORBITING");
            extraNode.AddNode("PART");
            Assert.False(RecordingSidecarStore.ConfigNodesEquivalent(a, extraNode));

            var childDiffers = Make("ORBITING");
            childDiffers.GetNode("PART").SetValue("name", "probeCoreHex");
            Assert.False(RecordingSidecarStore.ConfigNodesEquivalent(a, childDiffers));

            // Order matters: the same values in another order are not equivalent.
            var x = new ConfigNode("N"); x.AddValue("a", "1"); x.AddValue("b", "2");
            var y = new ConfigNode("N"); y.AddValue("b", "2"); y.AddValue("a", "1");
            Assert.False(RecordingSidecarStore.ConfigNodesEquivalent(x, y));
        }

        [Fact]
        public void ExpectedGhostSnapshotMode_StampedElseDetermined()
        {
            Assert.Equal(GhostSnapshotMode.Unspecified, RecordingSidecarStore.GetExpectedGhostSnapshotMode(null));

            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("name", "Probe");
            var alias = new Recording { VesselSnapshot = vessel, GhostVisualSnapshot = vessel.CreateCopy() };
            Assert.Equal(GhostSnapshotMode.AliasVessel, RecordingSidecarStore.GetExpectedGhostSnapshotMode(alias));

            // A stamped mode wins over what the snapshots would determine.
            alias.GhostSnapshotMode = GhostSnapshotMode.Separate;
            Assert.Equal(GhostSnapshotMode.Separate, RecordingSidecarStore.GetExpectedGhostSnapshotMode(alias));

            Assert.Equal(GhostSnapshotMode.Unspecified, RecordingSidecarStore.GetExpectedGhostSnapshotMode(new Recording()));
        }

        [Fact]
        public void ParseGhostSnapshotMode_ExactNamesOnlyAndWarnsOnTheRest()
        {
            Assert.Equal(GhostSnapshotMode.Unspecified, RecordingSidecarStore.ParseGhostSnapshotMode(null));
            Assert.Equal(GhostSnapshotMode.Unspecified, RecordingSidecarStore.ParseGhostSnapshotMode(""));
            Assert.Empty(logLines);

            Assert.Equal(GhostSnapshotMode.Separate, RecordingSidecarStore.ParseGhostSnapshotMode("Separate"));
            Assert.Equal(GhostSnapshotMode.AliasVessel, RecordingSidecarStore.ParseGhostSnapshotMode("AliasVessel"));
            // Enum.TryParse also reads a defined number.
            Assert.Equal(GhostSnapshotMode.AliasVessel, RecordingSidecarStore.ParseGhostSnapshotMode("2"));
            Assert.Empty(logLines);

            Assert.Equal(GhostSnapshotMode.Unspecified, RecordingSidecarStore.ParseGhostSnapshotMode("separate"));
            Assert.Contains(logLines, l => l.Contains("[WARN][RecordingStore]")
                && l.Contains("LoadRecordingFiles: invalid ghostSnapshotMode 'separate', treating as Unspecified"));

            logLines.Clear();
            Assert.Equal(GhostSnapshotMode.Unspecified, RecordingSidecarStore.ParseGhostSnapshotMode("9"));
            Assert.Contains(logLines, l => l.Contains("invalid ghostSnapshotMode '9'"));

            logLines.Clear();
            RecordingStore.SuppressLogging = true;
            Assert.Equal(GhostSnapshotMode.Unspecified, RecordingSidecarStore.ParseGhostSnapshotMode("bogus"));
            Assert.Empty(logLines);
        }

        // ---- CountPredictedOrbitSegments / encoding label ----

        [Fact]
        public void PredictedSegments_CountsTopLevelAndCheckpointSeparately()
        {
            int checkpoints;
            Assert.Equal(0, RecordingSidecarStore.CountPredictedOrbitSegments(null, out checkpoints));
            Assert.Equal(0, checkpoints);

            var rec = new Recording();
            rec.OrbitSegments.Add(new OrbitSegment { isPredicted = true });
            rec.OrbitSegments.Add(new OrbitSegment { isPredicted = false });
            rec.OrbitSegments.Add(new OrbitSegment { isPredicted = true });
            rec.TrackSections.Add(new TrackSection { checkpoints = null });
            rec.TrackSections.Add(new TrackSection
            {
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment { isPredicted = true },
                    new OrbitSegment { isPredicted = false },
                },
            });
            rec.TrackSections.Add(new TrackSection
            {
                checkpoints = new List<OrbitSegment> { new OrbitSegment { isPredicted = true } },
            });

            Assert.Equal(2, RecordingSidecarStore.CountPredictedOrbitSegments(rec, out checkpoints));
            Assert.Equal(2, checkpoints);

            var noSections = new Recording { TrackSections = null };
            noSections.OrbitSegments.Add(new OrbitSegment { isPredicted = true });
            Assert.Equal(1, RecordingSidecarStore.CountPredictedOrbitSegments(noSections, out checkpoints));
            Assert.Equal(0, checkpoints);
        }

        [Fact]
        public void EncodingLabel_IsBinaryV0ForEveryVersion()
        {
            Assert.Equal("BinaryV0", RecordingSidecarStore.GetTrajectorySidecarEncodingLabel(0));
            Assert.Equal("BinaryV0", RecordingSidecarStore.GetTrajectorySidecarEncodingLabel(
                RecordingStore.CurrentRecordingFormatVersion));
            Assert.Equal("BinaryV0", RecordingSidecarStore.GetTrajectorySidecarEncodingLabel(-1));
        }
    }
}
