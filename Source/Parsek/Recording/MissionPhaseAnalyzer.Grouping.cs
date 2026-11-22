using System;
using System.Collections.Generic;

namespace Parsek
{
    // Stratum (e) and stage 3 (design section 4.3): the interpreted spans merge into the
    // phases of section 3.1, which tile the line at each level; the events of section 3.2 are
    // placed in the phase that covers their UT; the include set clips; a totality check names
    // whatever no rule claimed.
    internal static partial class MissionPhaseAnalyzer
    {
        /// <summary>A surface stay this short at the very end of a line folds into the phase
        /// before it (a recovery right after touchdown).</summary>
        internal const double SurfaceFoldSeconds = 10.0;

        /// <summary>Drive: surface motion longer than this ...</summary>
        internal const double DriveMinSeconds = 30.0;
        /// <summary>... and farther than this ...</summary>
        internal const double DriveMinMeters = 50.0;
        /// <summary>... with stop-go runs closer than this merged.</summary>
        internal const double DriveMergeSeconds = 60.0;

        internal const string RuleAbortedAscent = "aborted-ascent";

        private sealed class Mark
        {
            public double UT;
            public ProfilePhaseKind Kind;
            public string Body;
            public string Rule;
            public string Reason;
            public ProfileAnchorInput Anchor;
        }

        private sealed class GroupState
        {
            public Ctx Ctx;
            public readonly List<Mark> Marks = new List<Mark>();
            public readonly List<ProfileSpan> AtmosphericFlybys = new List<ProfileSpan>();
            public bool HasCurrent => Marks.Count > 0;
            public ProfilePhaseKind Current => Marks.Count > 0 ? Marks[Marks.Count - 1].Kind : ProfilePhaseKind.Unclassified;
        }

        private static Mark Begin(GroupState st, double ut, ProfilePhaseKind kind, string body, string rule,
            string reason = null, ProfileAnchorInput anchor = null)
        {
            if (st.Marks.Count > 0)
            {
                Mark last = st.Marks[st.Marks.Count - 1];
                if (last.Kind == kind && SameBodyStrict(last.Body, body)) return last;
                if (ut < last.UT) ut = last.UT;
                if (ut <= last.UT + 1e-6) st.Marks.RemoveAt(st.Marks.Count - 1);
            }
            var m = new Mark { UT = ut, Kind = kind, Body = body, Rule = rule, Reason = reason, Anchor = anchor };
            st.Marks.Add(m);
            return m;
        }

        private static void GroupPhases(Ctx ctx)
        {
            var st = new GroupState { Ctx = ctx };
            for (int k = 0; k < ctx.Items.Count; k++)
            {
                switch (ctx.Items[k].Type)
                {
                    case ItemType.Floor: OnFloor(st, k); break;
                    case ItemType.Coast: OnCoast(st, k); break;
                    default: OnBurn(st, k); break;
                }
            }
            if (!st.HasCurrent)
                Begin(st, ctx.Items[0].Start, ProfilePhaseKind.Unclassified, ctx.Items[0].Body, null,
                    "no rule claimed the line");
            ApplyDockMarks(st);
            BuildPhasesFromMarks(ctx, st.Marks);
            BuildSubPhases(ctx, st);
        }

