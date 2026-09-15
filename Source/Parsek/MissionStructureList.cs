using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Pure read model + mission builder for the structure-list window (roadmap.md
    // Phase 13 Tier-1; docs/dev/plan-structure-list-window.md). A structure list is a
    // flat, chronological "what happened, step by step" view of one run, complementing
    // the Missions tab's composition-over-time tree. The builder is pure (no Unity
    // calls, no shared mutable state, no recording mutation) and reads ONLY
    // already-recorded data, so it is headless-testable. The route side of the window
    // is the Logistics-side RouteStructureListBuilder: Missions code must not reference
    // Logistics, so the route-reading builder lives with the routes it reads.

    internal enum StructureStepKind
    {
        Launch     = 0,
        Staging    = 1,  // separation that drops debris (no controlled-leg branch point)
        Separation = 2,  // decouple / breakup that produces a controlled child leg
        Dock       = 3,
        Undock     = 4,
        Eva        = 5,
        Origin     = 6,  // route only
        Delivery   = 7,  // route only
        Stop       = 8,  // route only (reserved for multi-stop)
        Terminal   = 9
    }

    /// <summary>One row of a structure list: a single event with its time, status and location.</summary>
    internal struct StructureStep
    {
        public double UT;            // event time; NaN for the route Origin pseudo-step (rendered first)
        public StructureStepKind Kind;
        public string Label;         // "Launch", "Decoupled booster", "Dock", "Deliver (50 LiquidFuel)"
        public string Status;        // vessel situation: "Prelaunch", "Flying", "Orbiting", "Landed", ...
        public string Location;      // always "SOI/body, biome" order: "Kerbin, LaunchPad", "Mun, Midlands"
        public string VesselName;    // the controlled vessel / piece this step concerns (may be empty)
        public uint SortPid;         // staging part PID, for a deterministic tiebreak only (not rendered)
    }

    /// <summary>
    /// Pure location text helpers shared by the mission builder and the Logistics-side
    /// route builder, which formats its route endpoints on top of these. Reuses the
    /// recordings-table formatters where they already format a recording's start/end
    /// position so the wording matches the Recordings tab. All numeric output uses
    /// InvariantCulture.
    /// </summary>
    internal static class StructureLocationFormatter
    {
        // Canonical location text: ALWAYS "SOI/body, biome" order (body first, biome
        // second). Either part may be empty. "-" when nothing is recorded.
        internal static string BodyBiome(string body, string biome)
        {
            bool hasBody = !string.IsNullOrEmpty(body);
            bool hasBiome = !string.IsNullOrEmpty(biome);
            if (hasBody && hasBiome) return body + ", " + biome;
            if (hasBody) return body;
            if (hasBiome) return biome;
            return "-";
        }

        // Mid-flight event: body + biome from the supplied recording's START context. This
        // is event-accurate for BRANCH events (the child branch's recording starts AT the
        // split / merge), but only start-accurate for part events; the staging emit site
        // gates on event-to-start freshness before using it. Per-UT exact coordinate
        // resolution is still deferred.
        internal static string MidLocation(Recording rec)
            => rec == null ? "" : BodyBiome(rec.StartBodyName, rec.StartBiome);

        // The vessel situation at the event (already humanized: "Flying", "Orbiting", ...).
        internal static string MidStatus(Recording rec)
            => rec != null && !string.IsNullOrEmpty(rec.StartSituation) ? rec.StartSituation : "";
    }

    internal static class MissionStructureListBuilder
    {
        // Silences the per-build Verbose summary for callers that rebuild as a pure
        // derivation (mirrors MissionStructureBuilder.SuppressLogging). Defaults to off.
        internal static bool SuppressLogging;

        /// <summary>
        /// Flattens a mission tree into a UT-ordered step list: launch(es), branch-point
        /// events (dock / undock / decouple / eva / breakup), debris-staging part events,
        /// and terminals. Pure. Takes the already-built <paramref name="structure"/> so
        /// the window passes its cached structure without a rebuild.
        /// </summary>
        internal static List<StructureStep> Build(RecordingTree tree, MissionStructure structure)
        {
            var steps = new List<StructureStep>();
            if (tree == null || structure == null || structure.LegsById.Count == 0)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("Mission",
                        $"BuildStructureList: empty tree={tree?.Id ?? "<null>"}");
                return steps;
            }

            Recording Rec(string id) =>
                id != null && tree.Recordings != null && tree.Recordings.TryGetValue(id, out var r) ? r : null;

            // 1. Launch: one per root leg.
            AddLaunchSteps(steps, structure, Rec);

            // 2. Branch-point events. Collect decoupler PIDs handled here so the staging
            //    pass can dedup the Decoupled PartEvent that mirrors a controlled split.
            var handledDecouplerPids = new HashSet<uint>();
            AddBranchPointSteps(steps, tree, structure, Rec, handledDecouplerPids);

            // 3. Staging part events across all member recordings.
            AddStagingSteps(steps, tree, handledDecouplerPids);

            // 4. Terminal: one per controlled leg that ends in a terminal state.
            AddTerminalSteps(steps, structure, Rec);

            // 5. Deterministic chronological sort.
            steps.Sort(CompareStep);

            // 6. Collapse simultaneous identical events into one "xN" row (e.g. several engine
            //    shrouds or radial decouplers separating in the same frame), so a big stack
            //    does not list "Shroud jettisoned" a dozen times.
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
                    $"terminal={CountKind(steps, StructureStepKind.Terminal)}");
            return steps;
        }

        /// <summary>
        /// Phase 1: emits one Launch step per root leg. Extracted verbatim from Build.
        /// </summary>
        private static void AddLaunchSteps(
            List<StructureStep> steps, MissionStructure structure, Func<string, Recording> Rec)
        {
            foreach (var rootId in structure.RootLegIds)
            {
                if (!structure.LegsById.TryGetValue(rootId, out MissionLeg leg))
                    continue;
                Recording rec = Rec(rootId);
                // Location biome slot = the launch-site name when launched from a site, else
                // the start biome. Status = the start situation (usually "Prelaunch").
                string launchBiome = rec == null ? null
                    : (!string.IsNullOrEmpty(rec.LaunchSiteName) ? rec.LaunchSiteName : rec.StartBiome);
                steps.Add(new StructureStep
                {
                    UT = leg.StartUT,
                    Kind = StructureStepKind.Launch,
                    Label = !string.IsNullOrEmpty(leg.EvaCrewName) ? "EVA " + leg.EvaCrewName : "Launch",
                    Status = rec != null ? StructureLocationFormatter.MidStatus(rec) : "",
                    Location = rec != null ? StructureLocationFormatter.BodyBiome(rec.StartBodyName, launchBiome) : "",
                    VesselName = LegLabel(leg)
                });
            }
        }

        /// <summary>
        /// Phase 2: emits branch-point event steps and records the handled decoupler PIDs
        /// (mutating <paramref name="handledDecouplerPids"/>) so the staging pass can dedup
        /// the mirrored Decoupled PartEvent. Extracted verbatim from Build.
        /// </summary>
        private static void AddBranchPointSteps(
            List<StructureStep> steps,
            RecordingTree tree,
            MissionStructure structure,
            Func<string, Recording> Rec,
            HashSet<uint> handledDecouplerPids)
        {
            if (tree.BranchPoints != null)
            {
                foreach (BranchPoint bp in tree.BranchPoints)
                {
                    if (bp == null) continue;
                    // Launch is the root step; VesselSwitchContinuation is an observation
                    // boundary, not a physical event; Terminal is surfaced via the leg pass.
                    if (bp.Type == BranchPointType.Launch
                        || bp.Type == BranchPointType.VesselSwitchContinuation
                        || bp.Type == BranchPointType.Terminal)
                        continue;

                    // Vessel name = the acting / continuing vessel (parent first); location =
                    // the event-coincident recording (the CHILD branch created at the event,
                    // whose captured start situation / biome / body IS the event context;
                    // parent's start is its earlier launch context, so it would mislabel
                    // biome). Fall back across each preference.
                    string vesselId = FirstControlled(bp.ParentRecordingIds, structure)
                        ?? FirstControlled(bp.ChildRecordingIds, structure);
                    string locId = FirstControlled(bp.ChildRecordingIds, structure)
                        ?? FirstControlled(bp.ParentRecordingIds, structure);
                    MissionLeg repLeg = vesselId != null && structure.LegsById.TryGetValue(vesselId, out MissionLeg l) ? l : null;
                    string cause = bp.SplitCause ?? bp.BreakupCause;

                    Recording locRec = Rec(locId);
                    steps.Add(new StructureStep
                    {
                        UT = bp.UT,
                        Kind = ClassifyBranch(bp.Type),
                        Label = MissionCompositionBuilder.BranchEventName(bp.Type, cause),
                        Status = StructureLocationFormatter.MidStatus(locRec),
                        Location = StructureLocationFormatter.MidLocation(locRec),
                        VesselName = repLeg != null ? LegLabel(repLeg) : ""
                    });

                    if (bp.DecouplerPartId != 0)
                        handledDecouplerPids.Add(bp.DecouplerPartId);
                }
            }
        }

        /// <summary>
        /// Phase 3: emits staging part-event steps across all member recordings, with the
        /// UT-tolerant cross-recording dedup and decoupler-PID drop. Extracted verbatim
        /// from Build.
        /// </summary>
        private static void AddStagingSteps(
            List<StructureStep> steps, RecordingTree tree, HashSet<uint> handledDecouplerPids)
        {
            // Decoupled events are
            // dropped when a controlled Separation branch point already covers the same
            // decoupler PID; fairing / shroud have no branch-point counterpart and pass
            // through. Cross-recording dedup is UT-TOLERANT, not UT-blind: the same
            // physical event recorded on more than one member recording carries the same
            // (pid, eventType) at NEARLY the same UT (sub-second recorder skew), so a
            // same-key event within the tolerance is a duplicate. A same-key event FAR
            // outside it is a genuinely DISTINCT staging of a craft-baked PID - e.g. a
            // Re-Fly fork of the same craft living in the same tree re-jettisoning its
            // fairing - and must survive (persistentId is craft-baked, NOT launch-unique).
            // Recordings iterate in RecordingId order so the surviving representative is
            // stable across save/load (Dictionary enumeration order is not).
            var seenStagingUts = new Dictionary<string, List<double>>();
            if (tree.Recordings != null)
            {
                var orderedRecs = new List<Recording>(tree.Recordings.Values);
                orderedRecs.Sort((a, b) => string.CompareOrdinal(a?.RecordingId, b?.RecordingId));
                foreach (Recording rec in orderedRecs)
                {
                    if (rec?.PartEvents == null) continue;
                    foreach (PartEvent pe in rec.PartEvents)
                    {
                        if (!IsStagingEvent(pe.eventType)) continue;
                        if (pe.eventType == PartEventType.Decoupled
                            && handledDecouplerPids.Contains(pe.partPersistentId))
                            continue;

                        string key = (int)pe.eventType + "|" + pe.partPersistentId.ToString(CultureInfo.InvariantCulture);
                        if (!seenStagingUts.TryGetValue(key, out List<double> uts))
                        {
                            uts = new List<double>();
                            seenStagingUts[key] = uts;
                        }
                        bool duplicate = false;
                        for (int u = 0; u < uts.Count; u++)
                        {
                            if (System.Math.Abs(uts[u] - pe.ut) <= StagingDedupToleranceSeconds)
                            {
                                duplicate = true;
                                break;
                            }
                        }
                        if (duplicate) continue;
                        uts.Add(pe.ut);

                        // Status / biome honesty: the owning recording's START context is only
                        // accurate near the recording start. A part event far into the segment
                        // (e.g. a fairing jettisoned mid-ascent on a pad-to-orbit recording)
                        // would wrongly read "Prelaunch / LaunchPad", so beyond the freshness
                        // window we keep only the segment-stable body and blank the rest
                        // (blank beats wrong; per-UT resolution is deferred).
                        bool contextFresh = pe.ut - rec.StartUT <= StagingContextMaxAgeSeconds;
                        steps.Add(new StructureStep
                        {
                            UT = pe.ut,
                            Kind = StructureStepKind.Staging,
                            Label = StagingLabel(pe),
                            Status = contextFresh ? StructureLocationFormatter.MidStatus(rec) : "",
                            Location = contextFresh
                                ? StructureLocationFormatter.MidLocation(rec)
                                : StructureLocationFormatter.BodyBiome(rec.StartBodyName, null),
                            VesselName = "",
                            SortPid = pe.partPersistentId
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Phase 4: emits one Terminal step per controlled leg that ends in a terminal
        /// state. Extracted verbatim from Build.
        /// </summary>
        private static void AddTerminalSteps(
            List<StructureStep> steps, MissionStructure structure, Func<string, Recording> Rec)
        {
            foreach (MissionLeg leg in structure.LegsById.Values)
            {
                if (!leg.TerminalStateValue.HasValue) continue;
                Recording rec = Rec(leg.RecordingId);
                // Body prefers the recorded terminal-orbit body, else the start body.
                string termBody = rec == null ? null
                    : (!string.IsNullOrEmpty(rec.TerminalOrbitBody) ? rec.TerminalOrbitBody : rec.StartBodyName);
                steps.Add(new StructureStep
                {
                    UT = leg.EndUT,
                    Kind = StructureStepKind.Terminal,
                    // Event = generic "End"; Status carries the terminal situation (Landed /
                    // Orbiting / Recovered / ...) so the two columns are not redundant.
                    Label = "End",
                    Status = MissionCompositionBuilder.TerminalName(leg.TerminalStateValue),
                    Location = rec != null ? StructureLocationFormatter.BodyBiome(termBody, rec.EndBiome) : "",
                    VesselName = LegLabel(leg)
                });
            }
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

        private static StructureStepKind ClassifyBranch(BranchPointType t)
        {
            switch (t)
            {
                case BranchPointType.Undock: return StructureStepKind.Undock;
                case BranchPointType.Dock: return StructureStepKind.Dock;
                case BranchPointType.Board: return StructureStepKind.Dock;
                case BranchPointType.EVA: return StructureStepKind.Eva;
                default: return StructureStepKind.Separation; // JointBreak / Breakup
            }
        }

        private static bool IsStagingEvent(PartEventType t)
            => t == PartEventType.Decoupled
            || t == PartEventType.FairingJettisoned
            || t == PartEventType.ShroudJettisoned;

        // Two events are the "same simultaneous batch" when everything visible is identical
        // and they fall within a tight time window. Same-frame separations share a recorded
        // UT and cross-recording samples of one frame differ by well under 0.1s, so 0.25s
        // absorbs all real jitter while NOT merging quick-succession ripple staging (e.g.
        // booster pairs dropped half a second apart are distinct stages, not one batch).
        private const double SimultaneousWindowSeconds = 0.25;

        // Cross-recording staging dedup tolerance: the same physical event recorded on two
        // member recordings lands within this window; a same-(pid,kind) event further apart
        // is a distinct staging (Re-Fly fork of the same craft-baked PID) and survives.
        private const double StagingDedupToleranceSeconds = 5.0;

        // Staging Status/biome freshness: the owning recording's start-captured context is
        // trusted only this close to the recording start (see the honesty note at the
        // staging emit site).
        private const double StagingContextMaxAgeSeconds = 30.0;

        // Collapses runs of identical simultaneous events (the already-sorted list groups them
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
                    head.Label = (head.Label ?? "") + " x" + count.ToString(CultureInfo.InvariantCulture);
                result.Add(head);
                i = j;
            }
            return result;
        }

        private static bool IsSameBatch(StructureStep a, StructureStep b)
        {
            return a.Kind == b.Kind
                && System.Math.Abs(a.UT - b.UT) <= SimultaneousWindowSeconds
                && string.Equals(a.Label, b.Label, StringComparison.Ordinal)
                && string.Equals(a.Status, b.Status, StringComparison.Ordinal)
                && string.Equals(a.Location, b.Location, StringComparison.Ordinal)
                && string.Equals(a.VesselName, b.VesselName, StringComparison.Ordinal);
        }

        private static string StagingLabel(PartEvent pe)
        {
            string part = string.IsNullOrEmpty(pe.partName) ? "" : " " + pe.partName;
            switch (pe.eventType)
            {
                case PartEventType.FairingJettisoned: return "Fairing jettisoned";
                case PartEventType.ShroudJettisoned: return "Shroud jettisoned";
                default: return "Staged" + part; // Decoupled
            }
        }

        private static int CountKind(List<StructureStep> steps, StructureStepKind kind)
        {
            int n = 0;
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Kind == kind) n++;
            return n;
        }

        // Total, deterministic ordering: UT, then kind, then vessel, then label, then PID.
        // NaN UTs (none on the mission path) sort last via double.CompareTo.
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
            return a.SortPid.CompareTo(b.SortPid);
        }
    }
}
