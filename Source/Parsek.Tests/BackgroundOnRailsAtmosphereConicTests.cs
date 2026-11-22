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
    /// A background vessel that goes on rails (packs) below the atmosphere top. Stock moves a
    /// packed vessel along its drag-free conic (no atmosphere on rails) until it deletes it
    /// (pressure above the packed-vessel threshold, or under the terrain), unpacks it, or the
    /// player takes it over. Before this fix the on-rails state skipped the segment, so the
    /// packed span had no trajectory while the recording's end time kept moving: the ghost
    /// held still, then exploded. Pins:
    /// <list type="bullet">
    /// <item>the on-rails span opens the conic and closes it as an OrbitalCheckpoint section
    /// labelled Atmospheric (one label for the whole span, never toggling);</item>
    /// <item>the predicted tail the applier appends starts at the conic's end (no overlap,
    /// no gap);</item>
    /// <item>the span adds no optimizer split candidate, under the version-1 and the current
    /// split rules, including across many warp checkpoints and a lagged Exo pack;</item>
    /// <item>the live-vessel finalization cache is kept, and an unseen deletion does not draw
    /// the conic under the surface.</item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundOnRailsAtmosphereConicTests : IDisposable
    {
        private const uint Pid = 7420u;
        private const string RecId = "rec_bg_atmo_stage";
        private const double PackUT = 1000.0;

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public BackgroundOnRailsAtmosphereConicTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private static RecordingTree MakeTree()
        {
            var tree = new RecordingTree
            {
                Id = "tree_bg_atmo",
                TreeName = "Kerbal X",
                RootRecordingId = "rec_upper_stage",
                ActiveRecordingId = "rec_upper_stage",
            };
            tree.Recordings["rec_upper_stage"] = new Recording
            {
                RecordingId = "rec_upper_stage",
                VesselName = "Kerbal X",
            };
            tree.Recordings[RecId] = new Recording
            {
                RecordingId = RecId,
                VesselName = "Kerbal X Booster",
                VesselPersistentId = Pid,
            };
            tree.BackgroundMap[Pid] = RecId;
            return tree;
        }

        // A suborbital arc on Kerbin packed at ~45 km: apoapsis ~ 90 km, periapsis deep below
        // the surface.
        private static OrbitSegment SuborbitalArc(double startUT)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                inclination = 0.1,
                eccentricity = 0.82,
                semiMajorAxis = 380000.0,
                longitudeOfAscendingNode = 12.0,
                argumentOfPeriapsis = 80.0,
                meanAnomalyAtEpoch = 2.9,
                epoch = startUT,
                bodyName = "Kerbin",
            };
        }

        private static TrajectoryPoint Point(double ut, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = UnityEngine.Quaternion.identity,
                velocity = new UnityEngine.Vector3(0f, -300f, 0f),
            };
        }

        private static TrackSection LoadedSection(
            SegmentEnvironment env, double startUT, double endUT, double alt)
        {
            var frames = new List<TrajectoryPoint>();
            for (double ut = startUT; ut <= endUT + 1e-9; ut += (endUT - startUT) / 4.0)
                frames.Add(Point(ut, alt));
            return new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = startUT,
                endUT = endUT,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                minAltitude = (float)alt,
                maxAltitude = (float)alt,
            };
        }

        // ---------------------------------------------------------------- pure decisions

        [Theory]
        [InlineData(false, false, true, true, (int)BackgroundRecorder.OnRailsInitialPayload.AtmosphereOrbit)]
        [InlineData(false, false, false, true, (int)BackgroundRecorder.OnRailsInitialPayload.Orbit)]
        [InlineData(true, false, true, true, (int)BackgroundRecorder.OnRailsInitialPayload.Landed)]
        [InlineData(false, true, true, true, (int)BackgroundRecorder.OnRailsInitialPayload.NoOrbit)]
        [InlineData(false, false, true, false, (int)BackgroundRecorder.OnRailsInitialPayload.NoOrbit)]
        [InlineData(false, false, false, false, (int)BackgroundRecorder.OnRailsInitialPayload.NoOrbit)]
        public void ResolveOnRailsInitialPayload_OpensTheConicBelowTheAtmosphereTop(
            bool isLanded, bool isPrelaunch, bool insideAtmosphere, bool hasOrbit,
            int expectedPayload)
        {
            var expected = (BackgroundRecorder.OnRailsInitialPayload)expectedPayload;
            Assert.Equal(expected, BackgroundRecorder.ResolveOnRailsInitialPayload(
                isLanded, isPrelaunch, insideAtmosphere, hasOrbit));
        }

        [Fact]
        public void ResolveOnRailsCheckpointEnvironment_AtmosphereSpanIsAtmospheric()
        {
            Assert.Equal(SegmentEnvironment.Atmospheric,
                BackgroundRecorder.ResolveOnRailsCheckpointEnvironment(true));
            Assert.Equal(SegmentEnvironment.ExoBallistic,
                BackgroundRecorder.ResolveOnRailsCheckpointEnvironment(false));
        }

        [Theory]
        // Close before the predicted ending: kept.
        [InlineData(1200.0, 1000.0, TerminalState.Destroyed, 1400.0, 1200.0)]
        // Close after the predicted impact (a deletion Parsek did not see): clamped.
        [InlineData(1600.0, 1000.0, TerminalState.Destroyed, 1400.0, 1400.0)]
        [InlineData(1600.0, 1000.0, TerminalState.Landed, 1400.0, 1400.0)]
        [InlineData(1600.0, 1000.0, TerminalState.Splashed, 1400.0, 1400.0)]
        // An orbit ending is no ground contact: kept.
        [InlineData(1600.0, 1000.0, TerminalState.Orbiting, 1400.0, 1600.0)]
        [InlineData(1600.0, 1000.0, TerminalState.SubOrbital, 1400.0, 1600.0)]
        // A stale ending before the span opened says nothing about this span: kept.
        [InlineData(1600.0, 1000.0, TerminalState.Destroyed, 900.0, 1600.0)]
        [InlineData(1600.0, 1000.0, TerminalState.Destroyed, double.NaN, 1600.0)]
        public void ClampAtmosphereConicCloseUT_EndsAtTheGroundContactParsekAlreadyPredicted(
            double closeUT, double startUT, TerminalState terminal, double terminalUT, double expected)
        {
            Assert.Equal(expected, BackgroundRecorder.ClampAtmosphereConicCloseUT(
                closeUT, startUT, terminal, terminalUT));
        }

        [Fact]
        public void ClampAtmosphereConicCloseUT_NoCache_Kept()
        {
            Assert.Equal(1600.0, BackgroundRecorder.ClampAtmosphereConicCloseUT(
                1600.0, 1000.0, null, double.NaN));
        }

        // ---------------------------------------------------------------- live open

        [Fact]
        public void GoOnRails_InsideAtmosphere_OpensTheDragFreeConic()
        {
            RecordingTree tree = MakeTree();
            Planetarium saved = InstallPlanetarium(PackUT);
            try
            {
                var bg = new BackgroundRecorder(tree);
                Vessel v = Vessel(Pid, alive: true, body: Body("Kerbin", true, 70000.0),
                    altitude: 45000.0, packed: true, loaded: false);
                v.situation = global::Vessel.Situations.SUB_ORBITAL;
                Orbit o = v.orbitDriver.orbit;
                o.eccentricity = 0.82;
                o.semiMajorAxis = 380000.0;
                o.inclination = 0.1;
                o.epoch = PackUT;

                try
                {
                    bg.OnBackgroundVesselGoOnRails(v);
                }
                catch (Exception ex) when (IsHeadlessNativeFault(ex))
                {
                    // The cache refresh after the state initialization touches live KSP; the
                    // on-rails state is already in place by then.
                }

                Assert.True(bg.GetOnRailsHasOpenSegment(Pid),
                    "a vessel packed inside the atmosphere must open its on-rails conic");
                Assert.True(bg.GetOnRailsPackedInsideAtmosphereForTesting(Pid));
                Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                    && l.Contains("On-rails state initialized (atmosphere, drag-free conic)")
                    && l.Contains("pid=" + Pid)
                    && l.Contains("sectionEnv=Atmospheric"));
            }
            finally
            {
                Planetarium.fetch = saved;
            }
        }

        private static bool IsHeadlessNativeFault(Exception ex)
        {
            return ex is System.Security.SecurityException
                || ex is MissingMethodException
                || ex is NullReferenceException
                || ex is TypeInitializationException;
        }

        // ---------------------------------------------------------------- close

        [Fact]
        public void GoOffRails_ClosesTheAtmosphereConicAsAnAtmosphericCheckpointSection()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(PackUT), packedInsideAtmosphere: true);

            Assert.True(bg.EndOnRailsStateForGoOffRails(Pid, 1300.0));

            Recording rec = tree.Recordings[RecId];
            TrackSection section = Assert.Single(rec.TrackSections);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, section.referenceFrame);
            Assert.Equal(SegmentEnvironment.Atmospheric, section.environment);
            Assert.Equal(TrackSectionSource.Checkpoint, section.source);
            Assert.Equal(PackUT, section.startUT);
            Assert.Equal(1300.0, section.endUT);
            OrbitSegment checkpoint = Assert.Single(section.checkpoints);
            Assert.False(checkpoint.isPredicted);
            Assert.Equal(1300.0, checkpoint.endUT);
            OrbitSegment flat = Assert.Single(rec.OrbitSegments);
            Assert.False(flat.isPredicted);
            Assert.Equal(1300.0, rec.ExplicitEndUT);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Orbit segment closed: pid=" + Pid)
                && l.Contains("sectionEnv=Atmospheric")
                && l.Contains("checkpointSection=added"));
        }

        [Fact]
        public void VacuumConic_StillClosesExoBallistic()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(PackUT));

            bg.EndOnRailsStateForGoOffRails(Pid, 1300.0);

            Assert.Equal(SegmentEnvironment.ExoBallistic,
                Assert.Single(tree.Recordings[RecId].TrackSections).environment);
        }

        [Fact]
        public void SoiChange_ClosesTheOldBodyAtmospheric_AndClearsTheLabel()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(PackUT), packedInsideAtmosphere: true);

            Assert.True(bg.CloseOnRailsSegmentForSoiChangeForTesting(Pid, 1300.0));

            Assert.Equal(SegmentEnvironment.Atmospheric,
                Assert.Single(tree.Recordings[RecId].TrackSections).environment);
            Assert.False(bg.GetOnRailsPackedInsideAtmosphereForTesting(Pid));
            Assert.Contains(logLines, l => l.Contains("On-rails atmosphere label cleared at SOI change")
                && l.Contains("pid=" + Pid));
        }

        [Fact]
        public void WarpCheckpoints_KeepOneAtmosphericLabel_AndAddNoSplitCandidate()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings[RecId];
            rec.TrackSections.Add(LoadedSection(SegmentEnvironment.Atmospheric, 800.0, PackUT, 45000.0));
            rec.Points.AddRange(rec.TrackSections[0].frames);

            // Fifty warp-rate changes inside one packed span: each closes the open conic at the
            // checkpoint UT (CheckpointAllVessels); the span keeps its label throughout.
            double ut = PackUT;
            for (int i = 0; i < 50; i++)
            {
                bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(ut), packedInsideAtmosphere: true);
                ut += 37.0;
                bg.CheckpointAllVessels(ut, _ => null);
            }

            List<TrackSection> checkpoints = rec.TrackSections
                .Where(s => s.referenceFrame == ReferenceFrame.OrbitalCheckpoint).ToList();
            Assert.Equal(50, checkpoints.Count);
            Assert.All(checkpoints, s => Assert.Equal(SegmentEnvironment.Atmospheric, s.environment));

            rec.SplitRuleVersion = 1;
            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(new List<Recording> { rec }));
            rec.SplitRuleVersion = RecordingOptimizer.SplitRuleVersionCurrent;
            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(new List<Recording> { rec }));
        }

        // ---------------------------------------------------------------- optimizer

        [Theory]
        [InlineData(1)]
        [InlineData(RecordingOptimizer.SplitRuleVersionCurrent)]
        public void AtmosphereSpanBetweenAtmosphericLoadedRuns_AddsNoSplitCandidate(int splitRuleVersion)
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings[RecId];
            rec.SplitRuleVersion = splitRuleVersion;
            rec.TrackSections.Add(LoadedSection(SegmentEnvironment.Atmospheric, 800.0, PackUT, 45000.0));

            // Packed for ten minutes on the conic, then back in physics range, still in the air.
            bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(PackUT), packedInsideAtmosphere: true);
            bg.EndOnRailsStateForGoOffRails(Pid, PackUT + 600.0);
            rec.TrackSections.Add(LoadedSection(SegmentEnvironment.Atmospheric, PackUT + 600.0, PackUT + 700.0, 30000.0));
            rec.Points.AddRange(rec.TrackSections[0].frames);
            rec.Points.AddRange(rec.TrackSections[2].frames);

            Assert.Equal(3, rec.TrackSections.Count);
            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(new List<Recording> { rec }));
        }

        [Fact]
        public void ExoLabelledAtmosphereSpan_WouldSplit_WhichIsWhyTheSpanIsAtmospheric()
        {
            // Control: the same span labelled ExoBallistic (the vacuum checkpoint label) reads
            // as a long space stretch inside the atmosphere and becomes a version-1 split
            // candidate. The Atmospheric label is what keeps the recorded conic cut-free.
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings[RecId];
            rec.SplitRuleVersion = 1;
            rec.TrackSections.Add(LoadedSection(SegmentEnvironment.Atmospheric, 800.0, PackUT, 45000.0));
            bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(PackUT), packedInsideAtmosphere: false);
            bg.EndOnRailsStateForGoOffRails(Pid, PackUT + 600.0);
            rec.TrackSections.Add(LoadedSection(SegmentEnvironment.Atmospheric, PackUT + 600.0, PackUT + 700.0, 30000.0));
            rec.Points.AddRange(rec.TrackSections[0].frames);
            rec.Points.AddRange(rec.TrackSections[2].frames);

            Assert.NotEmpty(RecordingOptimizer.FindSplitCandidatesForOptimizer(new List<Recording> { rec }));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(RecordingOptimizer.SplitRuleVersionCurrent)]
        public void LaggedExoPack_KeepsTheSeam_AndAddsNoSplitCandidate(int splitRuleVersion)
        {
            // The loaded hysteresis still says ExoBallistic when the vessel packs just under
            // the atmosphere top: the flush persists the no-payload seam (Exo -> Atmospheric)
            // as before, and the Atmospheric conic follows it, so no boundary is exposed.
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings[RecId];
            rec.SplitRuleVersion = splitRuleVersion;
            bg.InjectLoadedStateWithEnvironmentForTesting(Pid, RecId, SegmentEnvironment.ExoBallistic, 800.0);
            for (int i = 0; i < 5; i++)
                bg.InjectCurrentTrackSectionFrameForTesting(Pid, Point(800.0 + 40.0 * i, 71000.0 - 200.0 * i));

            bg.FlushLoadedStateForOnRailsTransitionForTesting(
                Pid,
                SegmentEnvironment.Atmospheric,
                willHavePlayableOnRailsPayload: false,
                boundaryPoint: Point(PackUT, 69500.0),
                ut: PackUT);
            // The loaded state is gone; the on-rails state opens the conic.
            bg.InjectOnRailsStateForTesting(Pid, RecId, PackUT);
            bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(PackUT), packedInsideAtmosphere: true);
            bg.EndOnRailsStateForGoOffRails(Pid, PackUT + 600.0);

            Assert.Equal(3, rec.TrackSections.Count);
            Assert.True(rec.TrackSections[1].isBoundarySeam);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[2].environment);
            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(new List<Recording> { rec }));
        }

        // ---------------------------------------------------------------- tail

        [Fact]
        public void PredictedTail_StartsAtTheRecordedConicEnd()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings[RecId];
            rec.Points.Add(Point(PackUT, 45000.0));
            bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(PackUT), packedInsideAtmosphere: true);

            // Stock deletes the stage at the 1 kPa altitude 300 s after it packed.
            const double deletionUT = PackUT + 300.0;
            bg.EndOnRailsStateForGoOffRails(Pid, deletionUT);

            // The cache the live vessel produced at pack time: a drag-free fall from the pack
            // UT to the ground impact.
            OrbitSegment fall = SuborbitalArc(PackUT);
            fall.endUT = PackUT + 420.0;
            fall.isPredicted = true;
            var cache = new RecordingFinalizationCache
            {
                RecordingId = RecId,
                VesselPersistentId = Pid,
                Owner = FinalizationCacheOwner.BackgroundOnRails,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = PackUT,
                TerminalState = TerminalState.Destroyed,
                TerminalUT = PackUT + 420.0,
                TerminalBodyName = "Kerbin",
                TailStartsAtUT = PackUT,
                PredictedSegments = new List<OrbitSegment> { fall },
                RefreshReason = "background_destroy",
            };

            Assert.True(RecordingFinalizationCacheApplier.TryApply(
                rec,
                cache,
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "test", AllowStale = true },
                out RecordingFinalizationCacheApplyResult result));

            Assert.Equal(deletionUT, result.LastAuthoredUT);
            OrbitSegment predicted = Assert.Single(rec.OrbitSegments, s => s.isPredicted);
            Assert.Equal(deletionUT, predicted.startUT);
            Assert.Equal(PackUT + 420.0, predicted.endUT);
            OrbitSegment real = Assert.Single(rec.OrbitSegments, s => !s.isPredicted);
            Assert.Equal(deletionUT, real.endUT);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
        }

        [Fact]
        public void UnseenDeletion_ConicCloseClampsToThePredictedImpact()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            bg.AdoptFinalizationCacheForTesting(Pid, RecId, new RecordingFinalizationCache
            {
                RecordingId = RecId,
                VesselPersistentId = Pid,
                Owner = FinalizationCacheOwner.BackgroundOnRails,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = PackUT,
                TerminalState = TerminalState.Destroyed,
                TerminalUT = PackUT + 420.0,
                TerminalBodyName = "Kerbin",
                PredictedSegments = new List<OrbitSegment>(),
            });
            bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(PackUT), packedInsideAtmosphere: true);

            bg.EndOnRailsStateForGoOffRails(Pid, PackUT + 2000.0);

            TrackSection section = Assert.Single(tree.Recordings[RecId].TrackSections);
            Assert.Equal(PackUT + 420.0, section.endUT);
            Assert.Contains(logLines, l => l.Contains("Atmosphere conic close clamped to the predicted ending")
                && l.Contains("pid=" + Pid));
        }

        // ---------------------------------------------------------------- cache

        [Fact]
        public void OnRailsRefresh_KeepsTheLiveVesselCacheForAnAtmosphereConic()
        {
            RecordingTree tree = MakeTree();
            var bg = new BackgroundRecorder(tree);
            var live = new RecordingFinalizationCache
            {
                RecordingId = RecId,
                VesselPersistentId = Pid,
                Owner = FinalizationCacheOwner.BackgroundOnRails,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = PackUT,
                TerminalState = TerminalState.Landed,
                TerminalUT = PackUT + 900.0,
                TerminalBodyName = "Kerbin",
                PredictedSegments = new List<OrbitSegment>(),
                RefreshReason = "background_go_on_rails",
            };
            bg.AdoptFinalizationCacheForTesting(Pid, RecId, live);
            bg.InjectOpenOrbitSegmentForTesting(Pid, SuborbitalArc(PackUT), packedInsideAtmosphere: true);

            Assert.False(bg.RefreshOnRailsFinalizationCacheForTesting(Pid, PackUT + 120.0, force: true));

            RecordingFinalizationCache after = bg.GetFinalizationCacheForTesting(Pid);
            Assert.Equal(TerminalState.Landed, after.TerminalState);
            Assert.Equal(PackUT + 900.0, after.TerminalUT);
            Assert.Contains(logLines, l => l.Contains("[FinalizerCache]")
                && l.Contains("On-rails atmosphere conic keeps the live-vessel cache")
                && l.Contains("pid=" + Pid));
        }

        [Theory]
        [InlineData(true, (int)FinalizationCacheStatus.Fresh, true)]
        [InlineData(true, (int)FinalizationCacheStatus.Stale, true)]
        [InlineData(true, (int)FinalizationCacheStatus.Failed, false)]
        [InlineData(true, (int)FinalizationCacheStatus.Empty, false)]
        [InlineData(false, (int)FinalizationCacheStatus.Fresh, false)]
        public void ShouldKeepLiveCacheForAtmosphereConic_Table(
            bool packedInsideAtmosphere, int statusValue, bool expected)
        {
            var cache = new RecordingFinalizationCache { Status = (FinalizationCacheStatus)statusValue };
            Assert.Equal(expected,
                BackgroundRecorder.ShouldKeepLiveCacheForAtmosphereConic(packedInsideAtmosphere, cache));
            Assert.False(BackgroundRecorder.ShouldKeepLiveCacheForAtmosphereConic(packedInsideAtmosphere, null));
        }

        // ---------------------------------------------------------------- bridge

        [Fact]
        public void LiveAppendClip_KeepsTheAtmosphericLabelOfTheClippedSection()
        {
            var rec = new Recording { RecordingId = RecId, VesselPersistentId = Pid };
            OrbitSegment older = SuborbitalArc(PackUT);
            older.endUT = PackUT + 300.0;
            Assert.True(OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, older, markDirty: false, out _, SegmentEnvironment.Atmospheric));

            OrbitSegment newer = SuborbitalArc(PackUT + 200.0);
            newer.endUT = PackUT + 400.0;
            Assert.True(OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, newer, markDirty: false, out _, SegmentEnvironment.Atmospheric));

            Assert.Equal(2, rec.TrackSections.Count);
            Assert.All(rec.TrackSections, s => Assert.Equal(SegmentEnvironment.Atmospheric, s.environment));
            Assert.Equal(PackUT + 200.0, rec.TrackSections[0].endUT);
        }
    }
}
