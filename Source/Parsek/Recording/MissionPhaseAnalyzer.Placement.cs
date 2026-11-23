using System;
using System.Collections.Generic;

namespace Parsek
{
    // Stage 3, the second half: the events of section 3.2 (the line's own boundaries, its
    // burns, and the anchors the line builder passed in) are placed in the phase that covers
    // their UT; the include set clips phases and drops the events inside excluded spans; the
    // totality check names whatever no rule claimed; the plan counts are read for the log.
    internal static partial class MissionPhaseAnalyzer
    {
        private static void PlaceEvents(Ctx ctx)
        {
            var events = new List<ProfileEvent>();
            AddBoundaryEvents(ctx, events);
            AddBurnEvents(ctx, events);
            CollapseApproachBurns(events);
            AddParachuteEvents(ctx, events);
            foreach (ProfileAnchorInput a in ctx.Line.Anchors)
                events.Add(new ProfileEvent
                {
                    Kind = a.Kind, UT = a.UT, EndUT = a.EndUT, Count = a.Count, Anchor = a,
                    Body = BodyAt(ctx, a.UT), Biome = a.Biome, Rule = "anchor",
                });
            events.Sort(CompareEvent);
            foreach (ProfileEvent e in events)
            {
                ProfilePhase p = PhaseAt(ctx.Plan.Phases, e.UT, IsClosingEvent(e.Kind));
                if (p == null && ctx.Plan.Phases.Count > 0)
                    p = e.UT < ctx.Plan.Phases[0].StartUT ? ctx.Plan.Phases[0] : ctx.Plan.Phases[ctx.Plan.Phases.Count - 1];
                if (p == null) continue;
                ProfilePhase sub = PhaseAt(p.SubPhases, e.UT, IsClosingEvent(e.Kind));
                (sub ?? p).Events.Add(e);
            }
        }

        private static int CompareEvent(ProfileEvent a, ProfileEvent b)
        {
            int c = a.UT.CompareTo(b.UT);
            return c != 0 ? c : ((int)a.Kind).CompareTo((int)b.Kind);
        }

        /// <summary>Events that end a span belong to the phase they close.</summary>
        internal static bool IsClosingEvent(ProfileEventKind kind)
            => kind == ProfileEventKind.Touchdown || kind == ProfileEventKind.Splashdown
               || kind == ProfileEventKind.AtmosphereExit || kind == ProfileEventKind.Undocking
               || kind == ProfileEventKind.End || kind == ProfileEventKind.Recovered;

        /// <summary>The phase of <paramref name="phases"/> that covers <paramref name="ut"/>:
        /// [start, end), or (start, end] for a closing event.</summary>
        private static ProfilePhase PhaseAt(List<ProfilePhase> phases, double ut, bool closing)
        {
            ProfilePhase hit = null;
            for (int i = 0; i < phases.Count; i++)
            {
                ProfilePhase p = phases[i];
                bool inside = closing ? (ut > p.StartUT && ut <= p.EndUT + 1e-6) : (ut >= p.StartUT - 1e-6 && ut < p.EndUT);
                if (inside) hit = closing ? (hit ?? p) : p;
            }
            if (hit == null && phases.Count > 0)
            {
                ProfilePhase last = phases[phases.Count - 1];
                if (Math.Abs(ut - last.EndUT) <= 1e-6) hit = last;
                else if (closing)
                    for (int i = 0; i < phases.Count; i++)
                        if (ut >= phases[i].StartUT - 1e-6 && ut <= phases[i].EndUT + 1e-6) { hit = phases[i]; break; }
            }
            return hit;
        }

