using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Pure read model + mission builder for the Log window (StructureListWindowUI;
    // roadmap.md Phase 13 Tier-1; docs/dev/plan-structure-list-window.md). A Log is a
    // flat, chronological "what happened, step by step" view of one run, complementing
    // the Missions tab's composition-over-time tree. The builder is pure (no Unity
    // calls, no shared mutable state, no recording mutation) and reads ONLY
    // already-recorded data, so it is headless-testable. The route side of the window
    // is the Logistics-side RouteStructureListBuilder: Missions code must not reference
    // Logistics, so the route-reading builder lives with the routes it reads.

    internal enum StructureStepKind
    {
        Launch     = 0,
        Staging    = 1,  // a stage (debris-only separation) or a jettison with no branch point
        Separation = 2,  // decouple / breakup that produces a controlled child leg
        Dock       = 3,
        Undock     = 4,
        Eva        = 5,
        Origin     = 6,  // route only
        Delivery   = 7,  // route only
        Stop       = 8,  // route only (reserved for multi-stop)
        Terminal   = 9
    }

    /// <summary>One row of a Log: a single event with its time, location and vessel.</summary>
    internal struct StructureStep
    {
        public double UT;            // event time; NaN for the route Origin pseudo-step (rendered first)
        public StructureStepKind Kind;
        public string Label;         // "Launch", "Staged: 2 pieces (...)", "Docked (CD)", "End: Orbiting"
        public string Tooltip;       // full label when Label had to be shortened for the cell; else null
        public string Location;      // "Kerbin, Launch Pad", "Kerbin orbit", "Mun, Midlands", "Kerbin", "-"
        public string VesselName;    // the vessel this step concerns (may be empty on route rows)
        public string RecordingId;   // the owning recording / leg; identity for the simultaneous collapse
        public uint SortPid;         // a part PID or 0, for a deterministic tiebreak only (not rendered)
    }

    /// <summary>
    /// Pure location text helpers shared by the mission builder and the Logistics-side
    /// route builder, which formats its route endpoints on top of these. All output is
    /// culture-free text.
    /// </summary>
    internal static class StructureLocationFormatter
    {
        /// <summary>The one text every Location cell shows when nothing honest is recorded.</summary>
        internal const string Missing = "-";

        // Canonical location text: ALWAYS "SOI/body, biome" order (body first, biome
        // second). Either part may be empty. Missing when nothing is recorded.
        internal static string BodyBiome(string body, string biome)
        {
            bool hasBody = !string.IsNullOrEmpty(body);
            bool hasBiome = !string.IsNullOrEmpty(biome);
            if (hasBody && hasBiome) return body + ", " + biome;
            if (hasBody) return body;
            if (hasBiome) return biome;
            return Missing;
        }

        /// <summary>"Kerbin orbit", or <see cref="Missing"/> with no body.</summary>
        internal static string Orbit(string body)
            => string.IsNullOrEmpty(body) ? Missing : body + " orbit";

        // A recording's start-captured situation / biome describe ONLY the moment the
        // recording began; nothing per-UT is recorded. So a row AT the recording's start
        // gets the captured context ("Kerbin orbit" when it began Orbiting, else
        // "body, biome", with the launch site in the biome slot for a launch), and any
        // later row keeps only the segment-stable body. Blank beats wrong.
        internal static string AtRecording(Recording rec, double ut)
        {
            if (rec == null) return Missing;
            if (Math.Abs(ut - rec.StartUT) <= MissionStructureListBuilder.StartContextSeconds)
            {
                if (!string.IsNullOrEmpty(rec.LaunchSiteName)
                    && string.IsNullOrEmpty(rec.ParentBranchPointId)
                    && rec.ChainIndex <= 0)
                    return BodyBiome(rec.StartBodyName, rec.LaunchSiteName);
                if (string.Equals(rec.StartSituation, "Orbiting", StringComparison.Ordinal))
                    return Orbit(rec.StartBodyName);
                return BodyBiome(rec.StartBodyName, rec.StartBiome);
            }
            return BodyBiome(rec.StartBodyName, null);
        }

        // True when the recording captured anything at its start (a BG-born child often
        // captured nothing: no situation, no body, no biome).
        internal static bool HasStartContext(Recording rec)
            => rec != null
               && (!string.IsNullOrEmpty(rec.StartSituation)
                   || !string.IsNullOrEmpty(rec.StartBodyName));
    }

    /// <summary>
    /// The Log's Time column: the first row with a time shows the calendar date, every
    /// later row the elapsed time since it, so rows in the same minute still read in
    /// order. Pure; the date formatter is injected (the window passes
    /// <c>KSPUtil.PrintDateCompact</c>, the Missions start-time cell's formatter).
    /// </summary>
    internal static class StructureTimeFormatter
    {
        /// <summary>"T+h:mm:ss" (hours unbounded), "T-h:mm:ss" before the reference.
        /// Whole seconds, truncated toward zero. InvariantCulture.</summary>
        internal static string FormatElapsed(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                return StructureLocationFormatter.Missing;
            string sign = seconds < 0 ? "T-" : "T+";
            long total = (long)Math.Floor(Math.Abs(seconds));
            long h = total / 3600;
            long m = (total / 60) % 60;
            long s = total % 60;
            return sign + h.ToString(CultureInfo.InvariantCulture) + ":"
                + m.ToString("00", CultureInfo.InvariantCulture) + ":"
                + s.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// One Time cell per step: the first finite-UT step gets
        /// <paramref name="dateFormatter"/>(UT), every later one its elapsed time from
        /// that step, and a NaN step (the route origin) the missing-value text.
        /// </summary>
        internal static string[] FormatStepTimes(
            List<StructureStep> steps, Func<double, string> dateFormatter)
        {
            if (steps == null) return new string[0];
            var cells = new string[steps.Count];
            double reference = double.NaN;
            for (int i = 0; i < steps.Count; i++)
            {
                double ut = steps[i].UT;
                if (double.IsNaN(ut))
                {
                    cells[i] = StructureLocationFormatter.Missing;
                    continue;
                }
                if (double.IsNaN(reference))
                {
                    reference = ut;
                    cells[i] = dateFormatter != null
                        ? (dateFormatter(ut) ?? "")
                        : ut.ToString("F0", CultureInfo.InvariantCulture);
                    continue;
                }
                cells[i] = FormatElapsed(ut - reference);
            }
            return cells;
        }
    }

    internal static class MissionStructureListBuilder
    {
        // Silences the per-build Verbose summary for callers that rebuild as a pure
        // derivation (mirrors MissionStructureBuilder.SuppressLogging). Defaults to off.
        internal static bool SuppressLogging;

        /// <summary>A row this close to its recording's start reads the start-captured
        /// context (situation / biome / launch site); anything later gets the body only.</summary>
        internal const double StartContextSeconds = 0.5;

        /// <summary>
        /// A recording that continues another (a split child or a chain segment) starts with
        /// SEEDS: the recorder re-states the part state the segment starts in so the ghost
        /// draws correctly (RecordingOptimizer.ForwardPermanentStateEvents at exactly the split
        /// UT; the background recorder's loaded-physics seed a moment later). A seed is a
        /// permanent event either AT the segment's start (within this epsilon, which absorbs
        /// only serialization noise) or one an ancestor recording already had for the same
        /// part. No time window beyond that: a REAL jettison a moment after a split (a
        /// scripted fairing deploy at an atmosphere-exit chain split) must stay a row.
        /// </summary>
        internal const double ContinuationSeedEpsilonSeconds = 1e-3;

        /// <summary>The active recorder seeds the jettison state a ROOT recording starts in
        /// at its exact start UT; a root's start-UT Decoupled is real (launch clamps).</summary>
        internal const double RootSeedSeconds = 0.05;

        /// <summary>Default "same physics moment" window for a stage's branch point when the
        /// recorder stored no coalesce window of its own.</summary>
        internal const double DefaultStageMomentSeconds = 0.1;

        /// <summary>Part events this close to a Dock / Undock / Board on a recording that
        /// takes part in it are the port / pod coupling changes of that event, not staging.</summary>
        internal const double DockEventPartWindowSeconds = 0.5;

        /// <summary>
        /// The longest Event cell text drawn unshortened: the Event column at the window's
        /// first-open width holds about this many characters. A longer piece list is
        /// shortened in the cell and carried whole in the cell tooltip.
        /// </summary>
        internal const int EventCellCharBudget = 60;

        /// <summary>
        /// Flattens a mission tree into a UT-ordered step list. Pure. Takes the already-built
        /// <paramref name="structure"/> so the window passes its cached structure without a
        /// rebuild. <paramref name="partTitleResolver"/> maps an internal part name to its
        /// player-facing title (null or an unknown name falls back to the internal name);
        /// it is called at most once per distinct name per build.
        /// <paramref name="mergePartnerResolver"/> names the other side of a Dock / Board
        /// branch point for a viewer recording, already formatted
        /// ("CD" or "CD (mission 'CD Freighter')"); null when it cannot.
        /// </summary>
        internal static List<StructureStep> Build(
            RecordingTree tree,
            MissionStructure structure,
            Func<string, string> partTitleResolver = null,
            Func<BranchPoint, string, string> mergePartnerResolver = null)
        {
            var steps = new List<StructureStep>();
            if (tree == null || structure == null || structure.LegsById.Count == 0)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("Mission",
                        $"BuildStructureList: empty tree={tree?.Id ?? "<null>"}");
                return steps;
            }

            var ctx = new BuildContext(tree, structure, partTitleResolver, mergePartnerResolver);

            // 1. Launch: one per root leg.
            AddLaunchSteps(steps, ctx);

            // 2. The part events that can still mean something to a reader: seeds,
            //    debris breakup and dock coupling events are dropped here.
            List<CandidateEvent> candidates = CollectCandidateEvents(ctx);

            // 3. Attach each candidate to the stage (split branch point) it belongs to.
            var absorbed = new Dictionary<BranchPoint, List<CandidateEvent>>();
            var loose = new List<CandidateEvent>();
            AssignToStages(ctx, candidates, absorbed, loose);

            // 4. Branch-point rows (a stage becomes ONE row with its absorbed parts).
            AddBranchPointSteps(steps, ctx, absorbed);

            // 5. Part events no branch point covers, grouped per recording and moment.
            AddLooseStagingSteps(steps, ctx, loose);

            // 6. Terminal: one per controlled leg that ends in a state of its own.
            AddTerminalSteps(steps, ctx);

            // 7. Deterministic chronological sort, then the simultaneous collapse.
            steps.Sort(CompareStep);
            steps = CollapseSimultaneous(steps);

            if (!SuppressLogging)
                ParsekLog.Verbose("Mission",
                    $"BuildStructureList: tree={tree.Id ?? "<null>"} steps={steps.Count} " +
                    $"launch={CountKind(steps, StructureStepKind.Launch)} " +
                    $"staging={CountKind(steps, StructureStepKind.Staging)} " +
                    $"sep={CountKind(steps, StructureStepKind.Separation)} " +
                    $"dock={CountKind(steps, StructureStepKind.Dock)} " +
                    $"undock={CountKind(steps, StructureStepKind.Undock)} " +
                    $"eva={CountKind(steps, StructureStepKind.Eva)} " +
                    $"terminal={CountKind(steps, StructureStepKind.Terminal)} " +
                    $"partEvents: seeds={ctx.SkippedSeeds} debris={ctx.SkippedDebris} " +
                    $"dockCoupling={ctx.SkippedDockCoupling} duplicates={ctx.SkippedDuplicates} " +
                    $"absorbed={ctx.AbsorbedCount} loose={loose.Count} " +
                    $"mergedEndsSkipped={ctx.SkippedMergedEnds}");
            return steps;
        }

        // ------------------------------------------------------------------
        // Build state
        // ------------------------------------------------------------------

        private sealed class BuildContext
        {
            internal readonly RecordingTree Tree;
            internal readonly MissionStructure Structure;
            private readonly Func<string, string> partTitleResolver;
            internal readonly Func<BranchPoint, string, string> MergePartnerResolver;
            private readonly Dictionary<string, string> titleCache =
                new Dictionary<string, string>(StringComparer.Ordinal);

            // recording id -> the recordings it continues (branch parents + chain predecessor).
            internal readonly Dictionary<string, List<string>> Predecessors =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);

            internal int SkippedSeeds;
            internal int SkippedDebris;
            internal int SkippedDockCoupling;
            internal int SkippedDuplicates;
            internal int AbsorbedCount;
            internal int SkippedMergedEnds;

            internal BuildContext(RecordingTree tree, MissionStructure structure,
                Func<string, string> partTitleResolver,
                Func<BranchPoint, string, string> mergePartnerResolver)
            {
                Tree = tree;
                Structure = structure;
                this.partTitleResolver = partTitleResolver;
                MergePartnerResolver = mergePartnerResolver;
                IndexPredecessors();
            }

            internal Recording Rec(string id)
                => id != null && Tree.Recordings != null
                   && Tree.Recordings.TryGetValue(id, out Recording r) ? r : null;

            internal MissionLeg Leg(string id)
                => id != null && Structure.LegsById.TryGetValue(id, out MissionLeg l) ? l : null;

            // The vessel name a row about this recording shows: the leg label for a
            // controlled leg, else the recording's own vessel name.
            internal string VesselOf(string recordingId)
            {
                MissionLeg leg = Leg(recordingId);
                if (leg != null) return LegLabel(leg);
                Recording rec = Rec(recordingId);
                if (rec == null) return "";
                if (!string.IsNullOrEmpty(rec.EvaCrewName)) return rec.EvaCrewName;
                return string.IsNullOrEmpty(rec.VesselName) ? "(vessel)" : rec.VesselName;
            }

            internal string Title(string partName)
            {
                string key = partName ?? "";
                if (titleCache.TryGetValue(key, out string cached))
                    return cached;
                string title = null;
                if (partTitleResolver != null && key.Length > 0)
                {
                    try { title = partTitleResolver(key); }
                    catch (Exception) { title = null; }
                }
                if (string.IsNullOrEmpty(title))
                    title = key.Length > 0 ? key : "part";
                titleCache[key] = title;
                return title;
            }

            private void IndexPredecessors()
            {
                if (Tree.BranchPoints != null)
                {
                    foreach (BranchPoint bp in Tree.BranchPoints)
                    {
                        if (bp?.ChildRecordingIds == null || bp.ParentRecordingIds == null) continue;
                        foreach (string child in bp.ChildRecordingIds)
                        {
                            if (child == null) continue;
                            AddPredecessors(child, bp.ParentRecordingIds);
                        }
                    }
                }
                if (Tree.Recordings == null) return;
                // Chain segments: segment k continues segment k-1 of the same chain.
                var byChain = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
                foreach (Recording r in Tree.Recordings.Values)
                {
                    if (r == null || string.IsNullOrEmpty(r.ChainId) || r.ChainIndex < 0) continue;
                    if (!byChain.TryGetValue(r.ChainId, out Dictionary<int, string> seg))
                    {
                        seg = new Dictionary<int, string>();
                        byChain[r.ChainId] = seg;
                    }
                    seg[r.ChainIndex] = r.RecordingId;
                }
                foreach (Recording r in Tree.Recordings.Values)
                {
                    if (r == null || string.IsNullOrEmpty(r.ChainId) || r.ChainIndex <= 0) continue;
                    if (byChain.TryGetValue(r.ChainId, out Dictionary<int, string> seg)
                        && seg.TryGetValue(r.ChainIndex - 1, out string prev))
                        AddPredecessors(r.RecordingId, new List<string> { prev });
                }
            }

            private void AddPredecessors(string child, List<string> parents)
            {
                if (!Predecessors.TryGetValue(child, out List<string> list))
                {
                    list = new List<string>();
                    Predecessors[child] = list;
                }
                foreach (string p in parents)
                    if (p != null && !string.Equals(p, child, StringComparison.Ordinal) && !list.Contains(p))
                        list.Add(p);
            }

            // True when the recording continues another one (split child or chain segment).
            internal bool IsContinuation(Recording rec)
                => rec != null
                   && (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                       || rec.ChainIndex > 0
                       || Predecessors.ContainsKey(rec.RecordingId ?? ""));

            // True when (type, pid) already happened on an ancestor recording at or before
            // ut: a permanent part state changes once per physical part along one lineage,
            // so a repeat on a descendant is a re-statement of state, not a new event.
            internal bool AncestorAlreadyHad(Recording rec, PartEvent pe)
            {
                if (rec == null || !Predecessors.ContainsKey(rec.RecordingId ?? "")) return false;
                var visited = new HashSet<string>(StringComparer.Ordinal) { rec.RecordingId };
                var stack = new Stack<string>(Predecessors[rec.RecordingId]);
                while (stack.Count > 0)
                {
                    string id = stack.Pop();
                    if (id == null || !visited.Add(id)) continue;
                    Recording a = Rec(id);
                    if (a?.PartEvents != null)
                    {
                        for (int i = 0; i < a.PartEvents.Count; i++)
                        {
                            PartEvent x = a.PartEvents[i];
                            if (x.eventType == pe.eventType
                                && x.partPersistentId == pe.partPersistentId
                                && x.ut <= pe.ut + RootSeedSeconds)
                                return true;
                        }
                    }
                    if (Predecessors.TryGetValue(id, out List<string> more))
                        foreach (string m in more) stack.Push(m);
                }
                return false;
            }
        }

        private struct CandidateEvent
        {
            internal Recording Rec;
            internal PartEvent Event;
        }

        // ------------------------------------------------------------------
        // Phases
        // ------------------------------------------------------------------

        private static void AddLaunchSteps(List<StructureStep> steps, BuildContext ctx)
        {
            foreach (var rootId in ctx.Structure.RootLegIds)
            {
                MissionLeg leg = ctx.Leg(rootId);
                if (leg == null) continue;
                Recording rec = ctx.Rec(rootId);
                // Location biome slot = the launch-site name when launched from a site, else
                // the start biome; a leg that began in orbit reads "<body> orbit".
                string location = StructureLocationFormatter.Missing;
                if (rec != null)
                {
                    if (!string.IsNullOrEmpty(rec.LaunchSiteName))
                        location = StructureLocationFormatter.BodyBiome(rec.StartBodyName, rec.LaunchSiteName);
                    else if (string.Equals(rec.StartSituation, "Orbiting", StringComparison.Ordinal))
                        location = StructureLocationFormatter.Orbit(rec.StartBodyName);
                    else
                        location = StructureLocationFormatter.BodyBiome(rec.StartBodyName, rec.StartBiome);
                }
                steps.Add(new StructureStep
                {
                    UT = leg.StartUT,
                    Kind = StructureStepKind.Launch,
                    Label = !string.IsNullOrEmpty(leg.EvaCrewName) ? "EVA " + leg.EvaCrewName : "Launch",
                    Location = location,
                    VesselName = LegLabel(leg),
                    RecordingId = rootId
                });
            }
        }

        // Collects the staging-type part events a reader should see, in RecordingId order so
        // the surviving duplicate is stable across save/load (Dictionary enumeration order
        // is not). Drops: every part event on a debris recording (a debris piece's own
        // breakup is not the mission's staging), seeds (state re-statements at a continuing
        // segment's start, or a root's start-UT jettison seed), the port / pod coupling
        // events of a Dock / Undock / Board, and cross-recording duplicates of one physical
        // event (UT-tolerant: a same-(pid, kind) event FAR apart is a distinct staging of a
        // craft-baked PID, e.g. a Re-Fly fork, and survives).
        private static List<CandidateEvent> CollectCandidateEvents(BuildContext ctx)
        {
            var result = new List<CandidateEvent>();
            if (ctx.Tree.Recordings == null) return result;

            var mergeBps = new List<BranchPoint>();
            if (ctx.Tree.BranchPoints != null)
                foreach (BranchPoint bp in ctx.Tree.BranchPoints)
                    if (bp != null && (bp.Type == BranchPointType.Dock
                                       || bp.Type == BranchPointType.Undock
                                       || bp.Type == BranchPointType.Board))
                        mergeBps.Add(bp);

            var seenUts = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            var orderedRecs = new List<Recording>(ctx.Tree.Recordings.Values);
            orderedRecs.Sort((a, b) => string.CompareOrdinal(a?.RecordingId, b?.RecordingId));
            foreach (Recording rec in orderedRecs)
            {
                if (rec?.PartEvents == null) continue;
                bool continuation = ctx.IsContinuation(rec);
                foreach (PartEvent pe in rec.PartEvents)
                {
                    if (!IsStagingEvent(pe.eventType)) continue;
                    if (rec.IsDebris)
                    {
                        ctx.SkippedDebris++;
                        continue;
                    }
                    if (IsSeed(ctx, rec, pe, continuation))
                    {
                        ctx.SkippedSeeds++;
                        continue;
                    }
                    if (IsDockCoupling(mergeBps, rec, pe))
                    {
                        ctx.SkippedDockCoupling++;
                        continue;
                    }

                    string key = (int)pe.eventType + "|" + pe.partPersistentId.ToString(CultureInfo.InvariantCulture);
                    if (!seenUts.TryGetValue(key, out List<double> uts))
                    {
                        uts = new List<double>();
                        seenUts[key] = uts;
                    }
                    bool duplicate = false;
                    for (int u = 0; u < uts.Count; u++)
                    {
                        if (Math.Abs(uts[u] - pe.ut) <= StagingDedupToleranceSeconds)
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (duplicate)
                    {
                        ctx.SkippedDuplicates++;
                        continue;
                    }
                    uts.Add(pe.ut);
                    result.Add(new CandidateEvent { Rec = rec, Event = pe });
                }
            }
            return result;
        }

        internal static bool IsSeedEvent(Recording rec, PartEvent pe, bool continuation)
        {
            if (rec == null) return false;
            double sinceStart = pe.ut - rec.StartUT;
            if (continuation)
                return Math.Abs(sinceStart) <= ContinuationSeedEpsilonSeconds;
            return pe.eventType != PartEventType.Decoupled
                   && Math.Abs(sinceStart) <= RootSeedSeconds;
        }

        // Seed = at the continuing segment's exact start, a root's start-UT jettison, or a
        // re-statement of a state an ancestor already recorded for the same part (the
        // background recorder's loaded-physics seed lands a fraction of a second after the
        // split, and only this lineage check tells it from a real jettison).
        private static bool IsSeed(BuildContext ctx, Recording rec, PartEvent pe, bool continuation)
        {
            if (IsSeedEvent(rec, pe, continuation)) return true;
            return continuation && ctx.AncestorAlreadyHad(rec, pe);
        }

        private static bool IsDockCoupling(List<BranchPoint> mergeBps, Recording rec, PartEvent pe)
        {
            for (int i = 0; i < mergeBps.Count; i++)
            {
                BranchPoint bp = mergeBps[i];
                if (Math.Abs(pe.ut - bp.UT) > DockEventPartWindowSeconds) continue;
                if (Contains(bp.ParentRecordingIds, rec.RecordingId)
                    || Contains(bp.ChildRecordingIds, rec.RecordingId))
                    return true;
            }
            return false;
        }

        private static bool IsSplitBranch(BranchPoint bp)
            => bp != null && (bp.Type == BranchPointType.JointBreak || bp.Type == BranchPointType.Breakup);

        private static double StageMomentSeconds(BranchPoint bp)
            => bp.CoalesceWindow > 0 ? bp.CoalesceWindow : DefaultStageMomentSeconds;

        // Each candidate joins the nearest split branch point that covers it: one on its own
        // recording within that branch point's coalesce window (the recorder grouped exactly
        // these into it), or the branch point whose stored decoupler PID it is. A branch
        // point is one physics moment, so two ripple stages half a second apart stay two
        // branch points and two rows.
        private static void AssignToStages(BuildContext ctx, List<CandidateEvent> candidates,
            Dictionary<BranchPoint, List<CandidateEvent>> absorbed, List<CandidateEvent> loose)
        {
            var splits = new List<BranchPoint>();
            if (ctx.Tree.BranchPoints != null)
                foreach (BranchPoint bp in ctx.Tree.BranchPoints)
                    if (IsSplitBranch(bp)) splits.Add(bp);

            foreach (CandidateEvent c in candidates)
            {
                BranchPoint best = null;
                double bestDt = double.MaxValue;
                for (int i = 0; i < splits.Count; i++)
                {
                    BranchPoint bp = splits[i];
                    double dt = Math.Abs(c.Event.ut - bp.UT);
                    bool onOwner = Contains(bp.ParentRecordingIds, c.Rec.RecordingId)
                                   && dt <= StageMomentSeconds(bp);
                    bool ownDecoupler = c.Event.eventType == PartEventType.Decoupled
                                        && bp.DecouplerPartId != 0
                                        && bp.DecouplerPartId == c.Event.partPersistentId
                                        && dt <= StagingDedupToleranceSeconds;
                    if (!onOwner && !ownDecoupler) continue;
                    if (dt < bestDt
                        || (dt == bestDt && string.CompareOrdinal(bp.Id, best?.Id) < 0))
                    {
                        best = bp;
                        bestDt = dt;
                    }
                }
                if (best == null)
                {
                    loose.Add(c);
                    continue;
                }
                if (!absorbed.TryGetValue(best, out List<CandidateEvent> list))
                {
                    list = new List<CandidateEvent>();
                    absorbed[best] = list;
                }
                list.Add(c);
                ctx.AbsorbedCount++;
            }
        }

        private static void AddBranchPointSteps(List<StructureStep> steps, BuildContext ctx,
            Dictionary<BranchPoint, List<CandidateEvent>> absorbed)
        {
            if (ctx.Tree.BranchPoints == null) return;
            foreach (BranchPoint bp in ctx.Tree.BranchPoints)
            {
                if (bp == null) continue;
                // Launch is the root step; VesselSwitchContinuation is an observation
                // boundary, not a physical event; Terminal is surfaced via the leg pass.
                if (bp.Type == BranchPointType.Launch
                    || bp.Type == BranchPointType.VesselSwitchContinuation
                    || bp.Type == BranchPointType.Terminal)
                    continue;

                absorbed.TryGetValue(bp, out List<CandidateEvent> parts);
                if (IsSplitBranch(bp))
                    AddSplitStep(steps, ctx, bp, parts);
                else
                    AddOtherBranchStep(steps, ctx, bp);
            }
        }

        // A split: with a controlled child it is a Separation naming the piece that left;
        // debris-only it is ONE stage row naming the parts that let go.
        private static void AddSplitStep(List<StructureStep> steps, BuildContext ctx,
            BranchPoint bp, List<CandidateEvent> parts)
        {
            string parentId = FirstInTree(bp.ParentRecordingIds, ctx);
            string ownerId = FirstControlled(bp.ParentRecordingIds, ctx.Structure) ?? parentId;
            Recording owner = ctx.Rec(ownerId);
            string cause = bp.SplitCause ?? bp.BreakupCause;
            string eventWord = MissionCompositionBuilder.BranchEventName(bp.Type, cause);

            var controlledChildren = new List<string>();
            if (bp.ChildRecordingIds != null)
                foreach (string id in bp.ChildRecordingIds)
                    if (id != null && ctx.Structure.LegsById.ContainsKey(id))
                        controlledChildren.Add(id);

            if (controlledChildren.Count > 0)
            {
                var names = new List<string>();
                foreach (string id in controlledChildren)
                {
                    string n = ctx.VesselOf(id);
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                }
                // Location: the child began AT the event, so its captured start context is
                // the event's; a child that captured nothing falls back to the owner, which
                // only keeps its body once past its own start.
                Recording child = ctx.Rec(controlledChildren[0]);
                string location = StructureLocationFormatter.HasStartContext(child)
                    ? StructureLocationFormatter.AtRecording(child, child.StartUT)
                    : StructureLocationFormatter.AtRecording(owner, bp.UT);
                string full = names.Count > 0
                    ? eventWord + " (" + string.Join(", ", names.ToArray()) + ")"
                    : eventWord;
                string shortLabel = names.Count > 0
                    ? ShortenList(eventWord, names)
                    : eventWord;
                steps.Add(new StructureStep
                {
                    UT = bp.UT,
                    Kind = StructureStepKind.Separation,
                    Label = shortLabel,
                    Tooltip = shortLabel == full ? null : full,
                    Location = location,
                    VesselName = ownerId != null ? ctx.VesselOf(ownerId) : "",
                    RecordingId = ownerId
                });
                return;
            }

            // Debris-only: the stage.
            var decouplers = new List<CandidateEvent>();
            if (parts != null)
            {
                var seenPids = new HashSet<uint>();
                foreach (CandidateEvent c in parts)
                    if (c.Event.eventType == PartEventType.Decoupled && seenPids.Add(c.Event.partPersistentId))
                        decouplers.Add(c);
            }
            int pieces = bp.DebrisCount > 0
                ? bp.DebrisCount
                : (bp.ChildRecordingIds != null && bp.ChildRecordingIds.Count > 0
                    ? bp.ChildRecordingIds.Count
                    : decouplers.Count);
            List<string> groups = TitleGroups(ctx, decouplers);
            bool isStaging = cause == null || string.Equals(cause, "DECOUPLE", StringComparison.Ordinal);
            string head = isStaging ? "Staged" : eventWord;
            if (pieces > 0) head += ": " + FormatPieces(pieces);
            string fullLabel = groups.Count > 0 ? head + " (" + string.Join(", ", groups.ToArray()) + ")" : head;
            string label = groups.Count > 0 ? ShortenList(head, groups) : head;
            steps.Add(new StructureStep
            {
                UT = bp.UT,
                Kind = isStaging ? StructureStepKind.Staging : StructureStepKind.Separation,
                Label = label,
                Tooltip = label == fullLabel ? null : fullLabel,
                Location = StructureLocationFormatter.AtRecording(owner, bp.UT),
                VesselName = ownerId != null ? ctx.VesselOf(ownerId) : "",
                RecordingId = ownerId,
                SortPid = bp.DecouplerPartId
            });
        }

        // Dock / Undock / Board / EVA / placed-part rows.
        private static void AddOtherBranchStep(List<StructureStep> steps, BuildContext ctx, BranchPoint bp)
        {
            // Vessel name = the acting / continuing vessel (parent first); location = the
            // event-coincident recording (the CHILD branch created at the event, whose
            // captured start context IS the event's).
            string vesselId = FirstControlled(bp.ParentRecordingIds, ctx.Structure)
                ?? FirstControlled(bp.ChildRecordingIds, ctx.Structure);
            string locId = FirstControlled(bp.ChildRecordingIds, ctx.Structure)
                ?? FirstControlled(bp.ParentRecordingIds, ctx.Structure);
            string cause = bp.SplitCause ?? bp.BreakupCause;
            string eventWord = MissionCompositionBuilder.BranchEventName(bp.Type, cause);

            string partner = null;
            if (bp.Type == BranchPointType.Dock)
                partner = DescribeMergePartner(ctx, bp, vesselId);
            else if (bp.Type == BranchPointType.Undock)
                partner = DescribeUndockPartner(ctx, bp);

            Recording locRec = ctx.Rec(locId);
            string location = locRec != null && StructureLocationFormatter.HasStartContext(locRec)
                && Contains(bp.ChildRecordingIds, locId)
                ? StructureLocationFormatter.AtRecording(locRec, locRec.StartUT)
                : StructureLocationFormatter.AtRecording(locRec, bp.UT);

            string full = string.IsNullOrEmpty(partner) ? eventWord : eventWord + " (" + partner + ")";
            string label = full.Length <= EventCellCharBudget ? full : eventWord + " (...)";
            steps.Add(new StructureStep
            {
                UT = bp.UT,
                Kind = ClassifyBranch(bp.Type),
                Label = label,
                Tooltip = label == full ? null : full,
                Location = location,
                VesselName = vesselId != null ? ctx.VesselOf(vesselId) : "",
                RecordingId = vesselId
            });
        }

        // The other side of a dock: a two-parent same-tree dock names the other parent;
        // otherwise the caller's resolver (the dock-event graph) answers, with the partner's
        // mission when it lives in another tree.
        private static string DescribeMergePartner(BuildContext ctx, BranchPoint bp, string viewerId)
        {
            if (bp.ParentRecordingIds != null && bp.ParentRecordingIds.Count == 2)
            {
                string a = bp.ParentRecordingIds[0];
                string b = bp.ParentRecordingIds[1];
                string other = string.Equals(a, viewerId, StringComparison.Ordinal) ? b
                    : string.Equals(b, viewerId, StringComparison.Ordinal) ? a : null;
                if (other != null && ctx.Rec(other) != null)
                    return ctx.VesselOf(other);
            }
            if (ctx.MergePartnerResolver == null) return null;
            try
            {
                string text = ctx.MergePartnerResolver(bp, viewerId);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The other side of a Dock / Board branch point read off the dock-event graph:
        /// "Kerbal X (mission 'Kerbal X')" when the partner lives in another tree, the bare
        /// vessel name when it is inside this mission (<paramref name="treeId"/>), null when
        /// the graph cannot name it. A viewer that is not a participant falls back to the
        /// single parent. Pure over its parameters (the window passes the cached graph and
        /// the Missions tab's mission-name resolver).
        /// </summary>
        internal static string DescribeMergePartnerFromGraph(
            DockEventGraph graph, string treeId, BranchPoint bp, string viewerId,
            Func<string, string, string> missionNameResolver)
        {
            if (graph == null || bp == null || string.IsNullOrEmpty(bp.Id)) return null;
            if (!DockEventGraph.TryDescribePartner(
                    graph, bp.Id, viewerId, missionNameResolver, out DockPartnerDescription d))
            {
                if (bp.ParentRecordingIds == null || bp.ParentRecordingIds.Count != 1
                    || !DockEventGraph.TryDescribePartner(
                        graph, bp.Id, bp.ParentRecordingIds[0], missionNameResolver, out d))
                    return null;
            }
            bool sameTree = string.Equals(d.PartnerTreeId, treeId, StringComparison.Ordinal);
            return MissionChapters.FormatPartnerWithMission(
                d.PartnerVesselName, sameTree ? null : d.PartnerMissionName);
        }

        // The vessel(s) that left at an undock: the children that are NOT the parent's own
        // vessel continuing (same persistent id), named like the Missions vessel phrase.
        private static string DescribeUndockPartner(BuildContext ctx, BranchPoint bp)
        {
            if (bp.ChildRecordingIds == null || bp.ChildRecordingIds.Count == 0) return null;
            var parentPids = new HashSet<uint>();
            if (bp.ParentRecordingIds != null)
                foreach (string p in bp.ParentRecordingIds)
                {
                    Recording pr = ctx.Rec(p);
                    if (pr != null && pr.VesselPersistentId != 0) parentPids.Add(pr.VesselPersistentId);
                }
            var names = new List<string>();
            var firstSkipped = false;
            foreach (string id in bp.ChildRecordingIds)
            {
                Recording c = ctx.Rec(id);
                if (c == null || c.IsDebris) continue;
                bool continues = c.VesselPersistentId != 0 && parentPids.Contains(c.VesselPersistentId);
                if (parentPids.Count == 0 && !firstSkipped)
                {
                    // No identity to compare: the first child is taken as the continuing side.
                    firstSkipped = true;
                    continue;
                }
                if (continues) continue;
                string n = ctx.VesselOf(id);
                if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            }
            return names.Count == 0 ? null : string.Join(", ", names.ToArray());
        }

        // Part events no branch point covers (a fairing deploy, a decouple the recorder
        // grouped into no branch point), grouped per recording and physics moment.
        private static void AddLooseStagingSteps(List<StructureStep> steps, BuildContext ctx,
            List<CandidateEvent> loose)
        {
            loose.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Rec.RecordingId, b.Rec.RecordingId);
                if (c != 0) return c;
                c = a.Event.ut.CompareTo(b.Event.ut);
                return c != 0 ? c : a.Event.partPersistentId.CompareTo(b.Event.partPersistentId);
            });
            int i = 0;
            while (i < loose.Count)
            {
                CandidateEvent headEvt = loose[i];
                int j = i + 1;
                while (j < loose.Count
                       && string.Equals(loose[j].Rec.RecordingId, headEvt.Rec.RecordingId, StringComparison.Ordinal)
                       && loose[j].Event.ut - headEvt.Event.ut <= DefaultStageMomentSeconds)
                    j++;
                var group = loose.GetRange(i, j - i);
                steps.Add(BuildLooseStep(ctx, group));
                i = j;
            }
        }

        private static StructureStep BuildLooseStep(BuildContext ctx, List<CandidateEvent> group)
        {
            var decouplers = new List<CandidateEvent>();
            var jettisons = new List<CandidateEvent>();
            bool anyShroud = false, anyFairing = false;
            foreach (CandidateEvent c in group)
            {
                if (c.Event.eventType == PartEventType.Decoupled) decouplers.Add(c);
                else
                {
                    jettisons.Add(c);
                    if (c.Event.eventType == PartEventType.ShroudJettisoned) anyShroud = true;
                    else anyFairing = true;
                }
            }
            string head;
            List<string> groups;
            if (decouplers.Count > 0)
            {
                head = "Staged: " + FormatPieces(decouplers.Count);
                groups = TitleGroups(ctx, decouplers);
            }
            else
            {
                head = anyShroud && anyFairing ? "Jettisoned"
                    : anyShroud ? "Shroud jettisoned" : "Fairing jettisoned";
                groups = TitleGroups(ctx, jettisons);
            }
            string full = groups.Count > 0 ? head + " (" + string.Join(", ", groups.ToArray()) + ")" : head;
            string label = groups.Count > 0 ? ShortenList(head, groups) : head;
            Recording rec = group[0].Rec;
            return new StructureStep
            {
                UT = group[0].Event.ut,
                Kind = StructureStepKind.Staging,
                Label = label,
                Tooltip = label == full ? null : full,
                Location = StructureLocationFormatter.AtRecording(rec, group[0].Event.ut),
                VesselName = ctx.VesselOf(rec.RecordingId),
                RecordingId = rec.RecordingId,
                SortPid = group[0].Event.partPersistentId
            };
        }

        private static void AddTerminalSteps(List<StructureStep> steps, BuildContext ctx)
        {
            foreach (MissionLeg leg in ctx.Structure.LegsById.Values)
            {
                if (!leg.TerminalStateValue.HasValue) continue;
                TerminalState term = leg.TerminalStateValue.Value;
                // A leg that ended by joining another already has its Docked / Boarded row.
                if (term == TerminalState.Docked || term == TerminalState.Boarded)
                {
                    ctx.SkippedMergedEnds++;
                    continue;
                }
                Recording rec = ctx.Rec(leg.RecordingId);
                steps.Add(new StructureStep
                {
                    UT = leg.EndUT,
                    Kind = StructureStepKind.Terminal,
                    Label = FormatEndLabel(term),
                    Location = TerminalLocation(rec, term),
                    VesselName = LegLabel(leg),
                    RecordingId = leg.RecordingId
                });
            }
        }

        /// <summary>"End: Orbiting" - the terminal word the Missions End column reads.</summary>
        internal static string FormatEndLabel(TerminalState? terminal)
        {
            string word = MissionCompositionBuilder.TerminalName(terminal);
            return string.IsNullOrEmpty(word) ? "End" : "End: " + word;
        }

        // An orbital ending reads "<body> orbit"; any other ending the recorded end biome.
        internal static string TerminalLocation(Recording rec, TerminalState terminal)
        {
            if (rec == null) return StructureLocationFormatter.Missing;
            string body = !string.IsNullOrEmpty(rec.TerminalOrbitBody) ? rec.TerminalOrbitBody : rec.StartBodyName;
            if (terminal == TerminalState.Orbiting)
                return StructureLocationFormatter.Orbit(body);
            return StructureLocationFormatter.BodyBiome(body, rec.EndBiome);
        }

        // ------------------------------------------------------------------
        // Label helpers
        // ------------------------------------------------------------------

        internal static string FormatPieces(int n)
            => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " piece" : " pieces");

        /// <summary>
        /// One "title xN" entry per distinct part title, in title order; "xN" only when
        /// N &gt; 1. Pure over the supplied titles.
        /// </summary>
        internal static List<string> FormatTitleGroups(IEnumerable<string> titles)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (string t in titles)
            {
                string key = t ?? "";
                if (!counts.ContainsKey(key))
                {
                    counts[key] = 0;
                    order.Add(key);
                }
                counts[key]++;
            }
            order.Sort(StringComparer.Ordinal);
            var result = new List<string>(order.Count);
            foreach (string t in order)
                result.Add(counts[t] > 1
                    ? t + " x" + counts[t].ToString(CultureInfo.InvariantCulture)
                    : t);
            return result;
        }

        private static List<string> TitleGroups(BuildContext ctx, List<CandidateEvent> events)
        {
            var titles = new List<string>(events.Count);
            foreach (CandidateEvent c in events)
                titles.Add(ctx.Title(c.Event.partName));
            return FormatTitleGroups(titles);
        }

        /// <summary>
        /// "head (a, b)" when that fits <see cref="EventCellCharBudget"/>, else as many
        /// leading entries as fit followed by "...", else "head (...)". The caller keeps
        /// the full text for the tooltip.
        /// </summary>
        internal static string ShortenList(string head, List<string> entries)
        {
            if (entries == null || entries.Count == 0) return head ?? "";
            string full = head + " (" + string.Join(", ", entries.ToArray()) + ")";
            if (full.Length <= EventCellCharBudget) return full;
            for (int keep = entries.Count - 1; keep >= 1; keep--)
            {
                string candidate = head + " (" + string.Join(", ", entries.GetRange(0, keep).ToArray()) + ", ...)";
                if (candidate.Length <= EventCellCharBudget) return candidate;
            }
            return head + " (...)";
        }

        private static string LegLabel(MissionLeg leg)
        {
            if (leg == null) return "";
            if (!string.IsNullOrEmpty(leg.EvaCrewName)) return leg.EvaCrewName;
            return string.IsNullOrEmpty(leg.VesselName) ? "(vessel)" : leg.VesselName;
        }

        // First recording id in the list that is a controlled leg (debris is not a leg).
        private static string FirstControlled(List<string> ids, MissionStructure structure)
        {
            if (ids == null) return null;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] != null && structure.LegsById.ContainsKey(ids[i]))
                    return ids[i];
            return null;
        }

        private static string FirstInTree(List<string> ids, BuildContext ctx)
        {
            if (ids == null) return null;
            for (int i = 0; i < ids.Count; i++)
                if (ctx.Rec(ids[i]) != null) return ids[i];
            return null;
        }

        private static bool Contains(List<string> ids, string id)
        {
            if (ids == null || id == null) return false;
            for (int i = 0; i < ids.Count; i++)
                if (string.Equals(ids[i], id, StringComparison.Ordinal)) return true;
            return false;
        }

        private static StructureStepKind ClassifyBranch(BranchPointType t)
        {
            switch (t)
            {
                case BranchPointType.Undock: return StructureStepKind.Undock;
                case BranchPointType.Dock: return StructureStepKind.Dock;
                case BranchPointType.Board: return StructureStepKind.Dock;
                case BranchPointType.EVA: return StructureStepKind.Eva;
                case BranchPointType.GroundPartPlaced: return StructureStepKind.Eva;
                default: return StructureStepKind.Separation; // JointBreak / Breakup
            }
        }

        private static bool IsStagingEvent(PartEventType t)
            => t == PartEventType.Decoupled
            || t == PartEventType.FairingJettisoned
            || t == PartEventType.ShroudJettisoned;

        // Two rows are the "same simultaneous batch" when everything visible is identical,
        // they concern the same recording, and they fall within a tight time window.
        // Same-frame separations share a recorded UT and cross-recording samples of one
        // frame differ by well under 0.1s, so 0.25s absorbs all real jitter while NOT
        // merging quick-succession ripple staging (e.g. booster pairs dropped half a second
        // apart are distinct stages, not one batch).
        private const double SimultaneousWindowSeconds = 0.25;

        // Cross-recording staging dedup tolerance: the same physical event recorded on two
        // member recordings lands within this window; a same-(pid,kind) event further apart
        // is a distinct staging (Re-Fly fork of the same craft-baked PID) and survives.
        private const double StagingDedupToleranceSeconds = 5.0;

        // Collapses runs of identical simultaneous rows (the already-sorted list groups them
        // adjacently) into one row, appending " xN" to the label. Compares each candidate to
        // the batch HEAD so a slow drift cannot chain unrelated events together.
        private static List<StructureStep> CollapseSimultaneous(List<StructureStep> steps)
        {
            if (steps.Count < 2) return steps;
            var result = new List<StructureStep>(steps.Count);
            int i = 0;
            while (i < steps.Count)
            {
                StructureStep head = steps[i];
                int count = 1;
                int j = i + 1;
                while (j < steps.Count && IsSameBatch(head, steps[j]))
                {
                    count++;
                    j++;
                }
                if (count > 1)
                {
                    head.Label = FormatCollapsedLabel(head.Label, count);
                    if (head.Tooltip != null)
                        head.Tooltip = FormatCollapsedLabel(head.Tooltip, count);
                }
                result.Add(head);
                i = j;
            }
            return result;
        }

        /// <summary>
        /// The label a run of simultaneous identical rows collapses to:
        /// <c>"Fairing jettisoned x2"</c>. Extracted from the collapse walk so a caller that
        /// needs the SPELLING without running the walk reaches this rather than a copy.
        /// </summary>
        internal static string FormatCollapsedLabel(string label, int count)
        {
            return (label ?? "") + " x" + count.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool IsSameBatch(StructureStep a, StructureStep b)
        {
            return a.Kind == b.Kind
                && Math.Abs(a.UT - b.UT) <= SimultaneousWindowSeconds
                && string.Equals(a.RecordingId, b.RecordingId, StringComparison.Ordinal)
                && string.Equals(a.Label, b.Label, StringComparison.Ordinal)
                && string.Equals(a.Location, b.Location, StringComparison.Ordinal)
                && string.Equals(a.VesselName, b.VesselName, StringComparison.Ordinal);
        }

        private static int CountKind(List<StructureStep> steps, StructureStepKind kind)
        {
            int n = 0;
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Kind == kind) n++;
            return n;
        }

        // Total, deterministic ordering: UT, then kind, then vessel, then label, then
        // recording, then PID. NaN UTs (none on the mission path) sort last.
        private static int CompareStep(StructureStep a, StructureStep b)
        {
            int c = a.UT.CompareTo(b.UT);
            if (c != 0) return c;
            c = ((int)a.Kind).CompareTo((int)b.Kind);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.VesselName ?? "", b.VesselName ?? "");
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Label ?? "", b.Label ?? "");
            if (c != 0) return c;
            c = string.CompareOrdinal(a.RecordingId ?? "", b.RecordingId ?? "");
            if (c != 0) return c;
            return a.SortPid.CompareTo(b.SortPid);
        }
    }
}
