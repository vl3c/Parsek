using System;
using System.Collections.Generic;

namespace Parsek.MapRender
{
    // Phase 1 / design §6: the concrete TrajectoryPhase subclasses. Each maps to existing code that
    // becomes its implementation (see the §6 phase table). NONE is wired into the live pipeline; the
    // Emit overrides return the same RenderSegment the legacy assembler would so Phase 2 can prove
    // geometry byte-parity, and ResolveTreatment / CoversUt are the per-phase decision logic the §15
    // test plan covers.
    //
    // A StockConic phase carries an OrbitSegment conic (Kepler currency, value-copied); a TracedPath
    // phase carries no conic (the treatment reads recorded Points by the [StartUt, EndUt) window).

    /// <summary>
    /// design §6: a CONIC phase drawn as a stock orbit line + icon (MANAGED vs KSP). The base for
    /// departure/arrival loiter, SOI departure/arrival, and the heliocentric transfer. Carries the
    /// <see cref="OrbitSegment"/> and emits one conic <see cref="RenderSegment"/>.
    /// </summary>
    internal abstract class ConicPhase : TrajectoryPhase
    {
        /// <summary>The orbit this phase draws (recorded loiter / arrival, or the generated transfer).</summary>
        internal OrbitSegment Conic { get; }

        private protected ConicPhase(
            PhaseId id, PhaseKind kind, SegmentProvenance provenance, AnchorFrame anchor,
            double startUt, double endUt, PhaseSeam leadingSeam, PhaseSeam trailingSeam,
            OrbitSegment conic)
            : base(id, kind, provenance, anchor, startUt, endUt, leadingSeam, trailingSeam)
        {
            Conic = conic;
        }

        internal override Treatment ResolveTreatment() => Treatment.StockConic;

        internal override IEnumerable<RenderSegment> Emit(SampleContext ctx)
        {
            yield return new RenderSegment(
                LegacyKind,
                Treatment.StockConic,
                StartUt,
                EndUt,
                ResolveFrameBodyName(ctx),
                SegmentPayload.ForConic(Conic),
                isGenerated: Provenance == SegmentProvenance.Synthesized,
                leadingSeam: ToLegacySeam(LeadingSeam),
                trailingSeam: ToLegacySeam(TrailingSeam));
        }

        /// <summary>The legacy <see cref="SegmentKind"/> this phase emits (geometry-irrelevant; see §6 map).</summary>
        private protected abstract SegmentKind LegacyKind { get; }

        /// <summary>
        /// Resolve the frame body name: prefer the <see cref="AnchorFrame.BodyAnchor"/> payload, else the
        /// conic's own body, else the sample context's resolved body.
        /// </summary>
        private protected string ResolveFrameBodyName(SampleContext ctx)
        {
            if (Anchor is AnchorFrame.BodyAnchor body && !string.IsNullOrEmpty(body.BodyName))
                return body.BodyName;
            if (!string.IsNullOrEmpty(Conic.bodyName))
                return Conic.bodyName;
            return ctx.FrameBodyName;
        }

        private protected static SeamKind ToLegacySeam(PhaseSeam seam)
            => PhaseGeometry.ToLegacySeam(seam);
    }

    /// <summary>
    /// design §6: a TRACED phase drawn as our owned polyline (atmospheric / surface / descent). Carries
    /// no conic; the treatment reads recorded Points by the <c>[StartUt, EndUt)</c> window.
    /// </summary>
    internal abstract class TracedPhase : TrajectoryPhase
    {
        private protected TracedPhase(
            PhaseId id, PhaseKind kind, SegmentProvenance provenance, AnchorFrame anchor,
            double startUt, double endUt, PhaseSeam leadingSeam, PhaseSeam trailingSeam)
            : base(id, kind, provenance, anchor, startUt, endUt, leadingSeam, trailingSeam) { }

        internal override Treatment ResolveTreatment() => Treatment.TracedPath;

        internal override IEnumerable<RenderSegment> Emit(SampleContext ctx)
        {
            yield return new RenderSegment(
                LegacyKind,
                Treatment.TracedPath,
                StartUt,
                EndUt,
                ResolveFrameBodyName(ctx),
                SegmentPayload.Traced,
                isGenerated: Provenance == SegmentProvenance.Synthesized,
                leadingSeam: PhaseGeometry.ToLegacySeam(LeadingSeam),
                trailingSeam: PhaseGeometry.ToLegacySeam(TrailingSeam));
        }

