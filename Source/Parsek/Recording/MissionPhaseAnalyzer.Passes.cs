using System;
using System.Collections.Generic;

namespace Parsek
{
    // Stratum (c), atmosphere passes (design section 4.3). A PASS is an atmosphere stretch
    // entered from space; hops back out under two minutes belong to the same pass (a skip);
    // a run of passes with only coasts between them and no burn is a GROUP. A group is read
    // by the energy before its first pass and by how it ENDS, so the first touch coming from
    // deep space is decided by the rest of the line, never by a forward pass.
    internal static partial class MissionPhaseAnalyzer
    {
        /// <summary>A hop back out of the atmosphere shorter than this is part of the same pass.</summary>
        internal const double PassHopSeconds = 120.0;

        private sealed class PassGroup
        {
            // Item index of the first and last Air item of every pass, in order.
            public readonly List<int> PassFirst = new List<int>();
            public readonly List<int> PassLast = new List<int>();
            // The UT each short hop left the atmosphere (a skip).
            public readonly List<double> HopExits = new List<double>();
            public ProfileOrbitEnergy EnergyBefore;
            public bool Exits;
            public bool ExitsBound;
            public bool ExitsEscaping;
            public int RaiseBurnItem = -1;
            public bool LineEndsInSpace;
            public string Body;

            public PassRule Rule;
            public int Passes => PassFirst.Count;
        }

        /// <summary>One row of the atmosphere-pass table: the facts it matches and the phase
        /// the group reads as. Rules are tried in priority order; the first match claims.</summary>
        internal sealed class PassRule
        {
            public string Name;
            public int Stratum;
            public int Priority;
            public ProfilePhaseKind Reads;
            // The phase the passes before the last read as when the last pass lands
            // (brake-then-land); Reads covers the last one.
            public bool BrakeThenLand;
            internal Func<PassGroupFacts, bool> Match;
        }

        /// <summary>What a pass rule may read: stage-1 facts and stratum (a) orbits only.</summary>
        internal struct PassGroupFacts
        {
            public int Passes;
            public int Hops;
            public ProfileOrbitEnergy EnergyBefore;
            public bool Exits;
            public bool ExitsBound;
            public bool ExitsEscaping;
            public bool RaiseBurn;
            public bool LineEndsInSpace;
        }

        internal const string RuleDirectEntry = "direct-entry";
        internal const string RuleSkipEntry = "skip-entry";
        internal const string RuleBrakeThenLand = "brake-then-land";
        internal const string RuleAerocapture = "aerocapture";
        internal const string RuleAerobraking = "aerobraking";
        internal const string RuleBrakingFlyby = "braking-flyby";
        internal const string RuleUnknownPass = "unknown-pass";

        internal const int PassStratum = 3;

        /// <summary>The atmosphere-pass table of section 4.3 (c), in priority order.</summary>
        internal static PassRule[] PassRules()
        {
            return new[]
            {
                new PassRule
                {
                    Name = RuleDirectEntry, Stratum = PassStratum, Priority = 10, Reads = ProfilePhaseKind.Reentry,
                    Match = f => f.Passes == 1 && !f.Exits && f.Hops == 0,
                },
                new PassRule
                {
                    Name = RuleSkipEntry, Stratum = PassStratum, Priority = 20, Reads = ProfilePhaseKind.Reentry,
                    Match = f => f.Passes == 1 && !f.Exits && f.Hops > 0,
                },
                new PassRule
                {
                    Name = RuleBrakeThenLand, Stratum = PassStratum, Priority = 30, Reads = ProfilePhaseKind.Reentry,
                    BrakeThenLand = true,
                    Match = f => f.Passes > 1 && !f.Exits,
                },
                new PassRule
                {
                    Name = RuleAerocapture, Stratum = PassStratum, Priority = 40, Reads = ProfilePhaseKind.Aerocapture,
                    Match = f => f.Exits && f.EnergyBefore == ProfileOrbitEnergy.Escaping
                        && (f.ExitsBound || f.Passes > 1) && (f.RaiseBurn || f.LineEndsInSpace),
                },
                new PassRule
                {
                    Name = RuleAerobraking, Stratum = PassStratum, Priority = 50, Reads = ProfilePhaseKind.Aerobraking,
                    Match = f => f.Exits && f.EnergyBefore == ProfileOrbitEnergy.Bound
                        && !f.ExitsEscaping && (f.RaiseBurn || f.Passes > 1),
                },
                new PassRule
                {
                    Name = RuleBrakingFlyby, Stratum = PassStratum, Priority = 60, Reads = ProfilePhaseKind.AtmosphericFlyby,
                    Match = f => f.Exits && f.EnergyBefore == ProfileOrbitEnergy.Escaping && f.ExitsEscaping,
                },
                new PassRule
                {
                    Name = RuleUnknownPass, Stratum = PassStratum, Priority = 70, Reads = ProfilePhaseKind.AtmospherePass,
                    Match = f => true,
                },
            };
        }

