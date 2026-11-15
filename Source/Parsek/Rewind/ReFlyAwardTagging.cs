using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>Pure result of <see cref="ReFlyAwardTagging.Resolve"/>.</summary>
    internal struct ReFlyAwardTagDecision
    {
        /// <summary>The recording id the captured event is tagged with.</summary>
        public string Tag;

        /// <summary>True when <see cref="Tag"/> is the session's provisional rather than the live tag.</summary>
        public bool Substituted;

        /// <summary>Grep-stable reason token.</summary>
        public string Reason;
    }

    /// <summary>
    /// REFLY-UNBOUND-PROVISIONAL-TAGS-AWARDS-TO-ORIGIN (owner ruling 2026-10-15, option A):
    /// a career award earned in FLIGHT while a Re-Fly session's provisional has no recorder
    /// bound belongs to the Re-Fly attempt.
    ///
    /// <para>
    /// Between the rewind load and the recorder binding to the provisional (and for the whole
    /// session when that binding never happens, <c>outcome=unbound-refly-provisional</c>), the
    /// live tag is either empty (the re-fly tree is still pending in Limbo, so there is no
    /// active tree) or names the origin the merge supersedes. An empty tag files the award as a
    /// permanent untagged ledger row that survives a discarded attempt; an origin tag lets the
    /// attempt's own merge tombstone it. Either way the attempt's earnings are not the
    /// attempt's. Tagging the provisional instead keeps the award with the attempt: it is
    /// filed when the attempt merges and dropped with a discarded attempt, exactly like every
    /// award the attempt earns once its recorder binds.
    /// </para>
    ///
    /// <para>
    /// A live tag naming any other recording (the bound provisional itself, a child the attempt
    /// split off, a vessel the player switched to) is a bound recorder and is left alone.
    /// Outside FLIGHT the live tag is always kept: a KSC action is never the attempt's.
    /// </para>
    ///
    /// <para>
    /// Design section 2.12 Rule 3 (awards filed by cause): during a session, an event that
    /// names a vessel (a science subject's transmitting or recovered vessel, a lab payout's
    /// lab vessel, the vessel an open <see cref="RecoveryDispatchScope"/> recovers) is tagged
    /// to that vessel's recording in the session tree (<see cref="ResolveByCause"/>, reason
    /// <c>cause-vessel</c>), with the same origin-to-provisional substitution. An event that
    /// names no vessel, or a vessel with no recording in the session, keeps the live path.
    /// </para>
    /// </summary>
    internal static class ReFlyAwardTagging
    {
        internal const string ReasonNoMarker = "no-refly-session";
        internal const string ReasonNotInFlight = "not-in-flight";
        internal const string ReasonNoProvisional = "marker-has-no-provisional";
        internal const string ReasonBound = "bound-to-provisional";
        internal const string ReasonOtherRecording = "live-tag-names-other-recording";
        internal const string ReasonUnboundEmpty = "unbound-empty-tag";
        internal const string ReasonUnboundOrigin = "unbound-origin-tag";
        internal const string ReasonUnboundSupersedeTarget = "unbound-supersede-target-tag";
        internal const string ReasonCauseVessel = "cause-vessel";

        /// <summary>Test seam for the scene probe; null in production.</summary>
        internal static Func<bool> InFlightProviderForTesting;

        internal static void ResetForTesting()
        {
            InFlightProviderForTesting = null;
        }

        /// <summary>
        /// Decides the tag for a captured event. Pure.
        /// </summary>
        /// <param name="liveTag">The tag the live flight state resolved (empty when none).</param>
        /// <param name="inFlight">True in the FLIGHT scene.</param>
        /// <param name="marker">The live Re-Fly session marker, or null.</param>
        internal static ReFlyAwardTagDecision Resolve(
            string liveTag, bool inFlight, ReFlySessionMarker marker)
        {
            string live = liveTag ?? "";
            if (marker == null)
                return Keep(live, ReasonNoMarker);
            if (!inFlight)
                return Keep(live, ReasonNotInFlight);
            string provisional = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(provisional))
                return Keep(live, ReasonNoProvisional);
            if (string.Equals(live, provisional, StringComparison.Ordinal))
                return Keep(live, ReasonBound);

            string reason;
            if (live.Length == 0)
                reason = ReasonUnboundEmpty;
            else if (string.Equals(live, marker.OriginChildRecordingId, StringComparison.Ordinal))
                reason = ReasonUnboundOrigin;
            else if (!string.IsNullOrEmpty(marker.SupersedeTargetId)
                && string.Equals(live, marker.SupersedeTargetId, StringComparison.Ordinal))
                reason = ReasonUnboundSupersedeTarget;
            else
                return Keep(live, ReasonOtherRecording);

            return new ReFlyAwardTagDecision
            {
                Tag = provisional,
                Substituted = true,
                Reason = reason,
            };
        }

        /// <summary>
        /// Applies <see cref="Resolve"/> to the live scene and session, logging each
        /// substitution (rate-limited per session and reason: world-record breaks resolve
        /// the tag every physics frame of a climb).
        /// </summary>
        internal static string ApplyToLiveTag(string liveTag)
        {
            var scenario = ParsekScenario.Instance;
            ReFlySessionMarker marker = ReferenceEquals(null, scenario)
                ? null
                : scenario.ActiveReFlySessionMarker;
            if (marker == null)
                return liveTag ?? "";

            var decision = Resolve(liveTag, IsInFlight(), marker);
            if (decision.Substituted)
            {
                ParsekLog.InfoRateLimited("GameStateRecorder",
                    "refly-unbound-award-tag-" + (marker.SessionId ?? "") + "-" + decision.Reason,
                    string.Format(CultureInfo.InvariantCulture,
                        "Re-Fly award tag: outcome=tagged-to-attempt reason={0} sess={1} " +
                        "provisional={2} liveTag='{3}' origin={4}",
                        decision.Reason,
                        marker.SessionId ?? "<no-id>",
                        decision.Tag,
                        liveTag ?? "",
                        marker.OriginChildRecordingId ?? "<none>"));
            }
            return decision.Tag ?? "";
        }

        /// <summary>
        /// Pure: the recording of vessel <paramref name="pid"/> in the session tree, or null
        /// when the tree has none. The active recording when it flies that vessel, else the
        /// tree's background map entry, else an open vessel-line end of that pid.
        /// </summary>
        internal static string ResolveSessionRecordingForVessel(RecordingTree tree, uint pid)
        {
            if (tree == null || tree.Recordings == null || pid == 0u)
                return null;
            Recording rec;
            if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                && tree.Recordings.TryGetValue(tree.ActiveRecordingId, out rec)
                && rec != null && rec.VesselPersistentId == pid)
                return rec.RecordingId;
            string bgId;
            if (tree.BackgroundMap != null
                && tree.BackgroundMap.TryGetValue(pid, out bgId)
                && !string.IsNullOrEmpty(bgId)
                && tree.Recordings.ContainsKey(bgId))
                return bgId;
            string found = null;
            foreach (var kv in tree.Recordings)
            {
                Recording r = kv.Value;
                if (r == null || r.VesselPersistentId != pid || r.TerminalStateValue != null) continue;
                if (!tree.IsVesselLineEnd(r)) continue;
                if (found == null || string.CompareOrdinal(r.RecordingId, found) < 0)
                    found = r.RecordingId;
            }
            return found;
        }

        /// <summary>
        /// Pure, design section 2.12 Rule 3 (awards filed by cause): during a live session in
        /// FLIGHT, an event that names vessel <paramref name="causePid"/> is tagged to that
        /// vessel's recording in the session tree, passed through <see cref="Resolve"/> so a
        /// cause recording that is the superseded origin still lands on the provisional.
        /// Returns a decision with a null <see cref="ReFlyAwardTagDecision.Tag"/> when the
        /// cause does not decide (no session, not in flight, no vessel named, or a vessel
        /// with no recording in the session); the caller then keeps the live-tag path.
        /// </summary>
        internal static ReFlyAwardTagDecision ResolveByCause(
            uint causePid, bool inFlight, ReFlySessionMarker marker, RecordingTree tree)
        {
            if (marker == null)
                return new ReFlyAwardTagDecision { Reason = ReasonNoMarker };
            if (!inFlight)
                return new ReFlyAwardTagDecision { Reason = ReasonNotInFlight };
            string causeTag = ResolveSessionRecordingForVessel(tree, causePid);
            if (string.IsNullOrEmpty(causeTag))
                return new ReFlyAwardTagDecision { Reason = "cause-vessel-not-in-session" };
            ReFlyAwardTagDecision decision = Resolve(causeTag, inFlight, marker);
            if (!decision.Substituted)
                decision.Reason = ReasonCauseVessel;
            return decision;
        }

        /// <summary>
        /// Live wrapper over <see cref="ResolveByCause"/>: true with the cause tag when a live
        /// Re-Fly session files the event under the vessel it names; false otherwise (the
        /// caller keeps its live-tag path, which is unchanged outside a session).
        /// </summary>
        internal static bool TryResolveCauseTag(uint causePid, string liveTag, out string tag)
        {
            tag = null;
            if (causePid == 0u)
                return false;
            ReFlySwitchGate.LiveSession session = ReFlySwitchGate.ResolveLiveSession();
            if (session.Marker == null)
                return false;
            ReFlyAwardTagDecision decision = ResolveByCause(causePid, IsInFlight(), session.Marker, session.Tree);
            if (string.IsNullOrEmpty(decision.Tag))
                return false;
            tag = decision.Tag;
            if (!string.Equals(tag, liveTag ?? "", StringComparison.Ordinal))
            {
                ParsekLog.InfoRateLimited("GameStateRecorder",
                    "refly-cause-award-tag-" + (session.Marker.SessionId ?? "") + "-" + causePid.ToString(CultureInfo.InvariantCulture),
                    string.Format(CultureInfo.InvariantCulture,
                        "Re-Fly award tag: outcome=tagged-by-cause reason={0} sess={1} causePid={2} " +
                        "tag={3} liveTag='{4}'",
                        decision.Reason,
                        session.Marker.SessionId ?? "<no-id>",
                        causePid,
                        tag,
                        liveTag ?? ""));
            }
            return true;
        }

        private static bool IsInFlight()
        {
            var provider = InFlightProviderForTesting;
            if (provider != null)
                return provider();
            return HighLogic.LoadedScene == GameScenes.FLIGHT;
        }

        private static ReFlyAwardTagDecision Keep(string live, string reason)
        {
            return new ReFlyAwardTagDecision
            {
                Tag = live,
                Substituted = false,
                Reason = reason,
            };
        }
    }
}