        private protected abstract SegmentKind LegacyKind { get; }

        private protected string ResolveFrameBodyName(SampleContext ctx)
        {
            if (Anchor is AnchorFrame.BodyAnchor body && !string.IsNullOrEmpty(body.BodyName))
                return body.BodyName;
            return ctx.FrameBodyName;
        }
    }

    // ---- Concrete phases (one per §6 row) ----

    /// <summary>
    /// design §6: powered ascent. TracedPath, Recorded. Implemented by
    /// <c>ChainAssembler.AppendTracedRuns/FlushRun</c>. The single-recording re-aim ascent re-time is a
    /// DEFERRED known gap (this class hosts the eventual fix; its producer fail-opens, design §4/§11).
    /// </summary>
    internal sealed class AscentPhase : TracedPhase
    {
        internal AscentPhase(
            PhaseId id, SegmentProvenance provenance, AnchorFrame anchor, double startUt, double endUt,
            PhaseSeam leadingSeam = null, PhaseSeam trailingSeam = null)
            : base(id, PhaseKind.Ascent, provenance, anchor, startUt, endUt, leadingSeam, trailingSeam) { }

        private protected override SegmentKind LegacyKind => SegmentKind.Ascent;
    }

    /// <summary>
    /// design §6: the parking-orbit loiter BEFORE the transfer. StockConic; Recorded, or SYNTHESIZED for
    /// the heliocentric-park→planet variant (s15: a recorded park copy LAN-re-phased via
    /// <c>RotateLanForParkRephase</c>, stamped by <c>DecideDepartureAnchor</c>) — built in v1.
    /// </summary>
    internal sealed class DepartureLoiterPhase : ConicPhase
    {
        internal DepartureLoiterPhase(
            PhaseId id, SegmentProvenance provenance, AnchorFrame anchor, double startUt, double endUt,
            OrbitSegment conic, PhaseSeam leadingSeam = null, PhaseSeam trailingSeam = null)
            : base(id, PhaseKind.DepartureLoiter, provenance, anchor, startUt, endUt,
                   leadingSeam, trailingSeam, conic) { }

        private protected override SegmentKind LegacyKind => SegmentKind.Loiter;
    }

    /// <summary>
    /// design §6: the SOI departure leg (the ejection out of the origin body's SOI). StockConic or
    /// TracedPath; Recorded or Synthesized. Implemented by <c>ReaimClassifier.RecordedSoiExitUT</c>.
    /// Carries an explicit treatment because the §6 table marks it dual.
    /// </summary>
    internal sealed class SoiDeparturePhase : DualTreatmentConicPhase
    {
        internal SoiDeparturePhase(
            PhaseId id, Treatment treatment, SegmentProvenance provenance, AnchorFrame anchor,
            double startUt, double endUt, OrbitSegment conic,
            PhaseSeam leadingSeam = null, PhaseSeam trailingSeam = null)
            : base(id, PhaseKind.SoiDeparture, SegmentKind.Eject, treatment, provenance, anchor,
                   startUt, endUt, conic, leadingSeam, trailingSeam) { }
    }

    /// <summary>
    /// design §6: the heliocentric (planet→planet / park→planet / →station) transfer. StockConic;
    /// SYNTHESIZED (re-aim) or Recorded (faithful). Implemented by
    /// <c>ReaimSegmentAssembler.ReplaceHeliocentricLeg</c> + <c>UvLambert</c>. This is the ONLY leg the
    /// recorded-vs-synthetic rule (design §7) re-solves when a re-aim window succeeds.
    /// </summary>
    internal sealed class HeliocentricTransferPhase : ConicPhase
    {
        internal HeliocentricTransferPhase(
            PhaseId id, SegmentProvenance provenance, AnchorFrame anchor, double startUt, double endUt,
            OrbitSegment conic, PhaseSeam leadingSeam = null, PhaseSeam trailingSeam = null)
            : base(id, PhaseKind.HeliocentricTransfer, provenance, anchor, startUt, endUt,
                   leadingSeam, trailingSeam, conic) { }

        private protected override SegmentKind LegacyKind => SegmentKind.Transfer;
    }

