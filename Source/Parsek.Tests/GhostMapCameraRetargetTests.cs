using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GHOST-MAP-TEARDOWN-NRE-WHEN-CAMERA-TARGETED: the pure choice of where the planetarium
    /// camera goes before Parsek destroys ghost map vessels it may be targeting.
    /// </summary>
    [Collection("Sequential")]
    public class GhostMapCameraRetargetTests
    {
        private const uint GhostA = 900001u;
        private const uint GhostB = 900002u;
        private const uint ActivePid = 4242u;

        private static readonly HashSet<uint> Dying = new HashSet<uint> { GhostA, GhostB };

        private static GhostCameraRetargetTarget Decide(
            bool cameraPresent = true,
            uint targetPid = GhostA,
            ICollection<uint> dying = null,
            uint activePid = ActivePid,
            bool activeHasMapObject = true,
            bool referenceBodyHasMapObject = true,
            bool homeBodyHasMapObject = true,
            bool sceneTeardown = false)
        {
            return GhostMapPresence.DecideCameraRetargetBeforeGhostRemoval(
                cameraPresent, targetPid, dying ?? Dying, activePid, activeHasMapObject,
                referenceBodyHasMapObject, homeBodyHasMapObject, sceneTeardown);
        }

        [Fact]
        public void NoCamera_NoRetarget()
        {
            Assert.Equal(GhostCameraRetargetTarget.None, Decide(cameraPresent: false));
        }

        [Fact]
        public void TargetIsNotADyingGhost_NoRetarget()
        {
            Assert.Equal(GhostCameraRetargetTarget.None, Decide(targetPid: ActivePid));
            Assert.Equal(GhostCameraRetargetTarget.None, Decide(targetPid: 0u));
            Assert.Equal(GhostCameraRetargetTarget.None, Decide(dying: new HashSet<uint>()));
            Assert.Equal(GhostCameraRetargetTarget.None, Decide(dying: null, targetPid: 77u));
        }

        [Fact]
        public void TargetIsADyingGhost_PrefersTheActiveVessel()
        {
            Assert.Equal(GhostCameraRetargetTarget.ActiveVessel, Decide());
            Assert.Equal(GhostCameraRetargetTarget.ActiveVessel, Decide(targetPid: GhostB));
        }

        [Fact]
        public void NoUsableActiveVessel_FallsBackToTheGhostsReferenceBody()
        {
            // The Tracking Station has no active vessel; stock's own retarget is the
            // nearest celestial body, which for a ghost is the body it orbits.
            Assert.Equal(GhostCameraRetargetTarget.ReferenceBody, Decide(activePid: 0u));
            Assert.Equal(GhostCameraRetargetTarget.ReferenceBody, Decide(activeHasMapObject: false));
            // An active vessel that is itself in the dying set is no target.
            Assert.Equal(GhostCameraRetargetTarget.ReferenceBody, Decide(activePid: GhostB));
        }

        [Fact]
        public void NoReferenceBody_FallsBackToTheHomeBody()
        {
            Assert.Equal(GhostCameraRetargetTarget.HomeBody,
                Decide(activePid: 0u, referenceBodyHasMapObject: false));
        }

        [Fact]
        public void NothingUsable_ReportsNoSafeTarget()
        {
            Assert.Equal(GhostCameraRetargetTarget.NoSafeTarget,
                Decide(activePid: 0u, referenceBodyHasMapObject: false, homeBodyHasMapObject: false));
        }

        [Fact]
        public void SceneTeardown_SkipsTheActiveVesselForABody()
        {
            // The active vessel is destroyed with the scene too, and stock retargets again
            // from its OnDestroy after the KnowledgeBase apps are gone. A body never fires
            // onVesselDestroy.
            Assert.Equal(GhostCameraRetargetTarget.ReferenceBody, Decide(sceneTeardown: true));
            Assert.Equal(GhostCameraRetargetTarget.HomeBody,
                Decide(sceneTeardown: true, referenceBodyHasMapObject: false));
            Assert.Equal(GhostCameraRetargetTarget.NoSafeTarget,
                Decide(sceneTeardown: true, referenceBodyHasMapObject: false, homeBodyHasMapObject: false));
            Assert.Equal(GhostCameraRetargetTarget.None, Decide(sceneTeardown: true, targetPid: ActivePid));
        }

        [Fact]
        public void LogLine_NamesGhostTargetAndReason_Invariantly()
        {
            string line = GhostMapPresence.FormatCameraRetargetLine(
                GhostCameraRetargetTarget.ReferenceBody, "Ghost: Kerbal X", GhostA, "Duna",
                "application-quit", sceneTeardown: true);

            Assert.Equal(
                "Planetarium camera retargeted off dying ghost 'Ghost: Kerbal X' pid=900001 "
                + "to ReferenceBody 'Duna' before application-quit (sceneTeardown=True)",
                line);
        }
    }
}
