using System;
using System.Collections.Generic;

namespace Parsek
{
    // Stratum (d), burns (design sections 4.3, 4.4, 4.6): each burn is named from the
    // resolved orbit facts on its two sides and the anchors around it, with hindsight (a burn
    // followed by a dock in the same orbit is a phasing or approach burn; a burn whose coast
    // ends in an encounter is a transfer). Delta-v comes from the recorded snapshots first,
    // else from an impulsive estimate between two stored conics.
    internal static partial class MissionPhaseAnalyzer
    {
        /// <summary>Approach burns are the rendezvous burns this close before the dock; the
        /// same window collapses several approach burns into one row.</summary>
        internal const double ApproachWindowSeconds = 600.0;

        /// <summary>A plane change moves the inclination by more than this ...</summary>
        internal const double PlaneChangeMinDeg = 2.0;
        /// <summary>... and keeps apoapsis and periapsis within this fraction.</summary>
        internal const double PlaneChangeShapeFraction = 0.05;

        /// <summary>The impulsive estimate is offered only for burns shorter than this.</summary>
        internal const double EstimateMaxBurnSeconds = 120.0;

        /// <summary>The snapshot read at a burn's start lands a few frames after ignition.</summary>
        internal const double SnapshotToleranceSeconds = 1.0;

        /// <summary>A staging's settled delta-v read lands within this window after it.</summary>
        internal const double StagingDropWindowSeconds = 5.0;

        internal const int BurnStratum = 4;

        /// <summary>What a burn rule may read: stratum (a) orbits and stratum (b) anchors.</summary>
        internal struct BurnFacts
        {
            public ProfileOrbitKind Before;
            public ProfileOrbitKind After;
            public ProfileOrbit BeforeOrbit;
            public ProfileOrbit AfterOrbit;
            public bool BodyHasAtmosphere;
            public bool InTransfer;
            public bool DockAhead;
            public double SecondsToDock;
            public bool PlaneChange;
            public bool TargetIsLaunchBody;
            public bool HasEncounter;
            // The last time the line was at the floor, it was an atmosphere pass (not an ascent).
            public bool AfterPass;
        }

        /// <summary>One row of table 4.4: the facts it matches and the name it gives.</summary>
        internal sealed class BurnRule
        {
            public string Name;
            public int Stratum;
            public int Priority;
            internal Func<BurnFacts, bool> Match;
            // The name the rule gives: Kind, unless NameFrom reads it off the facts.
            internal ProfileBurnKind Kind;
            internal Func<BurnFacts, ProfileBurnKind> NameFrom;

            internal ProfileBurnKind Names(BurnFacts f) => NameFrom != null ? NameFrom(f) : Kind;
        }

        internal const string RuleLanding = "landing";
        internal const string RuleCircularization = "circularization";
        internal const string RuleInjection = "injection";
        internal const string RuleCaptureAero = "capture-aerobraking";
        internal const string RuleCapture = "capture";
        internal const string RuleCorrection = "correction";
        internal const string RuleDeparture = "departure";
        internal const string RulePeriapsisRaise = "periapsis-raise";
        internal const string RuleDeorbit = "deorbit";
        internal const string RuleApproach = "approach";
        internal const string RulePhasing = "phasing";
        internal const string RulePlaneChange = "plane-change";
        internal const string RuleOrbitChange = "orbit-change";
        internal const string RuleUnknownBurn = "unknown-burn";

        private static bool InAirBound(ProfileOrbit o)
            => o.Tier != ProfileEvidenceTier.None && string.IsNullOrEmpty(o.Encounter)
               && o.Energy == ProfileOrbitEnergy.Bound && o.Periapsis == ProfilePeriapsis.InAir;

        private static bool OrbitLike(ProfileOrbitKind k) => k == ProfileOrbitKind.InOrbit || k == ProfileOrbitKind.Unknown;

        private static BurnRule Rule(string name, int priority, Func<BurnFacts, bool> match, ProfileBurnKind kind,
            Func<BurnFacts, ProfileBurnKind> nameFrom = null)
            => new BurnRule { Name = name, Stratum = BurnStratum, Priority = priority, Match = match, Kind = kind, NameFrom = nameFrom };