        // Liftoff, touchdown / splashdown, atmosphere exit / entry, passes and skips, and the
        // SOI changes: the line's own boundaries between items.
        private static void AddBoundaryEvents(Ctx ctx, List<ProfileEvent> events)
        {
            List<Item> items = ctx.Items;
            for (int k = 1; k < items.Count; k++)
            {
                Item prev = items[k - 1], it = items[k];
                bool prevSurface = prev.Regime == ProfileRegime.Surface;
                bool surface = it.Regime == ProfileRegime.Surface;
                if (prevSurface && !surface && !ctx.Line.IsEva)
                    events.Add(new ProfileEvent { Kind = ProfileEventKind.Liftoff, UT = it.Start, Body = it.Body, Rule = "leave-surface" });
                else if (!prevSurface && surface)
                    events.Add(TouchdownEvent(ctx, k));
                if (prev.Regime == ProfileRegime.Air && IsSpaceItem(it) && ctx.HasAtmosphere(prev.Body)
                    && !IsHopExit(prev))
                    events.Add(new ProfileEvent { Kind = ProfileEventKind.AtmosphereExit, UT = it.Start, Body = prev.Body, Rule = "env-boundary" });
                if (IsSpaceItem(prev) && it.Regime == ProfileRegime.Air && it.PassStart)
                    AddPassEntryEvent(ctx, events, k);
                if (IsSpaceItem(prev) && IsSpaceItem(it) && !SameBodyStrict(prev.Body, it.Body)
                    && !string.IsNullOrEmpty(prev.Body) && !string.IsNullOrEmpty(it.Body))
                    events.Add(SoiEvent(ctx, prev, it));
            }
            foreach (PassGroup g in ctx.PassGroups)
                foreach (double ut in g.HopExits)
                    events.Add(new ProfileEvent { Kind = ProfileEventKind.Skip, UT = ut, Body = g.Body, Rule = RuleSkipEntry });
        }

        private static bool IsHopExit(Item air)
        {
            if (air.Pass == null) return false;
            foreach (double ut in air.Pass.HopExits)
                if (Math.Abs(ut - air.End) < 1e-6) return true;
            return false;
        }

        private static void AddPassEntryEvent(Ctx ctx, List<ProfileEvent> events, int k)
        {
            Item it = ctx.Items[k];
            PassGroup g = it.Pass;
            int n = g.PassFirst.IndexOf(k) + 1;
            bool landing = g.Rule != null && g.Rule.Reads == ProfilePhaseKind.Reentry
                && (!g.Rule.BrakeThenLand || n == g.Passes);
            events.Add(new ProfileEvent
            {
                Kind = landing ? ProfileEventKind.AtmosphereEntry : ProfileEventKind.Pass,
                UT = it.Start, Body = it.Body, Count = n, Rule = g.Rule?.Name,
            });
        }

        private static ProfileEvent SoiEvent(Ctx ctx, Item from, Item to)
        {
            bool intoChild = string.Equals(ctx.Facts(to.Body).ParentName, from.Body, StringComparison.Ordinal);
            return new ProfileEvent
            {
                Kind = intoChild ? ProfileEventKind.SoiEntry : ProfileEventKind.SoiExit,
                UT = to.Start,
                Body = intoChild ? to.Body : from.Body,
                Rule = "body-change",
                After = to.StartOrbit,
            };
        }

        private static ProfileEvent TouchdownEvent(Ctx ctx, int k)
        {
            Item it = ctx.Items[k];
            bool lastSurface = true;
            for (int i = k + 1; i < ctx.Items.Count; i++)
                if (ctx.Items[i].Regime == ProfileRegime.Surface) lastSurface = false;
            Recording last = ctx.Line.Pieces[ctx.Line.Pieces.Count - 1];
            bool splashed = lastSurface && last?.TerminalStateValue == TerminalState.Splashed;
            Sec first = ctx.Secs[it.First];
            double alt = double.NaN;
            if (!first.Relative && first.S.frames != null && first.S.frames.Count > 0) alt = first.S.frames[0].altitude;
            else if (!first.Relative && !float.IsNaN(first.S.minAltitude)) alt = first.S.minAltitude;
            return new ProfileEvent
            {
                Kind = splashed ? ProfileEventKind.Splashdown : ProfileEventKind.Touchdown,
                UT = it.Start, Body = it.Body, Rule = "surface-boundary",
                SurfaceAltitude = alt,
                Biome = lastSurface ? LineEndBiome(ctx) : null,
            };
        }

