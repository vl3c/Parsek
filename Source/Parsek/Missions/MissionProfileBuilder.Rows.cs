using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // The window's rows (design sections 2.3, 2.4, 3): Summary first, then each drawn line as a
    // header with its phases, the events and sub-phases inside each phase, and a child line
    // under the event that created it. Every cell is a finished string; the window only draws.
    internal static partial class MissionProfileBuilder
    {
        private sealed class ProfileRowContext
        {
            public MissionProfileRequest Request;
            public MissionProfile Profile;
            public string LaunchBody;
            public double MissionStart = double.NaN;
            public bool FirstTimedRowDone;
            public readonly HashSet<string> EmittedLines = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> KeyUse = new Dictionary<string, int>(StringComparer.Ordinal);
            // The phase each Docked by another mission row sits in (null: after its line's end).
            // Keyed by the dock itself, so a child line emitted mid-parent cannot clobber it.
            public readonly Dictionary<ProfileOtherMissionDock, ProfilePhase> OtherDockPhase =
                new Dictionary<ProfileOtherMissionDock, ProfilePhase>();
        }

        private static void EmitRows(ProfileRowContext ctx)
        {
            MissionProfile profile = ctx.Profile;
            var drawn = new List<MissionProfileLine>();
            foreach (MissionProfileLine l in MissionProfileLineBuilder.All(profile.Lines))
                if (l.Plan != null && l.Plan.Phases.Count > 0) drawn.Add(l);
            if (drawn.Count == 0)
            {
                AddEmptyRow(profile);
                return;
            }
            foreach (MissionProfileLine l in drawn)
            {
                if (double.IsNaN(ctx.MissionStart) || l.Plan.StartUT < ctx.MissionStart) ctx.MissionStart = l.Plan.StartUT;
                if (double.IsNaN(profile.EndUT) || l.Plan.EndUT > profile.EndUT) profile.EndUT = l.Plan.EndUT;
            }
            profile.StartUT = ctx.MissionStart;
            EmitSummary(ctx, drawn);
            foreach (MissionProfileLine l in profile.Lines)
                EmitLineTree(ctx, l, 0, null);
        }

        // A drawn line, or (for a line the Profile does not draw: a partner, a collapsed EVA, a
        // bridged fork) nothing but its drawn children at the same depth.
        private static void EmitLineTree(ProfileRowContext ctx, MissionProfileLine line, int depth, string parentKey)
        {
            if (ctx.EmittedLines.Contains(line.LineId ?? "")) return;
            if (line.Plan == null || line.Plan.Phases.Count == 0)
            {
                foreach (MissionProfileLine c in line.Children) EmitLineTree(ctx, c, depth, parentKey);
                return;
            }
            EmitLine(ctx, line, depth, parentKey);
        }

        private static void EmitLine(ProfileRowContext ctx, MissionProfileLine line, int depth, string parentKey)
        {
            ctx.EmittedLines.Add(line.LineId ?? "");
            PhasePlan plan = line.Plan;
            string key = UniqueKey(ctx, "line:" + line.LineId);
            var header = new MissionProfileRow
            {
                Kind = MissionProfileRowKind.Line, Depth = depth, Key = key, ParentKey = parentKey,
                HasCaret = true, LineId = line.LineId, UT = plan.StartUT,
                Time = TimeCell(ctx, plan.StartUT),
                Element = LineElement(line),
                Location = LineLocation(line),
                Duration = ProfileLabels.Duration(plan.EndUT - plan.StartUT),
                Result = ProfileLabels.Outcome(plan.Terminal, plan.EndBody),
                IsReflown = line.Reflown,
                IsClipped = line.ExcludedSpans.Count > 0,
            };
            SetRecorded(header, plan.SpentRecorded, plan.LeftAtEnd);
            header.Hover = LineHover(ctx, line);
            ctx.Profile.Rows.Add(header);
            bool hasTopPhase = PlaceOtherMissionDocks(ctx, line);
            foreach (ProfilePhase p in plan.Phases)
            {
                if (p.Kind == ProfilePhaseKind.Prelaunch)
                {
                    foreach (ProfileEvent e in p.Events)
                        if (e.Kind != ProfileEventKind.Launch) EmitEvent(ctx, line, e, p, depth + 1, key);
                    continue;
                }
                EmitPhase(ctx, line, p, depth + 1, key);
            }
            // A line of nothing but a prelaunch: its other-mission rows sit under the header.
            if (!hasTopPhase)
                foreach (ProfileOtherMissionDock d in line.OtherMissionDocks)
                    EmitOtherMissionDock(ctx, line, d, depth + 1, key);
            foreach (MissionProfileLine c in line.Children) EmitLineTree(ctx, c, depth + 1, key);
        }

        private static void EmitPhase(ProfileRowContext ctx, MissionProfileLine line, ProfilePhase p, int depth, string parentKey)
        {
            string key = UniqueKey(ctx, "phase:" + line.LineId + ":" + p.Kind + ":" + p.StartUT.ToString("F1", Ic));
            string label = ProfileLabels.PhaseLabel(p, ctx.LaunchBody, p.Anchor?.RefName);
            var row = new MissionProfileRow
            {
                Kind = MissionProfileRowKind.Phase, Depth = depth, Key = key, ParentKey = parentKey, HasCaret = true,
                LineId = line.LineId, UT = p.StartUT, PhaseKind = p.Kind,
                PhaseClass = ProfileElementKinds.ClassOf(p.Kind),
                Time = TimeCell(ctx, p.StartUT),
                Element = label,
                Location = PhaseLocation(p),
                Duration = ProfileLabels.Duration(p.Duration),
                Result = PhaseResult(ctx, line, p),
                IsClipped = p.Clipped,
                IsInferred = p.LastOrbit.Tier == ProfileEvidenceTier.Inferred,
                IsUnclassified = p.Kind == ProfilePhaseKind.Unclassified,
            };
            SetRecorded(row, p.SpentRecorded, p.LeftAtEnd);
            row.Hover = PhaseHover(p, label, row.Result);
            ctx.Profile.Rows.Add(row);
            EmitChildren(ctx, line, p, depth + 1, key);
        }

        // Events and sub-phases of a phase, in UT order (a sub-phase reads before the events at
        // its own start).
        private static void EmitChildren(ProfileRowContext ctx, MissionProfileLine line, ProfilePhase p, int depth, string key)
        {
            var others = new List<ProfileOtherMissionDock>();
            foreach (ProfileOtherMissionDock d in line.OtherMissionDocks)
                if (ctx.OtherDockPhase.TryGetValue(d, out ProfilePhase owner) && owner == p) others.Add(d);
            int e = 0, s = 0, o = 0;
            while (e < p.Events.Count || s < p.SubPhases.Count || o < others.Count)
            {
                double eventUT = e < p.Events.Count ? p.Events[e].UT : double.PositiveInfinity;
                double subUT = s < p.SubPhases.Count ? p.SubPhases[s].StartUT : double.PositiveInfinity;
                double otherUT = o < others.Count ? others[o].UT : double.PositiveInfinity;
                if (s < p.SubPhases.Count && subUT <= eventUT && subUT <= otherUT)
                    EmitPhase(ctx, line, p.SubPhases[s++], depth, key);
                else if (e < p.Events.Count && eventUT <= otherUT)
                    EmitEvent(ctx, line, p.Events[e++], p, depth, key);
                else
                    EmitOtherMissionDock(ctx, line, others[o++], depth, key);
            }
            // After the line's end, after its End row: what another mission did to this vessel
            // once this recording had stopped.
            if (p != LastTopPhase(line.Plan)) return;
            foreach (ProfileOtherMissionDock d in line.OtherMissionDocks)
                if (ctx.OtherDockPhase.TryGetValue(d, out ProfilePhase owner) && owner == null)
                    EmitOtherMissionDock(ctx, line, d, depth, key);
        }

        private static ProfilePhase LastTopPhase(PhasePlan plan)
        {
            for (int i = plan.Phases.Count - 1; i >= 0; i--)
                if (plan.Phases[i].Kind != ProfilePhaseKind.Prelaunch) return plan.Phases[i];
            return null;
        }

        // Which phase each Docked by another mission row of the line sits in: the deepest phase
        // whose span covers its instant (before the first phase: the first; past the line's
        // end: none, drawn after the End row). False when the line has no top-level phase.
        private static bool PlaceOtherMissionDocks(ProfileRowContext ctx, MissionProfileLine line)
        {
            var tops = new List<ProfilePhase>();
            foreach (ProfilePhase p in line.Plan.Phases)
                if (p.Kind != ProfilePhaseKind.Prelaunch) tops.Add(p);
            if (tops.Count == 0) return false;
            foreach (ProfileOtherMissionDock d in line.OtherMissionDocks)
                ctx.OtherDockPhase[d] = d.AfterLineEnd ? null : CoveringPhase(tops, d.UT);
            return true;
        }

        private static ProfilePhase CoveringPhase(List<ProfilePhase> phases, double ut)
        {
            ProfilePhase owner = phases[0];
            for (int i = 0; i < phases.Count; i++)
            {
                if (ut >= phases[i].StartUT && (ut < phases[i].EndUT || i == phases.Count - 1))
                {
                    owner = phases[i];
                    break;
                }
            }
            foreach (ProfilePhase sub in owner.SubPhases)
                if (ut >= sub.StartUT && ut < sub.EndUT) return CoveringPhase(new List<ProfilePhase> { sub }, ut);
            return owner;
        }

        // A dock another mission recorded onto this vessel: named, never expanded (no caret,
        // no child line, no partner phases).
        private static void EmitOtherMissionDock(ProfileRowContext ctx, MissionProfileLine line, ProfileOtherMissionDock d,
            int depth, string parentKey)
        {
            string label = ProfileLabels.OtherMissionDockLabel(d);
            string kind = d.IsUndock ? "undock" : "dock";
            var row = new MissionProfileRow
            {
                Kind = MissionProfileRowKind.Event, Depth = depth, ParentKey = parentKey, HasCaret = false,
                Key = UniqueKey(ctx, "other:" + line.LineId + ":" + kind + ":" + d.UT.ToString("F2", Ic)),
                LineId = line.LineId, UT = d.UT,
                EventKind = d.IsUndock ? ProfileEventKind.Undocking
                    : d.ClaimType == BranchPointType.Board ? ProfileEventKind.Board : ProfileEventKind.Docking,
                Time = TimeCell(ctx, d.UT),
                Element = label,
                Result = d.AfterLineEnd ? "not recorded here" : "",
                IsOtherMission = true,
                IsAfterLineEnd = d.AfterLineEnd,
            };
            row.Hover = ProfileLabels.OtherMissionDockHover(d, label);
            ctx.Profile.Rows.Add(row);
            ctx.Profile.OtherMissionEvents++;
        }

        private static void EmitEvent(ProfileRowContext ctx, MissionProfileLine line, ProfileEvent e, ProfilePhase phase,
            int depth, string parentKey)
        {
            // The Docked phase row is the dock itself: no second row for the same instant.
            if (e.Kind == ProfileEventKind.Docking && phase.Kind == ProfilePhaseKind.Docked
                && Math.Abs(e.UT - phase.StartUT) < 1e-6)
                return;
            string name = e.Anchor?.RefName;
            string label = ProfileLabels.EventLabel(e, name, line.Plan.EndBody);
            string key = UniqueKey(ctx, "event:" + line.LineId + ":" + e.Kind + ":" + e.UT.ToString("F2", Ic));
            MissionProfileLine child = ChildLineOf(line, e);
            var row = new MissionProfileRow
            {
                Kind = MissionProfileRowKind.Event, Depth = depth, Key = key, ParentKey = parentKey,
                HasCaret = child != null, LineId = line.LineId, UT = e.UT, EventKind = e.Kind,
                BurnKind = e.Kind == ProfileEventKind.Burn ? e.BurnKind : (ProfileBurnKind?)null,
                Time = TimeCell(ctx, e.UT),
                Element = label,
                Location = EventLocation(e, phase),
                Duration = e.Kind == ProfileEventKind.Burn ? ProfileLabels.Duration(e.BurnSeconds) : "",
                Result = EventResult(line, e),
                IsReflown = e.Kind == ProfileEventKind.Reflown,
            };
            FillEventNumbers(row, e);
            row.Hover = EventHover(ctx, line, e, label);
            ctx.Profile.Rows.Add(row);
            if (child != null) EmitLineTree(ctx, child, depth + 1, key);
        }

        private static MissionProfileLine ChildLineOf(MissionProfileLine line, ProfileEvent e)
        {
            if (e.Anchor == null || string.IsNullOrEmpty(e.Anchor.Ref) || e.Anchor.Collapsed) return null;
            if (e.Kind != ProfileEventKind.Separation && e.Kind != ProfileEventKind.Eva
                && e.Kind != ProfileEventKind.Breakup && e.Kind != ProfileEventKind.Undocking
                && e.Kind != ProfileEventKind.PartPlaced)
                return null;
            foreach (MissionProfileLine c in line.Children)
                if (string.Equals(c.LineId, e.Anchor.Ref, StringComparison.Ordinal) && c.Plan != null && c.Plan.Phases.Count > 0)
                    return c;
            return null;
        }

        private static string UniqueKey(ProfileRowContext ctx, string key)
        {
            ctx.KeyUse.TryGetValue(key, out int n);
            ctx.KeyUse[key] = n + 1;
            return n == 0 ? key : key + "#" + n.ToString(Ic);
        }

        // ------------------------------------------------------------------ cells

        // The first timed row shows the launch date (the Log's formatter); every other row the
        // mission clock, floored, hours unbounded; child lines keep the mission clock.
        private static string TimeCell(ProfileRowContext ctx, double ut)
        {
            if (!ctx.FirstTimedRowDone)
            {
                ctx.FirstTimedRowDone = true;
                Func<double, string> date = ctx.Request.DateFormatter;
                return date != null ? (date(ut) ?? "") : ut.ToString("F0", Ic);
            }
            return StructureTimeFormatter.FormatElapsed(ut - ctx.MissionStart);
        }

        private static string LineElement(MissionProfileLine line)
        {
            if (line.IsPerson) return "EVA: " + line.Name;
            if (line.CrewNames.Count == 0) return line.Name ?? "";
            var first = new List<string>();
            foreach (string c in line.CrewNames) first.Add(FirstName(c));
            return (line.Name ?? "") + " (" + string.Join(", ", first.ToArray()) + ")";
        }

        // The line's final composition, the CompositionLabel the Missions tab's vessel-row
        // tooltip shows ("pod x1, crew x2"): the last interval of the line's row, or, on a
        // re-flown line, of the fork's row the Re-Fly bridge drew into it (the flown-again path
        // is what the line ends on). Null for a kerbal (its label is its name).
        internal static string FinalComposition(MissionProfile profile, MissionProfileLine line)
        {
            if (line?.Row == null || line.IsPerson) return null;
            MissionCompositionNode fork = null;
            if (profile != null && line.Reflown)
            {
                foreach (MissionProfileLine l in MissionProfileLineBuilder.All(profile.Lines))
                {
                    if (l.Row == null || !string.Equals(l.MergedIntoLineId, line.LineId, StringComparison.Ordinal))
                        continue;
                    MissionCompositionNode end = LastInterval(l.Row);
                    if (end != null && (fork == null || end.EndUT > fork.EndUT)) fork = end;
                }
            }
            return (fork ?? LastInterval(line.Row))?.CompositionLabel;
        }

        private static MissionCompositionNode LastInterval(MissionVesselRow row)
            => row.Intervals.Count > 0 ? row.Intervals[row.Intervals.Count - 1] : null;

        internal static string FirstName(string kerbal)
        {
            if (string.IsNullOrEmpty(kerbal)) return "";
            int space = kerbal.IndexOf(' ');
            return space > 0 ? kerbal.Substring(0, space) : kerbal;
        }

        private static string LineLocation(MissionProfileLine line)
        {
            Recording first = line.Pieces.Count > 0 ? line.Pieces[0] : null;
            if (first == null) return "";
            string body = first.StartBodyName ?? MissionPhaseAnalyzerBodyOf(first);
            if (!string.IsNullOrEmpty(first.LaunchSiteName) && line.ParentLineId == null)
                return StructureLocationFormatter.BodyBiome(body, first.LaunchSiteName);
            return string.IsNullOrEmpty(body) ? "" : body;
        }

        // Where a phase is flown, never a repeat of its label: a transfer reads body to body,
        // a surface stay names its biome.
        private static string PhaseLocation(ProfilePhase p)
        {
            if (p.Kind == ProfilePhaseKind.Transfer && !string.IsNullOrEmpty(p.TargetBody))
                return (p.Body ?? "") + ProfileLabels.Arrow + p.TargetBody;
            return p.Body ?? "";
        }

        // An event shows a location only when it differs from its phase's or names a biome.
        private static string EventLocation(ProfileEvent e, ProfilePhase phase)
        {
            string body = e.Kind == ProfileEventKind.SoiExit ? (e.After.Body ?? e.Body) : e.Body;
            string biome = e.Biome ?? (e.Anchor != null ? e.Anchor.Biome : null);
            bool namesBiome = !string.IsNullOrEmpty(biome)
                && (e.Kind == ProfileEventKind.Touchdown || e.Kind == ProfileEventKind.Splashdown
                    || e.Kind == ProfileEventKind.End || e.Kind == ProfileEventKind.Recovered);
            string where = namesBiome ? StructureLocationFormatter.BodyBiome(body, biome) : body;
            if (string.IsNullOrEmpty(where)) return "";
            return string.Equals(where, PhaseLocation(phase), StringComparison.Ordinal) ? "" : where;
        }

        private static void SetRecorded(MissionProfileRow row, double spent, double left)
        {
            if (!double.IsNaN(spent))
            {
                row.Spent = ProfileLabels.DeltaV(spent);
                row.HasSpent = true;
            }
            if (!double.IsNaN(left)) row.Left = ProfileLabels.DeltaV(left);
        }

        private static void FillEventNumbers(MissionProfileRow row, ProfileEvent e)
        {
            if (e.Kind == ProfileEventKind.StageSeparation)
            {
                if (!double.IsNaN(e.LeftRecorded)) row.Left = ProfileLabels.DeltaV(e.LeftRecorded);
                return;
            }
            if (e.Kind != ProfileEventKind.Burn) return;
            if (!double.IsNaN(e.SpentRecorded))
            {
                SetRecorded(row, e.SpentRecorded, e.LeftRecorded);
            }
            else if (!double.IsNaN(e.SpentEstimate))
            {
                row.Spent = ProfileLabels.EstimateMark + ProfileLabels.DeltaV(e.SpentEstimate);
                row.SpentIsEstimate = true;
                row.HasSpent = true;
            }
            else
            {
                row.Spent = ProfileLabels.Missing;
                row.HasDataGap = true;
            }
            if (!e.FloorBurn && !e.After.IsKnown) row.HasDataGap = true;
            row.IsInferred = e.After.Tier == ProfileEvidenceTier.Inferred || e.Before.Tier == ProfileEvidenceTier.Inferred;
        }

        private static string PhaseResult(ProfileRowContext ctx, MissionProfileLine line, ProfilePhase p)
        {
            switch (p.Kind)
            {
                case ProfilePhaseKind.Ascent:
                    return AscentResult(p);
                case ProfilePhaseKind.Transfer:
                    return TransferResult(line, p);
                case ProfilePhaseKind.Reentry:
                    return ReentryResult(p);
                case ProfilePhaseKind.Docked:
                    return p.Anchor != null && p.Anchor.OwnShipAbsorbed
                        ? "own ship absorbed into the partner" : "partner absorbed";
                case ProfilePhaseKind.SuborbitalHop:
                case ProfilePhaseKind.Flight:
                case ProfilePhaseKind.Flyby:
                case ProfilePhaseKind.AtmosphericFlyby:
                case ProfilePhaseKind.JetpackFlight:
                    return double.IsNaN(p.MaxAltitude) ? "" : "reached " + ProfileLabels.Distance(p.MaxAltitude);
                case ProfilePhaseKind.Drive:
                    return double.IsNaN(p.DriveDistance) ? "" : ProfileLabels.Distance(p.DriveDistance) + " driven";
                case ProfilePhaseKind.Unclassified:
                    return p.UnclassifiedReason ?? "";
                case ProfilePhaseKind.Rendezvous:
                case ProfilePhaseKind.SurfaceStay:
                case ProfilePhaseKind.EvaSurface:
                case ProfilePhaseKind.Eva:
                case ProfilePhaseKind.Descent:
                case ProfilePhaseKind.Deorbit:
                    return "";
                default:
                    return p.LastOrbit.IsKnown ? ProfileLabels.OrbitResult(p.LastOrbit, double.NaN) : "";
            }
        }

        // "to 70 km, Ap 80 km": the altitude the ascent left the atmosphere at and the
        // apoapsis of the orbit it left on.
        private static string AscentResult(ProfilePhase p)
        {
            if (p.Rule == MissionPhaseAnalyzer.RuleAbortedAscent)
                return double.IsNaN(p.MaxAltitude) ? "" : "reached " + ProfileLabels.Distance(p.MaxAltitude);
            var parts = new List<string>();
            if (!double.IsNaN(p.ExitAltitude)) parts.Add("to " + ProfileLabels.Distance(p.ExitAltitude));
            if (p.LastOrbit.HasElements && !double.IsInfinity(p.LastOrbit.ApoapsisAltitude))
                parts.Add("Ap " + ProfileLabels.Distance(p.LastOrbit.ApoapsisAltitude));
            return string.Join(", ", parts.ToArray());
        }

        private static string TransferResult(MissionProfileLine line, ProfilePhase p)
        {
            ProfilePhase next = NextPhase(line.Plan, p);
            if (next != null && next.Kind == ProfilePhaseKind.Orbit)
            {
                string orbit = ProfileLabels.OrbitName(next.Body);
                return next.FirstOrbit.Tier == ProfileEvidenceTier.Inferred
                    ? ProfileLabels.About(next.FirstOrbit.HeldAltitude) + " " + orbit : orbit;
            }
            if (p.LastOrbit.HasElements) return "Pe " + ProfileLabels.Distance(p.LastOrbit.PeriapsisAltitude);
            return "";
        }

        private static ProfilePhase NextPhase(PhasePlan plan, ProfilePhase p)
        {
            int i = plan.Phases.IndexOf(p);
            return i >= 0 && i + 1 < plan.Phases.Count ? plan.Phases[i + 1] : null;
        }

        private static string ReentryResult(ProfilePhase p)
        {
            int skips = 0;
            foreach (ProfileEvent e in p.Events) if (e.Kind == ProfileEventKind.Skip) skips++;
            switch (p.Rule)
            {
                case MissionPhaseAnalyzer.RuleSkipEntry:
                    return "skip entry (" + skips.ToString(Ic) + (skips == 1 ? " skip)" : " skips)");
                case MissionPhaseAnalyzer.RuleBrakeThenLand: return "after braking passes";
                default: return "direct entry";
            }
        }

        private static string EventResult(MissionProfileLine line, ProfileEvent e)
        {
            switch (e.Kind)
            {
                case ProfileEventKind.Burn:
                    if (e.FloorBurn) return "";
                    if (e.BurnKind == ProfileBurnKind.Descent && e.After.Tier == ProfileEvidenceTier.Structural)
                        return "falling (coast to the surface)";
                    return ProfileLabels.OrbitResult(e.After, EncounterPeriapsis(line.Plan, e));
                case ProfileEventKind.SoiEntry:
                    return e.After.HasElements ? "Pe " + ProfileLabels.Distance(e.After.PeriapsisAltitude) : "";
                case ProfileEventKind.SoiExit:
                    return e.After.HasElements ? ProfileLabels.ApPe(e.After) : "";
                case ProfileEventKind.Touchdown:
                    return double.IsNaN(e.SurfaceAltitude) ? "landed" : "landed " + ProfileLabels.Distance(e.SurfaceAltitude);
                case ProfileEventKind.Splashdown:
                    return "splashed";
                case ProfileEventKind.Docking:
                    return e.Anchor != null && e.Anchor.OwnShipAbsorbed ? "own ship absorbed into the partner" : "partner absorbed";
                case ProfileEventKind.Undocking:
                    return "own ship resumes";
                default:
                    return "";
            }
        }

        // The periapsis around the encountered body, from the first stored orbit after the SOI
        // entry the burn leads to.
        private static double EncounterPeriapsis(PhasePlan plan, ProfileEvent burn)
        {
            if (string.IsNullOrEmpty(burn.After.Encounter)) return double.NaN;
            foreach (ProfilePhase p in plan.Phases)
                foreach (ProfileEvent e in p.Events)
                    if (e.UT > burn.UT && (e.Kind == ProfileEventKind.SoiEntry || e.Kind == ProfileEventKind.SoiExit)
                        && e.After.HasElements
                        && string.Equals(e.After.Body, burn.After.Encounter, StringComparison.Ordinal))
                        return e.After.PeriapsisAltitude;
            return double.NaN;
        }
    }
}