        /// <summary>Table 4.4 in priority order (space burns; the landing burn is a floor burn).
        /// Each row's predicate is a named method below, so the table stays data.</summary>
        internal static BurnRule[] BurnRules()
        {
            return new[]
            {
                Rule(RulePeriapsisRaise, 15, MatchesPeriapsisRaise, ProfileBurnKind.PeriapsisRaise),
                Rule(RuleCircularization, 20, MatchesCircularization, ProfileBurnKind.Circularization),
                Rule(RuleInjection, 30, MatchesInjection, ProfileBurnKind.Injection),
                Rule(RuleCaptureAero, 40, MatchesCaptureAero, ProfileBurnKind.CaptureAerobraking),
                Rule(RuleCapture, 50, MatchesCapture, ProfileBurnKind.Capture),
                Rule(RuleCorrection, 60, MatchesCorrection, ProfileBurnKind.Correction),
                Rule(RuleDeparture, 70, MatchesDeparture, ProfileBurnKind.Transfer, NameDeparture),
                Rule(RuleDeorbit, 90, MatchesDeorbit, ProfileBurnKind.Deorbit, NameDeorbit),
                Rule(RuleApproach, 100, MatchesApproach, ProfileBurnKind.Approach),
                Rule(RulePhasing, 110, MatchesPhasing, ProfileBurnKind.Phasing),
                Rule(RulePlaneChange, 120, MatchesPlaneChange, ProfileBurnKind.PlaneChange),
                Rule(RuleOrbitChange, 130, MatchesOrbitChange, ProfileBurnKind.OrbitChange),
                Rule(RuleUnknownBurn, 999, MatchesAnyBurn, ProfileBurnKind.Unknown),
            };
        }

        private static bool MatchesPeriapsisRaise(BurnFacts f)
            => f.AfterPass && InAirBound(f.BeforeOrbit) && f.After == ProfileOrbitKind.InOrbit;

        private static bool MatchesCircularization(BurnFacts f)
            => f.Before == ProfileOrbitKind.Falling && f.After == ProfileOrbitKind.InOrbit;

        private static bool MatchesInjection(BurnFacts f)
            => f.Before == ProfileOrbitKind.Falling && f.After == ProfileOrbitKind.Leaving && !f.InTransfer;

        private static bool MatchesCaptureAero(BurnFacts f)
            => f.Before == ProfileOrbitKind.Leaving && InAirBound(f.AfterOrbit);

        private static bool MatchesCapture(BurnFacts f)
            => f.Before == ProfileOrbitKind.Leaving && f.After == ProfileOrbitKind.InOrbit;

        private static bool MatchesCorrection(BurnFacts f)
            => (f.Before == ProfileOrbitKind.Leaving && f.After == ProfileOrbitKind.Leaving)
               || (f.InTransfer && f.Before != ProfileOrbitKind.InOrbit
                   && (f.After == ProfileOrbitKind.Leaving || f.After == ProfileOrbitKind.Falling));

        private static bool MatchesDeparture(BurnFacts f)
            => f.Before == ProfileOrbitKind.InOrbit && f.After == ProfileOrbitKind.Leaving;

        private static ProfileBurnKind NameDeparture(BurnFacts f)
            => f.TargetIsLaunchBody ? ProfileBurnKind.Return
               : f.HasEncounter ? ProfileBurnKind.Transfer : ProfileBurnKind.Escape;

        private static bool MatchesDeorbit(BurnFacts f)
            => f.Before == ProfileOrbitKind.InOrbit && f.After == ProfileOrbitKind.Falling;

        private static ProfileBurnKind NameDeorbit(BurnFacts f)
            => f.BodyHasAtmosphere ? ProfileBurnKind.Deorbit : ProfileBurnKind.Descent;

        private static bool MatchesApproach(BurnFacts f)
            => f.DockAhead && f.SecondsToDock <= ApproachWindowSeconds && OrbitLike(f.Before) && OrbitLike(f.After);

        private static bool MatchesPhasing(BurnFacts f)
            => f.DockAhead && OrbitLike(f.Before) && OrbitLike(f.After)
               && (f.Before == ProfileOrbitKind.InOrbit || f.After == ProfileOrbitKind.InOrbit);

        private static bool MatchesPlaneChange(BurnFacts f)
            => f.Before == ProfileOrbitKind.InOrbit && f.After == ProfileOrbitKind.InOrbit && f.PlaneChange;

        private static bool MatchesOrbitChange(BurnFacts f)
            => f.Before == ProfileOrbitKind.InOrbit && f.After == ProfileOrbitKind.InOrbit;

        private static bool MatchesAnyBurn(BurnFacts f) => true;

