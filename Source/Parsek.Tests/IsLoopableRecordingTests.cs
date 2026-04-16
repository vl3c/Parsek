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
        public void TerminalStateDocked_ReturnsTrue()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Docked };
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
            var rec = new Recording { TerminalStateValue = TerminalState.Docked, IsDebris = true };
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
                TerminalStateValue = TerminalState.Docked
            };
            Assert.True(Recording.IsLoopableRecording(rec));
        }
    }
}
