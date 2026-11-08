using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    // Fork identity: the inherited identity copy onto the Re-Fly fork, its initial segment phase tag and the fork snapshot refresh from the live vessel.
    internal static partial class RewindInvoker
    {
        /// <summary>
        /// Copies identity-relevant fields from <paramref name="inheritFrom"/>
        /// onto <paramref name="provisional"/> for the in-place Re-Fly fork
        /// (issue #734). Pure field copies — no Unity types, no instance
        /// state — so xUnit can validate the contract directly.
        ///
        /// PR 3b review follow-up §3: extracting this block lets a
        /// regression test fail fast if a future refactor touches the
        /// inheritance set without preserving every field. The
        /// <c>ParentAnchorRecordingId</c> copy is the load-bearing current-schema
        /// debris-anchor inheritance the plan flags as a critical
        /// Re-Fly safety hook (plan §"Risk analysis": "Re-Fly inheritance
        /// loses the contract — High if RewindInvoker propagation is
        /// omitted").
        ///
        /// Side-effecting follow-up steps (`CapturePreReFlyAnchorTrajectoryFrom`,
        /// `FindTreeForReFlyFork`, the diagnostic log line) stay inline at
        /// the call site because they depend on the surrounding
        /// <c>sessionId</c>, instance state, or call-site context.
        ///
        /// Chain identity (<c>ChainId</c>/<c>ChainIndex</c>/<c>ChainBranch</c>)
        /// is intentionally NOT copied so the supersede table remains the
        /// only authority on chain-tip resolution — see the call-site
        /// comment in <c>AtomicMarkerWrite</c>.
        /// </summary>
        internal static void CopyInheritedIdentityForFork(
            Recording provisional,
            Recording inheritFrom)
        {
            if (provisional == null || inheritFrom == null) return;
            provisional.VesselPersistentId = inheritFrom.VesselPersistentId;
            // The re-fly fork restores from a quicksave that preserves the origin's Vessel.id, so it
            // is the same launch lineage. Inherit the launch guid (live capture overwrites it if the
            // fork later recaptures from a vessel that somehow differs).
            provisional.RecordedVesselGuid = inheritFrom.RecordedVesselGuid;
            provisional.VesselName = inheritFrom.VesselName;
            provisional.IsDebris = inheritFrom.IsDebris;
            // PR 3b: critical Re-Fly safety hook: propagate the debris
            // parent-anchor contract so a re-fly of a flight with debris children
            // doesn't silently lose the contract on the provisional. Without this,
            // the resolver's chain-walk would not have a parent recording id to
            // chase through supersede successors.
            provisional.ParentAnchorRecordingId = inheritFrom.ParentAnchorRecordingId;
            provisional.Generation = inheritFrom.Generation;
            // SegmentPhase / SegmentBodyName are intentionally NOT copied. Those fields
            // describe the most-recent segment of THIS recording — for the parent that
            // means its terminating phase, which is unrelated to where the fork actually
            // flies. The runtime tagger called from AtomicMarkerWrite immediately after
            // this helper populates them from the live post-Strip vessel instead. See
            // docs/dev/plans/fix-refly-fork-segment-phase-inheritance.md.
            provisional.StartBodyName = inheritFrom.StartBodyName;
            provisional.StartBiome = inheritFrom.StartBiome;
            provisional.StartSituation = inheritFrom.StartSituation;
            provisional.LaunchSiteName = inheritFrom.LaunchSiteName;
            provisional.VesselSnapshot = inheritFrom.VesselSnapshot != null
                ? inheritFrom.VesselSnapshot.CreateCopy()
                : null;
            provisional.GhostVisualSnapshot = inheritFrom.GhostVisualSnapshot != null
                ? inheritFrom.GhostVisualSnapshot.CreateCopy()
                : null;
        }

        /// <summary>
        /// Tags <paramref name="provisional"/>.<see cref="Recording.SegmentPhase"/>
        /// and <see cref="Recording.SegmentBodyName"/> from the live post-Strip
        /// vessel state at fork-creation time. Replaces the prior stale-inheritance
        /// path in <see cref="CopyInheritedIdentityForFork"/> — the fork is a new
        /// flight whose phase classification must come from its own starting state,
        /// not from the parent's most-recent-segment classification (which would be
        /// "atmo" for an inherited Suborbital flight even when the fork goes on to
        /// reach orbit).
        ///
        /// Delegates to <see cref="ParsekFlight.TagSegmentPhaseIfMissing"/> for the
        /// shared body/altitude/situation classification used by the rest of the
        /// runtime taggers, so the fork's tag uses the same vocabulary as every
        /// other recording's first segment.
        ///
        /// Logs Verbose at every branch (tagged / null-vessel / null-mainBody)
        /// for diagnostic visibility. Aligns with the sibling
        /// <see cref="TryRefreshForkSnapshotsFromLiveVessel"/> helper, which
        /// also runs immediately after this one in <see cref="AtomicMarkerWrite"/>
        /// and uses Verbose for the same null-vessel-in-test-fixture case.
        /// (Promoting either to Warn would fire on every existing
        /// <c>AtomicMarkerWriteTests</c> in-place test path that uses
        /// <c>MakeStripResult</c>'s null <c>SelectedVessel</c> stub, polluting
        /// the test log sink without an assertion.)
        /// </summary>
        internal static void TagForkInitialSegmentPhase(
            Recording provisional, Vessel liveVessel, string sessionId)
        {
            if (provisional == null) return;
            if (liveVessel == null)
            {
                ParsekLog.Verbose(InvokeTag,
                    "TagForkInitialSegmentPhase: live vessel null — leaving fork SegmentPhase " +
                    $"unset rec={provisional.RecordingId} sess={sessionId ?? "<none>"}");
                return;
            }

            ParsekFlight.TagSegmentPhaseIfMissing(provisional, liveVessel);

            if (string.IsNullOrEmpty(provisional.SegmentPhase))
            {
                ParsekLog.Verbose(InvokeTag,
                    $"TagForkInitialSegmentPhase: classification skipped (mainBody null) " +
                    $"rec={provisional.RecordingId} pid={liveVessel.persistentId} " +
                    $"sess={sessionId ?? "<none>"}");
                return;
            }

            ParsekLog.Verbose(InvokeTag,
                $"TagForkInitialSegmentPhase: tagged from live vessel " +
                $"rec={provisional.RecordingId} pid={liveVessel.persistentId} " +
                $"body={provisional.SegmentBodyName ?? "<none>"} " +
                $"phase={provisional.SegmentPhase} situation={liveVessel.situation} " +
                $"alt={liveVessel.altitude:F0}m sess={sessionId ?? "<none>"}");
        }

        /// <summary>
        /// Refreshes <see cref="Recording.VesselSnapshot"/> and
        /// <see cref="Recording.GhostVisualSnapshot"/> on an in-place Re-Fly
        /// fork from the live post-Strip vessel, if available, replacing the
        /// stale copies inherited via <see cref="CopyInheritedIdentityForFork"/>.
        ///
        /// <para>
        /// Why this is needed: the inheritance source's
        /// <c>GhostVisualSnapshot</c> was captured exactly once when the
        /// recording first started (see
        /// <c>FlightRecorder.captureInitialGhostSnapshot</c>) and is never
        /// refreshed by mid-flight events. <c>ProcessBreakupEvent</c> only
        /// refreshes <c>VesselSnapshot</c> on each breakup. So a fork created
        /// at a UT after staging or breakups inherits the pre-staging
        /// full-vessel ghost snapshot, and any subsequent ghost render of the
        /// fork shows the whole rocket instead of the current sub-assembly
        /// (issue surfaced in 2026-05-07 playtest: lower-stage Re-Fly spawned
        /// an 84-part Kerbal X ghost where the actual upper-stage capsule had
        /// shed all but a handful of parts).
        /// </para>
        ///
        /// <para>
        /// The strip selects exactly the vessel rooted at the slot's selected
        /// root part; <c>inPlaceContinuation</c> already gates on
        /// <c>inheritFrom.VesselPersistentId == stripResult.SelectedPid</c> so
        /// the live vessel is the right thing to snapshot. If
        /// <see cref="FlightRecorder.TryBackupSnapshot"/> fails (or the live
        /// vessel handle is null in tests), we leave the inherited copies in
        /// place — strictly no worse than before.
        /// </para>
        ///
        /// <para>
        /// Returns <c>true</c> when both snapshots were replaced from the
        /// live vessel, <c>false</c> when the inherited copies remain. The
        /// caller uses that flag to log <c>vesselSnapshot=live-refresh</c>
        /// vs <c>vesselSnapshot=copied</c> for diagnostic clarity.
        /// </para>
        /// </summary>
        internal static bool TryRefreshForkSnapshotsFromLiveVessel(
            Recording provisional, Vessel liveVessel, string sessionId)
        {
            if (provisional == null) return false;
            if (liveVessel == null)
            {
                ParsekLog.Verbose(InvokeTag,
                    $"TryRefreshForkSnapshotsFromLiveVessel: live vessel null — keeping inherited snapshots " +
                    $"rec={provisional.RecordingId} sess={sessionId ?? "<none>"}");
                return false;
            }

            ConfigNode freshSnap = FlightRecorder.TryBackupSnapshot(liveVessel);
            if (freshSnap == null)
            {
                ParsekLog.Warn(InvokeTag,
                    $"TryRefreshForkSnapshotsFromLiveVessel: TryBackupSnapshot returned null — keeping inherited snapshots " +
                    $"rec={provisional.RecordingId} pid={liveVessel.persistentId} sess={sessionId ?? "<none>"}");
                return false;
            }

            int liveParts = liveVessel.parts != null ? liveVessel.parts.Count : 0;
            int inheritedVesselParts = CountSnapshotParts(provisional.VesselSnapshot);
            int inheritedGhostParts = CountSnapshotParts(provisional.GhostVisualSnapshot);

            provisional.VesselSnapshot = freshSnap;
            provisional.GhostVisualSnapshot = freshSnap.CreateCopy();

            ParsekLog.Info(InvokeTag,
                $"TryRefreshForkSnapshotsFromLiveVessel: refreshed fork snapshots from live vessel " +
                $"rec={provisional.RecordingId} pid={liveVessel.persistentId} liveParts={liveParts} " +
                $"replaced(vesselSnapshotParts={inheritedVesselParts} ghostSnapshotParts={inheritedGhostParts}) " +
                $"sess={sessionId ?? "<none>"}");
            return true;
        }

        /// <summary>
        /// Counts <c>PART</c> child nodes of a backed-up vessel ConfigNode for
        /// diagnostic logging. Returns 0 for null/malformed snapshots —
        /// purely informational, no behavioural impact.
        /// </summary>
        internal static int CountSnapshotParts(ConfigNode snap)
        {
            if (snap == null) return 0;
            try
            {
                var parts = snap.GetNodes("PART");
                return parts != null ? parts.Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
