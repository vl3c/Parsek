using System;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §5.2: the render-layer notion of "which frame a phase lives in", replacing the
    /// bare <c>RenderSegment.FrameBodyName</c> string. A string cannot say "anchored to a <i>live
    /// vessel</i> that is the same craft as a recorded anchor", which is why the depot-double /
    /// dock-renders-absolute bug class existed. A discriminated union does.
    ///
    /// <para><b>NEW, additive, NOT wired in Phase 1.</b> v1 only needs the TYPES plus
    /// <see cref="BodyAnchor"/> and <see cref="ParentAnchoredChild"/> resolvable (the pure decision
    /// parts below); <see cref="LiveVesselAnchor"/>, <see cref="ParentGeneratedConicAnchor"/>, and
    /// <see cref="RecordedAnchorTrajectory"/> are typed placeholders for the fail-closed
    /// station / Jool / non-loop-relative cases (the producers are deferred — see the migration plan
    /// Phase 7).</para>
    ///
    /// <para><b>Loud-assertion carry-forward (design §6, <c>RenderSegment.cs:94-98</c>):</b> a
    /// <see cref="ParentAnchoredChild"/> is NEVER handed a re-aimed / generated segment list. The
    /// factory/assembler must keep that failure loud (assert) rather than silently body-framing it, so
    /// the transfer-leg-debris fail-closed (Phase 7) degrades loudly, not silently.</para>
    /// </summary>
    internal abstract class AnchorFrame
    {
        /// <summary>The discriminator for the union (grep-stable, switch-friendly, test-assertable).</summary>
        internal abstract AnchorFrameKind Kind { get; }

        /// <summary>Grep-stable lowercase token for trace lines (design §14 anchor-resolve).</summary>
        internal string ToToken() => AnchorFrameTokens.ToToken(Kind);

        // ---- Concrete variants ----

        /// <summary>
        /// design §5.2: the v1 common case — anchored to a celestial body by name.
        ///
        /// <para><b>Resolution contract:</b> a missing / renamed modded body name fails CLOSED
        /// (<c>SuppressedMarker</c> or hide), never NRE. Discovery level does NOT block resolution —
        /// all stock bodies exist regardless of whether the player has visited them, so a never-visited
        /// stock body resolves normally; only a missing body name fails (design §11.4). The pure
        /// decision part is <see cref="AnchorFrameResolver.TryResolveBody"/>.</para>
        /// </summary>
        internal sealed class BodyAnchor : AnchorFrame
        {
            internal string BodyName { get; }
            internal BodyAnchor(string bodyName) { BodyName = bodyName; }
            internal override AnchorFrameKind Kind => AnchorFrameKind.Body;
            public override string ToString() => "Body(" + (BodyName ?? "?") + ")";
        }

        /// <summary>
        /// design §5.2: a controlled-decoupled lander / probe off a decoupler
        /// (<c>IsDebris = false</c>, <c>ParentAnchorRecordingId != null</c>) — it passes the
        /// <c>IsDebris</c> gate and DOES render.
        ///
        /// <para><b>Resolution contract (CLAUDE.md parent-anchored invariant):</b> the faithful phase
        /// reads <c>TrackSection.bodyFixedFrames</c> as the PRIMARY surface (body-fixed degrees
        /// lat/lon/alt + srfRelRotation) and <c>frames</c> (anchor-local metres) as the
        /// SECONDARY/fallback for loop-anchored chains. Body-fixed primary requires <b>≥2 samples</b>
        /// AND a playback UT INSIDE the <c>bodyFixedFrames</c> endpoint range; an out-of-range UT must
        /// <b>RETIRE</b> the ghost — never clamp to a stale child offset. The pure decision part is
        /// <see cref="AnchorFrameResolver.ResolveParentAnchoredChild"/>.</para>
        /// </summary>
        internal sealed class ParentAnchoredChild : AnchorFrame
        {
            /// <summary>The parent recording id (the substrate's <c>Recording.ParentAnchorRecordingId</c>).</summary>
            internal string ParentRecordingId { get; }
            internal ParentAnchoredChild(string parentRecordingId) { ParentRecordingId = parentRecordingId; }
            internal override AnchorFrameKind Kind => AnchorFrameKind.ParentAnchoredChild;
            public override string ToString() => "ParentAnchoredChild(" + (ParentRecordingId ?? "?") + ")";
        }

        /// <summary>
        /// design §5.2: a moon riding a parent's GENERATED Jool-centric conic arc. Typed placeholder —
        /// the producer (nested-SOI synthetic re-aim) is deferred (fail-closed-to-faithful in v1).
        /// </summary>
        internal sealed class ParentGeneratedConicAnchor : AnchorFrame
        {
            /// <summary>The runtime render-layer id of the parent conic phase.</summary>
            internal PhaseId ParentConicPhaseId { get; }
            internal ParentGeneratedConicAnchor(PhaseId parentConicPhaseId) { ParentConicPhaseId = parentConicPhaseId; }
            internal override AnchorFrameKind Kind => AnchorFrameKind.ParentGeneratedConic;
            public override string ToString() => "ParentGeneratedConic(" + ParentConicPhaseId + ")";
        }

        /// <summary>
        /// design §5.2: rendezvous / docking to a LIVE same-craft vessel (a moving anchor, not a body
        /// center). Resolution reuses the existing guid-gated docking patch (the dock-anchor /
        /// depot-double fix). Typed placeholder in v1 — the moving-target producer is deferred.
        /// </summary>
        internal sealed class LiveVesselAnchor : AnchorFrame
        {
            /// <summary>The launch-unique discriminator (KSP <c>Vessel.id</c>), NOT the craft-baked persistentId.</summary>
            internal Guid LaunchGuid { get; }
            internal LiveVesselAnchor(Guid launchGuid) { LaunchGuid = launchGuid; }
            internal override AnchorFrameKind Kind => AnchorFrameKind.LiveVessel;
            public override string ToString() => "LiveVessel(" + LaunchGuid + ")";
        }

        /// <summary>
        /// design §5.2: a NON-LOOP Relative section anchored to a recorded anchor trajectory
        /// (<c>TrackSection.anchorRecordingId</c>). The contract SPLITS: a non-loop Relative section
        /// resolves through the recorded anchor trajectory, while a LOOP Relative section stays on the
        /// live-PID contract (<c>Recording.LoopAnchorVesselId</c>, → <see cref="LiveVesselAnchor"/>).
        /// When a re-aimed/looped mission shifts the anchor recording's UT out from under a dependent
        /// member, the member fails closed to faithful. Typed placeholder in v1.
        /// </summary>
        internal sealed class RecordedAnchorTrajectory : AnchorFrame
        {
            /// <summary>The anchor recording's id.</summary>
            internal string AnchorRecordingId { get; }
            internal RecordedAnchorTrajectory(string anchorRecordingId) { AnchorRecordingId = anchorRecordingId; }
            internal override AnchorFrameKind Kind => AnchorFrameKind.RecordedAnchor;
            public override string ToString() => "RecordedAnchor(" + (AnchorRecordingId ?? "?") + ")";
        }
    }

    /// <summary>The discriminator for <see cref="AnchorFrame"/> (design §5.2).</summary>
    internal enum AnchorFrameKind
    {
        Unknown = 0,
        Body = 1,
        ParentAnchoredChild = 2,
        ParentGeneratedConic = 3,
        LiveVessel = 4,
        RecordedAnchor = 5,
    }

    /// <summary>Grep-stable lowercase tokens for <see cref="AnchorFrameKind"/>.</summary>
    internal static class AnchorFrameTokens
    {
        internal static string ToToken(AnchorFrameKind kind)
        {
            switch (kind)
            {
                case AnchorFrameKind.Body: return "body";
                case AnchorFrameKind.ParentAnchoredChild: return "parent-anchored-child";
                case AnchorFrameKind.ParentGeneratedConic: return "parent-generated-conic";
                case AnchorFrameKind.LiveVessel: return "live-vessel";
                case AnchorFrameKind.RecordedAnchor: return "recorded-anchor";
                default: return "unknown";
            }
        }
    }
}
