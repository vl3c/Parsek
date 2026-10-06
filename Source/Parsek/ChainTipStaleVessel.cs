using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Where the stale vessel a chain-tip replacement removed was the player's map focus,
    /// navigation target or Tracking Station selection, so the scene can hand them to the
    /// spawned tip (same pid, new Vessel object).
    /// </summary>
    internal struct StaleChainVesselFocus
    {
        internal bool WasNavigationTarget;
        internal bool WasMapFocus;
        internal bool WasTrackingStationSelected;
    }

    /// <summary>
    /// The Ghost Chain Rule (design 12.5, 13.2) at the end-of-recording spawn sites outside
    /// the flight chain path.
    ///
    /// <para>Only the flight scene despawns a vessel a committed future mission claims
    /// (<see cref="VesselGhoster"/>, on flight load while the chain's spawn UT is still
    /// ahead). The Space Center and the Tracking Station never do, and a flight load past the
    /// spawn UT no longer ghosts the chain, so after a rewind or a load to before the claim
    /// the claimed vessel can still be live in its pre-claim form when the chain tip's spawn
    /// UT passes. It carries the tip's pid and the tip's launch guid (the station half of a
    /// dock keeps the station's Vessel.id), so the ordinary adoption check accepts it and the
    /// recorded change (a fuel transfer, a docked module) is lost.</para>
    ///
    /// <para>Two independent pieces of evidence must agree before a live vessel is called
    /// stale, because a pid and launch-guid match alone cannot tell the pre-claim original
    /// from the tip's own result (the station half keeps both). First, the timeline: this
    /// session saw the live playhead strictly before the tip's start
    /// (<see cref="PlaybackScopeTracker.WasPlayheadSeenBeforeActivation"/>), so a rewind or a
    /// load put the world before the vessel took its recorded final form. The flight
    /// contract draws the same line at load time (ghost while the spawn UT is ahead).
    /// Second, the vessel itself: its <c>lastUT</c> (the last UT KSP simulated it in
    /// physics; FlightIntegrator stamps it every physics frame and nothing advances it on
    /// rails) lies before the chain's last claim. Every claim (a dock, a boarding, a part
    /// event) happens with the claimed vessel in physics, so the vessel that went through
    /// every recorded change carries a later <c>lastUT</c>, while one loaded from before the
    /// last of them (before a dock, or between two committed docks) does not. The
    /// second check keeps a session that rewound and then loaded a save from after the change
    /// from replacing the real vessel there; the first keeps normal play, including a commit
    /// made seconds after the tip began, on the adoption path.</para>
    ///
    /// <para>The replacement mirrors the flight chain-tip spawn: the stale vessel is
    /// removed through <see cref="ClaimedVesselRemoval"/> (crew taken off and set Available,
    /// then <c>Vessel.Die()</c>: no recovery, so no funds and no ledger row, and no crew
    /// loss) and the tip spawns from its snapshot with its identity preserved.</para>
    /// </summary>
    internal static class ChainTipStaleVessel
    {
        private const string Tag = "ChainTip";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        internal const string ReasonReplace = "stale-pre-claim-vessel";
        internal const string ReasonNoRecording = "no-recording";
        internal const string ReasonTipSpawned = "tip-already-spawned";
        internal const string ReasonNoLiveVessel = "no-live-same-launch-vessel";
        internal const string ReasonNotChainTip = "not-chain-tip";
        internal const string ReasonTerminated = "terminated-chain";
        internal const string ReasonNotRewoundBeforeTip = "not-rewound-before-tip";
        internal const string ReasonSimulatedSinceClaim = "live-vessel-simulated-since-claim";
        internal const string ReasonInUse = "live-vessel-in-use";

        /// <summary>Test seam replacing the live despawn: (pid, scene) -> stale vessel removed.</summary>
        internal static Func<uint, string, bool> DespawnOverrideForTesting;

        /// <summary>Test seam replacing the live in-use check (active vessel, recorded by the live tree).</summary>
        internal static Func<uint, bool> LiveVesselInUseOverrideForTesting;

        /// <summary>Test seam replacing the live vessel's <c>lastUT</c> read (NaN = unknown).</summary>
        internal static Func<uint, double> LiveVesselLastUTOverrideForTesting;

        internal static void ResetForTesting()
        {
            DespawnOverrideForTesting = null;
            LiveVesselInUseOverrideForTesting = null;
            LiveVesselLastUTOverrideForTesting = null;
        }

        /// <summary>
        /// Pure: should the live vessel carrying <paramref name="rec"/>'s pid be replaced by
        /// the tip snapshot instead of adopted? True only when <paramref name="rec"/> is the tip
        /// of a non-terminated ghost chain on its own pid, the tip has not spawned, a live
        /// vessel of the same launch exists, the playhead was seen before the tip's start this
        /// session (a rewind or a load), the live vessel was last simulated before the chain's
        /// last claim (<paramref name="liveVesselLastUT"/>; NaN = unknown, never stale) and it
        /// is not in the player's hands. Every other answer leaves the site on its existing
        /// path; <paramref name="reason"/> names the deciding check.
        /// </summary>
        internal static bool ShouldReplaceStaleLiveVessel(
            Recording rec,
            Dictionary<uint, GhostChain> chains,
            bool liveSameLaunchVesselExists,
            bool playheadSeenBeforeTip,
            double liveVesselLastUT,
            bool liveVesselInUse,
            out string reason)
        {
            if (rec == null || rec.VesselPersistentId == 0 || string.IsNullOrEmpty(rec.RecordingId))
            {
                reason = ReasonNoRecording;
                return false;
            }
            if (rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0)
            {
                reason = ReasonTipSpawned;
                return false;
            }
            if (!liveSameLaunchVesselExists)
            {
                reason = ReasonNoLiveVessel;
                return false;
            }

            GhostChain chain = GhostChainWalker.FindChainForVessel(chains, rec.VesselPersistentId);
            if (chain == null
                || chain.OriginalVesselPid != rec.VesselPersistentId
                || !string.Equals(chain.TipRecordingId, rec.RecordingId, StringComparison.Ordinal))
            {
                reason = ReasonNotChainTip;
                return false;
            }
            if (chain.IsTerminated)
            {
                reason = ReasonTerminated;
                return false;
            }
            if (!playheadSeenBeforeTip)
            {
                reason = ReasonNotRewoundBeforeTip;
                return false;
            }
            double lastClaimUT = LatestClaimUT(chain);
            if (double.IsNaN(liveVesselLastUT) || double.IsNaN(lastClaimUT)
                || liveVesselLastUT >= lastClaimUT)
            {
                reason = ReasonSimulatedSinceClaim;
                return false;
            }
            if (liveVesselInUse)
            {
                reason = ReasonInUse;
                return false;
            }

            reason = ReasonReplace;
            return true;
        }

        /// <summary>The UT of the chain's latest claim, or NaN when it carries none.</summary>
        internal static double LatestClaimUT(GhostChain chain)
        {
            if (chain == null || chain.Links == null || chain.Links.Count == 0)
                return double.NaN;
            double latest = double.NegativeInfinity;
            for (int i = 0; i < chain.Links.Count; i++)
            {
                double ut = chain.Links[i].ut;
                if (!double.IsNaN(ut) && ut > latest)
                    latest = ut;
            }
            return double.IsNegativeInfinity(latest) ? double.NaN : latest;
        }

        /// <summary>
        /// Shared entry for the end-of-recording sites that resolve the live source vessel
        /// themselves (the Space Center end spawn and the flight leaf spawn). Reads the same
        /// guid-aware existence the adoption path reads, then defers to
        /// <see cref="TryReplaceStaleLiveVessel"/>. The #226 replay bypass never adopts, so it
        /// never replaces either. Returns true when the stale vessel is gone and the caller
        /// must spawn the tip with its identity preserved.
        /// </summary>
        internal static bool TryReplaceStaleSourceBeforeSpawn(
            Recording rec,
            string scene,
            int index,
            out StaleChainVesselFocus focus,
            bool allowExistingSourceDuplicate = false)
        {
            focus = default(StaleChainVesselFocus);
            if (allowExistingSourceDuplicate || !IsReplacementCandidate(rec))
                return false;

            bool liveSameLaunchVesselExists =
                VesselSpawner.MaterializedSourceVesselExists(rec, logAdoptionRejection: false);
            return TryReplaceStaleLiveVessel(
                rec, null, liveSameLaunchVesselExists, scene, index, out focus);
        }

        /// <summary>
        /// Evaluates <see cref="ShouldReplaceStaleLiveVessel"/> for a site that already knows
        /// whether a live same-launch vessel exists, and on a yes removes the stale vessel.
        /// <paramref name="chains"/> may be null: the chains are then walked from the
        /// committed trees, only once the cheap checks pass. Returns true when the stale
        /// vessel is gone; false leaves the site on its existing (adoption) path, including
        /// when the removal itself fails.
        /// </summary>
        internal static bool TryReplaceStaleLiveVessel(
            Recording rec,
            Dictionary<uint, GhostChain> chains,
            bool liveSameLaunchVesselExists,
            string scene,
            int index,
            out StaleChainVesselFocus focus)
        {
            focus = default(StaleChainVesselFocus);
            if (!IsReplacementCandidate(rec) || !liveSameLaunchVesselExists)
                return false;

            if (chains == null)
                chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 0.0);

            bool seenBeforeTip = PlaybackScopeTracker.WasPlayheadSeenBeforeActivation(rec.RecordingId);
            GhostChain chain = GhostChainWalker.FindChainForVessel(chains, rec.VesselPersistentId);
            bool candidateTip = chain != null
                && !chain.IsTerminated
                && string.Equals(chain.TipRecordingId, rec.RecordingId, StringComparison.Ordinal);
            double liveLastUT = candidateTip && seenBeforeTip
                ? ResolveLiveVesselLastUT(rec.VesselPersistentId)
                : double.NaN;
            string inUseWhy = null;
            bool inUse = candidateTip && seenBeforeTip
                && IsLiveVesselInUse(rec.VesselPersistentId, out inUseWhy);

            bool replace = ShouldReplaceStaleLiveVessel(
                rec, chains, liveSameLaunchVesselExists, seenBeforeTip, liveLastUT, inUse,
                out string reason);
            string sceneLabel = string.IsNullOrEmpty(scene) ? "(none)" : scene;
            if (!replace)
            {
                if (reason == ReasonInUse)
                {
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Stale chain-tip vessel kept ({0}) #{1} \"{2}\": live pid={3} is {4} - " +
                            "adopting it as before, the recorded tip state is not applied rec={5}",
                            sceneLabel, index, rec.VesselName ?? "(null)", rec.VesselPersistentId,
                            inUseWhy ?? "in use", rec.RecordingId));
                }
                else if (chain != null)
                {
                    ParsekLog.VerboseRateLimited(Tag,
                        "stale-tip-keep|" + rec.RecordingId + "|" + reason,
                        string.Format(ic,
                            "Live vessel on a claimed pid left to adoption ({0}) #{1} \"{2}\": pid={3} reason={4} " +
                            "liveLastUT={5} lastClaimUT={6} rec={7}",
                            sceneLabel, index, rec.VesselName ?? "(null)", rec.VesselPersistentId,
                            reason, liveLastUT.ToString("F1", ic),
                            LatestClaimUT(chain).ToString("F1", ic), rec.RecordingId));
                }
                return false;
            }

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Replacing stale pre-claim vessel ({0}) #{1} \"{2}\": live pid={3} is the chain's " +
                    "original before its recorded change; the tip spawns from its snapshot with identity " +
                    "preserved rec={4} originalPid={5} links={6} liveLastUT={7} lastClaimUT={8} spawnUT={9}",
                    sceneLabel, index, rec.VesselName ?? "(null)", rec.VesselPersistentId,
                    rec.RecordingId, chain.OriginalVesselPid, chain.Links != null ? chain.Links.Count : 0,
                    liveLastUT.ToString("F1", ic), LatestClaimUT(chain).ToString("F1", ic),
                    chain.SpawnUT.ToString("F1", ic)));

            if (!DespawnStaleVessel(rec.VesselPersistentId, sceneLabel, out focus))
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Stale chain-tip vessel removal failed ({0}) #{1} \"{2}\" pid={3} - adopting it as before rec={4}",
                        sceneLabel, index, rec.VesselName ?? "(null)", rec.VesselPersistentId,
                        rec.RecordingId));
                return false;
            }

            GhostPlaybackLogic.InvalidateVesselCache();
            return true;
        }

        private static bool IsReplacementCandidate(Recording rec)
        {
            return rec != null
                && rec.VesselPersistentId != 0
                && !string.IsNullOrEmpty(rec.RecordingId)
                && !rec.VesselSpawned
                && rec.SpawnedVesselPersistentId == 0;
        }

        /// <summary>
        /// The live vessel's <c>lastUT</c> (an unloaded vessel's equals its ProtoVessel's), or
        /// NaN when it cannot be read.
        /// </summary>
        private static double ResolveLiveVesselLastUT(uint pid)
        {
            if (LiveVesselLastUTOverrideForTesting != null)
                return LiveVesselLastUTOverrideForTesting(pid);

            try
            {
                return ResolveLiveVesselLastUTCore(pid);
            }
            catch (Exception ex) when (IsHeadlessAccessFailure(ex))
            {
                return double.NaN;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ResolveLiveVesselLastUTCore(uint pid)
        {
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v != null && v.persistentId == pid && !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                        return v.lastUT;
                }
            }

            var flightState = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flightState : null;
            if (flightState != null && flightState.protoVessels != null)
            {
                for (int i = 0; i < flightState.protoVessels.Count; i++)
                {
                    ProtoVessel pv = flightState.protoVessels[i];
                    if (pv != null && pv.persistentId == pid && !GhostMapPresence.IsGhostMapVessel(pv.persistentId))
                        return pv.lastUT;
                }
            }
            return double.NaN;
        }

        /// <summary>
        /// True when the live vessel is in the player's hands: the active vessel (removing it
        /// would yank the camera and the player's control), or a vessel the live recording tree
        /// records (removing it would stamp a destruction terminal on a live recording).
        /// </summary>
        private static bool IsLiveVesselInUse(uint pid, out string why)
        {
            why = null;
            if (LiveVesselInUseOverrideForTesting != null)
            {
                bool inUse = LiveVesselInUseOverrideForTesting(pid);
                if (inUse)
                    why = "in use (test seam)";
                return inUse;
            }

            try
            {
                return IsLiveVesselInUseCore(pid, out why);
            }
            catch (Exception ex) when (IsHeadlessAccessFailure(ex))
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsLiveVesselInUseCore(uint pid, out string why)
        {
            why = null;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != null && active.persistentId == pid)
            {
                why = "the active vessel";
                return true;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            RecordingTree liveTree = flight != null ? flight.ActiveTreeForSerialization : null;
            if (liveTree != null && liveTree.BackgroundMap != null && liveTree.BackgroundMap.ContainsKey(pid))
            {
                why = "recorded by the live tree";
                return true;
            }
            return false;
        }

        private static bool DespawnStaleVessel(uint pid, string scene, out StaleChainVesselFocus focus)
        {
            focus = default(StaleChainVesselFocus);
            if (DespawnOverrideForTesting != null)
                return DespawnOverrideForTesting(pid, scene);

            try
            {
                return DespawnStaleVesselCore(pid, scene, out focus);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Stale chain-tip vessel removal threw ({0}) pid={1}: {2}: {3}",
                        scene, pid, ex.GetType().Name, ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Removes the stale vessel the way the flight ghosting does (<c>Vessel.Die()</c>, which
        /// frees its vessel and part pids synchronously and pays nothing), with its crew
        /// detached first. <c>Die()</c> on an unloaded vessel runs <c>MurderCrew</c>, and every
        /// vessel at the Space Center and in the Tracking Station is unloaded, so the crew are
        /// taken off the parts beforehand and set Available under the crew suppression guard
        /// (the same silent shape the rewind strip's orphaned-crew rescue leaves).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool DespawnStaleVesselCore(uint pid, string scene, out StaleChainVesselFocus focus)
        {
            focus = default(StaleChainVesselFocus);

            Vessel vessel = null;
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v != null && v.persistentId == pid && !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    {
                        vessel = v;
                        break;
                    }
                }
            }

            var flightState = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flightState : null;
            ProtoVessel proto = vessel != null ? vessel.protoVessel : null;
            if (proto == null && flightState != null && flightState.protoVessels != null)
            {
                for (int i = 0; i < flightState.protoVessels.Count; i++)
                {
                    ProtoVessel pv = flightState.protoVessels[i];
                    if (pv != null && pv.persistentId == pid && !GhostMapPresence.IsGhostMapVessel(pv.persistentId))
                    {
                        proto = pv;
                        break;
                    }
                }
            }

            if (vessel == null && proto == null)
                return false;
            if (vessel != null && vessel == FlightGlobals.ActiveVessel)
                return false;

            if (vessel != null)
                focus = CaptureFocus(vessel);

            ClaimedVesselRemoval.Remove(
                new LiveClaimedVesselRemovalTarget(
                    vessel, proto, flightState != null ? flightState.protoVessels : null),
                "chain-tip-replace " + scene);
            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Stale chain-tip vessel removed ({0}): pid={1} hadVessel={2} navTarget={3} mapFocus={4} tsSelected={5}",
                    scene, pid, vessel != null,
                    focus.WasNavigationTarget, focus.WasMapFocus, focus.WasTrackingStationSelected));
            return true;
        }

        private static StaleChainVesselFocus CaptureFocus(Vessel vessel)
        {
            var focus = default(StaleChainVesselFocus);
            if (FlightGlobals.fetch != null && FlightGlobals.fetch.VesselTarget != null)
                focus.WasNavigationTarget = FlightGlobals.fetch.VesselTarget.GetVessel() == vessel;
            if (PlanetariumCamera.fetch != null && vessel.mapObject != null)
                focus.WasMapFocus = PlanetariumCamera.fetch.target == vessel.mapObject;
            if (HighLogic.LoadedScene == GameScenes.TRACKSTATION)
            {
                SpaceTracking tracking = UnityEngine.Object.FindObjectOfType<SpaceTracking>();
                focus.WasTrackingStationSelected = tracking != null && tracking.SelectedVessel == vessel;
            }
            return focus;
        }

        private static bool IsHeadlessAccessFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException
                    || current is MethodAccessException
                    || current is MissingMethodException
                    || current is TypeInitializationException
                    || current is NullReferenceException)
                    return true;
            }
            return false;
        }
    }
}
