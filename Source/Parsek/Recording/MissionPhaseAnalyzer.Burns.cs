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
            internal Func<BurnFacts, ProfileBurnKind> Names;
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

        /// <summary>Table 4.4 in priority order (space burns; the landing burn is a floor burn).</summary>
        internal static BurnRule[] BurnRules()
        {
            return new[]
            {
                new BurnRule
                {
                    Name = RulePeriapsisRaise, Stratum = BurnStratum, Priority = 15,
                    Match = f => f.AfterPass && InAirBound(f.BeforeOrbit) && f.After == ProfileOrbitKind.InOrbit,
                    Names = f => ProfileBurnKind.PeriapsisRaise,
                },
                new BurnRule
                {
                    Name = RuleCircularization, Stratum = BurnStratum, Priority = 20,
                    Match = f => f.Before == ProfileOrbitKind.Falling && f.After == ProfileOrbitKind.InOrbit,
                    Names = f => ProfileBurnKind.Circularization,
                },
                new BurnRule
                {
                    Name = RuleInjection, Stratum = BurnStratum, Priority = 30,
                    Match = f => f.Before == ProfileOrbitKind.Falling && f.After == ProfileOrbitKind.Leaving && !f.InTransfer,
                    Names = f => ProfileBurnKind.Injection,
                },
                new BurnRule
                {
                    Name = RuleCaptureAero, Stratum = BurnStratum, Priority = 40,
                    Match = f => f.Before == ProfileOrbitKind.Leaving && InAirBound(f.AfterOrbit),
                    Names = f => ProfileBurnKind.CaptureAerobraking,
                },
                new BurnRule
                {
                    Name = RuleCapture, Stratum = BurnStratum, Priority = 50,
                    Match = f => f.Before == ProfileOrbitKind.Leaving && f.After == ProfileOrbitKind.InOrbit,
                    Names = f => ProfileBurnKind.Capture,
                },
                new BurnRule
                {
                    Name = RuleCorrection, Stratum = BurnStratum, Priority = 60,
                    Match = f => (f.Before == ProfileOrbitKind.Leaving && f.After == ProfileOrbitKind.Leaving)
                        || (f.InTransfer && f.Before != ProfileOrbitKind.InOrbit
                            && (f.After == ProfileOrbitKind.Leaving || f.After == ProfileOrbitKind.Falling)),
                    Names = f => ProfileBurnKind.Correction,
                },
                new BurnRule
                {
                    Name = RuleDeparture, Stratum = BurnStratum, Priority = 70,
                    Match = f => f.Before == ProfileOrbitKind.InOrbit && f.After == ProfileOrbitKind.Leaving,
                    Names = f => f.TargetIsLaunchBody ? ProfileBurnKind.Return
                        : f.HasEncounter ? ProfileBurnKind.Transfer : ProfileBurnKind.Escape,
                },
                new BurnRule
                {
                    Name = RuleDeorbit, Stratum = BurnStratum, Priority = 90,
                    Match = f => f.Before == ProfileOrbitKind.InOrbit && f.After == ProfileOrbitKind.Falling,
                    Names = f => f.BodyHasAtmosphere ? ProfileBurnKind.Deorbit : ProfileBurnKind.Descent,
                },
                new BurnRule
                {
                    Name = RuleApproach, Stratum = BurnStratum, Priority = 100,
                    Match = f => f.DockAhead && f.SecondsToDock <= ApproachWindowSeconds
                        && OrbitLike(f.Before) && OrbitLike(f.After),
                    Names = f => ProfileBurnKind.Approach,
                },
                new BurnRule
                {
                    Name = RulePhasing, Stratum = BurnStratum, Priority = 110,
                    Match = f => f.DockAhead && OrbitLike(f.Before) && OrbitLike(f.After)
                        && (f.Before == ProfileOrbitKind.InOrbit || f.After == ProfileOrbitKind.InOrbit),
                    Names = f => ProfileBurnKind.Phasing,
                },
                new BurnRule
                {
                    Name = RulePlaneChange, Stratum = BurnStratum, Priority = 120,
                    Match = f => f.Before == ProfileOrbitKind.InOrbit && f.After == ProfileOrbitKind.InOrbit && f.PlaneChange,
                    Names = f => ProfileBurnKind.PlaneChange,
                },
                new BurnRule
                {
                    Name = RuleOrbitChange, Stratum = BurnStratum, Priority = 130,
                    Match = f => f.Before == ProfileOrbitKind.InOrbit && f.After == ProfileOrbitKind.InOrbit,
                    Names = f => ProfileBurnKind.OrbitChange,
                },
                new BurnRule
                {
                    Name = RuleUnknownBurn, Stratum = BurnStratum, Priority = 999,
                    Match = f => true,
                    Names = f => ProfileBurnKind.Unknown,
                },
            };
        }

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
            double spent = a.TotalVacuum - b.TotalVacuum;
            foreach (ProfileEvent e in phase.Events)
                if (e.Kind == ProfileEventKind.StageSeparation && !double.IsNaN(e.StagingDrop))
                    spent -= e.StagingDrop;
            return Math.Max(0, spent);
        }

        private static void ReadStagingDrop(Ctx ctx, ProfileEvent e)
        {
            if (ctx.Snapshots.Count == 0) return;
            if (!DeltaVSnapshotList.TryGetStagingDrop(ctx.Snapshots, e.UT, StagingDropWindowSeconds, out double drop))
                return;
            e.StagingDrop = drop;
            if (DeltaVSnapshotList.TryGetLeftAt(ctx.Snapshots, e.UT + StagingDropWindowSeconds, out DeltaVSnapshot left))
                e.LeftRecorded = left.TotalVacuum;
        }

        private static void ReadBurnDeltaV(Ctx ctx, ProfileEvent e)
        {
            if (ctx.Snapshots.Count > 0
                && DeltaVSnapshotList.TryGetBurnSpent(ctx.Snapshots, e.UT, e.EndUT, SnapshotToleranceSeconds,
                    out double spent, out double left))
            {
                spent -= StagingDropsInside(ctx, e.UT, e.EndUT);
                e.SpentRecorded = Math.Max(0, spent);
                e.LeftRecorded = left;
                return;
            }
            if (e.MergedBurns > 1)
            {
                e.DeltaVGap = GapDeltaVNoStoredOrbit;
                return;
            }
            if (e.BurnSeconds >= EstimateMaxBurnSeconds)
            {
                e.DeltaVGap = GapDeltaVLongBurn;
                return;
            }
            double est = EstimateDeltaV(ctx, e.Before, e.After, 0.5 * (e.UT + e.EndUT));
            if (double.IsNaN(est)) e.DeltaVGap = GapDeltaVNoStoredOrbit;
            else e.SpentEstimate = est;
        }

        private static double StagingDropsInside(Ctx ctx, double start, double end)
        {
            double sum = 0;
            foreach (ProfileAnchorInput a in ctx.Line.Anchors)
            {
                if (a.Kind != ProfileEventKind.StageSeparation || a.UT <= start || a.UT >= end) continue;
                if (DeltaVSnapshotList.TryGetStagingDrop(ctx.Snapshots, a.UT, StagingDropWindowSeconds, out double d)
                    && d > 0)
                    sum += d;
            }
            return sum;
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
