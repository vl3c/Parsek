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
