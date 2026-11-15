using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>Where the planetarium camera goes before a ghost map vessel it targets dies.</summary>
    internal enum GhostCameraRetargetTarget
    {
        /// <summary>No camera, or the camera does not target a ghost that is about to die.</summary>
        None,
        ActiveVessel,
        /// <summary>The body the targeted ghost orbits (what stock's own nearest-body retarget picks).</summary>
        ReferenceBody,
        HomeBody,
        /// <summary>The camera targets a dying ghost and nothing usable is left to move it to.</summary>
        NoSafeTarget,
    }

    internal static partial class GhostMapPresence
    {
        // GHOST-MAP-TEARDOWN-NRE-WHEN-CAMERA-TARGETED. Stock PlanetariumCamera.OnVesselDestroy
        // (and RemoveTarget) retarget to FindNearestTarget() - the nearest celestial body - when
        // the destroyed vessel is the camera target, and SetTarget fires onPlanetariumTargetChange
        // into the KnowledgeBase. During a quit or scene unload Unity destroys the KnowledgeBase's
        // KbApp children before Parsek's teardown runs, so that event NREs inside
        // KbApp_PlanetParameters.ActivateApp (collected logs: "KbApp.OnDestroy Planet Parameters"
        // precedes the "Focus:" line every time). The retarget therefore has to happen while the
        // UI is intact: before Die() on an in-scene removal, and from OnApplicationQuit for the
        // quit; an OnDestroy-time removal stands down rather than fire the same event itself.

        /// <summary>
        /// Pure choice of where to move the planetarium camera before Parsek destroys the ghost
        /// map vessels in <paramref name="dyingGhostPids"/>. Nothing moves unless the camera
        /// targets one of them. In a live scene the active vessel is preferred (the same target
        /// as the watch-cleanup guard in <c>ParsekFlight</c>); with
        /// <paramref name="sceneTeardown"/> the active vessel is skipped, because it is destroyed
        /// with the scene too and stock would retarget again from its OnDestroy after the
        /// KnowledgeBase is gone, while a body never fires <c>onVesselDestroy</c>. Then the body
        /// the targeted ghost orbits, then the home body.
        /// </summary>
        internal static GhostCameraRetargetTarget DecideCameraRetargetBeforeGhostRemoval(
            bool cameraPresent,
            uint cameraTargetVesselPid,
            ICollection<uint> dyingGhostPids,
            uint activeVesselPid,
            bool activeVesselHasMapObject,
            bool referenceBodyHasMapObject,
            bool homeBodyHasMapObject,
            bool sceneTeardown)
        {
            if (!cameraPresent
                || cameraTargetVesselPid == 0u
                || dyingGhostPids == null
                || !dyingGhostPids.Contains(cameraTargetVesselPid))
            {
                return GhostCameraRetargetTarget.None;
            }

            if (!sceneTeardown
                && activeVesselPid != 0u
                && activeVesselHasMapObject
                && !dyingGhostPids.Contains(activeVesselPid))
            {
                return GhostCameraRetargetTarget.ActiveVessel;
            }

            if (referenceBodyHasMapObject)
                return GhostCameraRetargetTarget.ReferenceBody;
            if (homeBodyHasMapObject)
                return GhostCameraRetargetTarget.HomeBody;
            return GhostCameraRetargetTarget.NoSafeTarget;
        }

        internal static string FormatCameraRetargetLine(
            GhostCameraRetargetTarget choice, string ghostName, uint ghostPid,
            string targetName, string reason, bool sceneTeardown)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Planetarium camera retargeted off dying ghost '{0}' pid={1} to {2} '{3}' before {4} (sceneTeardown={5})",
                ghostName ?? "(unknown)", ghostPid, choice, targetName ?? "(unknown)",
                reason ?? "(none)", sceneTeardown);
        }

        /// <summary>
        /// Moves the planetarium camera off every registered ghost map vessel while the scene is
        /// still intact. Called from <c>ParsekHarmony.OnApplicationQuit</c>, which Unity delivers
        /// before it destroys anything, so the OnDestroy-time <see cref="RemoveAllGhostVessels"/>
        /// that follows finds the camera elsewhere and stock has nothing to retarget.
        /// </summary>
        internal static void RetargetPlanetariumCameraOffGhostMapVessels(string reason)
        {
            int count = vesselsByChainPid.Count + vesselsByRecordingIndex.Count + overlapInstanceVessels.Count;
            if (count == 0)
            {
                ParsekLog.Verbose(Tag,
                    "Planetarium camera retarget skipped before " + (reason ?? "(none)")
                    + ": no ghost map vessels");
                return;
            }

            var vessels = new List<Vessel>(count);
            vessels.AddRange(vesselsByChainPid.Values);
            vessels.AddRange(vesselsByRecordingIndex.Values);
            vessels.AddRange(overlapInstanceVessels.Values);
            RetargetPlanetariumCameraOffDyingGhosts(vessels, reason, sceneTeardown: true);
        }

        /// <summary>
        /// Thin live wrapper over <see cref="DecideCameraRetargetBeforeGhostRemoval"/>: reads the
        /// camera, the active vessel and the bodies, and calls <c>PlanetariumCamera.SetTarget</c>.
        /// Kept out of line so the callers' JIT never touches <c>FlightGlobals</c> /
        /// <c>PlanetariumCamera</c> (headless mono runs their static initializers at JIT).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void RetargetPlanetariumCameraOffDyingGhosts(
            IList<Vessel> dying, string reason, bool sceneTeardown)
        {
            try
            {
                PlanetariumCamera camera = PlanetariumCamera.fetch;
                MapObject target = camera != null ? camera.target : null;
                Vessel targetVessel = target != null ? target.vessel : null;

                var dyingPids = new HashSet<uint>();
                Vessel dyingTarget = null;
                if (dying != null)
                {
                    for (int i = 0; i < dying.Count; i++)
                    {
                        Vessel v = dying[i];
                        if (v == null)
                            continue;
                        dyingPids.Add(v.persistentId);
                        if (targetVessel != null && ReferenceEquals(v, targetVessel))
                            dyingTarget = v;
                    }
                }

                Vessel active = FlightGlobals.fetch != null ? FlightGlobals.ActiveVessel : null;
                CelestialBody referenceBody = dyingTarget != null && dyingTarget.orbit != null
                    ? dyingTarget.orbit.referenceBody
                    : null;
                CelestialBody homeBody = FlightGlobals.fetch != null && FlightGlobals.Bodies != null
                    ? FlightGlobals.GetHomeBody()
                    : null;

                GhostCameraRetargetTarget choice = DecideCameraRetargetBeforeGhostRemoval(
                    camera != null,
                    dyingTarget != null ? dyingTarget.persistentId : 0u,
                    dyingPids,
                    active != null ? active.persistentId : 0u,
                    active != null && active.mapObject != null,
                    referenceBody != null && referenceBody.MapObject != null,
                    homeBody != null && homeBody.MapObject != null,
                    sceneTeardown);

                MapObject newTarget = null;
                string newTargetName = null;
                switch (choice)
                {
                    case GhostCameraRetargetTarget.None:
                        ParsekLog.Verbose(Tag,
                            string.Format(ic,
                                "Planetarium camera retarget not needed before {0}: camera={1} target='{2}' dyingGhosts={3}",
                                reason ?? "(none)", camera != null,
                                targetVessel != null ? targetVessel.vesselName : "(not a vessel)",
                                dyingPids.Count));
                        return;
                    case GhostCameraRetargetTarget.ActiveVessel:
                        newTarget = active.mapObject;
                        newTargetName = active.vesselName;
                        break;
                    case GhostCameraRetargetTarget.ReferenceBody:
                        newTarget = referenceBody.MapObject;
                        newTargetName = referenceBody.bodyName;
                        break;
                    case GhostCameraRetargetTarget.HomeBody:
                        newTarget = homeBody.MapObject;
                        newTargetName = homeBody.bodyName;
                        break;
                    default:
                        ParsekLog.Warn(Tag,
                            string.Format(ic,
                                "Planetarium camera targets dying ghost '{0}' pid={1} before {2} but no safe "
                                + "target is available; stock will retarget on destroy",
                                dyingTarget.vesselName, dyingTarget.persistentId, reason ?? "(none)"));
                        return;
                }

                camera.SetTarget(newTarget);
                ParsekLog.Info(Tag, FormatCameraRetargetLine(
                    choice, dyingTarget.vesselName, dyingTarget.persistentId,
                    newTargetName, reason, sceneTeardown));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Planetarium camera retarget before {0} threw {1}: {2}",
                        reason ?? "(none)", ex.GetType().Name, ex.Message));
            }
        }
    }
}
