using System;
using System.Globalization;

namespace Parsek
{
    public partial class ParsekScenario
    {
        /// <summary>
        /// Installs the removed-flag tombstones the loaded node carries when the policy cell says
        /// the save owns them (a cold load) and keeps memory's on every other load; those loads
        /// reconcile memory's copy against the loaded world's UT later
        /// (<see cref="ReconcileRemovedReplayFlagsAtLoad"/>). See
        /// <see cref="LoadStateCategory.RemovedReplayFlags"/>.
        /// </summary>
        private static void LoadRemovedReplayFlags(ConfigNode node, EarlyLoadKind earlyLoadKind)
        {
            LoadReconcileDecision decision = LoadReconcilePolicy.DecideEarly(
                earlyLoadKind, LoadStateCategory.RemovedReplayFlags);
            if (decision.Action == LoadReconcileAction.Save)
            {
                RemovedReplayFlags.Replace(RemovedReplayFlags.LoadFrom(node), "load " + earlyLoadKind);
                return;
            }
            ParsekLog.Verbose(RemovedReplayFlags.Tag, string.Format(CultureInfo.InvariantCulture,
                "Load ({0}): removed-flag tombstones kept from memory ({1}): tombstones={2}",
                earlyLoadKind, decision.Action, RemovedReplayFlags.Count));
        }

        /// <summary>
        /// Drops the tombstones of removals after the loaded world's UT when the policy cell
        /// reconciles at the cutoff (every in-session load): the loaded world is from before the
        /// removal, so the flag stands there again or replay plants it at its moment.
        /// </summary>
        private static void ReconcileRemovedReplayFlagsAtLoad(LoadKind kind, double loadedWorldUT)
        {
            LoadReconcileDecision decision = LoadReconcilePolicy.Decide(kind, LoadStateCategory.RemovedReplayFlags);
            if (decision.Action != LoadReconcileAction.ReconcileAtCutoff)
                return;
            RemovedReplayFlags.DropRemovedAfter(loadedWorldUT, "load " + kind);
        }

        /// <summary>A loaded flag vessel is about to be destroyed (picked up by a kerbal, recovered, crashed).</summary>
        internal static void NoteFlagVesselRemoved(Vessel v, string source)
        {
            if (v == null)
                return;
            if (!RemovedReplayFlags.ShouldRecordRemoval(
                    v.vesselType == VesselType.Flag,
                    GhostMapPresence.IsGhostMapVessel(v.persistentId),
                    RewindContext.IsRewinding))
                return;
            try
            {
                CelestialBody body = v.mainBody;
                RemovedReplayFlags.RecordRemoval(new RemovedReplayFlags.Tombstone
                {
                    BodyName = body != null ? body.name : null,
                    BodyRadius = body != null ? body.Radius : double.NaN,
                    Latitude = v.latitude,
                    Longitude = v.longitude,
                    Altitude = v.altitude,
                    RemovedUT = Planetarium.GetUniversalTime(),
                    FlagName = v.vesselName,
                    Source = source,
                });
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(RemovedReplayFlags.Tag,
                    $"Flag removal not recorded ({source}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>An unloaded flag was recovered or terminated (Tracking Station, Space Center).</summary>
        internal static void NoteFlagProtoRemoved(ProtoVessel pv, string source)
        {
            if (pv == null)
                return;
            if (!RemovedReplayFlags.ShouldRecordRemoval(
                    pv.vesselType == VesselType.Flag,
                    GhostMapPresence.IsGhostMapVessel(pv.persistentId),
                    RewindContext.IsRewinding))
                return;
            try
            {
                CelestialBody body = null;
                int bodyIndex = pv.orbitSnapShot != null ? pv.orbitSnapShot.ReferenceBodyIndex : -1;
                if (FlightGlobals.Bodies != null && bodyIndex >= 0 && bodyIndex < FlightGlobals.Bodies.Count)
                    body = FlightGlobals.Bodies[bodyIndex];
                RemovedReplayFlags.RecordRemoval(new RemovedReplayFlags.Tombstone
                {
                    BodyName = body != null ? body.name : null,
                    BodyRadius = body != null ? body.Radius : double.NaN,
                    Latitude = pv.latitude,
                    Longitude = pv.longitude,
                    Altitude = pv.altitude,
                    RemovedUT = Planetarium.GetUniversalTime(),
                    FlagName = pv.vesselName,
                    Source = source,
                });
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(RemovedReplayFlags.Tag,
                    $"Flag removal not recorded ({source}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