        /// <summary>The first rule of <paramref name="rules"/> (priority order) that matches.</summary>
        internal static BurnRule MatchBurnRule(BurnRule[] rules, BurnFacts facts)
        {
            BurnRule best = null;
            for (int i = 0; i < rules.Length; i++)
            {
                if (!rules[i].Match(facts)) continue;
                if (best == null || rules[i].Priority < best.Priority) best = rules[i];
            }
            return best;
        }

        // ------------------------------------------------------------------ orbits of a burn

        private static ProfileOrbit BurnBeforeOrbit(Ctx ctx, int i)
        {
            Item prev = i > 0 ? ctx.Items[i - 1] : null;
            Item b = ctx.Items[i];
            if (prev == null) return default(ProfileOrbit);
            if (prev.Type == ItemType.Coast) return prev.EndOrbit;
            if (prev.Type == ItemType.Burn) return prev.After;
            return IsFloorExit(prev) ? Falling(ctx, b.Body, ProfileEvidenceTier.Structural) : default(ProfileOrbit);
        }

        private static ProfileOrbit BurnAfterOrbit(Ctx ctx, int i)
        {
            Item next = i + 1 < ctx.Items.Count ? ctx.Items[i + 1] : null;
            Item b = ctx.Items[i];
            if (next == null) return TerminalAfterLastBurn(ctx, b);
            if (next.Type == ItemType.Coast) return next.StartOrbit;
            if (next.Type == ItemType.Burn) return default(ProfileOrbit);
            if (next.Regime == ProfileRegime.Air || next.Regime == ProfileRegime.Surface
                || EndsAtFloorEntry(ctx, i))
                return Falling(ctx, b.Body, ProfileEvidenceTier.Structural);
            return default(ProfileOrbit);
        }

        // A line that ends during or right after its last burn: the terminal orbit, when its
        // epoch is at or after the burn's end, is that burn's stored orbit after.
        private static ProfileOrbit TerminalAfterLastBurn(Ctx ctx, Item b)
        {
            Recording last = ctx.Line.Pieces[ctx.Line.Pieces.Count - 1];
            if (last == null || !last.TerminalStateValue.HasValue || last.TerminalOrbitSemiMajorAxis == 0
                || double.IsNaN(last.TerminalOrbitSemiMajorAxis)
                || !string.Equals(last.TerminalOrbitBody, b.Body, StringComparison.Ordinal)
                || last.TerminalOrbitEpoch < b.BurnEnd - EngineMatchSeconds)
                return default(ProfileOrbit);
            ProfileOrbit o = FromSegment(last.TerminalOrbitAsSegment(), ctx.Facts(b.Body), ProfileEvidenceTier.Stored);
            o.Body = b.Body;
            return o;
        }

        // ------------------------------------------------------------------ naming

        private static void NameBurns(Ctx ctx)
        {
            BurnRule[] rules = BurnRules();
            bool leaving = false;
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                Item it = ctx.Items[i];
                if (it.Type == ItemType.Floor)
                {
                    leaving = false;
                    continue;
                }
                if (it.Type != ItemType.Burn) continue;
                it.InTransfer = leaving;
                BurnFacts f = FactsOfBurn(ctx, i);
                BurnRule rule = MatchBurnRule(rules, f);
                it.BurnKind = rule.Names(f);
                it.BurnRule = rule.Name;
                // In-transfer reads the orbit facts only (never a burn's name): leaving
                // opens it, reaching an orbit closes it, falling keeps it (a return leg).
                ProfileOrbitKind after = it.After.Kind;
                if (after == ProfileOrbitKind.Leaving) leaving = true;
                else if (after == ProfileOrbitKind.InOrbit) leaving = false;
            }
        }

        private static BurnFacts FactsOfBurn(Ctx ctx, int i)
        {
            Item b = ctx.Items[i];
            var f = new BurnFacts
            {
                BeforeOrbit = b.Before,
                AfterOrbit = b.After,
                Before = b.Before.Kind,
                After = b.After.Kind,
                BodyHasAtmosphere = ctx.HasAtmosphere(b.Body),
                InTransfer = b.InTransfer,
                HasEncounter = !string.IsNullOrEmpty(b.After.Encounter)
                    && (!b.After.EncounterIsExit || ArrivesElsewhere(ctx, i)),
            };
            string target = TransferTarget(ctx, b);
            f.TargetIsLaunchBody = !string.IsNullOrEmpty(target)
                && string.Equals(target, ctx.Line.LaunchBodyName, StringComparison.Ordinal)
                && !string.Equals(target, b.Body, StringComparison.Ordinal);
            f.PlaneChange = IsPlaneChange(b.Before, b.After);
            f.AfterPass = LastFloorWasAPass(ctx, i);
            double dock = NextDockUT(ctx, i);
            f.DockAhead = !double.IsNaN(dock);
            f.SecondsToDock = f.DockAhead ? dock - b.BurnEnd : double.NaN;
            return f;
        }

