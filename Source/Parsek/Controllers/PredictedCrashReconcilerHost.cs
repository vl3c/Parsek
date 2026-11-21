using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The live shell of <see cref="PredictedCrashReconciler"/> (owner rulings P-STOCK /
    /// P-LEAVE / Q1b): in the two scenes where stock's <c>Vessel.CheckKill</c> runs (FLIGHT and
    /// TRACKSTATION), once the scene has settled, it compares the committed recordings that end
    /// in a predicted crash with the live vessel list and drops a prediction stock contradicted.
    /// <para>Cost: one pass at the settle, then a pass only when the committed list changed,
    /// the clock passed the earliest predicted crash still ahead, or a candidate needs a second
    /// look (awaiting confirmation, or still falling toward stock's deletion line). A pass walks
    /// the effective recordings once and the live vessel list at most once.</para>
    /// <para>Nothing in the type knot references this host (it is reached only by stock's addon
    /// loader), so it never joins the knot.</para>
    /// </summary>
    internal abstract class PredictedCrashReconcilerHost : MonoBehaviour
    {
        private const string Tag = PredictedCrashReconciler.LogTag;

        /// <summary>Real seconds after FlightGlobals is ready before the first pass.</summary>
        internal const float SettleRealSeconds = 5f;

        /// <summary>Real seconds before a candidate stock may still remove is looked at again.</summary>
        internal const float PendingRecheckRealSeconds = 10f;

        private bool inert;
        private float readySince = -1f;
        private bool evaluatedOnce;
        private int lastStateVersion = int.MinValue;
        private double nextDueUT = double.PositiveInfinity;
        private float nextRecheckRealtime = float.PositiveInfinity;
        private readonly Dictionary<string, double> firstObserved =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly HashSet<string> failedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> goneLogged = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> touchdownFirstObserved =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly HashSet<string> touchdownFailedIds = new HashSet<string>(StringComparer.Ordinal);

        protected abstract string SceneName { get; }

        private void Start()
        {
            inert = ParsekGameModeGate.CheckInert("PredictedCrashReconcilerHost.Start");
            ParsekLog.Verbose(Tag,
                $"Host started: scene={SceneName} inert={inert} settleSeconds=" +
                SettleRealSeconds.ToString("F0", CultureInfo.InvariantCulture));
        }

        private void Update()
        {
            if (inert)
                return;
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // A broken pass must never take the scene with it; the next trigger retries.
                ParsekLog.WarnRateLimited(Tag, "tick-threw-" + SceneName,
                    $"Pass threw {ex.GetType().Name}: {ex.Message} (scene={SceneName})");
                nextRecheckRealtime = ReadRealtimeCore() + PendingRecheckRealSeconds;
            }
        }

        private void Tick()
        {
            float now = ReadRealtimeCore();
            if (!ReadFlightGlobalsReadyCore())
            {
                readySince = -1f;
                return;
            }
            if (readySince < 0f)
                readySince = now;
            if (now - readySince < SettleRealSeconds)
                return;

            double ut = ReadUniversalTimeCore();
            int stateVersion = RecordingStore.StateVersion;
            bool due = !evaluatedOnce
                || stateVersion != lastStateVersion
                || ut >= nextDueUT
                || now >= nextRecheckRealtime;
            if (!due)
                return;

            evaluatedOnce = true;
            RunPass(ut, now);
            // Read after the pass: its own commit moves the version and needs no second pass.
            lastStateVersion = RecordingStore.StateVersion;
        }

        private void RunPass(double ut, float now)
        {
            string activeTreeId = ParsekFlight.Instance != null
                ? ParsekFlight.Instance.ActiveTreeForSerialization?.Id
                : null;
            string pendingTreeId = PendingTreeSlot.PendingTree?.Id;
            bool reFlyLive = ParsekScenario.Instance != null
                && ParsekScenario.Instance.ActiveReFlySessionMarker != null;
            Func<Recording, bool> ownedElsewhere = rec =>
                reFlyLive
                || (activeTreeId != null && string.Equals(rec.TreeId, activeTreeId, StringComparison.Ordinal))
                || (pendingTreeId != null && string.Equals(rec.TreeId, pendingTreeId, StringComparison.Ordinal));

            Dictionary<string, Vessel> liveByGuid = null;
            Func<Recording, PredictedCrashLiveFacts> lookup = rec =>
            {
                if (liveByGuid == null)
                    liveByGuid = BuildLiveVesselsByGuid();
                return ReadLiveFacts(rec, liveByGuid, ut);
            };

            // Q13b: the crash pass reads the touchdown pass's evidence too. A live tree that
            // docked with or boarded the vessel owns it (its commit takes the predicted ending
            // over), and a recovery or termination row explains its absence: neither is stock
            // confirming the crash.
            RecordingTree activeTreeForMerge = ParsekFlight.Instance != null
                ? ParsekFlight.Instance.ActiveTreeForSerialization
                : null;
            RecordingTree pendingTreeForMerge = PendingTreeSlot.PendingTree;
            Func<Recording, bool> crashOwnedElsewhere = rec =>
                ownedElsewhere(rec)
                || IsMergePartnerOfLiveTree(rec, activeTreeForMerge)
                || IsMergePartnerOfLiveTree(rec, pendingTreeForMerge);
            Func<Recording, bool> crashRemovedByPlayer = EffectiveState.HasRecoveryOrTerminationRowLive;

            PredictedCrashReconcilePassResult pass = PredictedCrashReconciler.EvaluatePass(
                EffectiveState.ComputeERS(), crashOwnedElsewhere, lookup, ut, sceneSettled: true,
                realtimeNow: now, firstObserved: firstObserved, skipIds: failedIds,
                isRemovedByPlayer: crashRemovedByPlayer);

            int applied = 0, failed = 0;
            for (int i = 0; i < pass.Contradicted.Count; i++)
            {
                Recording rec = pass.Contradicted[i];
                if (ApplyOne(rec, liveByGuid, ut))
                    applied++;
                else
                {
                    failed++;
                    failedIds.Add(rec.RecordingId);
                }
                firstObserved.Remove(rec.RecordingId);
            }

            int newlyGone = 0;
            for (int i = 0; i < pass.Gone.Count; i++)
            {
                Recording rec = pass.Gone[i];
                if (!goneLogged.Add(rec.RecordingId))
                    continue;
                newlyGone++;
                if (newlyGone <= 20)
                    ParsekLog.Verbose(Tag,
                        $"Predicted crash confirmed by stock: rec={rec.RecordingId} vessel='{rec.VesselName}' " +
                        $"predictedCrashUT={rec.EndUT.ToString("F3", CultureInfo.InvariantCulture)} - no vessel " +
                        "with its launch guid remains; the predicted Destroyed (crew dead) already says what " +
                        "stock did, nothing changes");
            }

            PredictedTouchdownPassResult touchdown = RunTouchdownPass(
                ut, now, ownedElsewhere, lookup, () => liveByGuid, out int tdDeleted, out int tdContradicted,
                out int tdFailed);

            nextDueUT = pass.NextDueUT;
            bool awaiting = pass.Count(PredictedCrashReconcileOutcome.AwaitingConfirmation) > 0
                || touchdown.AwaitingConfirmation;
            nextRecheckRealtime = pass.NeedsRecheck || touchdown.NeedsRecheck
                ? now + (awaiting
                    ? (float)PredictedCrashReconciler.ConfirmRealSeconds + 0.5f
                    : PendingRecheckRealSeconds)
                : float.PositiveInfinity;

            string summary = PredictedCrashReconciler.FormatPassSummary(pass, SceneName, ut, applied, failed);
            if (applied > 0 || failed > 0 || newlyGone > 0)
                ParsekLog.Info(Tag, summary);
            else
                ParsekLog.VerboseRateLimited(Tag, "pass-" + SceneName, summary);
        }

        /// <summary>
        /// The predicted-touchdown half of a pass (owner rulings P-STOCK / Q4c / Q4d,
        /// <see cref="PredictedTouchdownStockFate"/>): stamps every candidate stock holds now,
        /// records stock's deletion of a craft it held in this timeline, and drops a touchdown
        /// stock contradicted. A live tree that docked with or boarded the craft owns it (the
        /// commit takes the predicted ending over); a recovery or termination row explains a
        /// disappearance (Q13).
        /// </summary>
        private PredictedTouchdownPassResult RunTouchdownPass(
            double ut, float now, Func<Recording, bool> ownedBySession,
            Func<Recording, PredictedCrashLiveFacts> lookup, Func<Dictionary<string, Vessel>> liveMap,
            out int deleted, out int contradicted, out int failed)
        {
            deleted = 0;
            contradicted = 0;
            failed = 0;
            RecordingTree activeTree = ParsekFlight.Instance != null
                ? ParsekFlight.Instance.ActiveTreeForSerialization
                : null;
            RecordingTree pendingTree = PendingTreeSlot.PendingTree;
            Func<Recording, bool> ownedElsewhere = rec =>
                ownedBySession(rec)
                || IsMergePartnerOfLiveTree(rec, activeTree)
                || IsMergePartnerOfLiveTree(rec, pendingTree);
            Func<Recording, bool> removedByPlayer = rec =>
                EffectiveState.IsRecoveredByLedgerRowLive(rec) || EffectiveState.IsTerminatedByLedgerRowLive(rec);

            PredictedTouchdownPassResult pass = PredictedTouchdownStockFate.EvaluatePass(
                EffectiveState.ComputeERS(), ownedElsewhere, removedByPlayer, lookup, ut,
                sceneSettled: true, realtimeNow: now, firstObserved: touchdownFirstObserved,
                skipIds: touchdownFailedIds);

            int stamped = 0;
            for (int i = 0; i < pass.Sighted.Count; i++)
                if (PredictedTouchdownStockFate.NoteStockHeld(pass.Sighted[i], ut))
                    stamped++;

            for (int i = 0; i < pass.StockDeleted.Count; i++)
            {
                Recording rec = pass.StockDeleted[i];
                RecordingTree tree = RecordingStore.FindCommittedTreeById(rec.TreeId);
                PredictedCrashContradictionResult result = tree != null
                    ? PredictedCrashReconciler.ApplyStockDeletion(
                        tree, rec.RecordingId, ut, PredictedTouchdownStockFate.LogTag + ":" + SceneName)
                    : default(PredictedCrashContradictionResult);
                if (result.Applied)
                    deleted++;
                else
                {
                    failed++;
                    touchdownFailedIds.Add(rec.RecordingId);
                    if (tree == null)
                        ParsekLog.Warn(PredictedTouchdownStockFate.LogTag,
                            $"Predicted touchdown stock deletion not applied: rec={rec.RecordingId} " +
                            $"tree={rec.TreeId ?? "(null)"} treeFound=false");
                }
                touchdownFirstObserved.Remove(rec.RecordingId + "|gone");
            }

            for (int i = 0; i < pass.Contradicted.Count; i++)
            {
                Recording rec = pass.Contradicted[i];
                if (ApplyOne(rec, liveMap(), ut))
                    contradicted++;
                else
                {
                    failed++;
                    touchdownFailedIds.Add(rec.RecordingId);
                }
                touchdownFirstObserved.Remove(rec.RecordingId + "|contradicted");
            }

            string summary = PredictedTouchdownStockFate.FormatPassSummary(
                pass, SceneName, ut, deleted, contradicted, failed) +
                " stamped=" + stamped.ToString(CultureInfo.InvariantCulture);
            if (deleted > 0 || contradicted > 0 || failed > 0)
                ParsekLog.Info(PredictedTouchdownStockFate.LogTag, summary);
            else if (pass.Candidates > 0)
                ParsekLog.VerboseRateLimited(PredictedTouchdownStockFate.LogTag, "pass-" + SceneName, summary);
            return pass;
        }

        /// <summary>
        /// Pure: <paramref name="tree"/> (a live tree) docked with or boarded the vessel of
        /// <paramref name="rec"/> (a Dock / Board branch point whose partner stamp names its
        /// launch or its Parsek spawn): the merge, not stock, took the craft out of the world.
        /// </summary>
        internal static bool IsMergePartnerOfLiveTree(Recording rec, RecordingTree tree)
        {
            if (rec == null || tree?.BranchPoints == null)
                return false;
            if (string.Equals(rec.TreeId, tree.Id, StringComparison.Ordinal))
                return false;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp == null || (bp.Type != BranchPointType.Dock && bp.Type != BranchPointType.Board))
                    continue;
                if (bp.TargetVesselPersistentId == 0u)
                    continue;
                if (VesselLaunchIdentity.CandidateMatchesRecording(
                        rec, bp.TargetVesselPersistentId, bp.TargetVesselGuid, matchSource: true, matchSpawn: true))
                    return true;
            }
            return false;
        }

        private bool ApplyOne(Recording rec, Dictionary<string, Vessel> liveByGuid, double ut)
        {
            RecordingTree tree = RecordingStore.FindCommittedTreeById(rec.TreeId);
            Vessel vessel = null;
            string guid = VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid);
            if (liveByGuid != null && guid != null)
                liveByGuid.TryGetValue(guid, out vessel);
            if (tree == null || ReferenceEquals(vessel, null))
            {
                ParsekLog.Warn(Tag,
                    $"Predicted crash contradiction not applied: rec={rec.RecordingId} tree={rec.TreeId ?? "(null)"} " +
                    $"treeFound={tree != null} vesselFound={!ReferenceEquals(vessel, null)}");
                return false;
            }
            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyContradiction(
                tree,
                rec.RecordingId,
                ut,
                ParsekFlight.ReadTakeoverCoastEvidence(vessel, ut),
                clone => ParsekFlight.StampTerminalStateFromLiveVessel(
                    clone, vessel, isSceneExit: false,
                    ParsekFlight.FinalizationLiveVesselAccess.Default),
                "PredictedCrashReconcile:" + SceneName);
            return result.Applied;
        }

        private static Dictionary<string, Vessel> BuildLiveVesselsByGuid()
        {
            var map = new Dictionary<string, Vessel>(StringComparer.OrdinalIgnoreCase);
            List<Vessel> vessels = ReadFlightGlobalsVesselsCore();
            if (vessels == null)
                return map;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (ReferenceEquals(v, null))
                    continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    continue;
                if (v.id == Guid.Empty)
                    continue;
                string key = v.id.ToString("N", CultureInfo.InvariantCulture);
                if (!map.ContainsKey(key))
                    map[key] = v;
            }
            return map;
        }

        private static PredictedCrashLiveFacts ReadLiveFacts(
            Recording rec, Dictionary<string, Vessel> liveByGuid, double ut)
        {
            var facts = new PredictedCrashLiveFacts
            {
                Eccentricity = double.NaN,
                PeriapsisAltitude = double.NaN,
            };
            string guid = VesselLaunchIdentity.NormalizeGuid(rec?.RecordedVesselGuid);
            if (guid == null || liveByGuid == null || !liveByGuid.TryGetValue(guid, out Vessel v)
                || ReferenceEquals(v, null))
                return facts;
            facts.Found = true;
            facts.IsActiveVessel = ReferenceEquals(ReadActiveVesselCore(), v);
            facts.LandedOrSplashed = v.LandedOrSplashed;
            facts.InPhysics = v.loaded && !v.packed;
            Orbit orbit = v.orbit;
            if (orbit != null && orbit.referenceBody != null)
            {
                facts.Eccentricity = orbit.eccentricity;
                facts.PeriapsisAltitude = orbit.PeA;
                Vector3d pos = orbit.getRelativePositionAtUT(ut);
                Vector3d vel = orbit.getOrbitalVelocityAtUT(ut);
                facts.Descending = Vector3d.Dot(pos, vel) < 0.0;
                facts.StockRemovalAltitude = OrbitClearance.ResolveDeletionAltitude(orbit.referenceBody);
            }
            return facts;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float ReadRealtimeCore()
        {
            return Time.realtimeSinceStartup;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadFlightGlobalsReadyCore()
        {
            return FlightGlobals.ready;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadUniversalTimeCore()
        {
            return Planetarium.GetUniversalTime();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<Vessel> ReadFlightGlobalsVesselsCore()
        {
            return FlightGlobals.Vessels;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vessel ReadActiveVesselCore()
        {
            return HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
        }
    }

    /// <summary>The reconciler in the flight scene.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class PredictedCrashReconcilerFlight : PredictedCrashReconcilerHost
    {
        protected override string SceneName => "FLIGHT";
    }

    /// <summary>The reconciler in the Tracking Station.</summary>
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    internal sealed class PredictedCrashReconcilerTrackingStation : PredictedCrashReconcilerHost
    {
        protected override string SceneName => "TRACKSTATION";
    }
}
