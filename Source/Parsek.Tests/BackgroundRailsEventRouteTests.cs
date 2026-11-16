using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;
using static Parsek.Tests.BackgroundRecorderCharacterizationSupport;

namespace Parsek.Tests
{
    /// <summary>
    /// Decision cover for the background recorder's go-off-rails and SOI-change handlers
    /// (test gap audit C4): the pure route both handlers take, the vessel-free head that
    /// resolves the recording and applies the destroyed-recording guard, and the vessel-free
    /// on-rails halves. The public go-off-rails handler is also driven end to end with a
    /// packed alive vessel stub, which stops before any native member; the unpacked half
    /// (InitializeLoadedState, the finalization-cache refresh, the boundary sample) reads the
    /// vessel's transform and stays covered by InitializeLoadedStateCharacterizationTests and
    /// the in-game recorder lanes.
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundRailsEventRouteTests : IDisposable
    {
        private const uint Pid = 5150u;
        private const string RecId = "rec_bg_rails";

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;
        private readonly Planetarium savedPlanetarium;

        public BackgroundRailsEventRouteTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            savedPlanetarium = InstallPlanetarium(400.0);
        }

        public void Dispose()
        {
            Planetarium.fetch = savedPlanetarium;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private static RecordingTree MakeTree(bool mapped = true)
        {
            var tree = new RecordingTree
            {
                Id = "tree_bg_rails",
                TreeName = "BG Rails",
                RootRecordingId = "rec_active",
                ActiveRecordingId = "rec_active",
            };
            tree.Recordings["rec_active"] = new Recording { RecordingId = "rec_active", VesselName = "Lead" };
            tree.Recordings[RecId] = new Recording
            {
                RecordingId = RecId,
                VesselName = "Satellite",
                VesselPersistentId = Pid,
            };
            if (mapped)
                tree.BackgroundMap[Pid] = RecId;
            return tree;
        }

        private static OrbitSegment OpenSegment(double startUT)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                inclination = 0.1,
                eccentricity = 0.001,
                semiMajorAxis = 701000.0,
                longitudeOfAscendingNode = 10.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = startUT,
                bodyName = "Kerbin",
            };
        }

        // ---- the pure route ----

        [Theory]
        // mapped, destroyed, onRails, loaded -> route
        [InlineData(false, false, false, false, (int)BackgroundRecorder.BackgroundRailsEventRoute.Untracked)]
        [InlineData(false, true, true, true, (int)BackgroundRecorder.BackgroundRailsEventRoute.Untracked)]
        [InlineData(true, true, false, false, (int)BackgroundRecorder.BackgroundRailsEventRoute.DestroyedSkip)]
        [InlineData(true, true, true, true, (int)BackgroundRecorder.BackgroundRailsEventRoute.DestroyedSkip)]
        [InlineData(true, false, true, false, (int)BackgroundRecorder.BackgroundRailsEventRoute.OnRails)]
        [InlineData(true, false, true, true, (int)BackgroundRecorder.BackgroundRailsEventRoute.OnRails)]
        [InlineData(true, false, false, true, (int)BackgroundRecorder.BackgroundRailsEventRoute.Loaded)]
        [InlineData(true, false, false, false, (int)BackgroundRecorder.BackgroundRailsEventRoute.NoState)]
        public void Classify_AppliesTheGuardOrder(bool mapped, bool destroyed, bool onRails, bool loaded,
            int expected)
        {
            // int, not the enum: the route type is internal and an xUnit theory is public.
            Assert.Equal((BackgroundRecorder.BackgroundRailsEventRoute)expected,
                BackgroundRecorder.ClassifyBackgroundRailsEvent(mapped, destroyed, onRails, loaded));
        }

        [Theory]
        [InlineData((int)BackgroundRecorder.BackgroundRailsEventRoute.Untracked, false)]
        [InlineData((int)BackgroundRecorder.BackgroundRailsEventRoute.DestroyedSkip, false)]
        [InlineData((int)BackgroundRecorder.BackgroundRailsEventRoute.OnRails, true)]
        [InlineData((int)BackgroundRecorder.BackgroundRailsEventRoute.Loaded, true)]
        [InlineData((int)BackgroundRecorder.BackgroundRailsEventRoute.NoState, true)]
        public void Handled_IsEverythingPastTheLookupAndTheDestroyedGuard(
            int route, bool expected)
        {
            Assert.Equal(expected, BackgroundRecorder.IsBackgroundRailsEventHandled(
                (BackgroundRecorder.BackgroundRailsEventRoute)route));
        }

        // ---- the vessel-free head ----

        [Fact]
        public void Route_UnmappedVessel_IsUntrackedAndSilent()
        {
            var bg = new BackgroundRecorder(MakeTree(mapped: false));
            string recId;
            var route = bg.RouteBackgroundRailsEvent(Pid, "Handler", "key", out recId);
            Assert.Equal(BackgroundRecorder.BackgroundRailsEventRoute.Untracked, route);
            Assert.Null(recId);
            Assert.DoesNotContain(logLines, l => l.Contains("skipping destroyed recording"));
        }

        [Fact]
        public void Route_MappedToNullRecordingId_IsUntracked()
        {
            RecordingTree tree = MakeTree(mapped: false);
            tree.BackgroundMap[Pid] = null;
            var bg = new BackgroundRecorder(tree);
            string recId;
            Assert.Equal(BackgroundRecorder.BackgroundRailsEventRoute.Untracked,
                bg.RouteBackgroundRailsEvent(Pid, "Handler", "key", out recId));
        }

        [Fact]
        public void Route_FreshOnRailsVessel_IsOnRails()
        {
            var bg = new BackgroundRecorder(MakeTree());
            string recId;
            Assert.Equal(BackgroundRecorder.BackgroundRailsEventRoute.OnRails,
                bg.RouteBackgroundRailsEvent(Pid, "Handler", "key", out recId));
            Assert.Equal(RecId, recId);
        }

        [Fact]
        public void Route_OnRailsWinsOverALoadedStateOfTheSameVessel()
        {
            var bg = new BackgroundRecorder(MakeTree());
            bg.InjectLoadedStateForTesting(Pid, RecId);
            Assert.True(bg.HasOnRailsState(Pid));
            Assert.True(bg.HasLoadedState(Pid));
            string recId;
            Assert.Equal(BackgroundRecorder.BackgroundRailsEventRoute.OnRails,
                bg.RouteBackgroundRailsEvent(Pid, "Handler", "key", out recId));
        }

        [Fact]
        public void Route_LoadedOnly_IsLoaded_AndNeitherIsNoState()
        {
            var bg = new BackgroundRecorder(MakeTree());
            Assert.True(bg.EndOnRailsStateForGoOffRails(Pid, 300.0));
            string recId;
            Assert.Equal(BackgroundRecorder.BackgroundRailsEventRoute.NoState,
                bg.RouteBackgroundRailsEvent(Pid, "Handler", "key", out recId));

            bg.InjectLoadedStateForTesting(Pid, RecId);
            Assert.Equal(BackgroundRecorder.BackgroundRailsEventRoute.Loaded,
                bg.RouteBackgroundRailsEvent(Pid, "Handler", "key", out recId));
        }

        [Fact]
        public void Route_DestroyedRecording_SkipsWithTheHandlersOwnLine()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            tree.Recordings[RecId].VesselDestroyed = true;
            string recId;
            var route = bg.RouteBackgroundRailsEvent(
                Pid, "OnBackgroundVesselGoOffRails", "go_off_rails", out recId);
            Assert.Equal(BackgroundRecorder.BackgroundRailsEventRoute.DestroyedSkip, route);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("OnBackgroundVesselGoOffRails: skipping destroyed recording pid=5150 rec=rec_bg_rails"));
        }

        // ---- the vessel-free on-rails halves ----

        [Fact]
        public void EndOnRails_ClosesTheOpenSegmentAsACheckpointAndDropsTheState()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.InjectOpenOrbitSegmentForTesting(Pid, OpenSegment(200.0));

            Assert.True(bg.EndOnRailsStateForGoOffRails(Pid, 350.0));

            Assert.False(bg.HasOnRailsState(Pid));
            Recording rec = tree.Recordings[RecId];
            OrbitSegment closed = Assert.Single(rec.OrbitSegments);
            Assert.Equal(200.0, closed.startUT);
            Assert.Equal(350.0, closed.endUT);
            TrackSection checkpoint = Assert.Single(rec.TrackSections);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, checkpoint.referenceFrame);
            Assert.Equal(350.0, rec.ExplicitEndUT);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Mode transition on-rails") && l.Contains("pid=5150"));
        }

        [Fact]
        public void EndOnRails_WithoutAnOpenSegment_DropsTheStateAndWritesNothing()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);

            Assert.True(bg.EndOnRailsStateForGoOffRails(Pid, 350.0));

            Assert.False(bg.HasOnRailsState(Pid));
            Assert.Empty(tree.Recordings[RecId].OrbitSegments);
            Assert.Empty(tree.Recordings[RecId].TrackSections);
        }

        [Fact]
        public void EndOnRails_NoState_IsFalseAndSilent()
        {
            var bg = new BackgroundRecorder(MakeTree(mapped: false));
            Assert.False(bg.EndOnRailsStateForGoOffRails(Pid, 350.0));
            Assert.DoesNotContain(logLines, l => l.Contains("Mode transition on-rails"));
        }

        [Fact]
        public void SoiClose_ClosesTheOldBodysSegmentButKeepsTheState()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.InjectOpenOrbitSegmentForTesting(Pid, OpenSegment(200.0));

            Assert.True(bg.CloseOnRailsSegmentForSoiChangeForTesting(Pid, 360.0));

            Assert.True(bg.HasOnRailsState(Pid));
            Assert.False(bg.GetOnRailsHasOpenSegment(Pid));
            Assert.Equal(360.0, Assert.Single(tree.Recordings[RecId].OrbitSegments).endUT);
            Assert.False(bg.CloseOnRailsSegmentForSoiChangeForTesting(Pid + 1, 360.0));
        }

        // ---- the public handlers over a vessel stub ----

        [Fact]
        public void GoOffRails_PackedVessel_ClosesAtPlanetariumUT_AndInitializesNothing()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.InjectOpenOrbitSegmentForTesting(Pid, OpenSegment(200.0));

            bg.OnBackgroundVesselGoOffRails(Vessel(Pid, alive: true, body: null, packed: true));

            Assert.False(bg.HasOnRailsState(Pid));
            Assert.False(bg.HasLoadedState(Pid));
            Assert.Equal(400.0, Assert.Single(tree.Recordings[RecId].OrbitSegments).endUT);
            Assert.Equal(400.0, tree.Recordings[RecId].ExplicitEndUT);
        }

        [Fact]
        public void GoOffRails_DestroyedRecording_LeavesTheSealedRecordingUntouched()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.InjectOpenOrbitSegmentForTesting(Pid, OpenSegment(200.0));
            Recording rec = tree.Recordings[RecId];
            rec.VesselDestroyed = true;
            double endBefore = rec.ExplicitEndUT;

            bg.OnBackgroundVesselGoOffRails(Vessel(Pid, alive: true, body: null, packed: false));

            // No close, no state change, no loaded init (an unpacked vessel would have
            // reached InitializeLoadedState and thrown on the stub's transform).
            Assert.True(bg.HasOnRailsState(Pid));
            Assert.True(bg.GetOnRailsHasOpenSegment(Pid));
            Assert.False(bg.HasLoadedState(Pid));
            Assert.Empty(rec.OrbitSegments);
            Assert.Empty(rec.TrackSections);
            Assert.Equal(endBefore, rec.ExplicitEndUT);
            Assert.Contains(logLines, l => l.Contains(
                "OnBackgroundVesselGoOffRails: skipping destroyed recording pid=5150 rec=rec_bg_rails"));
        }

        [Fact]
        public void GoOffRails_UntrackedOrDeadVessel_DoesNothing()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.InjectOpenOrbitSegmentForTesting(Pid, OpenSegment(200.0));

            bg.OnBackgroundVesselGoOffRails(Vessel(Pid + 7, alive: true, body: null, packed: false));
            bg.OnBackgroundVesselGoOffRails(Vessel(Pid, alive: false, body: null, packed: true));
            bg.OnBackgroundVesselGoOffRails(null);

            Assert.True(bg.GetOnRailsHasOpenSegment(Pid));
            Assert.Empty(tree.Recordings[RecId].OrbitSegments);
        }

        [Fact]
        public void SoiChange_DestroyedRecording_ClosesNoSegment()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.InjectOpenOrbitSegmentForTesting(Pid, OpenSegment(200.0));
            tree.Recordings[RecId].VesselDestroyed = true;

            bg.OnBackgroundVesselSOIChanged(Vessel(Pid, alive: true, body: null), null);

            Assert.True(bg.GetOnRailsHasOpenSegment(Pid));
            Assert.Empty(tree.Recordings[RecId].OrbitSegments);
            Assert.Contains(logLines, l => l.Contains(
                "OnBackgroundVesselSOIChanged: skipping destroyed recording pid=5150 rec=rec_bg_rails"));
        }

        [Fact]
        public void SoiChange_LoadedVessel_ArmsTheSectionSplitSample()
        {
            var bg = new BackgroundRecorder(MakeTree());
            bg.EndOnRailsStateForGoOffRails(Pid, 300.0);
            bg.InjectLoadedStateForTesting(Pid, RecId);

            bg.OnBackgroundVesselSOIChanged(Vessel(Pid, alive: true, body: null), null);

            Assert.True(bg.GetLoadedPhysicsSoiSamplePendingForTesting(Pid));
            Assert.Contains(logLines, l => l.Contains("SOI changed off rails for background vessel: pid=5150")
                && l.Contains("at UT=400.00"));
        }

        [Fact]
        public void SoiChange_MappedVesselWithNoState_ArmsNothing()
        {
            var bg = new BackgroundRecorder(MakeTree());
            bg.EndOnRailsStateForGoOffRails(Pid, 300.0);

            bg.OnBackgroundVesselSOIChanged(Vessel(Pid, alive: true, body: null), null);

            Assert.False(bg.HasLoadedState(Pid));
            Assert.DoesNotContain(logLines, l => l.Contains("SOI changed off rails"));
            Assert.DoesNotContain(logLines, l => l.Contains("SOI change for background vessel"));
        }
    }
}
