using System;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §5.2: the PURE decision parts of <see cref="AnchorFrame"/> resolution that v1
    /// implements (<see cref="AnchorFrame.BodyAnchor"/> + <see cref="AnchorFrame.ParentAnchoredChild"/>).
    /// Unity-ECall-FREE and KSP-API-free: body existence is supplied as a delegate so the fail-closed
    /// decision is directly unit-testable, and the parent-anchored dual-surface routing is pure
    /// arithmetic over sample count + UT endpoints.
    ///
    /// <para>NOT wired into the live pipeline in Phase 1. The live resolver (a later phase) supplies a
    /// real body-existence probe (<c>FlightGlobals.Bodies</c> name lookup) and the real
    /// <c>TrackSection.bodyFixedFrames</c> / <c>frames</c> endpoints; the DECISION stays here.</para>
    /// </summary>
    internal static class AnchorFrameResolver
    {
        /// <summary>
        /// The outcome of a <see cref="AnchorFrame.BodyAnchor"/> resolution.
        /// </summary>
        internal enum BodyResolveOutcome
        {
            /// <summary>The body name resolved — render normally.</summary>
            Resolved = 0,
            /// <summary>A null / empty body name — fail closed (SuppressedMarker or hide), never NRE.</summary>
            FailClosedMissingName = 1,
            /// <summary>A non-empty body name that does not exist (renamed / removed modded body) — fail closed.</summary>
            FailClosedUnknownBody = 2,
        }

        /// <summary>
        /// design §5.2 / §11.4: resolve a <see cref="AnchorFrame.BodyAnchor"/> against a body-existence
        /// probe. A null / whitespace name is <see cref="BodyResolveOutcome.FailClosedMissingName"/>; a
        /// non-empty name the probe rejects is <see cref="BodyResolveOutcome.FailClosedUnknownBody"/>;
        /// otherwise <see cref="BodyResolveOutcome.Resolved"/>. NEVER throws — a null
        /// <paramref name="bodyExists"/> probe is treated as "cannot confirm" → fail closed (the
        /// caller's safe default, not an NRE). Discovery level is irrelevant: the probe is expected to
        /// answer purely on body existence, so a never-visited stock body resolves.
        /// </summary>
        internal static BodyResolveOutcome ResolveBody(string bodyName, Func<string, bool> bodyExists)
        {
            if (string.IsNullOrWhiteSpace(bodyName))
                return BodyResolveOutcome.FailClosedMissingName;
            if (bodyExists == null)
                return BodyResolveOutcome.FailClosedUnknownBody;

            bool exists;
            try { exists = bodyExists(bodyName); }
            catch { exists = false; }
            return exists ? BodyResolveOutcome.Resolved : BodyResolveOutcome.FailClosedUnknownBody;
        }

        /// <summary>True iff <see cref="ResolveBody"/> says the body anchor renders.</summary>
        internal static bool TryResolveBody(string bodyName, Func<string, bool> bodyExists)
            => ResolveBody(bodyName, bodyExists) == BodyResolveOutcome.Resolved;

        /// <summary>
        /// The outcome of a <see cref="AnchorFrame.ParentAnchoredChild"/> dual-surface resolution at a
        /// playback UT (design §5.2 / CLAUDE.md parent-anchored invariant).
        /// </summary>
        internal enum ParentChildSurface
        {
            /// <summary>
            /// Resolve via <c>bodyFixedFrames</c> (PRIMARY): ≥2 body-fixed samples and the playback UT
            /// inside their endpoint range.
            /// </summary>
            BodyFixedPrimary = 0,
            /// <summary>
            /// Resolve via <c>frames</c> (SECONDARY/fallback for loop-anchored chains): the body-fixed
            /// primary is unusable but an anchor-local <c>frames</c> surface covers the UT.
            /// </summary>
            AnchorLocalSecondary = 1,
            /// <summary>
            /// Neither surface covers the UT (out-of-range / too few samples) — RETIRE the ghost. Never
            /// clamp to a stale child offset.
            /// </summary>
            Retire = 2,
        }

        /// <summary>
        /// Minimum body-fixed samples for the PRIMARY surface (design §5.2: "requires ≥2 samples").
        /// </summary>
        internal const int MinBodyFixedSamples = 2;

        /// <summary>
        /// design §5.2: choose the dual surface for a parent-anchored child at <paramref name="ut"/>.
        /// PRIMARY (<see cref="ParentChildSurface.BodyFixedPrimary"/>) requires
        /// <paramref name="bodyFixedSampleCount"/> ≥ <see cref="MinBodyFixedSamples"/> AND
        /// <c><paramref name="bodyFixedStartUt"/> ≤ ut ≤ <paramref name="bodyFixedEndUt"/></c>. When the
        /// primary is unusable, fall back to the anchor-local <c>frames</c> SECONDARY only if it covers
        /// the UT (<paramref name="hasAnchorLocalFrames"/> and the local-frames range covers it).
        /// Otherwise RETIRE — an out-of-range body-fixed UT must never clamp to a stale child offset.
        ///
        /// <para>Non-finite endpoints are treated as "no usable range" for that surface (fail toward
        /// retire, never NaN-compare into a spurious in-range). A start &gt; end body-fixed range is
        /// degenerate and rejected.</para>
        /// </summary>
        internal static ParentChildSurface ResolveParentAnchoredChild(
            double ut,
            int bodyFixedSampleCount,
            double bodyFixedStartUt,
            double bodyFixedEndUt,
            bool hasAnchorLocalFrames,
            double anchorLocalStartUt,
            double anchorLocalEndUt)
        {
            if (bodyFixedSampleCount >= MinBodyFixedSamples
                && RangeCoversUt(bodyFixedStartUt, bodyFixedEndUt, ut))
            {
                return ParentChildSurface.BodyFixedPrimary;
            }

            if (hasAnchorLocalFrames && RangeCoversUt(anchorLocalStartUt, anchorLocalEndUt, ut))
                return ParentChildSurface.AnchorLocalSecondary;

            return ParentChildSurface.Retire;
        }

        /// <summary>
        /// Inclusive in-range test, NaN/Inf-safe and degenerate-range-safe (start &gt; end → false).
        /// </summary>
        private static bool RangeCoversUt(double startUt, double endUt, double ut)
        {
            if (!IsFinite(startUt) || !IsFinite(endUt) || !IsFinite(ut))
                return false;
            if (startUt > endUt)
                return false;
            return ut >= startUt && ut <= endUt;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
