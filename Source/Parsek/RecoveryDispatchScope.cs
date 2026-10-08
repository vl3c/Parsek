using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>Who owns a progress event raised inside a vessel recovery.</summary>
    internal enum RecoveryEventOwnerKind
    {
        /// <summary>No recording: the event stays untagged (a KSC row, as before).</summary>
        None = 0,

        /// <summary>
        /// A committed recording (picked exactly like the recovery payout): the event is
        /// tagged to it and its ledger row is written at once, tagged to it.
        /// </summary>
        CommittedRecording = 1,

        /// <summary>
        /// A pending-tree recording that still owns the vessel: the event is tagged to it and
        /// the recording's commit books it (the payout takes the same route).
        /// </summary>
        PendingRecording = 2,
    }

    /// <summary>The owner decision for one recovery scope (see <see cref="RecoveryDispatchScope"/>).</summary>
    internal struct RecoveryEventOwner
    {
        public RecoveryEventOwnerKind Kind;
        public string RecordingId;
        public string Reason;

        public bool HasOwner =>
            Kind != RecoveryEventOwnerKind.None && !string.IsNullOrEmpty(RecordingId);

        internal static RecoveryEventOwner NoOwner(string reason)
        {
            return new RecoveryEventOwner { Kind = RecoveryEventOwnerKind.None, Reason = reason };
        }
    }

    /// <summary>
    /// QUICKLOAD-UNTAGGED-RECOVERY-MILESTONE-SURVIVES-F9: the window in which stock dispatches
    /// <c>GameEvents.onVesselRecovered</c> for ONE vessel. The Harmony prefixes in
    /// <c>Patches/RecoveryDispatchScopePatches.cs</c> open it on the stock methods that fire
    /// that event for a player or stock recovery, and their finalizers close it.
    ///
    /// <para>
    /// Why a scope (decompiled KSP 1.12.5): a progress node a recovery completes
    /// (<c>KSPAchievements.CrewRecovery</c> = <c>FirstCrewToSurvive</c>, and any other node
    /// listening on <c>onVesselRecovered</c>) completes INSIDE that dispatch, and
    /// <c>EventData.Fire</c> walks its listeners last-added-first. The node subscribes when
    /// <c>ProgressTracking</c> deploys it on scenario load, after
    /// <c>ParsekScenario.OnVesselRecovered</c> and stock <c>VesselRecovery</c> subscribed, so
    /// the node completes before Parsek or the payout hears of the recovery. At the Space
    /// Center or the Tracking Station there is no live recorder, so the milestone was recorded
    /// untagged and a quickload into a quicksave taken before the recovery kept its reward.
    /// </para>
    ///
    /// <para>
    /// Inside the scope, a progress event (a <c>MilestoneAchieved</c> and the resource
    /// changes stock books under <c>TransactionReasons.Progression</c>) that would otherwise
    /// be untagged is owned by the recovered vessel's recording, picked the way the payout is:
    /// a pending-tree recording that still owns the vessel first (the payout waits for that
    /// commit too), else <see cref="LedgerOrchestrator.PickRecoveryRecordingId(RecoveredVesselIdentity, double)"/>.
    /// A live recorder's tag always wins, and a vessel no recording matches stays untagged.
    /// The owner is resolved once per scope, at its first progress event.
    /// </para>
    /// </summary>
    internal static class RecoveryDispatchScope
    {
        private const string Tag = "RecoveryScope";

        /// <summary>The stock reason key progress-node rewards carry (<c>TransactionReasons.Progression</c>).</summary>
        internal const string ProgressionReasonKey = "Progression";

        private sealed class Frame
        {
            public RecoveredVesselIdentity Identity;
            public uint PersistentId;
            public string Route;
            public bool Resolved;
            public RecoveryEventOwner Owner;
            public int TaggedEvents;
            public int UntaggedEvents;
        }

        private static readonly List<Frame> frames = new List<Frame>();

        internal static int Depth => frames.Count;

        internal static bool IsOpen => frames.Count > 0;

        /// <summary>
        /// Pure: why a recovery dispatch opens no scope, or null to open one. A ghost map
        /// vessel, a rewind strip and Parsek's own crew-suppressed housekeeping recoveries are
        /// not a recovery of a recorded flight, and a nameless vessel cannot be matched.
        /// </summary>
        internal static string DecideOpenSkipReason(
            bool hasVessel, bool hasName, bool isGhostMapVessel, bool isRewinding, bool suppressCrewEvents)
        {
            if (!hasVessel) return "no-vessel";
            if (isGhostMapVessel) return "ghost-map-vessel";
            if (isRewinding) return "rewinding";
            if (suppressCrewEvents) return "programmatic-recovery";
            if (!hasName) return "no-name";
            return null;
        }

        /// <summary>
        /// Opens a scope for <paramref name="pv"/> (the live seam). Returns true when a scope
        /// was opened, so the caller's finalizer closes exactly what it opened.
        /// </summary>
        internal static bool TryOpen(ProtoVessel pv, string route)
        {
            RecoveredVesselIdentity identity = default(RecoveredVesselIdentity);
            uint pid = 0;
            bool isGhost = false;
            if (pv != null)
            {
                pid = pv.persistentId;
                identity = RecoveredVesselIdentity.FromRawName(
                    pv.vesselName, VesselLaunchIdentity.ReadLaunchGuid(pv), pid);
                isGhost = GhostMapPresence.IsGhostMapVessel(pid);
            }

            string skip = DecideOpenSkipReason(
                pv != null,
                identity.HasName,
                isGhost,
                RewindContext.IsRewinding,
                GameStateRecorder.SuppressCrewEvents);
            if (skip != null)
            {
                ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                    "Recovery scope not opened: route={0} pid={1} reason={2}",
                    route ?? "(none)", pid, skip));
                return false;
            }

            Open(identity, pid, route);
            return true;
        }

        /// <summary>Headless core of <see cref="TryOpen"/>.</summary>
        internal static void Open(RecoveredVesselIdentity identity, uint persistentId, string route)
        {
            frames.Add(new Frame
            {
                Identity = identity,
                PersistentId = persistentId,
                Route = route ?? "(none)",
            });
            ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                "Recovery scope opened: route={0} {1} pid={2} depth={3}",
                route ?? "(none)", identity.FormatForLog(), persistentId, frames.Count));
        }

        /// <summary>Closes the innermost scope (a no-op when none is open).</summary>
        internal static void Close(string route)
        {
            if (frames.Count == 0)
            {
                ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                    "Recovery scope close with none open: route={0}", route ?? "(none)"));
                return;
            }

            Frame frame = frames[frames.Count - 1];
            frames.RemoveAt(frames.Count - 1);
            ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                "Recovery scope closed: route={0} {1} pid={2} owner={3} recordingId={4} " +
                "taggedEvents={5} untaggedProgressEvents={6} depth={7}",
                frame.Route, frame.Identity.FormatForLog(), frame.PersistentId,
                frame.Resolved ? frame.Owner.Kind.ToString() : "unresolved",
                string.IsNullOrEmpty(frame.Owner.RecordingId) ? "(none)" : frame.Owner.RecordingId,
                frame.TaggedEvents, frame.UntaggedEvents, frames.Count));
        }

        /// <summary>
        /// Pure: true for the events a progress node's completion raises: the
        /// <c>MilestoneAchieved</c> itself and a funds / reputation / science change keyed
        /// <see cref="ProgressionReasonKey"/>. The recovery's own payout (<c>VesselRecovery</c>),
        /// crew and XP events are not progress events and keep their existing routing.
        /// </summary>
        internal static bool IsProgressEvent(GameStateEventType type, string key)
        {
            switch (type)
            {
                case GameStateEventType.MilestoneAchieved:
                    return true;
                case GameStateEventType.FundsChanged:
                case GameStateEventType.ReputationChanged:
                case GameStateEventType.ScienceChanged:
                    return string.Equals(key, ProgressionReasonKey, StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure owner decision, in the payout's order: a pending-tree recording that still
        /// owns the vessel first (exactly one terminal target, else no owner), then the
        /// committed pick, else no owner (a vessel Parsek never recorded).
        /// </summary>
        internal static RecoveryEventOwner DecideOwner(
            bool pendingMatched, int pendingTargetCount, string pendingTargetId, string committedPickId)
        {
            if (pendingMatched)
            {
                if (pendingTargetCount == 1 && !string.IsNullOrEmpty(pendingTargetId))
                {
                    return new RecoveryEventOwner
                    {
                        Kind = RecoveryEventOwnerKind.PendingRecording,
                        RecordingId = pendingTargetId,
                        Reason = "pending-tree-owner",
                    };
                }

                return RecoveryEventOwner.NoOwner(
                    pendingTargetCount == 0 ? "pending-owner-no-target" : "pending-owner-ambiguous");
            }

            if (!string.IsNullOrEmpty(committedPickId))
            {
                return new RecoveryEventOwner
                {
                    Kind = RecoveryEventOwnerKind.CommittedRecording,
                    RecordingId = committedPickId,
                    Reason = "recovered-recording",
                };
            }

            return RecoveryEventOwner.NoOwner("no-recording");
        }

        /// <summary>
        /// The owner of an event the recorder is about to emit untagged: none outside a scope
        /// or for a non-progress event; otherwise the innermost scope's owner, resolved at its
        /// first progress event and logged once.
        /// </summary>
        internal static RecoveryEventOwner ResolveOwnerForEvent(GameStateEventType type, string key, double ut)
        {
            if (frames.Count == 0)
                return RecoveryEventOwner.NoOwner("no-recovery-scope");
            if (!IsProgressEvent(type, key))
                return RecoveryEventOwner.NoOwner("not-a-progress-event");

            Frame frame = frames[frames.Count - 1];
            if (!frame.Resolved)
            {
                frame.Owner = ResolveOwner(frame.Identity, frame.PersistentId, ut);
                frame.Resolved = true;
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "Recovery-scoped progress event owner: route={0} {1} pid={2} ut={3} owner={4} " +
                    "recordingId={5} reason={6} firstEvent={7} key='{8}'",
                    frame.Route, frame.Identity.FormatForLog(), frame.PersistentId,
                    ut.ToString("F1", CultureInfo.InvariantCulture),
                    frame.Owner.Kind,
                    string.IsNullOrEmpty(frame.Owner.RecordingId) ? "(none)" : frame.Owner.RecordingId,
                    frame.Owner.Reason ?? "(none)", type, key ?? ""));
            }

            if (frame.Owner.HasOwner)
                frame.TaggedEvents++;
            else
                frame.UntaggedEvents++;
            return frame.Owner;
        }

        private static RecoveryEventOwner ResolveOwner(
            RecoveredVesselIdentity identity, uint persistentId, double ut)
        {
            bool pendingMatched = false;
            int targetCount = 0;
            string targetId = null;
            if (RecordingStore.HasPendingTree)
            {
                RecordingTree pendingTree = RecordingStore.PendingTree;
                foreach (Recording rec in pendingTree.Recordings.Values)
                {
                    if (!ParsekScenario.MatchesVessel(rec, identity, persistentId))
                        continue;
                    pendingMatched = true;
                    if (!ParsekScenario.IsTerminalEventTarget(rec, pendingTree))
                        continue;
                    targetCount++;
                    targetId = rec.RecordingId;
                }
            }

            string committedPick = pendingMatched
                ? null
                : LedgerOrchestrator.PickRecoveryRecordingId(identity, ut);
            return DecideOwner(pendingMatched, targetCount, targetId, committedPick);
        }

        internal static void ResetForTesting()
        {
            frames.Clear();
        }
    }
}
