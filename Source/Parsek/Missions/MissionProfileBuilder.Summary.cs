using System;
using System.Collections.Generic;
using System.Text;

namespace Parsek
{
    // The hover sentences of section 2.4 and the Summary block of section 3 (bodies and
    // landing sites, crew and EVA time, career results, craft facts).
    internal static partial class MissionProfileBuilder
    {
        internal const string ExcludedSentence =
            "Part of this span is not in the mission: excluded on the Missions tab (Advanced).";

        // ------------------------------------------------------------------ hovers

        private static string LineHover(ProfileRowContext ctx, MissionProfileLine line)
        {
            PhasePlan plan = line.Plan;
            var sb = new StringBuilder();
            sb.Append(line.IsPerson ? "EVA: " + line.Name : line.Name ?? "").Append(": ");
            if (line.ParentLineId == null) sb.Append("launched");
            else sb.Append(StartWord(line.StartEvent)).Append(" at ").Append(StructureTimeFormatter.FormatElapsed(plan.StartUT - ctx.MissionStart));
            int phases = CountPhases(plan.Phases);
            sb.Append("; ").Append(phases.ToString(Ic)).Append(phases == 1 ? " phase" : " phases");
            string outcome = ProfileLabels.Outcome(plan.Terminal, plan.EndBody);
            if (!string.IsNullOrEmpty(outcome)) sb.Append("; ends ").Append(outcome);
            sb.Append('.');
            string aboard = FinalComposition(ctx.Profile, line);
            if (!string.IsNullOrEmpty(aboard)) sb.Append(" Aboard: ").Append(aboard).Append('.');
            if (line.Reflown) sb.Append(' ').Append(ReflownSentence(ctx, line.ReflownUT));
            if (line.ExcludedSpans.Count > 0) sb.Append(' ').Append(ExcludedSentence);
            return sb.ToString();
        }

        private static string StartWord(string startEvent)
        {
            switch (startEvent)
            {
                case "Decoupled": return "separated (decoupled)";
                case "Undocked": return "undocked";
                case "EVA": return "went on EVA";
                case "Broke off": return "broke off";
                case "Broke up": return "survived a breakup";
                default: return string.IsNullOrEmpty(startEvent) ? "started" : startEvent.ToLowerInvariant();
            }
        }

        private static string ReflownSentence(ProfileRowContext ctx, double ut)
            => "Flown again from here (Re-Fly at " + StructureTimeFormatter.FormatElapsed(ut - ctx.MissionStart)
               + "). The first attempt is not shown.";