        // The end biome of the line: the last piece's, else the latest earlier piece that
        // recorded one (a chain split leaves it on the piece it was captured on).
        private static string LineEndBiome(Ctx ctx)
        {
            for (int i = ctx.Line.Pieces.Count - 1; i >= 0; i--)
                if (!string.IsNullOrEmpty(ctx.Line.Pieces[i]?.EndBiome)) return ctx.Line.Pieces[i].EndBiome;
            return null;
        }

        private static void AddBurnEvents(Ctx ctx, List<ProfileEvent> events)
        {
            for (int k = 0; k < ctx.Items.Count; k++)
            {
                Item it = ctx.Items[k];
                if (it.Type == ItemType.Burn)
                {
                    var e = new ProfileEvent
                    {
                        Kind = ProfileEventKind.Burn, UT = it.BurnStart, EndUT = it.BurnEnd, Body = it.Body,
                        BurnKind = it.BurnKind, Rule = it.BurnRule, Before = it.Before, After = it.After,
                        EngineTimed = it.EngineTimed, BurnSeconds = it.BurnEnd - it.BurnStart,
                        AltitudeAtStart = AltitudeAt(ctx, it.First),
                        Tier = WeakerTier(it.Before.Tier, it.After.Tier),
                    };
                    e.Engines.AddRange(it.Engines);
                    events.Add(e);
                }
                else if (it.Type == ItemType.Floor && !ctx.Line.IsEva && !IsTakeoffFloor(ctx, k))
                {
                    foreach (FloorBurn fb in it.FloorBurns)
                        events.Add(FloorBurnEvent(ctx, k, fb));
                }
            }
        }

        // A floor run the vessel took off through: its firings are the ascent's (or the
        // flight's), never burn rows of their own.
        private static bool IsTakeoffFloor(Ctx ctx, int k)
        {
            for (int i = k - 1; i >= 0; i--)
            {
                Item it = ctx.Items[i];
                if (it.Regime == ProfileRegime.Surface) return true;
                if (it.Type != ItemType.Floor) return false;
            }
            return true;
        }

        private static ProfileEvent FloorBurnEvent(Ctx ctx, int k, FloorBurn fb)
        {
            Item it = ctx.Items[k];
            bool landing = LeadsToSurface(ctx, k) || it.Regime == ProfileRegime.Surface;
            var e = new ProfileEvent
            {
                Kind = ProfileEventKind.Burn, UT = fb.Start, EndUT = fb.End, Body = it.Body,
                BurnKind = landing ? ProfileBurnKind.Landing : ProfileBurnKind.Unknown,
                Rule = landing ? RuleLanding : RuleUnknownBurn, EngineTimed = true,
                BurnSeconds = fb.End - fb.Start, FloorBurn = true, Tier = ProfileEvidenceTier.Structural,
                AltitudeAtStart = AltitudeAt(ctx, it.First),
            };
            e.Engines.AddRange(fb.Parts);
            return e;
        }

        private static ProfileEvidenceTier WeakerTier(ProfileEvidenceTier a, ProfileEvidenceTier b)
            => (ProfileEvidenceTier)Math.Min((int)a, (int)b);

        private static double AltitudeAt(Ctx ctx, int secIndex)
        {
            Sec s = ctx.Secs[secIndex];
            if (s.Relative || s.OnRails) return double.NaN;
            if (s.S.frames != null && s.S.frames.Count > 0) return s.S.frames[0].altitude;
            return float.IsNaN(s.S.minAltitude) ? double.NaN : s.S.minAltitude;
        }