        private static bool IsLaunchStart(Ctx ctx)
        {
            Recording first = ctx.Line.Pieces[0];
            return first != null && (!string.IsNullOrEmpty(first.LaunchSiteName)
                || string.Equals(first.StartSituation, "Prelaunch", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ floor items

        private static void OnFloor(GroupState st, int k)
        {
            Ctx ctx = st.Ctx;
            Item it = ctx.Items[k];
            Item prev = k > 0 ? ctx.Items[k - 1] : null;
            bool fromGround = prev == null || (prev.Type == ItemType.Floor && prev.Regime == ProfileRegime.Surface);
            switch (it.Regime)
            {
                case ProfileRegime.Surface:
                    OnSurface(st, k, prev);
                    break;
                case ProfileRegime.Air:
                    if (fromGround) OnTakeoff(st, k);
                    else if (IsSpaceItem(prev)) OnPassEntry(st, k);
                    break;
                default:
                    if (fromGround) OnTakeoff(st, k);
                    else if (IsSpaceItem(prev) && LeadsToSurface(ctx, k)
                        && st.Current != ProfilePhaseKind.Descent && st.Current != ProfilePhaseKind.SuborbitalHop)
                        Begin(st, it.Start, ProfilePhaseKind.Descent, it.Body, "low-band-arrival");
                    break;
            }
        }

        private static void OnSurface(GroupState st, int k, Item prev)
        {
            Ctx ctx = st.Ctx;
            Item it = ctx.Items[k];
            if (prev == null)
            {
                if (IsLaunchStart(ctx)) Begin(st, it.Start, ProfilePhaseKind.Prelaunch, it.Body, "launch-site");
                else Begin(st, it.Start, ProfilePhaseKind.SurfaceStay, it.Body, "surface-start");
                return;
            }
            bool lastItem = k == ctx.Items.Count - 1;
            if (lastItem && it.End - it.Start < SurfaceFoldSeconds) return;
            Begin(st, it.Start, ProfilePhaseKind.SurfaceStay, it.Body, "touchdown");
        }

        // A takeoff is a launch, a suborbital flight or an atmospheric flight depending on what
        // the rest of the line does (hindsight): space before the ground again, and an orbit or
        // an injection after that.
        private static void OnTakeoff(GroupState st, int k)
        {
            Ctx ctx = st.Ctx;
            Item it = ctx.Items[k];
            if (!ReachesSpaceBeforeGround(ctx, k))
            {
                // A launch that never reaches space and never comes down to the ground again
                // (destroyed in the air) is an aborted ascent; anything else is a flight.
                bool aborted = IsLaunchStart(ctx) && !LandsAgainAfter(ctx, k)
                    && ctx.Plan.Terminal == TerminalState.Destroyed;
                Begin(st, it.Start, aborted ? ProfilePhaseKind.Ascent : ProfilePhaseKind.Flight, it.Body,
                    aborted ? RuleAbortedAscent : "flight");
                return;
            }
            if (ReachesOrbitOrLeaves(ctx, k)) Begin(st, it.Start, ProfilePhaseKind.Ascent, it.Body, "ascent");
            else Begin(st, it.Start, ProfilePhaseKind.SuborbitalHop, it.Body, "suborbital");
        }

        private static bool LandsAgainAfter(Ctx ctx, int k)
        {
            for (int i = k + 1; i < ctx.Items.Count; i++)
                if (ctx.Items[i].Regime == ProfileRegime.Surface) return true;
            return false;
        }

        private static bool ReachesSpaceBeforeGround(Ctx ctx, int k)
        {
            for (int i = k; i < ctx.Items.Count; i++)
            {
                Item it = ctx.Items[i];
                if (IsSpaceItem(it)) return true;
                if (it.Regime == ProfileRegime.Surface) return false;
            }
            return false;
        }

        private static bool ReachesOrbitOrLeaves(Ctx ctx, int k)
        {
            for (int i = k; i < ctx.Items.Count; i++)
            {
                Item it = ctx.Items[i];
                if (it.Type == ItemType.Floor && (it.Regime == ProfileRegime.Surface || it.PassStart)) return false;
                if (it.Type == ItemType.Floor && it.Regime == ProfileRegime.Low && i > k + 1) return false;
                if (it.Type == ItemType.Burn && IsOrbitOrLeaving(it.After)) return true;
                if (it.Type == ItemType.Coast && (IsOrbitOrLeaving(it.StartOrbit) || IsOrbitOrLeaving(it.EndOrbit)))
                    return true;
            }
            return false;
        }

        private static bool IsOrbitOrLeaving(ProfileOrbit o)
            => o.Kind == ProfileOrbitKind.InOrbit || o.Kind == ProfileOrbitKind.Leaving;

        private static bool LeadsToSurface(Ctx ctx, int k)
        {
            for (int i = k + 1; i < ctx.Items.Count; i++)
            {
                Item it = ctx.Items[i];
                if (it.Type != ItemType.Floor) return false;
                if (it.Regime == ProfileRegime.Surface) return true;
            }
            return false;
        }

        private static void OnPassEntry(GroupState st, int k)
        {
            Item it = st.Ctx.Items[k];
            PassGroup g = it.Pass;
            if (g == null || !it.PassStart || g.Rule == null) return;
            int passIndex = g.PassFirst.IndexOf(k);
            if (g.Rule.BrakeThenLand)
            {
                if (passIndex == g.Passes - 1)
                    Begin(st, it.Start, ProfilePhaseKind.Reentry, it.Body, g.Rule.Name);
                else if (passIndex == 0)
                    Begin(st, it.Start, g.EnergyBefore == ProfileOrbitEnergy.Escaping
                        ? ProfilePhaseKind.Aerocapture : ProfilePhaseKind.Aerobraking, it.Body, g.Rule.Name);
                return;
            }
            if (passIndex != 0) return;
            if (g.Rule.Reads == ProfilePhaseKind.AtmosphericFlyby)
            {
                Item lastAir = st.Ctx.Items[g.PassLast[g.PassLast.Count - 1]];
                st.AtmosphericFlybys.Add(new ProfileSpan(it.Start, lastAir.End));
                return;
            }
            Begin(st, it.Start, g.Rule.Reads, it.Body, g.Rule.Name);
        }

        // ------------------------------------------------------------------ space items

        private static void OnCoast(GroupState st, int k)
        {
            Ctx ctx = st.Ctx;
            Item it = ctx.Items[k];
            Item prev = k > 0 ? ctx.Items[k - 1] : null;
            if (prev == null)
            {
                BeginFromOrbit(st, it.Start, it, "space-start");
                return;
            }
            if (prev.Type == ItemType.Floor)
            {
                switch (st.Current)
                {
                    case ProfilePhaseKind.Ascent: AfterAscent(st, k); break;
                    case ProfilePhaseKind.AtmospherePass: BeginFromOrbit(st, it.Start, it, "after-pass"); break;
                    case ProfilePhaseKind.Prelaunch:
                    case ProfilePhaseKind.SurfaceStay: OnTakeoff(st, k); break;
                }
                return;
            }
            if (!SameBodyStrict(prev.Body, it.Body)) OnSoiChange(st, k);
        }

        private static void AfterAscent(GroupState st, int k)
        {
            Ctx ctx = st.Ctx;
            Item it = ctx.Items[k];
            ProfileBurnKind next = NextBurnKind(ctx, k);
            if (next == ProfileBurnKind.Circularization)
                Begin(st, it.Start, ProfilePhaseKind.Circularization, it.Body, "ascent-exit-falling");
            else if (next == ProfileBurnKind.Injection)
                Begin(st, it.Start, ProfilePhaseKind.Injection, it.Body, "ascent-exit-injection");
            else
                BeginFromOrbit(st, it.Start, it, "ascent-exit");
        }

        private static ProfileBurnKind NextBurnKind(Ctx ctx, int k)
        {
            for (int i = k + 1; i < ctx.Items.Count; i++)
            {
                if (ctx.Items[i].Type == ItemType.Floor) return ProfileBurnKind.Unknown;
                if (ctx.Items[i].Type == ItemType.Burn) return ctx.Items[i].BurnKind;
            }
            return ProfileBurnKind.Unknown;
        }

        private static void BeginFromOrbit(GroupState st, double ut, Item coast, string rule)
        {
            ProfileOrbit o = coast.StartOrbit;
            // An open orbit coming in, and a closed one that only dips into the atmosphere,
            // read by their energy: arriving, and in orbit.
            if (o.Kind == ProfileOrbitKind.Falling && o.Periapsis == ProfilePeriapsis.InAir)
            {
                Begin(st, ut, o.Energy == ProfileOrbitEnergy.Escaping ? ProfilePhaseKind.Transfer : ProfilePhaseKind.Orbit,
                    coast.Body, rule);
                return;
            }
            switch (o.Kind)
            {
                case ProfileOrbitKind.InOrbit:
                    Begin(st, ut, ProfilePhaseKind.Orbit, coast.Body, rule);
                    break;
                case ProfileOrbitKind.Leaving:
                    Begin(st, ut, ProfilePhaseKind.Transfer, coast.Body, rule);
                    break;
                case ProfileOrbitKind.Falling:
                    Begin(st, ut, ProfilePhaseKind.Unclassified, coast.Body, rule, "falling with no ascent or deorbit recorded");
                    break;
                default:
                    Begin(st, ut, ProfilePhaseKind.Unclassified, coast.Body, rule, "orbit unknown");
                    break;
            }
        }

        // An SOI change is never a phase boundary inside a transfer (it is an event); it opens a
        // transfer when the line was in an orbit, and it ends a transfer when the vessel arrives
        // in a bound clear orbit with nothing later to change it.
        private static void OnSoiChange(GroupState st, int k)
        {
            Ctx ctx = st.Ctx;
            Item it = ctx.Items[k];
            if (st.Current == ProfilePhaseKind.Transfer)
            {
                if (it.StartOrbit.Kind == ProfileOrbitKind.InOrbit && NothingLaterInSpace(ctx, k))
                    Begin(st, it.Start, ProfilePhaseKind.Orbit, it.Body, "soi-into-orbit");
                return;
            }
            if (st.Current == ProfilePhaseKind.Orbit || st.Current == ProfilePhaseKind.Unclassified
                || st.Current == ProfilePhaseKind.Circularization || st.Current == ProfilePhaseKind.Rendezvous)
                Begin(st, it.Start, ProfilePhaseKind.Transfer, ctx.Items[k - 1].Body, "soi-exit");
        }

        private static bool NothingLaterInSpace(Ctx ctx, int k)
        {
            for (int i = k + 1; i < ctx.Items.Count; i++)
                if (ctx.Items[i].Type != ItemType.Coast || !SameBodyStrict(ctx.Items[i].Body, ctx.Items[k].Body))
                    return false;
            return true;
        }

        private static void OnBurn(GroupState st, int k)
        {
            Item b = st.Ctx.Items[k];
            switch (b.BurnKind)
            {
                case ProfileBurnKind.Circularization:
                case ProfileBurnKind.Capture:
                case ProfileBurnKind.CaptureAerobraking:
                case ProfileBurnKind.PeriapsisRaise:
                    Begin(st, b.BurnEnd, ProfilePhaseKind.Orbit, b.Body, b.BurnRule);
                    break;
                case ProfileBurnKind.Injection:
                    Begin(st, b.BurnEnd, ProfilePhaseKind.Transfer, b.Body, b.BurnRule);
                    break;
                case ProfileBurnKind.Transfer:
                case ProfileBurnKind.Return:
                case ProfileBurnKind.Escape:
                    Begin(st, b.BurnStart, ProfilePhaseKind.Transfer, b.Body, b.BurnRule);
                    break;
                case ProfileBurnKind.Deorbit:
                    Begin(st, b.BurnStart, ProfilePhaseKind.Deorbit, b.Body, b.BurnRule);
                    break;
                case ProfileBurnKind.Descent:
                    Begin(st, b.BurnStart, ProfilePhaseKind.Descent, b.Body, b.BurnRule);
                    break;
                case ProfileBurnKind.Phasing:
                case ProfileBurnKind.Approach:
                    if (st.Current != ProfilePhaseKind.Rendezvous)
                        Begin(st, b.BurnStart, ProfilePhaseKind.Rendezvous, b.Body, b.BurnRule,
                            null, NextAnchor(st.Ctx, ProfileEventKind.Docking, b.BurnEnd));
                    break;
                default:
                    if (st.Current == ProfilePhaseKind.Unclassified && b.After.Kind == ProfileOrbitKind.InOrbit)
                        Begin(st, b.BurnEnd, ProfilePhaseKind.Orbit, b.Body, "orbit-reached");
                    break;
            }
        }

        private static ProfileAnchorInput NextAnchor(Ctx ctx, ProfileEventKind kind, double afterUT)
        {
            ProfileAnchorInput best = null;
            foreach (ProfileAnchorInput a in ctx.Line.Anchors)
                if (a.Kind == kind && a.UT >= afterUT && (best == null || a.UT < best.UT)) best = a;
            return best;
        }

        // ------------------------------------------------------------------ docks

        // A dock cuts the line into a Docked phase up to the undock (or the line's end); the
        // phase that was flying resumes after the undock (a rendezvous resumes as the orbit).
        private static void ApplyDockMarks(GroupState st)
        {
            var docks = new List<ProfileAnchorInput>();
            foreach (ProfileAnchorInput a in st.Ctx.Line.Anchors)
                if (a.Kind == ProfileEventKind.Docking) docks.Add(a);
            docks.Sort((a, b) => a.UT.CompareTo(b.UT));
            foreach (ProfileAnchorInput dock in docks)
            {
                ProfileAnchorInput undock = NextAnchor(st.Ctx, ProfileEventKind.Undocking, dock.UT);
                double end = undock != null ? undock.UT : double.PositiveInfinity;
                Mark resume = null;
                for (int i = st.Marks.Count - 1; i >= 0; i--)
                {
                    Mark m = st.Marks[i];
                    if (m.UT <= end && resume == null) resume = m;
                    if (m.UT > dock.UT && m.UT <= end) st.Marks.RemoveAt(i);
                }
                InsertMark(st.Marks, new Mark
                {
                    UT = dock.UT, Kind = ProfilePhaseKind.Docked, Body = resume?.Body, Rule = "dock", Anchor = dock,
                });
                if (undock == null || resume == null) continue;
                ProfilePhaseKind kind = resume.Kind == ProfilePhaseKind.Rendezvous || resume.Kind == ProfilePhaseKind.Docked
                    ? ProfilePhaseKind.Orbit : resume.Kind;
                InsertMark(st.Marks, new Mark { UT = undock.UT, Kind = kind, Body = resume.Body, Rule = "undock" });
            }
        }

        private static void InsertMark(List<Mark> marks, Mark m)
        {
            int i = marks.Count;
            while (i > 0 && marks[i - 1].UT > m.UT) i--;
            if (i > 0 && Math.Abs(marks[i - 1].UT - m.UT) < 1e-6) marks[i - 1] = m;
            else marks.Insert(i, m);
        }

        // ------------------------------------------------------------------ phases

        private static void BuildPhasesFromMarks(Ctx ctx, List<Mark> marks)
        {
            double lineEnd = ctx.Plan.EndUT;
            for (int i = 0; i < marks.Count; i++)
            {
                Mark m = marks[i];
                double end = i + 1 < marks.Count ? marks[i + 1].UT : lineEnd;
                if (end - m.UT <= 0 && i + 1 < marks.Count) continue;
                var p = new ProfilePhase
                {
                    Kind = m.Kind,
                    StartUT = m.UT,
                    EndUT = Math.Max(end, m.UT),
                    Body = m.Body,
                    Rule = m.Rule,
                    UnclassifiedReason = m.Reason,
                    Anchor = m.Anchor,
                };
                if (p.Kind == ProfilePhaseKind.Transfer) p.TargetBody = ArrivalBody(ctx, p);
                ReadPhaseNumbers(ctx, p);
                ctx.Plan.Phases.Add(p);
            }
        }

        private static string ArrivalBody(Ctx ctx, ProfilePhase p)
        {
            string body = null;
            foreach (Sec s in ctx.Secs)
            {
                if (s.Start >= p.EndUT - 1e-6) break;
                if (!string.IsNullOrEmpty(s.Body)) body = s.Body;
            }
            return string.Equals(body, p.Body, StringComparison.Ordinal) ? null : body;
        }

        // The orbit a phase starts on and the one it ends on, the highest altitude it reached,
        // the altitude it left the atmosphere at, and its biome.
        private static void ReadPhaseNumbers(Ctx ctx, ProfilePhase p)
        {
            bool first = true;
            foreach (Item c in ctx.Items)
            {
                if (c.Type != ItemType.Coast || c.End < p.StartUT || c.Start > p.EndUT + 2.0) continue;
                if (first && c.Start >= p.StartUT - 2.0) { p.FirstOrbit = c.StartOrbit; first = false; }
                if (c.Start >= p.EndUT - 2.0) { p.LastOrbit = c.StartOrbit; break; }
                p.LastOrbit = c.EndOrbit;
                if (first) { p.FirstOrbit = c.EndOrbit; first = false; }
            }
            double max = double.NaN;
            foreach (Sec s in ctx.Secs)
            {
                if (s.End < p.StartUT || s.Start > p.EndUT || s.OnRails || s.Relative) continue;
                if (!float.IsNaN(s.S.maxAltitude) && (double.IsNaN(max) || s.S.maxAltitude > max)) max = s.S.maxAltitude;
                if (s.Regime == ProfileRegime.Air && s.End <= p.EndUT + 1e-6) p.ExitAltitude = LastAltitude(s);
            }
            p.MaxAltitude = max;
        }

        private static double LastAltitude(Sec s)
        {
            if (s.S.frames != null && s.S.frames.Count > 0) return s.S.frames[s.S.frames.Count - 1].altitude;
            return float.IsNaN(s.S.maxAltitude) ? double.NaN : s.S.maxAltitude;
        }

        // ------------------------------------------------------------------ sub-phases

        private static void BuildSubPhases(Ctx ctx, GroupState st)
        {
            foreach (ProfilePhase p in ctx.Plan.Phases)
            {
                if (p.Kind == ProfilePhaseKind.Transfer) AddFlybys(ctx, p);
                if (p.Kind == ProfilePhaseKind.SurfaceStay) AddDrives(ctx, p);
            }
            foreach (ProfileSpan span in st.AtmosphericFlybys)
            {
                ProfilePhase parent = PhaseAt(ctx.Plan.Phases, span.StartUT, false);
                if (parent == null) continue;
                var sub = new ProfilePhase
                {
                    Kind = ProfilePhaseKind.AtmosphericFlyby, StartUT = span.StartUT, EndUT = span.EndUT,
                    Body = BodyAt(ctx, span.StartUT), Rule = RuleBrakingFlyby,
                };
                ReadPhaseNumbers(ctx, sub);
                parent.SubPhases.Add(sub);
                parent.SubPhases.Sort((a, b) => a.StartUT.CompareTo(b.StartUT));
            }
        }

        // A flyby is an SOI entry with no capture before the SOI exit, inside a transfer.
        private static void AddFlybys(Ctx ctx, ProfilePhase p)
        {
            for (int i = 1; i < ctx.Items.Count; i++)
            {
                Item enter = ctx.Items[i];
                if (enter.Start <= p.StartUT || enter.Start >= p.EndUT || !IsSpaceItem(enter)) continue;
                Item before = ctx.Items[i - 1];
                if (!IsSpaceItem(before) || SameBodyStrict(before.Body, enter.Body)) continue;
                if (!string.Equals(ctx.Facts(enter.Body).ParentName, before.Body, StringComparison.Ordinal)) continue;
                for (int j = i + 1; j < ctx.Items.Count; j++)
                {
                    Item leave = ctx.Items[j];
                    if (leave.Start > p.EndUT + 1e-6 || !IsSpaceItem(leave)) break;
                    if (SameBodyStrict(leave.Body, enter.Body)) continue;
                    var sub = new ProfilePhase
                    {
                        Kind = ProfilePhaseKind.Flyby, StartUT = enter.Start, EndUT = leave.Start,
                        Body = enter.Body, Rule = "flyby",
                    };
                    ReadPhaseNumbers(ctx, sub);
                    p.SubPhases.Add(sub);
                    break;
                }
            }
        }

        // A drive is surface motion inside a surface stay (a takeoff or landing roll at the
        // stay's edge is not one); stop-go runs closer than DriveMergeSeconds merge.
        private static void AddDrives(Ctx ctx, ProfilePhase p)
        {
            Sec runFirst = null, runLast = null;
            foreach (Sec s in ctx.Secs)
            {
                if (s.Start < p.StartUT - 1e-6 || s.End > p.EndUT + 1e-6) continue;
                if (s.S.environment != SegmentEnvironment.SurfaceMobile || s.OnRails || s.Relative) continue;
                if (runLast != null && s.Start - runLast.End <= DriveMergeSeconds)
                {
                    runLast = s;
                    continue;
                }
                AddDrive(ctx, p, runFirst, runLast);
                runFirst = runLast = s;
            }
            AddDrive(ctx, p, runFirst, runLast);
        }

        private static void AddDrive(Ctx ctx, ProfilePhase p, Sec first, Sec last)
        {
            if (first == null || last == null) return;
            if (last.End - first.Start <= DriveMinSeconds) return;
            bool atStart = first.Start <= p.StartUT + 1.0 && p.StartUT > ctx.Plan.StartUT + 1.0;
            bool atEnd = last.End >= p.EndUT - 1.0 && p.EndUT < ctx.Plan.EndUT - 1.0;
            if (atStart || atEnd) return;
            double meters = SurfaceDistance(ctx, first, last);
            if (double.IsNaN(meters) || meters <= DriveMinMeters) return;
            p.SubPhases.Add(new ProfilePhase
            {
                Kind = ProfilePhaseKind.Drive, StartUT = first.Start, EndUT = last.End, Body = first.Body,
                Rule = "drive", DriveDistance = meters,
            });
        }

        private static double SurfaceDistance(Ctx ctx, Sec first, Sec last)
        {
            if (first.S.frames == null || first.S.frames.Count == 0 || last.S.frames == null || last.S.frames.Count == 0)
                return double.NaN;
            TrajectoryPoint a = first.S.frames[0], b = last.S.frames[last.S.frames.Count - 1];
            var f = ctx.Facts(first.Body);
            if (!f.Known) return double.NaN;
            double rad = Math.PI / 180.0;
            double dLat = (b.latitude - a.latitude) * rad, dLon = (b.longitude - a.longitude) * rad;
            double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(a.latitude * rad) * Math.Cos(b.latitude * rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * f.Radius * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
        }

        private static string BodyAt(Ctx ctx, double ut)
        {
            string body = null;
            foreach (Sec s in ctx.Secs)
            {
                if (s.Start > ut) break;
                body = s.Body ?? body;
            }
            return body;
        }

        // ------------------------------------------------------------------ EVA lines

        // EVA lines skip strata (c) and (d): a kerbal is not a vessel the orbit rules describe.
        private static void GroupEva(Ctx ctx)
        {
            var st = new GroupState { Ctx = ctx };
            for (int k = 0; k < ctx.Items.Count; k++)
            {
                Item it = ctx.Items[k];
                if (it.Type == ItemType.Floor && it.Regime == ProfileRegime.Surface)
                {
                    Begin(st, it.Start, ProfilePhaseKind.EvaSurface, it.Body, "eva-surface");
                    continue;
                }
                bool fromSurface = st.Current == ProfilePhaseKind.EvaSurface && st.HasCurrent;
                if (fromSurface && LandsAgain(ctx, k))
                    Begin(st, it.Start, ProfilePhaseKind.JetpackFlight, it.Body, "jetpack-hop");
                else if (st.Current != ProfilePhaseKind.JetpackFlight || !st.HasCurrent)
                    Begin(st, it.Start, ProfilePhaseKind.Eva, it.Body, "eva");
            }
            BuildPhasesFromMarks(ctx, st.Marks);
        }

        private static bool LandsAgain(Ctx ctx, int k)
        {
            for (int i = k + 1; i < ctx.Items.Count; i++)
                if (ctx.Items[i].Type == ItemType.Floor && ctx.Items[i].Regime == ProfileRegime.Surface) return true;
            return false;
        }
    }
}