        private static bool LastFloorWasAPass(Ctx ctx, int i)
        {
            for (int k = i - 1; k >= 0; k--)
            {
                Item it = ctx.Items[k];
                if (it.Type == ItemType.Floor) return it.Pass != null;
            }
            return false;
        }

        // An escape that later enters another body's SOI (not the one it left, not that
        // body's parent) is a transfer out to that body, not an escape.
        private static bool ArrivesElsewhere(Ctx ctx, int i)
        {
            Item b = ctx.Items[i];
            string parent = ctx.Facts(b.Body).ParentName;
            for (int k = i + 1; k < ctx.Items.Count; k++)
            {
                Item it = ctx.Items[k];
                if (it.Type == ItemType.Floor) return false;
                if (string.IsNullOrEmpty(it.Body) || SameBodyStrict(it.Body, b.Body)
                    || SameBodyStrict(it.Body, parent))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>Where a burn sends the vessel: its encounter, else the parent body when it
        /// escapes.</summary>
        private static string TransferTarget(Ctx ctx, Item b)
        {
            if (!string.IsNullOrEmpty(b.After.Encounter)) return b.After.Encounter;
            if (b.After.Energy == ProfileOrbitEnergy.Escaping) return ctx.Facts(b.Body).ParentName;
            return null;
        }

        private static bool IsPlaneChange(ProfileOrbit a, ProfileOrbit b)
        {
            if (!a.HasElements || !b.HasElements) return false;
            if (Math.Abs(b.InclinationDeg - a.InclinationDeg) <= PlaneChangeMinDeg) return false;
            return Near(a.ApoapsisAltitude, b.ApoapsisAltitude) && Near(a.PeriapsisAltitude, b.PeriapsisAltitude);
        }

        private static bool Near(double x, double y)
            => !double.IsNaN(x) && !double.IsNaN(y)
               && Math.Abs(x - y) <= PlaneChangeShapeFraction * Math.Max(1.0, Math.Abs(x));

        // The dock this burn leads to in the same orbit: a Docking anchor later than the burn
        // with no floor and no burn that leaves or drops the orbit between them.
        private static double NextDockUT(Ctx ctx, int i)
        {
            Item b = ctx.Items[i];
            double dock = double.NaN;
            foreach (ProfileAnchorInput a in ctx.Line.Anchors)
                if (a.Kind == ProfileEventKind.Docking && a.UT >= b.BurnEnd && (double.IsNaN(dock) || a.UT < dock))
                    dock = a.UT;
            if (double.IsNaN(dock)) return double.NaN;
            for (int k = i + 1; k < ctx.Items.Count && ctx.Items[k].Start < dock; k++)
            {
                Item it = ctx.Items[k];
                if (it.Type == ItemType.Floor || !SameBody(it.Body, b.Body)) return double.NaN;
                if (it.Type == ItemType.Burn && (it.After.Kind == ProfileOrbitKind.Leaving
                    || it.After.Kind == ProfileOrbitKind.Falling))
                    return double.NaN;
            }
            return dock;
        }

        // ------------------------------------------------------------------ delta-v

        private static void ComputeDeltaV(Ctx ctx)
        {
            foreach (ProfilePhase p in ctx.Plan.Phases)
                ComputeDeltaVIn(ctx, p);
            ComputeLineDeltaV(ctx);
        }

        private static void ComputeDeltaVIn(Ctx ctx, ProfilePhase phase)
        {
            double phaseSpent = double.NaN;
            foreach (ProfileEvent e in phase.Events)
            {
                if (e.Kind == ProfileEventKind.StageSeparation) ReadStagingDrop(ctx, e);
                if (e.Kind != ProfileEventKind.Burn) continue;
                ReadBurnDeltaV(ctx, e);
                if (!double.IsNaN(e.SpentRecorded))
                    phaseSpent = (double.IsNaN(phaseSpent) ? 0 : phaseSpent) + e.SpentRecorded;
            }
            foreach (ProfilePhase sub in phase.SubPhases)
            {
                ComputeDeltaVIn(ctx, sub);
                if (!double.IsNaN(sub.SpentRecorded))
                    phaseSpent = (double.IsNaN(phaseSpent) ? 0 : phaseSpent) + sub.SpentRecorded;
            }
            if (ctx.Snapshots.Count == 0) return;
            if (IsPoweredFloorPhase(phase.Kind))
                phaseSpent = SpentAcross(ctx, phase.StartUT, phase.EndUT, phase);
            phase.SpentRecorded = phaseSpent;
            if (DeltaVSnapshotList.TryGetLeftAt(ctx.Snapshots, phase.EndUT + SnapshotToleranceSeconds, out DeltaVSnapshot left))
                phase.LeftAtEnd = left.TotalVacuum;
        }

        // Ascents and atmospheric flights burn with no burn rows: their Spent is the fall in
        // Left across the phase, less what the stagings inside it dropped.
        private static bool IsPoweredFloorPhase(ProfilePhaseKind kind)
            => kind == ProfilePhaseKind.Ascent || kind == ProfilePhaseKind.Flight
               || kind == ProfilePhaseKind.SuborbitalHop;

        private static double SpentAcross(Ctx ctx, double start, double end, ProfilePhase phase)
        {
            if (!DeltaVSnapshotList.TryGetLeftAt(ctx.Snapshots, start + SnapshotToleranceSeconds, out DeltaVSnapshot a)
                || !DeltaVSnapshotList.TryGetLeftAt(ctx.Snapshots, end + SnapshotToleranceSeconds, out DeltaVSnapshot b))
                return double.NaN;
            return SpentBetween(ctx, a, b);
        }

        /// <summary>
        /// Spent between two Ok snapshots: the fall in Left less what the stagings between
        /// them dropped (each staging's PreStaging / Staging pair, section 8.1). NaN when a
        /// staging between them has no complete pair, or its pair straddles either snapshot:
        /// then the fall mixes dropped and burned fuel and no split of it is honest.
        /// </summary>
        private static double SpentBetween(Ctx ctx, DeltaVSnapshot a, DeltaVSnapshot b)
        {
            if (a == null || b == null || b.UT < a.UT) return double.NaN;
            double spent = a.TotalVacuum - b.TotalVacuum;
            foreach (ProfileAnchorInput anchor in ctx.Line.Anchors)
            {
                if (anchor.Kind != ProfileEventKind.StageSeparation) continue;
                double drop = StagingDropBetween(ctx, anchor, a.UT, b.UT);
                if (double.IsNaN(drop)) return double.NaN;
                spent -= drop;
            }
            return Math.Max(0, spent);
        }

        // The drop of one staging between the snapshots read at fromUT and toUT: 0 when it
        // lies outside them, NaN when it lies inside (or its pair straddles one of them) with
        // no usable pair. By UT, not list order: a burn start the recorder served from the
        // same kept reading as the PreStaging shares its UT and its figures.
        private static double StagingDropBetween(Ctx ctx, ProfileAnchorInput anchor, double fromUT, double toUT)
        {
            if (DeltaVSnapshotList.TryGetStagingDrop(ctx.Snapshots, anchor.UT, anchor.EndUT, StagingDropWindowSeconds,
                    out double drop, out int first, out int last))
            {
                double pre = ctx.Snapshots[first].UT, post = ctx.Snapshots[last].UT;
                if (post <= fromUT || pre >= toUT) return 0;
                return pre >= fromUT && post <= toUT ? drop : double.NaN;
            }
            double end = double.IsNaN(anchor.EndUT) ? anchor.UT : Math.Max(anchor.UT, anchor.EndUT);
            return end <= fromUT || anchor.UT >= toUT ? 0 : double.NaN;
        }

        private static void ReadStagingDrop(Ctx ctx, ProfileEvent e)
        {
            if (ctx.Snapshots.Count == 0) return;
            double endUT = e.Anchor != null ? e.Anchor.EndUT : double.NaN;
            if (!DeltaVSnapshotList.TryGetStagingDrop(ctx.Snapshots, e.UT, endUT, StagingDropWindowSeconds,
                    out double drop, out int _, out int last))
            {
                e.DeltaVGap = GapDeltaVStagingUnread;
                return;
            }
            e.StagingDrop = drop;
            e.LeftRecorded = ctx.Snapshots[last].TotalVacuum;
        }

        private static void ReadBurnDeltaV(Ctx ctx, ProfileEvent e)
        {
            bool stagingUnread = false;
            if (ctx.Snapshots.Count > 0
                && DeltaVSnapshotList.TryGetBurnSpent(ctx.Snapshots, e.UT, e.EndUT, SnapshotToleranceSeconds,
                    out double _, out double left, out DeltaVSnapshot before, out DeltaVSnapshot after))
            {
                double spent = SpentBetween(ctx, before, after);
                if (!double.IsNaN(spent))
                {
                    e.SpentRecorded = spent;
                    e.LeftRecorded = left;
                    return;
                }
                stagingUnread = true;
            }
            string gap = EstimateBurnDeltaV(ctx, e);
            if (gap != null) e.DeltaVGap = stagingUnread ? GapDeltaVStagingUnread : gap;
        }

        // The orbit estimate of a burn with no recorded figures; returns the gap reason when
        // none can be given.
        private static string EstimateBurnDeltaV(Ctx ctx, ProfileEvent e)
        {
            if (e.MergedBurns > 1) return GapDeltaVNoStoredOrbit;
            if (e.BurnSeconds >= EstimateMaxBurnSeconds) return GapDeltaVLongBurn;
            double est = EstimateDeltaV(ctx, e.Before, e.After, 0.5 * (e.UT + e.EndUT));
            if (double.IsNaN(est)) return GapDeltaVNoStoredOrbit;
            e.SpentEstimate = est;
            return null;
        }

        /// <summary>
        /// The impulsive delta-v estimate of a burn (section 4.6): both conics STORED around one
        /// body, propagated to the burn's midpoint in the stored element frame (no live Zup:
        /// the magnitude of a velocity difference does not depend on the frame), and the
        /// magnitude of their velocity difference. NaN when either side is not stored.
        /// </summary>
        internal static double EstimateDeltaV(
            RecordingOptimizer.BodyFacts body, ProfileOrbit before, ProfileOrbit after, double midUT)
        {
            if (before.Tier != ProfileEvidenceTier.Stored || after.Tier != ProfileEvidenceTier.Stored
                || !before.HasElements || !after.HasElements || !body.Known || !(body.GravParameter > 0))
                return double.NaN;
            if (!string.Equals(before.Elements.bodyName, after.Elements.bodyName, StringComparison.Ordinal))
                return double.NaN;
            if (!BallisticExtrapolator.TwoBodyOrbit.TryCreateFromSegmentStoredFrame(
                    before.Elements, body.GravParameter, out BallisticExtrapolator.TwoBodyOrbit a)
                || !BallisticExtrapolator.TwoBodyOrbit.TryCreateFromSegmentStoredFrame(
                    after.Elements, body.GravParameter, out BallisticExtrapolator.TwoBodyOrbit b))
                return double.NaN;
            a.GetStateAtUT(midUT, out Vector3d _, out Vector3d va);
            b.GetStateAtUT(midUT, out Vector3d _, out Vector3d vb);
            double dv = (vb - va).magnitude;
            return double.IsNaN(dv) || double.IsInfinity(dv) ? double.NaN : dv;
        }

        private static double EstimateDeltaV(Ctx ctx, ProfileOrbit before, ProfileOrbit after, double midUT)
            => EstimateDeltaV(ctx.Facts(before.Elements.bodyName ?? before.Body), before, after, midUT);

        private static void ComputeLineDeltaV(Ctx ctx)
        {
            PhasePlan plan = ctx.Plan;
            if (ctx.Snapshots.Count == 0) return;
            double sum = double.NaN;
            foreach (ProfilePhase p in plan.Phases)
                if (!double.IsNaN(p.SpentRecorded))
                    sum = (double.IsNaN(sum) ? 0 : sum) + p.SpentRecorded;
            plan.SpentRecorded = sum;
            if (DeltaVSnapshotList.TryGetLeftAt(ctx.Snapshots, plan.EndUT + SnapshotToleranceSeconds, out DeltaVSnapshot end))
                plan.LeftAtEnd = end.TotalVacuum;
            if (DeltaVSnapshotList.TryGetLeftAt(ctx.Snapshots, plan.StartUT + SnapshotToleranceSeconds, out DeltaVSnapshot start))
                plan.LeftAtStart = start.TotalVacuum;
        }
    }
}