    /// <summary>
    /// design §6: the SOI arrival leg (capture into the destination body's SOI). StockConic or
    /// TracedPath; Recorded or Synthesized. Implemented by <c>ReaimClassifier.ArrivalLeg</c> + intra-arc
    /// moon split. Dual-treatment like <see cref="SoiDeparturePhase"/>.
    /// </summary>
    internal sealed class SoiArrivalPhase : DualTreatmentConicPhase
    {
        internal SoiArrivalPhase(
            PhaseId id, Treatment treatment, SegmentProvenance provenance, AnchorFrame anchor,
            double startUt, double endUt, OrbitSegment conic,
            PhaseSeam leadingSeam = null, PhaseSeam trailingSeam = null)
            : base(id, PhaseKind.SoiArrival, SegmentKind.Approach, treatment, provenance, anchor,
                   startUt, endUt, conic, leadingSeam, trailingSeam) { }
    }

    /// <summary>
    /// design §6: the loiter AFTER arrival (parking around the destination). StockConic; Recorded.
    /// Same role-blind conic emit as <see cref="DepartureLoiterPhase"/> with the NEW arrival role;
    /// <c>DestinationLoiterTrim</c>.
    /// </summary>
    internal sealed class ArrivalLoiterPhase : ConicPhase
    {
        internal ArrivalLoiterPhase(
            PhaseId id, SegmentProvenance provenance, AnchorFrame anchor, double startUt, double endUt,
            OrbitSegment conic, PhaseSeam leadingSeam = null, PhaseSeam trailingSeam = null)
            : base(id, PhaseKind.ArrivalLoiter, provenance, anchor, startUt, endUt,
                   leadingSeam, trailingSeam, conic) { }

        private protected override SegmentKind LegacyKind => SegmentKind.ArrivalLoiter;
    }

    /// <summary>
    /// design §6 / §9.1: atmospheric / powered descent to landing. TracedPath; Recorded. Implemented by
    /// <c>DescentTrigger.*</c>; in v1 it gains a cross-member stitcher (Phase 6) owning the orbit↔landing
    /// G1 seam. Promoted to a visible first-class phase (no longer hidden in the transfer member).
    /// </summary>
    internal sealed class DescentPhase : TracedPhase
    {
        internal DescentPhase(
            PhaseId id, SegmentProvenance provenance, AnchorFrame anchor, double startUt, double endUt,
            PhaseSeam leadingSeam = null, PhaseSeam trailingSeam = null)
            : base(id, PhaseKind.Descent, provenance, anchor, startUt, endUt, leadingSeam, trailingSeam) { }

        private protected override SegmentKind LegacyKind => SegmentKind.Landing;
    }

    /// <summary>
    /// design §6: a landed / splashed / prelaunch / rover surface stretch. TracedPath; Recorded. Traced
    /// runs below surface. (A no-bounds surface ghost falls to a SuppressedMarker via the director, §11.4
    /// — that is a director decision, not this phase's treatment.)
    /// </summary>
    internal sealed class SurfacePhase : TracedPhase
    {
        internal SurfacePhase(
            PhaseId id, SegmentProvenance provenance, AnchorFrame anchor, double startUt, double endUt,
            PhaseSeam leadingSeam = null, PhaseSeam trailingSeam = null)
            : base(id, PhaseKind.Surface, provenance, anchor, startUt, endUt, leadingSeam, trailingSeam) { }

        private protected override SegmentKind LegacyKind => SegmentKind.Surface;
    }

    /// <summary>
    /// design §6: a first-class "parked" identity, promoted from the invisible <c>InInteriorGap</c>
    /// clock insertion (<c>ArrivalHoldPlanner</c> / launch-hold). It renders quietly (no own geometry —
    /// the prior intent is held) but EXISTS in the chain for debugging / composition.
    ///
    /// <para><b>Warp-step safety (design §11.3):</b> a single high-warp frame can advance the live UT
    /// across an entire hold. <see cref="CoversUt"/> covers the WHOLE <c>[StartUt, EndUt)</c> span so the
    /// hold never resolves to "no phase" mid-warp and freeze the ghost for multiple frames; the span
    /// clock then resolves the correct post-hold assembled-UT.</para>
    /// </summary>
    internal sealed class HoldPhase : TrajectoryPhase
    {
        internal HoldPhase(
            PhaseId id, AnchorFrame anchor, double startUt, double endUt,
            PhaseSeam leadingSeam = null, PhaseSeam trailingSeam = null)
            : base(id, PhaseKind.Hold, SegmentProvenance.Recorded, anchor, startUt, endUt,
                   leadingSeam, trailingSeam) { }

