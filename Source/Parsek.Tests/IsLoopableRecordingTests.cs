using Xunit;

namespace Parsek.Tests
{
    public class IsLoopableRecordingTests
    {
        [Fact]
        public void Null_ReturnsFalse()
        {
            Assert.False(Recording.IsLoopableRecording(null));
        }

        [Fact]
        public void PlainRecording_ReturnsFalse()
        {
            var rec = new Recording();
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void LaunchSite_ReturnsTrue()
        {
            var rec = new Recording { LaunchSiteName = "LaunchPad" };
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void PrelaunchSituation_ReturnsTrue()
        {
            var rec = new Recording { StartSituation = "Prelaunch" };
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void AtmoPhase_ReturnsTrue()
        {
            var rec = new Recording { SegmentPhase = "atmo" };
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void ApproachPhase_ReturnsTrue()
        {
            var rec = new Recording { SegmentPhase = "approach" };
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void SurfacePhase_ReturnsTrue()
        {
            var rec = new Recording { SegmentPhase = "surface" };
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void DockTargetPid_ReturnsTrue()
        {
            var rec = new Recording { DockTargetVesselPid = 12345 };
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void DockedTerminalState_WithoutDockTarget_ReturnsFalse()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Docked };
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void DockedTerminalState_WithDockTarget_ReturnsTrue()
        {
            var rec = new Recording
            {
                DockTargetVesselPid = 12345,
                TerminalStateValue = TerminalState.Docked
            };
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void ExoPhase_NoOtherCriteria_ReturnsFalse()
        {
            var rec = new Recording { SegmentPhase = "exo" };
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void Debris_ExcludedEvenWithLaunchSite()
        {
            var rec = new Recording { LaunchSiteName = "LaunchPad", IsDebris = true };
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void Debris_ExcludedEvenWithDocking()
        {
            var rec = new Recording
            {
                DockTargetVesselPid = 12345,
                TerminalStateValue = TerminalState.Docked,
                IsDebris = true
            };
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void OrbitalTerminalState_ReturnsFalse()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Orbiting };
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void BoardedTerminalState_ReturnsFalse()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Boarded };
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void DestroyedTerminalState_ReturnsFalse()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Destroyed };
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void FlyingSituation_NoOtherCriteria_ReturnsFalse()
        {
            var rec = new Recording { StartSituation = "Flying" };
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void MultipleLoopableCriteria_ReturnsTrue()
        {
            var rec = new Recording
            {
                LaunchSiteName = "Runway",
                SegmentPhase = "atmo",
                DockTargetVesselPid = 12345
            };
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void RelativeTrackWithRecordedAnchor_ReturnsTrue()
        {
            // An orbital rendezvous / flyby approaching a base or station that
            // never hard-docks (SegmentPhase "exo", no DockTargetVesselPid) is
            // still viewable through its RELATIVE track anchored to the station's
            // recorded leg.
            var rec = new Recording { SegmentPhase = "exo" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                anchorRecordingId = "station-leg-1"
            });
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void RelativeTrackWithLiveAnchorVessel_ReturnsTrue()
        {
            var rec = new Recording { SegmentPhase = "exo" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                anchorVesselId = 4242
            });
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void RelativeTrackWithLoopAnchor_ReturnsTrue()
        {
            var rec = new Recording { SegmentPhase = "exo", LoopAnchorVesselId = 99 };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative
            });
            Assert.True(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void RelativeTrackWithoutAnchor_ReturnsFalse()
        {
            // A RELATIVE section with no anchor at all cannot be positioned, so
            // it is not a viewable approach.
            var rec = new Recording { SegmentPhase = "exo" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative
            });
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void AbsoluteTrackOnly_NoOtherCriteria_ReturnsFalse()
        {
            var rec = new Recording { SegmentPhase = "exo" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute
            });
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void ParentAnchoredRelativeTrack_ExcludedFromRelativeRule()
        {
            // A parent-anchored child's RELATIVE track follows its own parent, not
            // an independent base/station, so the relative-track rule does not make
            // it loopable. (If such a child actually lands it becomes loopable via
            // its surface/approach/atmo phase instead.)
            var rec = new Recording
            {
                SegmentPhase = "exo",
                ParentAnchorRecordingId = "parent-rec-1"
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                anchorRecordingId = "parent-rec-1"
            });
            Assert.False(Recording.IsLoopableRecording(rec));
        }

        [Fact]
        public void HasViewableRelativeTrack_NullOrEmpty_ReturnsFalse()
        {
            Assert.False(Recording.HasViewableRelativeTrack(null));
            Assert.False(Recording.HasViewableRelativeTrack(new Recording()));
        }
    }
}
