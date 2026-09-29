using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The ghost reapply entries registered in Update carry the LateUpdate terrain clamp.
    /// Stock crew portraits call Camera.Render() from a coroutine between Update and
    /// LateUpdate, which fires Camera.onPreCull early. That early pre-cull must leave the
    /// entries for LateUpdate, or the frame renders a surface ghost at its raw (unclamped)
    /// Update height while the frames around it render the clamped height: the ghost jumps
    /// up and down at the portrait refresh rate.
    /// </summary>
    public class GhostCameraPreCullFrameTests
    {
        private const double RawAltitude = 64.6;
        private const double ClampedAltitude = 65.3;

        [Fact]
        public void PreCullBeforeLateUpdate_KeepsEntriesForLateUpdate()
        {
            var action = ParsekFlight.ResolveGhostCameraPreCullAction(
                frameCount: 100,
                lastLateUpdateReapplyFrame: 99,
                lastPreCullReapplyFrame: 99,
                pendingEntryCount: 3);

            Assert.Equal(ParsekFlight.GhostCameraPreCullAction.KeepForLateUpdate, action);
        }

        [Fact]
        public void FirstPreCullAfterLateUpdate_RunsAndConsumes()
        {
            var action = ParsekFlight.ResolveGhostCameraPreCullAction(
                frameCount: 100,
                lastLateUpdateReapplyFrame: 100,
                lastPreCullReapplyFrame: 99,
                pendingEntryCount: 3);

            Assert.Equal(ParsekFlight.GhostCameraPreCullAction.RunAndConsume, action);
        }

        [Fact]
        public void SecondCameraAfterLateUpdateSameFrame_DoesNothing()
        {
            var action = ParsekFlight.ResolveGhostCameraPreCullAction(
                frameCount: 100,
                lastLateUpdateReapplyFrame: 100,
                lastPreCullReapplyFrame: 100,
                pendingEntryCount: 3);

            Assert.Equal(ParsekFlight.GhostCameraPreCullAction.Nothing, action);
        }

        [Fact]
        public void NoPendingEntries_DoesNothing()
        {
            Assert.Equal(
                ParsekFlight.GhostCameraPreCullAction.Nothing,
                ParsekFlight.ResolveGhostCameraPreCullAction(100, 99, 99, 0));
            Assert.Equal(
                ParsekFlight.GhostCameraPreCullAction.Nothing,
                ParsekFlight.ResolveGhostCameraPreCullAction(100, 100, 99, 0));
        }

        /// <summary>
        /// Drives the production frame order (Update registers the raw pose, an optional
        /// mid-frame portrait render, LateUpdate reapply + clamp, the main camera render)
        /// through the real pre-cull decision and the real reapply phase gate, and records
        /// the height each main-camera render would draw. A portrait render every third
        /// frame must not change the drawn height.
        /// </summary>
        [Fact]
        public void PortraitRenderMidFrame_SurfaceGhostRenderedHeightIsStable()
        {
            List<double> rendered = SimulateRenderedHeights(frames: 30, portraitEveryNFrames: 3);

            Assert.Equal(30, rendered.Count);
            for (int i = 0; i < rendered.Count; i++)
                Assert.True(rendered[i] == ClampedAltitude,
                    "frame " + i.ToString(CultureInfo.InvariantCulture)
                    + " rendered the ghost at " + rendered[i].ToString("R", CultureInfo.InvariantCulture)
                    + " instead of the clamped " + ClampedAltitude.ToString("R", CultureInfo.InvariantCulture));
        }

        [Fact]
        public void NoMidFrameRender_SurfaceGhostRenderedHeightIsClamped()
        {
            List<double> rendered = SimulateRenderedHeights(frames: 10, portraitEveryNFrames: 0);

            Assert.All(rendered, h => Assert.Equal(ClampedAltitude, h));
        }

        private static List<double> SimulateRenderedHeights(int frames, int portraitEveryNFrames)
        {
            var rendered = new List<double>();
            int lastLateUpdateFrame = -1;
            int lastPreCullFrame = -1;
            int pendingEntries = 0;
            double ghostAltitude = RawAltitude;

            for (int frame = 1; frame <= frames; frame++)
            {
                // Update: the defensive clear, then playback positions the ghost at its raw
                // recorded height and registers one reapply entry.
                pendingEntries = 0;
                ghostAltitude = RawAltitude;
                pendingEntries++;

                // Coroutine phase: a stock portrait camera renders.
                if (portraitEveryNFrames > 0 && frame % portraitEveryNFrames == 0)
                {
                    PreCull(frame, lastLateUpdateFrame, ref lastPreCullFrame,
                        ref pendingEntries, ref ghostAltitude);
                }

                // LateUpdate: reapply + terrain clamp over whatever entries survived.
                if (pendingEntries > 0
                    && ParsekFlight.ShouldProcessGhostPositionReapply(
                        ParsekFlight.GhostPositionReapplyPhase.LateUpdate))
                {
                    ghostAltitude = ClampedAltitude;
                }
                lastLateUpdateFrame = frame;

                // Render: the flight cameras.
                PreCull(frame, lastLateUpdateFrame, ref lastPreCullFrame,
                    ref pendingEntries, ref ghostAltitude);
                rendered.Add(ghostAltitude);
            }

            return rendered;
        }

        private static void PreCull(
            int frame,
            int lastLateUpdateFrame,
            ref int lastPreCullFrame,
            ref int pendingEntries,
            ref double ghostAltitude)
        {
            var action = ParsekFlight.ResolveGhostCameraPreCullAction(
                frame, lastLateUpdateFrame, lastPreCullFrame, pendingEntries);
            if (action != ParsekFlight.GhostCameraPreCullAction.RunAndConsume)
                return;

            lastPreCullFrame = frame;
            if (ParsekFlight.ShouldProcessGhostPositionReapply(
                    ParsekFlight.GhostPositionReapplyPhase.CameraPreCull))
            {
                ghostAltitude = ClampedAltitude;
            }
            pendingEntries = 0;
        }
    }
}