        /// <summary>A hold draws nothing of its own; the prior intent is held (design §6).</summary>
        internal override Treatment ResolveTreatment() => Treatment.None;

        /// <summary>No geometry — yields nothing.</summary>
        internal override IEnumerable<RenderSegment> Emit(SampleContext ctx)
            => Array.Empty<RenderSegment>();

        // CoversUt uses the base half-open [StartUt, EndUt) — already covers the whole hold span, so a
        // warp step that lands anywhere inside the hold resolves to THIS phase (never a spurious gap).
        // The inclusive end of the chain's LAST phase is handled by the chain (see PhaseChain).
    }

    /// <summary>
    /// design §6: shared base for the two DUAL-treatment conic phases (<see cref="SoiDeparturePhase"/> /
    /// <see cref="SoiArrivalPhase"/>) that the §6 table marks as StockConic OR TracedPath. Carries an
    /// explicit treatment; the conic is still carried (a TracedPath SOI leg may still have a conic
    /// reference for its endpoints, but the treatment governs the draw).
    /// </summary>
    internal abstract class DualTreatmentConicPhase : TrajectoryPhase
    {
        internal OrbitSegment Conic { get; }
        private readonly Treatment treatment;
        private readonly SegmentKind legacyKind;

        private protected DualTreatmentConicPhase(
            PhaseId id, PhaseKind kind, SegmentKind legacyKind, Treatment treatment,
            SegmentProvenance provenance, AnchorFrame anchor, double startUt, double endUt,
            OrbitSegment conic, PhaseSeam leadingSeam, PhaseSeam trailingSeam)
            : base(id, kind, provenance, anchor, startUt, endUt, leadingSeam, trailingSeam)
        {
            this.treatment = treatment;
            this.legacyKind = legacyKind;
            Conic = conic;
        }

        internal override Treatment ResolveTreatment() => treatment;

        internal override IEnumerable<RenderSegment> Emit(SampleContext ctx)
        {
            bool conicTreatment = treatment == Treatment.StockConic;
            string body = (Anchor is AnchorFrame.BodyAnchor b && !string.IsNullOrEmpty(b.BodyName))
                ? b.BodyName
                : (!string.IsNullOrEmpty(Conic.bodyName) ? Conic.bodyName : ctx.FrameBodyName);

            yield return new RenderSegment(
                legacyKind,
                treatment,
                StartUt,
                EndUt,
                body,
                conicTreatment ? SegmentPayload.ForConic(Conic) : SegmentPayload.Traced,
                isGenerated: Provenance == SegmentProvenance.Synthesized,
                leadingSeam: PhaseGeometry.ToLegacySeam(LeadingSeam),
                trailingSeam: PhaseGeometry.ToLegacySeam(TrailingSeam));
        }
    }

    /// <summary>
    /// Pure geometry/seam helpers shared by the phase <c>Emit</c> overrides. Kept Unity-free.
    /// </summary>
    internal static class PhaseGeometry
    {
        /// <summary>
        /// Map a Phase-1 <see cref="PhaseSeam"/> down to the legacy <see cref="SeamKind"/> the emitted
        /// <see cref="RenderSegment"/> carries (the live draw path still reads <c>SeamKind</c>). The new
        /// <see cref="PhaseSeamKind.SwitchContinuation"/> has no legacy equivalent (it is a member
        /// boundary, not an intra-chain seam) and maps to <see cref="SeamKind.None"/>; a null seam is
        /// <see cref="SeamKind.None"/>.
        /// </summary>
        internal static SeamKind ToLegacySeam(PhaseSeam seam)
        {
            if (seam == null)
                return SeamKind.None;
            switch (seam.Kind)
            {
                case PhaseSeamKind.Rigid: return SeamKind.Rigid;
                case PhaseSeamKind.FlexibleSoi: return SeamKind.FlexibleSoi;
                default: return SeamKind.None;
            }
        }
    }
}
