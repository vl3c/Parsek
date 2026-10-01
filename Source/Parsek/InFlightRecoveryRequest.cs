using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The flight-scene Recover button's intent, carried from the moment stock announces it
    /// to Parsek's scene-exit finalize, so a recovered flight commits
    /// <see cref="TerminalState.Recovered"/> instead of its landing situation (operator
    /// ruling 2026-10-01).
    ///
    /// <para>
    /// Stock order (decompiled KSP 1.12.5): <c>AltimeterSliderButtons.recoverVessel</c>
    /// fires <c>GameEvents.OnVesselRecoveryRequested(FlightGlobals.ActiveVessel)</c> while
    /// still in FLIGHT. Stock's own listener <c>VesselRetrieval.onVesselRecoveryRequested</c>
    /// remembers <c>v.id</c>, runs <c>GamePersistence.SaveGame</c>, then
    /// <c>HighLogic.LoadScene(SPACECENTER)</c>; only after the Space Center has loaded (8
    /// frames later) does <c>VesselRetrieval.recoverVessels</c> fire
    /// <c>onVesselRecovered</c>. Parsek finalizes the tree inside that LoadScene (the
    /// scene-exit stash, or the pre-transition merge dialog's finalize when auto-merge is
    /// off) and the auto-merge commit runs in the Space Center's <c>OnLoad</c>, so both
    /// commits used to land before the recovery and read the vessel's situation (Landed).
    /// kRPC's <c>Vessel.Recover</c> fires the same event.
    /// </para>
    ///
    /// <para>
    /// Why this is not a stamp on a committed recording: the request is applied to the
    /// ACTIVE tree during its scene-exit finalize (or to the just-stashed pending tree if
    /// stock's listener happened to run first), i.e. before the commit, exactly where the
    /// manual Space Center path stamps the pending tree from <c>onVesselRecovered</c>
    /// (<see cref="ParsekScenario.UpdateRecordingsForTerminalEvent(RecoveredVesselIdentity, TerminalState, double, uint)"/>).
    /// A revert cannot follow it: once the Recover button fires, stock saves and loads the
    /// Space Center, and a flight revert is not offered there. Loading an older save
    /// restores that save's own recordings, which never saw the stamp. The request is held
    /// in memory only and is dropped at the end of the scene change that consumes it, on a
    /// FLIGHT destination, and on any game load.
    /// </para>
    ///
    /// <para>
    /// Identity is the launch (<see cref="VesselLaunchIdentity.LiveVesselIsRecordedLaunch"/>:
    /// pid plus a launch guid that does not conclusively differ), never the vessel name, and
    /// only the vessel's tip segment is stamped
    /// (<see cref="ParsekScenario.IsTerminalEventTarget"/>), as on the pending-tree path.
    /// </para>
    /// </summary>
    internal static class InFlightRecoveryRequest
    {
        private const string Tag = "Recovery";

        internal sealed class Request
        {
            public uint VesselPid;
            public string LaunchGuid;
            public string VesselName;
            public double RequestUT;
        }

        private static Request armed;

        // Recordings stamped Recovered by a request whose stock payout has not arrived yet.
        // The commit-time recovery pairing (LedgerOrchestrator.AddVesselRecoveryCostActions)
        // finds no FundsChanged(VesselRecovery) event for them (it fires at the Space Center
        // after the commit) and must not fall back to the last-two-points funds heuristic:
        // the real payout is written by the #444 path when onVesselRecovered fires.
        private static readonly HashSet<string> awaitingStockPayout =
            new HashSet<string>(StringComparer.Ordinal);

        internal static Request Armed => armed;

        internal static void Arm(uint vesselPid, string launchGuid, string vesselName, double requestUT)
        {
            armed = new Request
            {
                VesselPid = vesselPid,
                LaunchGuid = VesselLaunchIdentity.NormalizeGuid(launchGuid),
                VesselName = vesselName,
                RequestUT = requestUT,
            };
            ParsekLog.Info(Tag,
                $"In-flight recovery requested: vessel='{vesselName ?? "(null)"}' pid={vesselPid} " +
                $"guid={armed.LaunchGuid ?? "(none)"} " +
                $"ut={requestUT.ToString("F1", CultureInfo.InvariantCulture)} - " +
                "the scene-exit finalize will commit its recording Recovered");
        }

        internal static void Clear(string reason)
        {
            if (armed == null) return;
            ParsekLog.Info(Tag,
                $"In-flight recovery request dropped unapplied: vessel='{armed.VesselName ?? "(null)"}' " +
                $"pid={armed.VesselPid} reason={reason ?? "(none)"}");
            armed = null;
        }

        /// <summary>
        /// Applies and consumes the armed request against <paramref name="tree"/> at a
        /// scene-exit finalize. A destination other than the Space Center (the only scene
        /// stock's recovery loads) drops the request unapplied. Returns the number of
        /// recordings stamped.
        /// </summary>
        internal static int ApplyAtSceneExit(RecordingTree tree, GameScenes destination, string context)
        {
            var req = armed;
            if (req == null) return 0;
            armed = null;
            if (destination != GameScenes.SPACECENTER)
            {
                ParsekLog.Info(Tag,
                    $"In-flight recovery request dropped: vessel='{req.VesselName ?? "(null)"}' " +
                    $"pid={req.VesselPid} dest={destination} context={context} " +
                    "(stock recovery always loads the Space Center)");
                return 0;
            }
            return StampRecovered(tree, req, context);
        }

        /// <summary>
        /// The late-listener path: when stock's <c>VesselRetrieval</c> ran before Parsek's
        /// listener, the tree was already finalized and stashed pending by the time the
        /// request arrives. Applies and consumes the request only when the pending tree
        /// holds this launch's tip, so an unrelated pending tree cannot swallow it.
        /// </summary>
        internal static int TryApplyToFinalizedPendingTree(RecordingTree pendingTree, string context)
        {
            var req = armed;
            if (req == null || pendingTree == null) return 0;
            if (!HasStampTarget(pendingTree, req)) return 0;
            armed = null;
            return StampRecovered(pendingTree, req, context);
        }

        internal static bool HasStampTarget(RecordingTree tree, Request req)
        {
            if (tree?.Recordings == null || req == null) return false;
            foreach (var rec in tree.Recordings.Values)
            {
                if (IsStampTarget(rec, tree, req))
                    return true;
            }
            return false;
        }

        private static bool IsStampTarget(Recording rec, RecordingTree tree, Request req)
        {
            return rec != null
                && VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, req.VesselPid, req.LaunchGuid)
                && ParsekScenario.CanOverwriteTerminalState(rec.TerminalStateValue, TerminalState.Recovered)
                && ParsekScenario.IsTerminalEventTarget(rec, tree);
        }

        /// <summary>
        /// Stamps the request's launch tip in <paramref name="tree"/> Recovered through the
        /// same stamp the pending-tree recovery path uses. The ghost-visual snapshot is
        /// preserved first so the replay still has geometry once the vessel snapshot is
        /// dropped.
        /// </summary>
        internal static int StampRecovered(RecordingTree tree, Request req, string context)
        {
            if (tree?.Recordings == null || req == null) return 0;
            int stamped = 0;
            int launchMatches = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null
                    || !VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, req.VesselPid, req.LaunchGuid))
                    continue;
                launchMatches++;
                if (!IsStampTarget(rec, tree, req))
                    continue;

                TerminalState? before = rec.TerminalStateValue;
                if (rec.GhostVisualSnapshot == null && rec.VesselSnapshot != null)
                    rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();
                double endUT = req.RequestUT;
                if (!double.IsNaN(rec.ExplicitEndUT) && rec.ExplicitEndUT > endUT)
                    endUT = rec.ExplicitEndUT;
                ParsekScenario.ApplyTerminalEventStamp(
                    rec, TerminalState.Recovered, endUT, "InFlightRecoveryRequest");
                rec.MarkFilesDirty();
                if (!string.IsNullOrEmpty(rec.RecordingId))
                    awaitingStockPayout.Add(rec.RecordingId);
                stamped++;
                ParsekLog.Info(Tag,
                    $"In-flight recovery: recording '{rec.RecordingId}' vessel='{rec.VesselName}' " +
                    $"pid={rec.VesselPersistentId} terminal {(before.HasValue ? before.Value.ToString() : "<none>")} -> Recovered " +
                    $"endUT={endUT.ToString("F1", CultureInfo.InvariantCulture)} context={context}");
            }

            if (stamped == 0)
            {
                ParsekLog.Info(Tag,
                    $"In-flight recovery: no recording stamped for vessel='{req.VesselName ?? "(null)"}' " +
                    $"pid={req.VesselPid} launchMatches={launchMatches} context={context} " +
                    "(not recorded in this tree, or its tip already ended)");
            }
            return stamped;
        }

        /// <summary>
        /// True (once) when <paramref name="recordingId"/> was stamped Recovered by a request
        /// and its stock payout is still to come; consumes the entry.
        /// </summary>
        internal static bool TryConsumeAwaitingStockPayout(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            return awaitingStockPayout.Remove(recordingId);
        }

        internal static void ResetForTesting()
        {
            armed = null;
            awaitingStockPayout.Clear();
        }
    }
}