        // Several approach burns within ApproachWindowSeconds read as one row.
        private static void CollapseApproachBurns(List<ProfileEvent> events)
        {
            events.Sort(CompareEvent);
            for (int i = 0; i < events.Count; i++)
            {
                ProfileEvent a = events[i];
                if (a.Kind != ProfileEventKind.Burn || a.BurnKind != ProfileBurnKind.Approach) continue;
                for (int j = i + 1; j < events.Count; j++)
                {
                    ProfileEvent b = events[j];
                    if (b.Kind != ProfileEventKind.Burn) continue;
                    if (b.BurnKind != ProfileBurnKind.Approach || b.UT - a.UT > ApproachWindowSeconds) break;
                    a.EndUT = Math.Max(a.EndUT, b.EndUT);
                    a.BurnSeconds += b.BurnSeconds;
                    a.MergedBurns++;
                    a.After = b.After;
                    foreach (string eng in b.Engines) if (!a.Engines.Contains(eng)) a.Engines.Add(eng);
                    events.RemoveAt(j);
                    j--;
                }
            }
        }

        // One Parachutes deployed per descent: the first chute that opens.
        private static void AddParachuteEvents(Ctx ctx, List<ProfileEvent> events)
        {
            var seen = new HashSet<ProfilePhase>();
            foreach (Recording r in ctx.Line.Pieces)
            {
                if (r?.PartEvents == null) continue;
                foreach (PartEvent pe in r.PartEvents)
                {
                    if (pe.eventType != PartEventType.ParachuteSemiDeployed && pe.eventType != PartEventType.ParachuteDeployed)
                        continue;
                    ProfilePhase p = PhaseAt(ctx.Plan.Phases, pe.ut, false);
                    if (p == null || !seen.Add(p)) continue;
                    events.Add(new ProfileEvent
                    {
                        Kind = ProfileEventKind.ParachutesDeployed, UT = pe.ut, Body = BodyAt(ctx, pe.ut), Rule = "first-chute",
                    });
                }
            }
        }

        // ------------------------------------------------------------------ include set

        // Per interval, non-cascading (the Missions contract): an excluded span drops the events
        // inside it, clips the phases that overlap it and drops a phase wholly inside it.
        private static void ApplyExclusions(Ctx ctx)
        {
            foreach (ProfileSpan span in ctx.Line.ExcludedSpans)
            {
                if (!(span.EndUT > span.StartUT)) continue;
                ClipPhases(ctx.Plan, ctx.Plan.Phases, span);
            }
        }

        private static void ClipPhases(PhasePlan plan, List<ProfilePhase> phases, ProfileSpan span)
        {
            for (int i = phases.Count - 1; i >= 0; i--)
            {
                ProfilePhase p = phases[i];
                p.Events.RemoveAll(e => e.UT > span.StartUT && e.UT < span.EndUT);
                ClipPhases(plan, p.SubPhases, span);
                if (p.EndUT <= span.StartUT || p.StartUT >= span.EndUT) continue;
                if (p.StartUT >= span.StartUT && p.EndUT <= span.EndUT)
                {
                    phases.RemoveAt(i);
                    plan.DroppedByInclude++;
                    continue;
                }
                if (p.StartUT >= span.StartUT) p.StartUT = span.EndUT;
                else if (p.EndUT <= span.EndUT) p.EndUT = span.StartUT;
                p.Clipped = true;
                plan.ClippedByInclude++;
            }
        }

        // ------------------------------------------------------------------ totality

