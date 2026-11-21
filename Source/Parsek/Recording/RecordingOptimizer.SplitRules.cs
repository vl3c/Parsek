using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The optimizer's split-rule versions (owner ruling Q5, "freeze old recordings") and the
    /// version-2 boundary rules: atmosphere passes (rulings Q7, A1, B1), one row across an SOI
    /// change in space (Q9) and a brief touchdown as a graze (Q8). Version 1 is
    /// <see cref="IsSplittableEnvOrBodyBoundary"/>'s original body, unchanged; a recording is
    /// judged by the version it was committed under, so a rule change never re-cuts a flight
    /// committed before it. Design: <c>docs/dev/done/plans/optimizer-persistence-split.md</c>
    /// section 14.
    /// </summary>
    internal static partial class RecordingOptimizer
    {
        /// <summary>Not yet committed: stamped at commit (<see cref="StampSplitRulesAtCommit"/>).
        /// The rules treat it as version 1.</summary>
        internal const int SplitRuleVersionUnset = 0;

        /// <summary>The rules before 2026-11-21. A recording saved without the key loads as this.</summary>
        internal const int SplitRuleVersionLegacy = 1;

        /// <summary>The rules every new commit is stamped with.</summary>
        internal const int SplitRuleVersionCurrent = 2;

        /// <summary>
        /// Ruling Q8: a Surface run shorter than this, bracketed by Atmospheric / Approach on
        /// both sides (a touch-and-go, a bounce), is a graze under version 2 and adds no row.
        /// </summary>
        internal const double BriefTouchdownMaxSeconds = 30.0;

        /// <summary>
        /// The row roles the split pass stamps on version-2 rows (<see cref="Recording.SegmentRole"/>).
        /// A role names what the row is in the mission profile; two rows with different roles
        /// never merge back (<see cref="CanAutoMerge"/>). Extensible: the orbit-kind cuts of a
        /// later rule version add roles (circularization, transfer, capture, deorbit).
        /// </summary>
        internal static class SegmentRoles
        {
            /// <summary>Leaves the ground: a launch (pad time included) or a takeoff.</summary>
            internal const string Ascent = "ascent";
            /// <summary>In space above the atmosphere / approach band.</summary>
            internal const string Space = "space";
            /// <summary>From the first touch of a braking atmosphere pass (aerobraking, aerocapture).</summary>
            internal const string Aerobraking = "aerobraking";
            /// <summary>From the first atmosphere entry of the final descent.</summary>
            internal const string Reentry = "reentry";
            /// <summary>Low over an airless body, entered from space.</summary>
            internal const string Approach = "approach";
            /// <summary>On the ground or the water.</summary>
            internal const string Surface = "surface";
            /// <summary>In an atmosphere, not entered from space within the row (a start mid-air).</summary>
            internal const string Atmosphere = "atmosphere";
        }

        /// <summary>
        /// True when <paramref name="rec"/> is judged by the version-2 rules: stamped 2 or
        /// later, and not an EVA kerbal (EVA rows keep the version-1 rules and get no role, so
        /// the continuous EVA atmo / surface merge is untouched).
        /// </summary>
        internal static bool UsesCurrentSplitRules(Recording rec)
        {
            return rec != null
                && rec.SplitRuleVersion >= SplitRuleVersionCurrent
                && !IsEvaRecording(rec);
        }

        /// <summary>
        /// The forward gate (save-format policy: data newer than the build is held, never
        /// coerced): true when <paramref name="rec"/> carries a split rule version above
        /// <see cref="SplitRuleVersionCurrent"/>. A newer build cut it under rules this build
        /// does not know, so the optimizer holds it verbatim: never split, merged, trimmed or
        /// folded here, and the codec writes its version back.
        /// </summary>
        internal static bool IsSplitRuleVersionNewerThanBuild(Recording rec)
            => rec != null && rec.SplitRuleVersion > SplitRuleVersionCurrent;

        /// <summary>
        /// The merge-side half of the version gate (<see cref="CanAutoMerge"/>): true, logged,
        /// when <paramref name="a"/> and <paramref name="b"/> were cut under different rule
        /// versions or carry different row roles, or either was cut under a version newer
        /// than this build (<see cref="IsSplitRuleVersionNewerThanBuild"/>).
        /// </summary>
        internal static bool SplitRulesKeepApart(Recording a, Recording b)
        {
            int va = EffectiveSplitRuleVersion(a);
            int vb = EffectiveSplitRuleVersion(b);
            if (IsSplitRuleVersionNewerThanBuild(a) || IsSplitRuleVersionNewerThanBuild(b))
            {
                ParsekLog.Verbose("Optimizer",
                    $"CanAutoMerge: rejecting merge of '{a.RecordingId}' + '{b.RecordingId}' " +
                    $"- split rules v{va.ToString(CultureInfo.InvariantCulture)}/v{vb.ToString(CultureInfo.InvariantCulture)} " +
                    $"newer than this build (v{SplitRuleVersionCurrent.ToString(CultureInfo.InvariantCulture)}), held verbatim");
                return true;
            }
            if (va == vb && string.Equals(a.SegmentRole, b.SegmentRole, StringComparison.Ordinal))
            {
                if (!UsesCurrentSplitRules(a) || !UsesCurrentSplitRules(b) || !WouldSplitRulesCutJoin(a, b))
                    return false;
                ParsekLog.Verbose("Optimizer",
                    $"CanAutoMerge: rejecting merge of '{a.RecordingId}' + '{b.RecordingId}' " +
                    "- the split rules would cut the join again");
                return true;
            }
            ParsekLog.Verbose("Optimizer",
                $"CanAutoMerge: rejecting merge of '{a.RecordingId}' + '{b.RecordingId}' " +
                $"- split rules v{va.ToString(CultureInfo.InvariantCulture)}/v{vb.ToString(CultureInfo.InvariantCulture)} " +
                $"roles '{a.SegmentRole ?? "<none>"}'/'{b.SegmentRole ?? "<none>"}'");
            return true;
        }

        /// <summary>The rules a recording is judged by: an unset version counts as version 1.</summary>
        internal static int EffectiveSplitRuleVersion(Recording rec)
        {
            if (rec == null || rec.SplitRuleVersion <= SplitRuleVersionUnset) return SplitRuleVersionLegacy;
            return rec.SplitRuleVersion;
        }

        /// <summary>
        /// Commit-time stamp: a recording entering the committed list with no rule version
        /// (<see cref="SplitRuleVersionUnset"/>) gets <see cref="SplitRuleVersionCurrent"/>. A
        /// recording that already carries a version (a resumed committed flight, a clone)
        /// keeps it. Returns true when it stamped.
        /// </summary>
        internal static bool StampSplitRulesAtCommit(Recording rec, string context)
        {
            if (rec == null || rec.SplitRuleVersion != SplitRuleVersionUnset) return false;
            rec.SplitRuleVersion = SplitRuleVersionCurrent;
            ParsekLog.Verbose("Optimizer",
                $"Split rules: stamped version {SplitRuleVersionCurrent.ToString(CultureInfo.InvariantCulture)} " +
                $"on rec={rec.RecordingId ?? "<no-id>"} at {context ?? "commit"}");
            return true;
        }

        /// <summary>
        /// Save-time stamp for an in-flight (active or pending) tree: every recording of it
        /// with no rule version gets the current one, as its commit would. The codec writes no
        /// key for an unset version (a recording without the key loads as version 1), so
        /// without this a flight saved and loaded before its commit would be judged by the old
        /// rules. A recording that already carries a version (a resumed committed flight) keeps
        /// it. One Verbose line when anything was stamped.
        /// </summary>
        internal static int StampSplitRulesForSave(RecordingTree tree, string context)
        {
            if (tree?.Recordings == null) return 0;
            int stamped = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null || rec.SplitRuleVersion != SplitRuleVersionUnset) continue;
                rec.SplitRuleVersion = SplitRuleVersionCurrent;
                stamped++;
            }
            if (stamped > 0)
                ParsekLog.Verbose("Optimizer",
                    $"Split rules: stamped version {SplitRuleVersionCurrent.ToString(CultureInfo.InvariantCulture)} " +
                    $"on {stamped.ToString(CultureInfo.InvariantCulture)} recording(s) of tree " +
                    $"{tree.Id ?? "<no-id>"} at {context ?? "save"}");
            return stamped;
        }

        /// <summary>
        /// Test hook (fixtures, the Phase 0 measurement): forces <paramref name="version"/> on
        /// every recording given, so a committed fixture recorded before version 2 can be
        /// judged by the version-2 rules.
        /// </summary>
        internal static void ForceSplitRuleVersionForTesting(IEnumerable<Recording> recordings, int version)
        {
            if (recordings == null) return;
            foreach (var rec in recordings)
                if (rec != null) rec.SplitRuleVersion = version;
        }

        /// <summary>The role of a row that starts the way <paramref name="rec"/> starts, or null
        /// for an EVA kerbal: what a first half that has no role yet is tagged with.</summary>
        internal static string ResolveStartRole(Recording rec)
        {
            if (rec?.TrackSections == null || rec.TrackSections.Count == 0 || IsEvaRecording(rec))
                return null;
            int firstClass = SplitEnvironmentClass(rec.TrackSections[0].environment);
            switch (firstClass)
            {
                case SurfaceSplitClass:
                    return FindLaunchSiteDepartureSection(rec) > 0 ? SegmentRoles.Ascent : SegmentRoles.Surface;
                case AtmosphericSplitClass:
                    return SegmentRoles.Atmosphere;
                case ApproachSplitClass:
                    return SegmentRoles.Approach;
                default:
                    return SegmentRoles.Space;
            }
        }

        /// <summary>
        /// The role of the row a cut for <paramref name="reason"/> starts; <paramref name="tail"/>
        /// is that row (the split's second half). A pass entry names the pass; a cut onto the
        /// ground starts a surface row and one off it an ascent; any other cut is named by the
        /// place the row starts in.
        /// </summary>
        internal static string ResolveCutRole(SplitBoundaryReason reason, Recording tail)
        {
            if (tail?.TrackSections == null || tail.TrackSections.Count == 0 || IsEvaRecording(tail))
                return null;
            if (reason == SplitBoundaryReason.FinalDescentEntry) return SegmentRoles.Reentry;
            if (reason == SplitBoundaryReason.BrakingPassEntry) return SegmentRoles.Aerobraking;
            int firstClass = SplitEnvironmentClass(tail.TrackSections[0].environment);
            if (reason == SplitBoundaryReason.SurfaceInvolved)
                return firstClass == SurfaceSplitClass ? SegmentRoles.Surface : SegmentRoles.Ascent;
            switch (firstClass)
            {
                case SurfaceSplitClass: return SegmentRoles.Surface;
                case AtmosphericSplitClass: return SegmentRoles.Atmosphere;
                case ApproachSplitClass: return SegmentRoles.Approach;
                default: return SegmentRoles.Space;
            }
        }

        private static bool IsPassRole(string role)
        {
            return string.Equals(role, SegmentRoles.Reentry, StringComparison.Ordinal)
                || string.Equals(role, SegmentRoles.Aerobraking, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------ the version-2 predicate

        internal struct ClassRun
        {
            internal int Class;
            internal int First;
            internal int Last;
            internal double Duration;
        }

        internal enum PassKind { Final, Braking }

        internal sealed class AtmosphereStretch
        {
            internal int FirstRun;
            internal int LastRun;
            internal bool FromSpace;
            internal bool OwnPass;
            internal PassKind Kind;
        }

        /// <summary>
        /// The version-2 view of a recording's sections: the effective split class of each
        /// section (a brief touchdown, ruling Q8, reads as the air around it), the runs of equal
        /// effective class, and the atmosphere stretches (an Atmospheric run with every brief
        /// Exo hop back into the atmosphere, under <see cref="BriefSectionMaxSeconds"/>, folded
        /// in), each marked entered-from-space or not and classified by how it ends.
        /// </summary>
        internal sealed class PassMap
        {
            internal int[] RawClass;
            internal int[] EffClass;
            internal bool[] BriefTouchdown;
            internal int[] RunOfSection;
            internal List<ClassRun> Runs;
            internal List<AtmosphereStretch> Stretches;
            internal int[] StretchOfRun;
        }

        internal static PassMap BuildPassMap(Recording rec)
        {
            var sections = rec.TrackSections;
            int n = sections.Count;
            var map = new PassMap
            {
                RawClass = new int[n],
                EffClass = new int[n],
                BriefTouchdown = new bool[n],
                RunOfSection = new int[n],
            };
            for (int i = 0; i < n; i++)
            {
                map.RawClass[i] = SplitEnvironmentClass(sections[i].environment);
                map.EffClass[i] = map.RawClass[i];
            }

            // Q8: a brief Surface run bracketed by Atmospheric / Approach reads as the air.
            // Not inside a launch-site start's leading run: rule 5b already keeps a bounce off
            // the pad with the ascent (FindLaunchSiteDepartureSection reads the raw classes, and
            // the two must agree on where the leading run ends). A launch-site start that never
            // departs keeps every touchdown, as version 1 does.
            int launchDeparture = FindLaunchSiteDepartureSection(rec);
            bool launchNeverDeparts = launchDeparture < 0 && IsLaunchSiteStart(rec)
                && n > 0 && map.RawClass[0] == SurfaceSplitClass;
            var rawRuns = BuildRuns(sections, map.RawClass);
            for (int r = 1; r + 1 < rawRuns.Count; r++)
            {
                var run = rawRuns[r];
                if (run.Class != SurfaceSplitClass || run.Duration >= BriefTouchdownMaxSeconds) continue;
                if (launchNeverDeparts || run.First < launchDeparture) continue;
                int before = rawRuns[r - 1].Class;
                int after = rawRuns[r + 1].Class;
                if (!IsSurfaceGrazeBriefClass(before) || !IsSurfaceGrazeBriefClass(after)) continue;
                for (int i = run.First; i <= run.Last; i++)
                {
                    map.EffClass[i] = before;
                    map.BriefTouchdown[i] = true;
                }
            }

            map.Runs = BuildRuns(sections, map.EffClass);
            for (int r = 0; r < map.Runs.Count; r++)
                for (int i = map.Runs[r].First; i <= map.Runs[r].Last; i++)
                    map.RunOfSection[i] = r;

            map.Stretches = new List<AtmosphereStretch>();
            map.StretchOfRun = new int[map.Runs.Count];
            for (int r = 0; r < map.Runs.Count; r++) map.StretchOfRun[r] = -1;
            bool rowHasPassRole = IsPassRole(rec.SegmentRole);
            for (int r = 0; r < map.Runs.Count; r++)
            {
                if (map.Runs[r].Class != AtmosphericSplitClass || map.StretchOfRun[r] >= 0) continue;
                int k = r;
                while (k + 2 < map.Runs.Count
                    && map.Runs[k + 1].Class == ExoSplitClass
                    && map.Runs[k + 1].Duration < BriefSectionMaxSeconds
                    && map.Runs[k + 2].Class == AtmosphericSplitClass)
                {
                    k += 2;
                }
                var st = new AtmosphereStretch { FirstRun = r, LastRun = k };
                if (r == 0)
                    st.FromSpace = rowHasPassRole;
                else
                    st.FromSpace = map.Runs[r - 1].Class == ExoSplitClass;
                if (k + 1 >= map.Runs.Count || map.Runs[k + 1].Class == SurfaceSplitClass)
                    st.Kind = PassKind.Final;
                else
                    st.Kind = PassKind.Braking;
                if (rowHasPassRole && map.Stretches.Count == 0 && st.FromSpace)
                {
                    bool onlySpaceBefore = true;
                    for (int b = 0; b < r; b++)
                        if (map.Runs[b].Class != ExoSplitClass) { onlySpaceBefore = false; break; }
                    st.OwnPass = onlySpaceBefore;
                }
                for (int x = r; x <= k; x++) map.StretchOfRun[x] = map.Stretches.Count;
                map.Stretches.Add(st);
                r = k;
            }
            return map;
        }

        private static List<ClassRun> BuildRuns(List<TrackSection> sections, int[] classes)
        {
            var runs = new List<ClassRun>();
            int i = 0;
            while (i < sections.Count)
            {
                int j = i;
                while (j + 1 < sections.Count && classes[j + 1] == classes[i]) j++;
                runs.Add(new ClassRun
                {
                    Class = classes[i],
                    First = i,
                    Last = j,
                    Duration = sections[j].endUT - sections[i].startUT,
                });
                i = j + 1;
            }
            return runs;
        }

        /// <summary>
        /// Version-2 boundary predicate. Same steps as version 1
        /// (<see cref="IsSplittableEnvOrBodyBoundary"/>) with four changes:
        /// (Q9) any body change between two Exo-class sections stays one row, burning or not;
        /// (Q8) a brief touchdown (a Surface run under <see cref="BriefTouchdownMaxSeconds"/>
        /// between Atmospheric / Approach runs) is never cut and reads as air for every other
        /// rule; (Q7 / A1 / B1) an atmosphere stretch entered from space is cut only at its
        /// first entry: always when it is the final descent (it ends on the ground or the
        /// recording ends in the air), and for a braking pass (it ends back in space) only when
        /// the row is not already a braking row; nothing inside a pass and no exit from a
        /// braking pass is cut, and the engine at the crossing does not matter for such a pass.
        /// Ascents (atmosphere entered from the ground) and the airless approach band keep the
        /// version-1 steps 6 and 7.
        /// </summary>
        internal static bool IsSplittableBoundaryV2(
            Recording rec, int s, PassMap map, out SplitBoundaryReason reason)
        {
            var sections = rec.TrackSections;
            var prev = sections[s - 1];
            var next = sections[s];
            if (map == null) map = BuildPassMap(rec);

            // Step 1: seam short-circuit.
            if (prev.isBoundarySeam || next.isBoundarySeam)
            {
                reason = SplitBoundaryReason.SuppressedBoundarySeam;
                return false;
            }

            // Step 2: not a boundary.
            int rawPrev = map.RawClass[s - 1];
            int rawNext = map.RawClass[s];
            bool bodyChanged = SectionBodyChanged(prev, next);
            if (rawPrev == rawNext && !bodyChanged)
            {
                reason = SplitBoundaryReason.NotABoundary;
                return false;
            }

            // Step 3 (Q9): an SOI change in space is one row, whatever the engine did.
            if (bodyChanged && rawPrev == ExoSplitClass && rawNext == ExoSplitClass)
            {
                reason = SplitBoundaryReason.SuppressedExoCoastBodyChange;
                return false;
            }

            // Step 4: other body changes.
            if (bodyChanged)
            {
                reason = SplitBoundaryReason.BodyChange;
                return true;
            }

            // Q8: a brief touchdown is never cut.
            if (map.BriefTouchdown[s - 1] || map.BriefTouchdown[s])
            {
                reason = SplitBoundaryReason.SuppressedBriefTouchdown;
                return false;
            }

            int prevClass = map.EffClass[s - 1];
            int nextClass = map.EffClass[s];
            if (prevClass == nextClass)
            {
                reason = SplitBoundaryReason.NotABoundary;
                return false;
            }

            // Step 5: Surface involved.
            if (prevClass == SurfaceSplitClass || nextClass == SurfaceSplitClass)
            {
                if (IsSurfaceGrazeOverRuns(map, s, out var surfaceGrazeDirection))
                {
                    reason = surfaceGrazeDirection;
                    return false;
                }
                if (IsLaunchSiteDepartureBoundary(rec, s))
                {
                    reason = SplitBoundaryReason.SuppressedLaunchSiteLeadingRun;
                    return false;
                }
                reason = SplitBoundaryReason.SurfaceInvolved;
                return true;
            }

            // Atmosphere passes entered from space.
            if ((prevClass == AtmosphericSplitClass && nextClass == ExoSplitClass)
                || (prevClass == ExoSplitClass && nextClass == AtmosphericSplitClass))
            {
                if (TryClassifyPassBoundary(rec, s, map, out reason, out bool split))
                    return split;
            }

            // Step 6: engine at the crossing (ascents, the approach band).
            if (prev.environment == SegmentEnvironment.ExoPropulsive
                || next.environment == SegmentEnvironment.ExoPropulsive)
            {
                reason = SplitBoundaryReason.ExoPropulsiveAtCrossing;
                return true;
            }

            // Step 7: persistence (graze) predicate over the effective classes.
            if (IsGrazeOverRuns(map, s, out var grazeDirection))
            {
                reason = grazeDirection;
                return false;
            }

            reason = SplitBoundaryReason.PersistedPhaseChange;
            return true;
        }

        /// <summary>
        /// Classifies an Exo / Atmospheric boundary against the atmosphere stretches. Returns
        /// false when the stretch it belongs to was not entered from space (an ascent), so the
        /// caller falls through to steps 6 and 7.
        /// </summary>
        private static bool TryClassifyPassBoundary(
            Recording rec, int s, PassMap map, out SplitBoundaryReason reason, out bool split)
        {
            reason = SplitBoundaryReason.NotABoundary;
            split = false;
            int prevRun = map.RunOfSection[s - 1];
            int nextRun = map.RunOfSection[s];
            bool entering = map.Runs[nextRun].Class == AtmosphericSplitClass;
            int atmoRun = entering ? nextRun : prevRun;
            int stretchIdx = map.StretchOfRun[atmoRun];
            if (stretchIdx < 0) return false;
            var st = map.Stretches[stretchIdx];
            if (!st.FromSpace) return false;

            if (entering)
            {
                if (nextRun != st.FirstRun)
                {
                    // The end of a brief hop back into the same pass.
                    reason = SplitBoundaryReason.SuppressedInsidePass;
                    return true;
                }
                if (st.OwnPass)
                {
                    reason = SplitBoundaryReason.SuppressedOwnPassEntry;
                    return true;
                }
                if (st.Kind == PassKind.Final)
                {
                    reason = SplitBoundaryReason.FinalDescentEntry;
                    split = true;
                    return true;
                }
                if (string.Equals(rec.SegmentRole, SegmentRoles.Aerobraking, StringComparison.Ordinal)
                    && IsSameBodyAsRowFirstPass(rec, map, stretchIdx, s))
                {
                    reason = SplitBoundaryReason.SuppressedLaterBrakingPass;
                    return true;
                }
                reason = SplitBoundaryReason.BrakingPassEntry;
                split = true;
                return true;
            }

            // Leaving the air: a hop inside the pass, or the exit of a braking pass.
            reason = prevRun < st.LastRun
                ? SplitBoundaryReason.SuppressedInsidePass
                : SplitBoundaryReason.SuppressedBrakingPassExit;
            return true;
        }

        /// <summary>
        /// The "first touch of the row" resets at a body change: a later braking pass stays
        /// inside an aerobraking row only when it is at the body of the row's first pass from
        /// space (a Duna aerocapture after a Kerbin aerobrake opens its own row). True when the
        /// row has no earlier pass from space or a body is unknown.
        /// </summary>
        private static bool IsSameBodyAsRowFirstPass(Recording rec, PassMap map, int stretchIdx, int s)
        {
            for (int i = 0; i < stretchIdx; i++)
            {
                var first = map.Stretches[i];
                if (!first.FromSpace) continue;
                string rowBody = GetSectionBody(rec, rec.TrackSections[map.Runs[first.FirstRun].First]);
                string entryBody = GetSectionBody(rec, rec.TrackSections[s]);
                return string.IsNullOrEmpty(rowBody) || string.IsNullOrEmpty(entryBody)
                    || string.Equals(rowBody, entryBody, StringComparison.Ordinal);
            }
            return true;
        }

        /// <summary>
        /// Review B2: the role a first half with a pass role keeps after a cut, read from its own
        /// content. A committed pass row that grew (a resumed tip) and was cut again can now end
        /// back in space: its first pass is then a braking pass, not the final descent, so the
        /// head becomes an aerobraking row (and the reentry row the cut made does not share its
        /// role). A head with another role keeps it; one with no pass from space left falls
        /// back to <see cref="ResolveStartRole"/>.
        /// </summary>
        internal static string ResolveHeadRoleAfterCut(Recording head)
        {
            if (head == null) return null;
            if (string.IsNullOrEmpty(head.SegmentRole)) return ResolveStartRole(head);
            if (!IsPassRole(head.SegmentRole)) return head.SegmentRole;
            if (head.TrackSections == null || head.TrackSections.Count == 0) return head.SegmentRole;
            var map = BuildPassMap(head);
            for (int i = 0; i < map.Stretches.Count; i++)
            {
                var st = map.Stretches[i];
                if (!st.FromSpace) continue;
                return st.Kind == PassKind.Final ? SegmentRoles.Reentry : SegmentRoles.Aerobraking;
            }
            return ResolveStartRole(head);
        }

        /// <summary>
        /// Review B2, the merge side of the version gate: true when the version-2 split rules
        /// would cut the joined recording of <paramref name="a"/> and <paramref name="b"/> again
        /// (any boundary the predicate splits and the split guard accepts). Such a join is never
        /// made, so a merge pass and a split pass can never undo each other on every load. The
        /// join is a read-only view carrying <paramref name="a"/>'s identity, role and start.
        /// </summary>
        internal static bool WouldSplitRulesCutJoin(Recording a, Recording b)
        {
            if (a?.TrackSections == null || b?.TrackSections == null) return false;
            var joined = new Recording
            {
                RecordingId = a.RecordingId,
                SplitRuleVersion = a.SplitRuleVersion,
                SegmentRole = a.SegmentRole,
                EvaCrewName = a.EvaCrewName,
                LaunchSiteName = a.LaunchSiteName,
                StartSituation = a.StartSituation,
                StartBodyName = a.StartBodyName,
            };
            joined.TrackSections.AddRange(a.TrackSections);
            joined.TrackSections.AddRange(b.TrackSections);
            if (a.Points != null) joined.Points.AddRange(a.Points);
            if (b.Points != null) joined.Points.AddRange(b.Points);
            if (a.OrbitSegments != null) joined.OrbitSegments.AddRange(a.OrbitSegments);
            if (b.OrbitSegments != null) joined.OrbitSegments.AddRange(b.OrbitSegments);
            if (joined.TrackSections.Count < 2) return false;
            var map = BuildPassMap(joined);
            for (int s = 1; s < joined.TrackSections.Count; s++)
            {
                if (IsSplittableBoundaryV2(joined, s, map, out _)
                    && CanAutoSplitIgnoringGhostTriggers(joined, s))
                    return true;
            }
            return false;
        }

        private static bool IsGrazeOverRuns(PassMap map, int s, out SplitBoundaryReason direction)
        {
            int a = map.RunOfSection[s - 1];
            int b = map.RunOfSection[s];
            var runs = map.Runs;
            if (runs[b].Duration < BriefSectionMaxSeconds && b + 1 < runs.Count
                && runs[b + 1].Class == runs[a].Class)
            {
                direction = SplitBoundaryReason.SuppressedGrazeForward;
                return true;
            }
            if (runs[a].Duration < BriefSectionMaxSeconds && a - 1 >= 0
                && runs[a - 1].Class == runs[b].Class)
            {
                direction = SplitBoundaryReason.SuppressedGrazeBackward;
                return true;
            }
            direction = SplitBoundaryReason.PersistedPhaseChange;
            return false;
        }

        private static bool IsSurfaceGrazeOverRuns(PassMap map, int s, out SplitBoundaryReason direction)
        {
            int a = map.RunOfSection[s - 1];
            int b = map.RunOfSection[s];
            var runs = map.Runs;
            if (runs[a].Class == SurfaceSplitClass && IsSurfaceGrazeBriefClass(runs[b].Class)
                && runs[b].Duration < BriefSectionMaxSeconds
                && b + 1 < runs.Count && runs[b + 1].Class == SurfaceSplitClass)
            {
                direction = SplitBoundaryReason.SuppressedSurfaceGrazeForward;
                return true;
            }
            if (runs[b].Class == SurfaceSplitClass && IsSurfaceGrazeBriefClass(runs[a].Class)
                && runs[a].Duration < BriefSectionMaxSeconds
                && a - 1 >= 0 && runs[a - 1].Class == SurfaceSplitClass)
            {
                direction = SplitBoundaryReason.SuppressedSurfaceGrazeBackward;
                return true;
            }
            direction = SplitBoundaryReason.PersistedPhaseChange;
            return false;
        }
    }
}
