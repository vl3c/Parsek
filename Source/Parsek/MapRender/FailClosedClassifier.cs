using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 7 (migration plan §9 / design §4 / §9.2 / §10): the PURE fail-closed DECISION — given a
    /// member's recorded body sequence + anchor situation, decide whether it is one of the three
    /// UNSUPPORTED synthetic producers (cross-SOI whole-chain synthesis / nested-SOI moon-to-moon re-aim /
    /// moving-target station rendezvous) and, if so, classify it
    /// <see cref="SegmentProvenance.FaithfulFallback"/> — render the RECORDED trajectory VERBATIM, never a
    /// broken synthetic guess.
    ///
    /// <para><b>DEFINE-ONLY, additive, geometry-NEUTRAL (the v1 contract).</b> This classifier is a
    /// CLASSIFICATION layer only: it decides PROVENANCE (and names the unsupported producer for the
    /// tracer). It does NOT change any geometry. The three unsupported producers HAVE NO SYNTHETIC
    /// IMPLEMENTATION in v1 (the recursive nested-SOI producer, the whole-patched-conic-chain cross-SOI
    /// synthesis, and the synthetic moving-target producer are all deferred — design §17), so "fail-closed
    /// to faithful" is exactly what the pipeline already does for them. The cross-SOI kink renders the
    /// current <c>FlexibleSoi</c> G0 behavior UNCHANGED. The win is the typed, logged, test-assertable
    /// DECISION (so the three cases have a real home and a tracer event) rather than a silent absence of a
    /// producer.</para>
    ///
    /// <para><b>Pure + headless.</b> No Unity / KSP-API reads. The body parent chain is supplied as a
    /// delegate (mirroring <see cref="Parsek.IBodyInfo.ReferenceBodyName"/>) so the nesting
    /// decision is directly unit-testable; the cross-SOI / station signals are pure properties of the
    /// recorded body sequence + the member's anchor. The trace emit is gated on
    /// <see cref="MapRenderTrace.IsEnabled"/> (free in normal play) and deduped once-per-event via
    /// <see cref="MapRenderTrace.ShouldEmitFailClosedOnChange"/>.</para>
    /// </summary>
    internal static class FailClosedClassifier
    {
        /// <summary>
        /// Why a member fell back to faithful replay (the unsupported producer). Grep-stable lowercase
        /// tokens via <see cref="ReasonToken"/>; carried on the Tier-A
        /// <see cref="MapRenderTrace.EventFailClosedToFaithful"/> detail line.
        /// </summary>
        internal enum FailClosedReason
        {
            /// <summary>Not unsupported — the member is supported (no fail-closed). Default.</summary>
            None = 0,

            /// <summary>
            /// A nested-SOI (moon-rich, Jool) tour: the SYNTHETIC moon-to-moon re-aim producer is deferred
            /// (design §10) -> render the recorded tour faithfully.
            /// </summary>
            NestedSoi = 1,

            /// <summary>
            /// A moving-target station rendezvous: the target is a live moving vessel, not a body center;
            /// the SYNTHETIC moving-target producer is deferred (design §9.2) -> render the recorded
            /// approach faithfully.
            /// </summary>
            MovingTargetStation = 2,

            /// <summary>
            /// A cross-SOI transfer whose WHOLE-patched-conic-chain synthesis (the ~62 deg kink fix) is a
            /// separate test-gated effort (design §9.2 / §17). v1 renders the current per-crossing
            /// <c>FlexibleSoi</c> G0 behavior UNCHANGED; this reason is recorded for the future producer's
            /// home but does NOT itself change geometry. Reserved — see
            /// <see cref="Classify"/> for why v1 does NOT auto-raise it (it would be noise on every
            /// ordinary interplanetary mission that already renders correctly).
            /// </summary>
            CrossSoiChain = 3,
        }

        /// <summary>Grep-stable lowercase token for a <see cref="FailClosedReason"/>.</summary>
        internal static string ReasonToken(FailClosedReason reason)
        {
            switch (reason)
            {
                case FailClosedReason.NestedSoi: return "nested-soi";
                case FailClosedReason.MovingTargetStation: return "moving-target-station";
                case FailClosedReason.CrossSoiChain: return "cross-soi-chain";
                default: return "none";
            }
        }

        /// <summary>
        /// The outcome of one fail-closed classification: whether the member is unsupported, the reason,
        /// and (when nested-SOI) the typed <see cref="NestedSoiSubtree"/> payload (so the future producer
        /// + the tracer have the structured identity). A SUPPORTED member yields
        /// <see cref="FailClosedReason.None"/> and <see cref="IsFailClosed"/> = false.
        /// </summary>
        internal readonly struct FailClosedDecision
        {
            internal FailClosedReason Reason { get; }

            /// <summary>The nested-SOI subtree when <see cref="Reason"/> is
            /// <see cref="FailClosedReason.NestedSoi"/>; null otherwise.</summary>
            internal NestedSoiSubtree NestedSubtree { get; }

            internal FailClosedDecision(FailClosedReason reason, NestedSoiSubtree nestedSubtree)
            {
                Reason = reason;
                NestedSubtree = nestedSubtree;
            }

            /// <summary>True iff the member is an unsupported producer -> render recorded verbatim.</summary>
            internal bool IsFailClosed => Reason != FailClosedReason.None;

            /// <summary>
            /// The provenance the member's phases carry under this decision:
            /// <see cref="SegmentProvenance.FaithfulFallback"/> when fail-closed,
            /// <see cref="SegmentProvenance.Unknown"/> otherwise (the caller keeps its own per-phase
            /// provenance — this decision only overrides on fail-closed).
            /// </summary>
            internal SegmentProvenance Provenance =>
                IsFailClosed ? SegmentProvenance.FaithfulFallback : SegmentProvenance.Unknown;

            internal static readonly FailClosedDecision Supported =
                new FailClosedDecision(FailClosedReason.None, null);
        }

        /// <summary>
        /// design §9.2 / §10: the PURE fail-closed decision for ONE member.
        ///
        /// <list type="number">
        ///   <item><b>Moving-target station</b> (highest precedence — the least-supported phase): the
        ///     member's arrival anchor is a LIVE moving vessel
        ///     (<paramref name="hasLiveVesselArrivalAnchor"/>), so a synthetic moving-target solve is
        ///     unsupported -> fail closed. design §9.2.</item>
        ///   <item><b>Nested-SOI (Jool)</b>: the recorded body sequence is a moon-rich tour (two visited
        ///     bodies are siblings under a shared non-root ancestor, via
        ///     <see cref="NestedSoiSubtree.TryBuildFromBodySequence"/>), so a synthetic moon-to-moon
        ///     re-aim is unsupported -> fail closed. design §10.</item>
        ///   <item><b>Everything else is SUPPORTED.</b> A single-level cross-SOI transfer
        ///     (Kerbin->Mun->Sun->Duna) is NOT auto-failed: it already renders correctly through the
        ///     per-crossing <c>FlexibleSoi</c> G0 path, and auto-raising
        ///     <see cref="FailClosedReason.CrossSoiChain"/> on every ordinary interplanetary mission would
        ///     be noise that changes nothing (design §9.2: define-only, no v1 fix — render current
        ///     behavior unchanged). The CrossSoiChain reason is defined for the future whole-chain
        ///     synthesis effort's home, surfaced only by the explicit
        ///     <see cref="ClassifyCrossSoiChainForTesting"/> seam, never by the live path.</item>
        /// </list>
        ///
        /// <para><b>Geometry-neutral:</b> a fail-closed decision changes only PROVENANCE (and emits the
        /// tracer event). The caller renders the RECORDED trajectory verbatim either way — the only thing
        /// fail-closed disables is a synthetic producer that does not exist in v1.</para>
        /// </summary>
        internal static FailClosedDecision Classify(
            IReadOnlyList<string> orderedRecordedBodies,
            bool hasLiveVesselArrivalAnchor,
            Func<string, string> referenceBodyName)
        {
            // (1) Moving-target station: the arrival anchor is a live moving vessel. Highest precedence
            // because it is the least-supported phase and is independent of the body sequence (a station
            // approach may stay in one heliocentric/parking frame the whole time).
            if (hasLiveVesselArrivalAnchor)
                return new FailClosedDecision(FailClosedReason.MovingTargetStation, null);

            // (2) Nested-SOI (Jool moon tour): two visited bodies are siblings under a shared non-root
            // ancestor. TryBuildFromBodySequence returns null for a single-level mission, so an ordinary
            // interplanetary or Kerbin<->Mun trajectory stays SUPPORTED here.
            NestedSoiSubtree subtree =
                NestedSoiSubtree.TryBuildFromBodySequence(orderedRecordedBodies, referenceBodyName);
            if (subtree != null && subtree.IsNested)
                return new FailClosedDecision(FailClosedReason.NestedSoi, subtree);

            // (3) Everything else is supported (the single-level cross-SOI path renders unchanged).
            return FailClosedDecision.Supported;
        }

        /// <summary>
        /// Test/future-producer seam: classify a member as <see cref="FailClosedReason.CrossSoiChain"/>
        /// when its recorded body sequence makes MORE THAN ONE single-level SOI crossing (a multi-hop
        /// interplanetary chain the future whole-patched-conic-chain synthesis would own). This is NOT on
        /// the live classification path (design §9.2: cross-SOI is define-only, render current behavior
        /// unchanged), so the cross-SOI G0 kink stays byte-identical; it exists so the reason + its
        /// detection have a tested home for the deferred synthesis effort. Pure.
        /// </summary>
        internal static FailClosedDecision ClassifyCrossSoiChainForTesting(
            IReadOnlyList<string> orderedRecordedBodies)
        {
            if (CountBodyChanges(orderedRecordedBodies) >= 2)
                return new FailClosedDecision(FailClosedReason.CrossSoiChain, null);
            return FailClosedDecision.Supported;
        }

        /// <summary>
        /// Count adjacent distinct-body changes (single-level SOI crossings) in a recorded body sequence.
        /// Pure; tolerates null / empty / single-element sequences (0 changes).
        /// </summary>
        internal static int CountBodyChanges(IReadOnlyList<string> orderedBodies)
        {
            if (orderedBodies == null || orderedBodies.Count < 2)
                return 0;
            int changes = 0;
            for (int i = 1; i < orderedBodies.Count; i++)
            {
                string a = orderedBodies[i - 1];
                string b = orderedBodies[i];
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                    continue;
                if (!string.Equals(a, b, StringComparison.Ordinal))
                    changes++;
            }
            return changes;
        }

        /// <summary>
        /// PURE detail-line builder for the Tier-A <see cref="MapRenderTrace.EventFailClosedToFaithful"/>
        /// structural event (kept pure — no Unity reads, no global sink — so the line schema is directly
        /// unit-testable, mirroring <see cref="CrossMemberSeamStitcher.BuildDescentStitchedDetails"/> /
        /// <see cref="MapRenderTrace.BuildLifecycleDetails"/>). Names the unsupported producer + the
        /// faithful-fallback provenance + the case payload.
        /// </summary>
        internal static string BuildFailClosedDetails(FailClosedDecision decision)
        {
            string reason = ReasonToken(decision.Reason);
            string prov = SegmentProvenanceTokens.ToToken(SegmentProvenance.FaithfulFallback);
            string payload = decision.NestedSubtree != null
                ? " " + decision.NestedSubtree.ToSummaryToken()
                : string.Empty;
            return string.Format(CultureInfo.InvariantCulture,
                "producer={0} provenance={1} action=render-recorded-verbatim{2}",
                reason, prov, payload);
        }

        /// <summary>
        /// Emit the Tier-A <see cref="MapRenderTrace.EventFailClosedToFaithful"/> structural event naming
        /// the unsupported producer. Gated by <see cref="MapRenderTrace.IsEnabled"/> (the off-by-default
        /// <c>mapRenderTracing</c> setting), so normal play pays nothing, and deduped ONCE-PER-EVENT
        /// (recording id + reason) via <see cref="MapRenderTrace.ShouldEmitFailClosedOnChange"/> so a
        /// steady fail-closed member emits ONE line, not one per frame (the VerboseRateLimited
        /// convention). Resolves the ghost pid from the committed index ONLY behind the
        /// <see cref="MapRenderTrace.IsEnabled"/> gate, so the flag-OFF / tracing-OFF path never touches
        /// <see cref="GhostMapPresence"/> and the headless unit tests stay pure; pid resolves to 0 when no
        /// ghost vessel exists yet (the recId still names the line). A SUPPORTED decision emits nothing.
        /// Surface = ProtoOrbitLine (the fail-closed member renders its recorded conic/line through the
        /// stock proto path).
        /// </summary>
        internal static void EmitFailClosedToFaithful(
            string recordingId, int committedIndex, double currentUT, FailClosedDecision decision)
        {
            if (!MapRenderTrace.IsEnabled)
                return;
            if (!decision.IsFailClosed)
                return;

            uint pid = GhostMapPresence.GetGhostVesselPidForRecording(committedIndex);
            string pidKey = pid.ToString(CultureInfo.InvariantCulture);
            string signature = (recordingId ?? "?") + ":" + ReasonToken(decision.Reason);
            if (!MapRenderTrace.ShouldEmitFailClosedOnChange(pidKey, signature))
                return;

            string details = BuildFailClosedDetails(decision);
            MapRenderTrace.EmitStructural(
                MapRenderTrace.EventFailClosedToFaithful,
                MapRenderTrace.RenderSurface.ProtoOrbitLine,
                pidKey, currentUT, currentUT,
                MapRenderTrace.SegmentChangeWindowSeconds, details, recordingId);
        }
    }
}