        // Every section is claimed exactly once: phases tile the line from its first section
        // with no gap and no overlap. A leading stretch no rule claimed becomes "In space"
        // with the reason, so the totality holds by construction and the tests can assert it.
        private static void CheckTotality(Ctx ctx)
        {
            List<ProfilePhase> phases = ctx.Plan.Phases;
            phases.Sort((a, b) => a.StartUT.CompareTo(b.StartUT));
            if (ctx.Line.ExcludedSpans.Count > 0 || phases.Count == 0) return;
            double first = ctx.Secs[0].Start;
            if (phases[0].StartUT > first + 1e-6)
                phases.Insert(0, new ProfilePhase
                {
                    Kind = ProfilePhaseKind.Unclassified, StartUT = first, EndUT = phases[0].StartUT,
                    Body = ctx.Secs[0].Body, UnclassifiedReason = "no rule claimed it",
                });
            if (!PhasesTile(ctx.Plan, out string problem)) ctx.Plan.TilingProblem = problem;
        }

        /// <summary>True when the phases of <paramref name="plan"/> tile its span: sorted, no
        /// overlap, no gap (sub-phases inside their parent and apart from each other).</summary>
        internal static bool PhasesTile(PhasePlan plan, out string problem)
        {
            problem = null;
            List<ProfilePhase> phases = plan.Phases;
            for (int i = 0; i < phases.Count; i++)
            {
                ProfilePhase p = phases[i];
                if (p.EndUT < p.StartUT) { problem = "phase " + i + " ends before it starts"; return false; }
                if (i > 0 && Math.Abs(p.StartUT - phases[i - 1].EndUT) > 1e-6)
                {
                    problem = "gap or overlap before phase " + i;
                    return false;
                }
                for (int j = 0; j < p.SubPhases.Count; j++)
                {
                    ProfilePhase s = p.SubPhases[j];
                    if (s.StartUT < p.StartUT - 1e-6 || s.EndUT > p.EndUT + 1e-6)
                    {
                        problem = "sub-phase outside its parent in phase " + i;
                        return false;
                    }
                    if (j > 0 && s.StartUT < p.SubPhases[j - 1].EndUT - 1e-6)
                    {
                        problem = "sub-phases overlap in phase " + i;
                        return false;
                    }
                }
            }
            return true;
        }

        private static void CountPlan(Ctx ctx)
        {
            PhasePlan plan = ctx.Plan;
            foreach (ProfilePhase p in plan.Phases) CountPhase(plan, p);
        }

        private static void CountPhase(PhasePlan plan, ProfilePhase p)
        {
            if (p.Kind == ProfilePhaseKind.Unclassified) plan.Unclassified++;
            if (p.LastOrbit.Tier == ProfileEvidenceTier.Inferred) plan.Inferred++;
            foreach (ProfileEvent e in p.Events)
            {
                plan.Events++;
                if (e.Kind == ProfileEventKind.Reflown) plan.Reflown++;
                if (e.Kind == ProfileEventKind.StageSeparation && !string.IsNullOrEmpty(e.DeltaVGap))
                    plan.Gaps.Add(new ProfileDataGap { UT = e.UT, Reason = e.DeltaVGap });
                if (e.Kind != ProfileEventKind.Burn) continue;
                plan.Burns += e.MergedBurns;
                if (!double.IsNaN(e.SpentRecorded)) plan.DvRecorded++;
                if (!double.IsNaN(e.SpentEstimate)) plan.DvEstimated++;
                if (e.FloorBurn) continue;
                if (e.After.Tier == ProfileEvidenceTier.Inferred)
                    plan.Gaps.Add(new ProfileDataGap { UT = e.EndUT, Reason = GapOrbitInferred });
                if (!e.After.IsKnown)
                {
                    plan.OrbitUnknown++;
                    plan.Gaps.Add(new ProfileDataGap { UT = e.EndUT, Reason = GapOrbitUnknown });
                }
                if (!string.IsNullOrEmpty(e.DeltaVGap) && double.IsNaN(e.SpentRecorded))
                    plan.Gaps.Add(new ProfileDataGap { UT = e.UT, Reason = e.DeltaVGap });
            }
            foreach (ProfilePhase s in p.SubPhases) CountPhase(plan, s);
        }
    }
}