        private static string PhaseHover(ProfilePhase p, string label, string result)
        {
            var sb = new StringBuilder(label).Append(": ").Append(ProfileLabels.Duration(p.Duration));
            string first = OrbitText(p.FirstOrbit), last = OrbitText(p.LastOrbit);
            if (!ShowsOrbitInHover(p.Kind))
            {
                if (!string.IsNullOrEmpty(result)) sb.Append(", ").Append(result);
            }
            else if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last) && first != last)
                sb.Append(", ").Append(first).Append(" then ").Append(last);
            else if (!string.IsNullOrEmpty(last))
                sb.Append(", ").Append(last);
            int burns = 0, evas = 0;
            foreach (ProfileEvent e in p.Events)
            {
                if (e.Kind == ProfileEventKind.Burn) burns += e.MergedBurns;
                if (e.Kind == ProfileEventKind.Eva) evas++;
            }
            if (burns > 0) sb.Append(", ").Append(burns.ToString(Ic)).Append(burns == 1 ? " burn" : " burns");
            if (evas > 0) sb.Append(", ").Append(evas.ToString(Ic)).Append(evas == 1 ? " EVA" : " EVAs");
            sb.Append('.');
            if (p.Kind == ProfilePhaseKind.Unclassified)
                sb.Append(" No rule named this span: ").Append(p.UnclassifiedReason ?? "unknown").Append('.');
            if (p.Clipped) sb.Append(' ').Append(ExcludedSentence);
            return sb.ToString();
        }

        private static bool ShowsOrbitInHover(ProfilePhaseKind k)
            => k == ProfilePhaseKind.Orbit || k == ProfilePhaseKind.Circularization || k == ProfilePhaseKind.Transfer
               || k == ProfilePhaseKind.Rendezvous || k == ProfilePhaseKind.Aerocapture
               || k == ProfilePhaseKind.Aerobraking || k == ProfilePhaseKind.Injection || k == ProfilePhaseKind.Docked;

        private static string OrbitText(ProfileOrbit o)
        {
            if (!o.IsKnown) return "";
            if (o.Tier == ProfileEvidenceTier.Inferred) return ProfileLabels.About(o.HeldAltitude);
            if (!o.HasElements) return "";
            return o.Kind == ProfileOrbitKind.InOrbit ? ProfileLabels.OrbitShape(o) : ProfileLabels.ApPe(o);
        }

        private static string EventHover(ProfileRowContext ctx, MissionProfileLine line, ProfileEvent e, string label)
        {
            switch (e.Kind)
            {
                case ProfileEventKind.Burn: return BurnHover(ctx, line, e, label);
                case ProfileEventKind.StageSeparation: return StagingHover(e, label);
                case ProfileEventKind.Reflown: return ReflownSentence(ctx, e.UT);
                case ProfileEventKind.Eva:
                    if (e.Anchor != null && e.Anchor.Collapsed && !double.IsNaN(e.Anchor.EndUT))
                        return (e.Anchor.RefName ?? "The kerbal") + " left the vessel for "
                            + ProfileLabels.Duration(e.Anchor.EndUT - e.UT) + " and boarded it again.";
                    return label + ".";
                default:
                    string result = EventResult(line, e);
                    return string.IsNullOrEmpty(result) ? label + "." : label + ": " + result + ".";
            }
        }

        private static string StagingHover(ProfileEvent e, string label)
        {
            var sb = new StringBuilder(label);
            string endings = e.Anchor?.RefName;
            if (!string.IsNullOrEmpty(endings)) sb.Append(": ").Append(endings);
            if (!double.IsNaN(e.StagingDrop) && e.StagingDrop > 0)
                sb.Append("; the dropped fuel took ").Append(ProfileLabels.DeltaV(e.StagingDrop)).Append(" m/s");
            return sb.Append('.').ToString();
        }

        // "Transfer burn: 29 s at 82 km on the LV-T45, 862 m/s spent, 1,022 m/s left. Orbit
        // after: Ap 11,524 km, Pe 82 km, 0.9 deg; Mun encounter."
        private static string BurnHover(ProfileRowContext ctx, MissionProfileLine line, ProfileEvent e, string label)
        {
            var sb = new StringBuilder(label).Append(": ").Append(ProfileLabels.Duration(e.BurnSeconds));
            if (!double.IsNaN(e.AltitudeAtStart)) sb.Append(" at ").Append(ProfileLabels.Distance(e.AltitudeAtStart));
            string engines = EngineTitles(ctx, e.Engines);
            if (!string.IsNullOrEmpty(engines)) sb.Append(" on the ").Append(engines);
            if (!double.IsNaN(e.SpentRecorded))
            {
                sb.Append(", ").Append(ProfileLabels.DeltaV(e.SpentRecorded)).Append(" m/s spent");
                if (!double.IsNaN(e.LeftRecorded)) sb.Append(", ").Append(ProfileLabels.DeltaV(e.LeftRecorded)).Append(" m/s left");
            }
            else if (!double.IsNaN(e.SpentEstimate))
            {
                sb.Append(", about ").Append(ProfileLabels.DeltaV(e.SpentEstimate)).Append(" m/s (estimated from the orbits)");
            }
            sb.Append('.');
            AppendOrbitAfter(sb, line, e);
            if (double.IsNaN(e.SpentRecorded) && double.IsNaN(e.SpentEstimate) && !string.IsNullOrEmpty(e.DeltaVGap))
                sb.Append(' ').Append(Capitalize(e.DeltaVGap)).Append('.');
            return sb.ToString();
        }

        private static void AppendOrbitAfter(StringBuilder sb, MissionProfileLine line, ProfileEvent e)
        {
            if (e.FloorBurn) return;
            ProfileOrbit o = e.After;
            if (o.Tier == ProfileEvidenceTier.Inferred)
            {
                sb.Append(" Orbit after inferred from the altitude holding at ").Append(ProfileLabels.Distance(o.HeldAltitude))
                    .Append("; no orbit sample, so no delta-v.");
                return;
            }
            if (!o.IsKnown)
            {
                sb.Append(" Orbit after the burn is unknown: the vessel stayed in physics range and no orbit sample was recorded.");
                return;
            }
            if (!o.HasElements)
            {
                sb.Append(" Orbit after: ").Append(o.Kind == ProfileOrbitKind.Falling ? "falling" : "leaving");
                if (!string.IsNullOrEmpty(o.Encounter)) sb.Append("; ").Append(o.Encounter).Append(" encounter");
                sb.Append('.');
                return;
            }
            if (o.Energy == ProfileOrbitEnergy.Escaping && double.IsInfinity(o.ApoapsisAltitude))
            {
                sb.Append(" Orbit after: escape, Pe ").Append(ProfileLabels.Distance(o.PeriapsisAltitude));
                if (!string.IsNullOrEmpty(o.Encounter) && !o.EncounterIsExit)
                    sb.Append("; ").Append(o.Encounter).Append(" encounter");
                sb.Append('.');
                return;
            }
            sb.Append(" Orbit after: ").Append(ProfileLabels.ApPe(o));
            string inc = ProfileLabels.Inclination(o.InclinationDeg);
            if (!string.IsNullOrEmpty(inc)) sb.Append(", ").Append(inc);
            if (!string.IsNullOrEmpty(o.Encounter) && !o.EncounterIsExit)
                sb.Append("; ").Append(o.Encounter).Append(" encounter");
            sb.Append('.');
        }

        private static string EngineTitles(ProfileRowContext ctx, List<string> parts)
        {
            if (parts == null || parts.Count == 0) return null;
            var titles = new List<string>();
            foreach (string p in parts)
            {
                string t = null;
                try { t = ctx.Request.PartTitleResolver?.Invoke(p); }
                catch (Exception) { t = null; }
                t = string.IsNullOrEmpty(t) ? p : t;
                if (!string.IsNullOrEmpty(t) && !titles.Contains(t)) titles.Add(t);
            }
            return titles.Count == 0 ? null : string.Join(", ", titles.ToArray());
        }

        private static string Capitalize(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ------------------------------------------------------------------ summary

        private static void EmitSummary(ProfileRowContext ctx, List<MissionProfileLine> drawn)
        {
            MissionProfile profile = ctx.Profile;
            profile.SummaryFacts.Clear();
            AddFact(profile, BodiesFact(drawn));
            AddFact(profile, LandingsFact(drawn));
            foreach (MissionProfileLine l in drawn) AddFact(profile, CrewFact(l, drawn));
            AddFact(profile, CareerFact(ctx.Request.Career));
            AddFact(profile, CraftFact(drawn));
            AddFact(profile, VesselsFact(drawn));
            AddFact(profile, DebrisFact(ctx.Request.Tree, drawn));
            profile.Rows.Add(new MissionProfileRow
            {
                Kind = MissionProfileRowKind.Summary, Key = "summary", HasCaret = true, Element = "Summary",
                Duration = ProfileLabels.Duration(profile.EndUT - profile.StartUT),
                Hover = "The mission at a glance: bodies, landings, crew, career results and craft.",
            });
            foreach (string fact in profile.SummaryFacts)
                profile.Rows.Add(new MissionProfileRow
                {
                    Kind = MissionProfileRowKind.SummaryFact, Depth = 1, Key = UniqueKey(ctx, "summary:" + fact),
                    ParentKey = "summary", Element = fact, Hover = fact,
                });
        }

        private static void AddFact(MissionProfile profile, string fact)
        {
            if (!string.IsNullOrEmpty(fact)) profile.SummaryFacts.Add(fact);
        }

        private static string BodiesFact(List<MissionProfileLine> drawn)
        {
            var bodies = new List<string>();
            foreach (MissionProfileLine l in drawn)
                foreach (ProfilePhase p in l.Plan.Phases)
                {
                    AddDistinct(bodies, p.Body);
                    AddDistinct(bodies, p.TargetBody);
                }
            return bodies.Count == 0 ? null : "Bodies: " + string.Join(", ", bodies.ToArray());
        }

        private static void AddDistinct(List<string> list, string s)
        {
            if (!string.IsNullOrEmpty(s) && !list.Contains(s)) list.Add(s);
        }

        private static string LandingsFact(List<MissionProfileLine> drawn)
        {
            var sites = new List<string>();
            foreach (MissionProfileLine l in drawn)
                foreach (ProfileEvent e in AllEvents(l.Plan.Phases))
                    if (e.Kind == ProfileEventKind.Touchdown || e.Kind == ProfileEventKind.Splashdown)
                        AddDistinct(sites, StructureLocationFormatter.BodyBiome(e.Body, e.Biome)
                            + (e.Kind == ProfileEventKind.Splashdown ? " (splashdown)" : ""));
            return sites.Count == 0 ? null : "Landings: " + string.Join("; ", sites.ToArray());
        }

        internal static IEnumerable<ProfileEvent> AllEvents(List<ProfilePhase> phases)
        {
            foreach (ProfilePhase p in phases)
            {
                foreach (ProfileEvent e in p.Events) yield return e;
                foreach (ProfileEvent e in AllEvents(p.SubPhases)) yield return e;
            }
        }

        // "Crew of CS Mun Science: Jebediah Kerman (2 EVAs, 3s on EVA, recovered)".
        private static string CrewFact(MissionProfileLine line, List<MissionProfileLine> drawn)
        {
            if (line.IsPerson || line.CrewNames.Count == 0) return null;
            var parts = new List<string>();
            foreach (string kerbal in line.CrewNames)
            {
                int evas = 0;
                double evaSeconds = 0;
                foreach (ProfileEvent e in AllEvents(line.Plan.Phases))
                {
                    if (e.Kind != ProfileEventKind.Eva || e.Anchor == null
                        || !string.Equals(e.Anchor.RefName, kerbal, StringComparison.Ordinal))
                        continue;
                    evas++;
                    evaSeconds += EvaSeconds(e, drawn);
                }
                var sb = new StringBuilder(kerbal);
                var notes = new List<string>();
                if (evas > 0)
                {
                    notes.Add(evas.ToString(Ic) + (evas == 1 ? " EVA" : " EVAs"));
                    notes.Add(ProfileLabels.Duration(evaSeconds) + " on EVA");
                }
                string outcome = KerbalOutcome(line, kerbal);
                if (!string.IsNullOrEmpty(outcome)) notes.Add(outcome);
                if (notes.Count > 0) sb.Append(" (").Append(string.Join(", ", notes.ToArray())).Append(')');
                parts.Add(sb.ToString());
            }
            return "Crew of " + line.Name + ": " + string.Join("; ", parts.ToArray());
        }

        private static double EvaSeconds(ProfileEvent e, List<MissionProfileLine> drawn)
        {
            if (e.Anchor.Collapsed && !double.IsNaN(e.Anchor.EndUT)) return e.Anchor.EndUT - e.UT;
            foreach (MissionProfileLine l in drawn)
                if (string.Equals(l.LineId, e.Anchor.Ref, StringComparison.Ordinal))
                    return l.Plan.EndUT - l.Plan.StartUT;
            return 0;
        }

        private static string KerbalOutcome(MissionProfileLine line, string kerbal)
        {
            Recording last = line.Pieces.Count > 0 ? line.Pieces[line.Pieces.Count - 1] : null;
            if (last?.CrewEndStates != null && last.CrewEndStates.TryGetValue(kerbal, out KerbalEndState s))
            {
                switch (s)
                {
                    case KerbalEndState.Dead: return "lost";
                    case KerbalEndState.Recovered: return "recovered";
                    case KerbalEndState.Aboard: return "aboard";
                }
            }
            if (last?.TerminalStateValue == TerminalState.Recovered) return "recovered";
            return null;
        }

        private static string CareerFact(MissionProfileCareerTotals c)
        {
            if (!c.Known) return null;
            var parts = new List<string>();
            if (c.Science != 0) parts.Add("science " + Signed(c.Science, "0.#"));
            if (c.Funds != 0) parts.Add("funds " + Signed(c.Funds, "N0"));
            if (c.Reputation != 0) parts.Add("reputation " + Signed(c.Reputation, "0.#"));
            return parts.Count == 0 ? null : "Career: " + string.Join(", ", parts.ToArray());
        }

        private static string Signed(double v, string format)
            => (v > 0 ? "+" : "") + v.ToString(format, Ic);

        // Part count and stage count at launch, from the root line's first snapshot.
        private static string CraftFact(List<MissionProfileLine> drawn)
        {
            Recording first = drawn.Count > 0 && drawn[0].Pieces.Count > 0 ? drawn[0].Pieces[0] : null;
            ConfigNode snap = first?.GhostVisualSnapshot ?? first?.VesselSnapshot;
            if (snap == null) return null;
            ConfigNode[] parts = snap.GetNodes("PART");
            if (parts == null || parts.Length == 0) return null;
            int maxStage = -1;
            foreach (ConfigNode p in parts)
                if (int.TryParse(p.GetValue("istg"), System.Globalization.NumberStyles.Integer, Ic, out int s) && s > maxStage)
                    maxStage = s;
            string text = "Craft: " + parts.Length.ToString(Ic) + (parts.Length == 1 ? " part" : " parts");
            if (maxStage >= 0) text += ", " + (maxStage + 1).ToString(Ic) + (maxStage == 0 ? " stage" : " stages");
            return text + " at launch";
        }

        private static string VesselsFact(List<MissionProfileLine> drawn)
        {
            var parts = new List<string>();
            foreach (MissionProfileLine l in drawn)
            {
                string outcome = ProfileLabels.Outcome(l.Plan.Terminal, l.Plan.EndBody);
                parts.Add((l.IsPerson ? "EVA: " + l.Name : l.Name) + (string.IsNullOrEmpty(outcome) ? "" : " (" + outcome + ")"));
            }
            return parts.Count == 0 ? null : "Vessels: " + string.Join("; ", parts.ToArray());
        }

        private static string DebrisFact(RecordingTree tree, List<MissionProfileLine> drawn)
        {
            if (tree == null) return null;
            var pieces = new HashSet<string>(StringComparer.Ordinal);
            foreach (MissionProfileLine l in drawn)
                foreach (Recording r in l.Pieces) pieces.Add(r.RecordingId);
            int n = 0;
            foreach (BranchPoint bp in tree.BranchPoints)
            {
                if (bp?.ParentRecordingIds == null || bp.ChildRecordingIds == null) continue;
                bool ours = false;
                foreach (string id in bp.ParentRecordingIds) if (id != null && pieces.Contains(id)) ours = true;
                if (!ours) continue;
                foreach (string id in bp.ChildRecordingIds)
                    if (id != null && tree.Recordings.TryGetValue(id, out Recording r) && r != null && r.IsDebris) n++;
            }
            return n == 0 ? null : "Debris: " + ProfileLabels.Pieces(n);
        }
    }
}