        /// <summary>The first rule of <paramref name="rules"/> (priority order) that matches.</summary>
        internal static PassRule MatchPassRule(PassRule[] rules, PassGroupFacts facts)
        {
            PassRule best = null;
            for (int i = 0; i < rules.Length; i++)
            {
                if (!rules[i].Match(facts)) continue;
                if (best == null || rules[i].Priority < best.Priority) best = rules[i];
            }
            return best;
        }

        private static void ResolvePasses(Ctx ctx)
        {
            List<Item> items = ctx.Items;
            PassRule[] rules = PassRules();
            PassGroup group = null;
            for (int k = 0; k < items.Count; k++)
            {
                Item it = items[k];
                if (it.Type != ItemType.Floor || it.Regime != ProfileRegime.Air) continue;
                if (k == 0 || !IsSpaceItem(items[k - 1])) continue;
                if (group != null && !OnlyCoastsBetween(items, group.PassLast[group.PassLast.Count - 1], k))
                    group = null;
                if (group == null)
                {
                    group = new PassGroup { Body = it.Body, EnergyBefore = EnergyBefore(ctx, k) };
                    ctx.PassGroups.Add(group);
                }
                int last = ExtendPass(ctx, k, group);
                group.PassFirst.Add(k);
                group.PassLast.Add(last);
                it.PassStart = true;
                for (int m = k; m <= last; m++) items[m].Pass = group;
                k = last;
            }
            foreach (PassGroup g in ctx.PassGroups)
            {
                ReadGroupEnd(ctx, g);
                g.Rule = MatchPassRule(rules, FactsOf(g));
            }
        }

        // The energy of the orbit the first pass is flown on: the coast before it, else the
        // energy any earlier coast on the same body showed with no burn between.
        private static ProfileOrbitEnergy EnergyBefore(Ctx ctx, int k)
        {
            for (int i = k - 1; i >= 0; i--)
            {
                Item it = ctx.Items[i];
                if (it.Type == ItemType.Floor) return ProfileOrbitEnergy.Unknown;
                if (it.Type == ItemType.Burn) return it.After.Energy;
                if (it.EndOrbit.Energy != ProfileOrbitEnergy.Unknown) return it.EndOrbit.Energy;
            }
            return ProfileOrbitEnergy.Unknown;
        }

        // The last Air item of the pass that starts at k: hops out shorter than PassHopSeconds
        // (only space between) fold in, each one recorded as a skip.
        private static int ExtendPass(Ctx ctx, int k, PassGroup group)
        {
            List<Item> items = ctx.Items;
            int last = k;
            while (true)
            {
                int next = last + 1;
                double hop = 0;
                while (next < items.Count && IsSpaceItem(items[next]))
                {
                    hop += items[next].End - items[next].Start;
                    next++;
                }
                if (next >= items.Count || next == last + 1 || hop >= PassHopSeconds
                    || items[next].Type != ItemType.Floor || items[next].Regime != ProfileRegime.Air
                    || HasBurnBetween(items, last, next))
                    return last;
                group.HopExits.Add(items[last].End);
                last = next;
            }
        }

        private static bool HasBurnBetween(List<Item> items, int from, int to)
        {
            for (int i = from + 1; i < to; i++)
                if (items[i].Type == ItemType.Burn) return true;
            return false;
        }

        private static bool OnlyCoastsBetween(List<Item> items, int from, int to)
        {
            for (int i = from + 1; i < to; i++)
                if (items[i].Type != ItemType.Coast) return false;
            return to > from + 1;
        }

        private static void ReadGroupEnd(Ctx ctx, PassGroup g)
        {
            List<Item> items = ctx.Items;
            int last = g.PassLast[g.PassLast.Count - 1];
            g.Exits = last + 1 < items.Count && IsSpaceItem(items[last + 1]);
            if (!g.Exits) return;
            Item after = items[last + 1];
            ProfileOrbit o = after.Type == ItemType.Coast ? after.StartOrbit : after.Before;
            g.ExitsBound = o.Tier == ProfileEvidenceTier.Stored && o.Energy == ProfileOrbitEnergy.Bound
                && string.IsNullOrEmpty(o.Encounter);
            g.ExitsEscaping = o.Kind == ProfileOrbitKind.Leaving;
            g.LineEndsInSpace = true;
            for (int i = last + 1; i < items.Count; i++)
            {
                if (items[i].Type == ItemType.Floor) g.LineEndsInSpace = false;
                if (items[i].Type == ItemType.Burn && g.RaiseBurnItem < 0)
                {
                    ProfileOrbit raised = BurnAfterOrbit(ctx, i);
                    if (raised.Kind == ProfileOrbitKind.InOrbit) g.RaiseBurnItem = i;
                    break;
                }
                if (items[i].Type == ItemType.Floor) break;
            }
        }

        private static PassGroupFacts FactsOf(PassGroup g)
            => new PassGroupFacts
            {
                Passes = g.Passes,
                Hops = g.HopExits.Count,
                EnergyBefore = g.EnergyBefore,
                Exits = g.Exits,
                ExitsBound = g.ExitsBound,
                ExitsEscaping = g.ExitsEscaping,
                RaiseBurn = g.RaiseBurnItem >= 0,
                LineEndsInSpace = g.LineEndsInSpace,
            };
    }
}
