using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// One vessel line of a mission profile (docs/dev/design-mission-profile.md section 5): one
    /// row of the Missions tab's vessel rows (<see cref="MissionVesselRowBuilder.Build"/>) with
    /// the recordings it flew, the instants read off the tree, and the spans the mission's
    /// include set excludes. A partner line (another mission's vessel) is named, never analyzed.
    /// </summary>
    internal sealed class MissionProfileLine
    {
        public string LineId;
        public string Name;
        public bool IsPerson;
        public bool IsPartner;
        public int Depth;
        public string ParentLineId;
        public MissionVesselRow Row;
        public readonly List<Recording> Pieces = new List<Recording>();
        public readonly List<ProfileAnchorInput> Anchors = new List<ProfileAnchorInput>();
        public readonly List<ProfileSpan> ExcludedSpans = new List<ProfileSpan>();
        public readonly List<string> CrewNames = new List<string>();
        public readonly List<MissionProfileLine> Children = new List<MissionProfileLine>();
        // Docks (and their undocks) another mission recorded onto this line's vessel, in UT
        // order (section 3.2): named, never expanded.
        public readonly List<ProfileOtherMissionDock> OtherMissionDocks = new List<ProfileOtherMissionDock>();
        // A kerbal line that is only a board back into the vessel it left within the short-EVA
        // window: drawn as one event row on that vessel, never as a line.
        public bool CollapsedToEvent;
        // A line whose recordings a Re-Fly bridge carried into another line (the fork of a
        // re-flown slot): drawn there, not on its own.
        public string MergedIntoLineId;
        // Every piece is outside the effective recording set (a superseded attempt with no
        // fork in this tree).
        public bool Hidden;
        public bool Reflown;
        public double ReflownUT = double.NaN;
        public string StartEvent;
        public double StartUT = double.NaN;
        public double EndUT = double.NaN;
        public PhasePlan Plan;

        /// <summary>True for a line the Profile draws with its own header and phases.</summary>
        public bool IsDrawn => !IsPartner && !CollapsedToEvent && MergedIntoLineId == null && !Hidden;
    }

    /// <summary>Counts of one line build, for the profile's summary log line.</summary>
    internal struct MissionProfileLineTally
    {
        public int Lines;
        public int Partners;
        public int Collapsed;
        public int Bridged;
        public int Hidden;
        public int Anchors;
    }

    /// <summary>
    /// Builds the vessel lines of one mission from the Missions tab's vessel rows, so the
    /// Profile's lines agree with the tab's rows and include keys and no second "same vessel"
    /// rule exists. Pure apart from the caller's resolvers.
    /// </summary>
    internal static partial class MissionProfileLineBuilder
    {
        /// <summary>An EVA whose kerbal boards the same vessel within this is one event row.</summary>
        internal const double ShortEvaSeconds = 120.0;

        /// <summary>Stagings this close together are one batch (the Log's stage moment).</summary>
        internal const double StageBatchSeconds = MissionStructureListBuilder.DefaultStageMomentSeconds;

        /// <summary>
        /// The lines of <paramref name="rows"/> (the Missions tab's vessel rows of the tree),
        /// in row order with their child lines nested. Pure.
        /// </summary>
        internal static List<MissionProfileLine> Build(
            MissionProfileRequest req, MissionStructure structure, List<MissionVesselRow> rows,
            out MissionProfileLineTally tally)
        {
            tally = default(MissionProfileLineTally);
            var lines = new List<MissionProfileLine>();
            if (req?.Tree == null || structure == null || rows == null) return lines;
            var byId = new Dictionary<string, MissionProfileLine>(StringComparer.Ordinal);
            foreach (MissionVesselRow row in rows)
                lines.Add(BuildLine(req, structure, row, 0, null, byId));
            var all = new List<MissionProfileLine>(byId.Values);
            foreach (MissionProfileLine line in all) ApplyEffectiveSet(req, structure, line, byId);
            foreach (MissionProfileLine line in all) BuildAnchors(req, structure, line, byId);
            foreach (MissionProfileLine line in all) MarkShortEva(req, line, byId);
            foreach (MissionProfileLine line in all)
            {
                tally.Lines++;
                if (line.IsPartner) tally.Partners++;
                if (line.CollapsedToEvent) tally.Collapsed++;
                if (line.MergedIntoLineId != null) tally.Bridged++;
                if (line.Hidden) tally.Hidden++;
                tally.Anchors += line.Anchors.Count;
            }
            return lines;
        }

        /// <summary>Every line of <paramref name="lines"/>, depth first.</summary>
        internal static IEnumerable<MissionProfileLine> All(IEnumerable<MissionProfileLine> lines)
        {
            foreach (MissionProfileLine l in lines)
            {
                yield return l;
                foreach (MissionProfileLine c in All(l.Children)) yield return c;
            }
        }

        private static MissionProfileLine BuildLine(MissionProfileRequest req, MissionStructure structure,
            MissionVesselRow row, int depth, string parentId, Dictionary<string, MissionProfileLine> byId)
        {
            var line = new MissionProfileLine
            {
                LineId = row.OwnerHeadId,
                Name = row.VesselName,
                IsPerson = row.IsPerson,
                IsPartner = row.IsPartner,
                Depth = depth,
                ParentLineId = parentId,
                Row = row,
                StartEvent = row.StartEvent,
                StartUT = row.StartUT,
                EndUT = row.EndUT,
            };
            if (!string.IsNullOrEmpty(line.LineId)) byId[line.LineId] = line;
            CollectPieces(req, structure, row, line);
            CollectExcludedSpans(req, row, line);
            foreach (MissionVesselRow child in row.Children)
                line.Children.Add(BuildLine(req, structure, child, depth + 1, line.LineId, byId));
            return line;
        }

        // The recordings of one row: for each through-line owner its intervals name, the run
        // of legs from that owner (MissionThroughLineBuilder.ContinuationSuccessor) that the
        // row's own intervals overlap. An own-side undock's partner half is in another row.
        private static void CollectPieces(MissionProfileRequest req, MissionStructure structure,
            MissionVesselRow row, MissionProfileLine line)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var owners = new List<string>();
            foreach (MissionCompositionNode iv in row.Intervals)
                if (!string.IsNullOrEmpty(iv.OwnerHeadId) && !owners.Contains(iv.OwnerHeadId))
                    owners.Add(iv.OwnerHeadId);
            foreach (string owner in owners)
            {
                foreach (MissionLeg leg in Run(structure, owner))
                {
                    if (seen.Contains(leg.RecordingId) || !OverlapsOwnIntervals(row, owner, leg)) continue;
                    if (!req.Tree.Recordings.TryGetValue(leg.RecordingId, out Recording rec) || rec == null) continue;
                    seen.Add(leg.RecordingId);
                    line.Pieces.Add(rec);
                    foreach (string c in leg.CrewNames)
                        if (!line.CrewNames.Contains(c)) line.CrewNames.Add(c);
                }
            }
            line.Pieces.Sort(ComparePieces);
        }

        private static int ComparePieces(Recording a, Recording b)
        {
            int c = a.StartUT.CompareTo(b.StartUT);
            return c != 0 ? c : string.CompareOrdinal(a.RecordingId, b.RecordingId);
        }

        internal static List<MissionLeg> Run(MissionStructure structure, string headId)
        {
            var run = new List<MissionLeg>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string cur = headId;
            while (cur != null && visited.Add(cur) && structure.LegsById.TryGetValue(cur, out MissionLeg leg))
            {
                run.Add(leg);
                cur = MissionThroughLineBuilder.ContinuationSuccessor(structure, leg);
            }
            return run;
        }

        private static bool OverlapsOwnIntervals(MissionVesselRow row, string owner, MissionLeg leg)
        {
            foreach (MissionCompositionNode iv in row.Intervals)
            {
                if (!string.Equals(iv.OwnerHeadId, owner, StringComparison.Ordinal)) continue;
                if (leg.StartUT < iv.EndUT && leg.EndUT > iv.StartUT) return true;
                // A zero-length leg on an interval's edge (a continuation that starts and ends
                // in one instant) belongs to the interval it sits in.
                if (leg.EndUT <= leg.StartUT && leg.StartUT >= iv.StartUT && leg.StartUT < iv.EndUT) return true;
            }
            return false;
        }

        // Per interval, non-cascading: the spans of this row's own intervals the mission excludes.
        private static void CollectExcludedSpans(MissionProfileRequest req, MissionVesselRow row, MissionProfileLine line)
        {
            ICollection<string> excluded = req.Mission?.ExcludedIntervalKeys;
            if (excluded == null || excluded.Count == 0) return;
            foreach (MissionCompositionNode iv in row.Intervals)
                if (!MissionIntervalSelection.IsIntervalIncluded(iv, excluded))
                    line.ExcludedSpans.Add(new ProfileSpan(iv.StartUT, iv.EndUT));
        }

        // ------------------------------------------------------------------ Re-Fly

        // The effective recording set (ERS, computed by the UI where that read is allowed): a
        // piece outside it was superseded. Its supersede row (old -> new) bridges the line to
        // the fork, which carries on with its own run, and a "Flown again from here" anchor
        // goes at the fork's start; the fork's own row, if it has one, is drawn here instead.
        private static void ApplyEffectiveSet(MissionProfileRequest req, MissionStructure structure,
            MissionProfileLine line, Dictionary<string, MissionProfileLine> byId)
        {
            ICollection<string> effective = req.EffectiveRecordingIds;
            if (effective == null || line.IsPartner) return;
            var kept = new List<Recording>();
            foreach (Recording piece in line.Pieces)
            {
                if (effective.Contains(piece.RecordingId)) { kept.Add(piece); continue; }
                string fork = ResolveSupersede(req, piece.RecordingId, effective);
                if (fork == null || !req.Tree.Recordings.TryGetValue(fork, out Recording forkRec)) continue;
                if (line.Reflown) continue;
                line.Reflown = true;
                line.ReflownUT = forkRec.StartUT;
                foreach (MissionLeg leg in Run(structure, fork))
                {
                    if (!effective.Contains(leg.RecordingId)) continue;
                    if (req.Tree.Recordings.TryGetValue(leg.RecordingId, out Recording r) && !kept.Contains(r))
                        kept.Add(r);
                    if (byId.TryGetValue(leg.RecordingId, out MissionProfileLine forkLine) && forkLine != line)
                        forkLine.MergedIntoLineId = line.LineId;
                }
            }
            line.Pieces.Clear();
            line.Pieces.AddRange(kept);
            line.Pieces.Sort(ComparePieces);
            if (line.Pieces.Count == 0 && line.MergedIntoLineId == null) line.Hidden = true;
        }

        private static string ResolveSupersede(MissionProfileRequest req, string id, ICollection<string> effective)
        {
            if (req.SupersedeRelations == null) return null;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string cur = id;
            while (cur != null && visited.Add(cur))
            {
                string next = null;
                foreach (RecordingSupersedeRelation rel in req.SupersedeRelations)
                    if (rel != null && string.Equals(rel.OldRecordingId, cur, StringComparison.Ordinal))
                    {
                        next = rel.NewRecordingId;
                        break;
                    }
                if (next == null) return null;
                if (effective.Contains(next)) return next;
                cur = next;
            }
            return null;
        }

        // ------------------------------------------------------------------ anchors

        // The instants stratum (b) hangs on, read off the tree's branch points for this line's
        // recordings: stagings (batched like the Log), separations, breakups, docks and undocks,
        // EVAs and boards, ground parts, switch joins, the Re-Fly bridge and the line's end.
        private static void BuildAnchors(MissionProfileRequest req, MissionStructure structure,
            MissionProfileLine line, Dictionary<string, MissionProfileLine> byId)
        {
            if (line.IsPartner || line.Pieces.Count == 0) return;
            var pieceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Recording r in line.Pieces) pieceIds.Add(r.RecordingId);
            var bps = new List<BranchPoint>(req.Tree.BranchPoints);
            bps.Sort((a, b) => a.UT.CompareTo(b.UT));
            ProfileAnchorInput lastStage = null;
            foreach (BranchPoint bp in bps)
            {
                if (bp == null) continue;
                bool parentIn = AnyIn(bp.ParentRecordingIds, pieceIds);
                bool childIn = AnyIn(bp.ChildRecordingIds, pieceIds);
                if (!parentIn && !childIn) continue;
                if (IsSeparation(bp.Type) && parentIn)
                    lastStage = AddSeparationAnchors(req, line, bp, pieceIds, lastStage, byId);
                else
                    AddMergeAnchors(req, structure, line, bp, pieceIds, parentIn, childIn);
            }
            if (line.Reflown)
                line.Anchors.Add(new ProfileAnchorInput { Kind = ProfileEventKind.Reflown, UT = line.ReflownUT });
            AddEndAnchor(req, line);
        }

        private static bool IsSeparation(BranchPointType t)
            => t == BranchPointType.JointBreak || t == BranchPointType.Breakup || t == BranchPointType.EVA
               || t == BranchPointType.GroundPartPlaced || t == BranchPointType.InventoryTransfer;

        private static bool AnyIn(List<string> ids, HashSet<string> set)
        {
            if (ids == null) return false;
            foreach (string id in ids) if (id != null && set.Contains(id)) return true;
            return false;
        }

        private static ProfileAnchorInput AddSeparationAnchors(MissionProfileRequest req, MissionProfileLine line,
            BranchPoint bp, HashSet<string> pieceIds, ProfileAnchorInput lastStage,
            Dictionary<string, MissionProfileLine> byId)
        {
            var debris = new List<Recording>();
            var controlled = new List<Recording>();
            foreach (string id in bp.ChildRecordingIds)
            {
                if (id == null || pieceIds.Contains(id) || !req.Tree.Recordings.TryGetValue(id, out Recording c) || c == null)
                    continue;
                if (c.IsDebris) debris.Add(c);
                else controlled.Add(c);
            }
            switch (bp.Type)
            {
                case BranchPointType.EVA:
                    foreach (Recording k in controlled)
                        if (!string.IsNullOrEmpty(k.EvaCrewName))
                            line.Anchors.Add(Anchor(ProfileEventKind.Eva, bp, k.RecordingId, k.EvaCrewName));
                    return lastStage;
                case BranchPointType.GroundPartPlaced:
                case BranchPointType.InventoryTransfer:
                    ProfileEventKind kind = bp.Type == BranchPointType.GroundPartPlaced
                        ? ProfileEventKind.PartPlaced : ProfileEventKind.InventoryTransfer;
                    foreach (Recording c in controlled)
                        line.Anchors.Add(Anchor(kind, bp, c.RecordingId, NameOf(req, c)));
                    return lastStage;
            }
            if (bp.Type == BranchPointType.Breakup && controlled.Count > 0)
            {
                var a = Anchor(ProfileEventKind.Breakup, bp, controlled[0].RecordingId, NameOf(req, controlled[0]));
                a.Count = debris.Count + controlled.Count;
                a.Vessels = controlled.Count;
                line.Anchors.Add(a);
                return lastStage;
            }
            foreach (Recording c in controlled)
                line.Anchors.Add(Anchor(ProfileEventKind.Separation, bp, c.RecordingId, NameOf(req, c), bp.SplitCause));
            if (debris.Count == 0) return lastStage;
            double window = bp.CoalesceWindow > 0 ? bp.CoalesceWindow : StageBatchSeconds;
            if (lastStage != null && bp.UT - lastStage.UT <= window)
            {
                lastStage.Count += debris.Count;
                lastStage.RefName = JoinEndings(lastStage.RefName, debris);
                return lastStage;
            }
            var stage = Anchor(ProfileEventKind.StageSeparation, bp, null, JoinEndings(null, debris));
            stage.Count = debris.Count;
            line.Anchors.Add(stage);
            return stage;
        }

        // The endings of the dropped pieces, for the stage separation's hover.
        private static string JoinEndings(string existing, List<Recording> debris)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(existing)) parts.Add(existing);
            foreach (Recording d in debris) parts.Add(DebrisEnding(d.TerminalStateValue));
            return string.Join(", ", parts.ToArray());
        }

        internal static string DebrisEnding(TerminalState? t)
        {
            switch (t)
            {
                case TerminalState.Destroyed: return "destroyed";
                case TerminalState.Landed: return "landed";
                case TerminalState.Splashed: return "splashed";
                case TerminalState.Orbiting: return "in orbit";
                case TerminalState.SubOrbital: return "suborbital";
                case TerminalState.Recovered: return "recovered";
                default: return "not tracked";
            }
        }

        private static void AddMergeAnchors(MissionProfileRequest req, MissionStructure structure,
            MissionProfileLine line, BranchPoint bp, HashSet<string> pieceIds, bool parentIn, bool childIn)
        {
            switch (bp.Type)
            {
                case BranchPointType.Dock:
                    line.Anchors.Add(DockAnchor(req, line, bp, pieceIds));
                    break;
                case BranchPointType.Undock:
                    if (parentIn) line.Anchors.Add(UndockAnchor(req, structure, bp, pieceIds));
                    break;
                case BranchPointType.Board:
                    if (childIn && !AllIn(bp.ParentRecordingIds, pieceIds))
                        line.Anchors.Add(Anchor(ProfileEventKind.Board, bp, null, BoarderName(req, bp, pieceIds)));
                    break;
                case BranchPointType.VesselSwitchContinuation:
                    if (childIn && !parentIn)
                        line.Anchors.Add(Anchor(ProfileEventKind.SwitchedTo, bp, null, line.Name));
                    break;
            }
        }

        private static bool AllIn(List<string> ids, HashSet<string> set)
        {
            if (ids == null || ids.Count == 0) return false;
            foreach (string id in ids) if (id == null || !set.Contains(id)) return false;
            return true;
        }

        // A dock: which side KSP kept is read off the legs (the own leg ends Docked and the
        // merged leg is another mission's vessel = this ship was absorbed into the partner).
        private static ProfileAnchorInput DockAnchor(MissionProfileRequest req, MissionProfileLine line,
            BranchPoint bp, HashSet<string> pieceIds)
        {
            string viewer = FirstIn(bp.ParentRecordingIds, pieceIds) ?? FirstIn(bp.ChildRecordingIds, pieceIds);
            string merged = bp.ChildRecordingIds != null && bp.ChildRecordingIds.Count > 0 ? bp.ChildRecordingIds[0] : null;
            bool ownEndsDocked = viewer != null && req.Tree.Recordings.TryGetValue(viewer, out Recording own)
                && own != null && own.TerminalStateValue == TerminalState.Docked;
            bool mergedIsPartner = merged != null && req.PartnerLegIds != null && req.PartnerLegIds.Contains(merged);
            var a = Anchor(ProfileEventKind.Docking, bp, merged, MergePartner(req, bp, viewer));
            a.OwnShipAbsorbed = ownEndsDocked && (mergedIsPartner || !pieceIds.Contains(merged ?? ""));
            return a;
        }

        private static ProfileAnchorInput UndockAnchor(MissionProfileRequest req, MissionStructure structure,
            BranchPoint bp, HashSet<string> pieceIds)
        {
            string other = null;
            if (bp.ChildRecordingIds != null)
                foreach (string id in bp.ChildRecordingIds)
                    if (id != null && !pieceIds.Contains(id) && structure.LegsById.ContainsKey(id)) { other = id; break; }
            string name = other != null && req.Tree.Recordings.TryGetValue(other, out Recording r) ? NameOf(req, r) : null;
            return Anchor(ProfileEventKind.Undocking, bp, other, name);
        }

        private static string BoarderName(MissionProfileRequest req, BranchPoint bp, HashSet<string> pieceIds)
        {
            foreach (string id in bp.ParentRecordingIds)
                if (id != null && !pieceIds.Contains(id) && req.Tree.Recordings.TryGetValue(id, out Recording r)
                    && r != null && !string.IsNullOrEmpty(r.EvaCrewName))
                    return r.EvaCrewName;
            return null;
        }

        private static string FirstIn(List<string> ids, HashSet<string> set)
        {
            if (ids == null) return null;
            foreach (string id in ids) if (id != null && set.Contains(id)) return id;
            return null;
        }

        private static string MergePartner(MissionProfileRequest req, BranchPoint bp, string viewer)
        {
            if (bp.ParentRecordingIds != null && bp.ParentRecordingIds.Count == 2)
            {
                string other = string.Equals(bp.ParentRecordingIds[0], viewer, StringComparison.Ordinal)
                    ? bp.ParentRecordingIds[1] : bp.ParentRecordingIds[0];
                if (other != null && req.Tree.Recordings.TryGetValue(other, out Recording r) && r != null)
                    return NameOf(req, r);
            }
            if (req.MergePartnerResolver == null) return null;
            try
            {
                string text = req.MergePartnerResolver(bp, viewer);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string NameOf(MissionProfileRequest req, Recording r)
        {
            if (r == null) return null;
            if (!string.IsNullOrEmpty(r.EvaCrewName)) return r.EvaCrewName;
            if (req.VesselNames != null && req.VesselNames.TryGetValue(r.RecordingId, out string n) && !string.IsNullOrEmpty(n))
                return n;
            return r.VesselName;
        }

        private static ProfileAnchorInput Anchor(ProfileEventKind kind, BranchPoint bp, string reference,
            string name, string cause = null)
            => new ProfileAnchorInput { Kind = kind, UT = bp.UT, Ref = reference, RefName = name, Cause = cause };

        // The line's end: Recovered, or End with the terminal state; none for a leg that ends
        // Docked / Boarded (the line it joined goes on, or a board names the vessel).
        private static void AddEndAnchor(MissionProfileRequest req, MissionProfileLine line)
        {
            Recording last = line.Pieces[line.Pieces.Count - 1];
            TerminalState? t = last.TerminalStateValue;
            double ut = last.EndUT;
            if (t == TerminalState.Recovered)
            {
                line.Anchors.Add(new ProfileAnchorInput { Kind = ProfileEventKind.Recovered, UT = ut, Terminal = t, Biome = last.EndBiome });
                return;
            }
            if (t == TerminalState.Boarded)
            {
                BranchPoint board = FindBranchPoint(req.Tree, last.ChildBranchPointId);
                string vessel = null;
                if (board != null)
                    foreach (string id in board.ParentRecordingIds)
                        if (id != last.RecordingId && req.Tree.Recordings.TryGetValue(id, out Recording v)) { vessel = NameOf(req, v); break; }
                line.Anchors.Add(new ProfileAnchorInput { Kind = ProfileEventKind.Board, UT = ut, RefName = vessel, Terminal = t });
                return;
            }
            if (t == TerminalState.Docked || !t.HasValue) return;
            line.Anchors.Add(new ProfileAnchorInput { Kind = ProfileEventKind.End, UT = ut, Terminal = t, Biome = last.EndBiome });
        }

        private static BranchPoint FindBranchPoint(RecordingTree tree, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (BranchPoint bp in tree.BranchPoints)
                if (bp != null && string.Equals(bp.Id, id, StringComparison.Ordinal)) return bp;
            return null;
        }

        // ------------------------------------------------------------------ short EVA

        // An EVA kerbal line whose only content is a board back into the same vessel within
        // ShortEvaSeconds is one event row on that vessel, not a child line.
        private static void MarkShortEva(MissionProfileRequest req, MissionProfileLine line,
            Dictionary<string, MissionProfileLine> byId)
        {
            if (!line.IsPerson || line.Pieces.Count == 0 || line.ParentLineId == null) return;
            if (!byId.TryGetValue(line.ParentLineId, out MissionProfileLine parent)) return;
            Recording last = line.Pieces[line.Pieces.Count - 1];
            if (last.TerminalStateValue != TerminalState.Boarded) return;
            double start = line.Pieces[0].StartUT, end = last.EndUT;
            if (end - start > ShortEvaSeconds) return;
            BranchPoint board = FindBranchPoint(req.Tree, last.ChildBranchPointId);
            if (board == null || !AnyIn(board.ChildRecordingIds, IdsOf(parent))) return;
            foreach (ProfileAnchorInput a in parent.Anchors)
            {
                if (a.Kind != ProfileEventKind.Eva || !string.Equals(a.Ref, line.Pieces[0].RecordingId, StringComparison.Ordinal))
                    continue;
                a.Collapsed = true;
                a.EndUT = board.UT;
                line.CollapsedToEvent = true;
            }
            if (line.CollapsedToEvent)
                parent.Anchors.RemoveAll(a => a.Kind == ProfileEventKind.Board && Math.Abs(a.UT - board.UT) < 1e-6);
        }

        private static HashSet<string> IdsOf(MissionProfileLine line)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Recording r in line.Pieces) ids.Add(r.RecordingId);
            return ids;
        }
    }
}
