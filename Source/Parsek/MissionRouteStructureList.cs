using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Pure read model + builders for the Mission / Route structure-list window
    // (roadmap.md Phase 13 Tier-1; docs/dev/plan-structure-list-window.md). A
    // structure list is a flat, chronological "what happened, step by step" view of
    // one run, complementing the Missions tab's composition-over-time tree. Both
    // builders are pure (no Unity calls, no shared mutable state, no recording
    // mutation) and read ONLY already-recorded data, so they are headless-testable.

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
    /// Pure location text helpers shared by both builders. Reuses the recordings-table
    /// formatters where they already format a recording's start/end position so the
    /// wording matches the Recordings tab. All numeric output uses InvariantCulture.
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

        // A route endpoint (origin / dock / delivery / undock). RouteEndpoint is a struct with
        // no backing Recording and NO recorded biome, so for a surface endpoint the biome is
        // resolved at DISPLAY time from body + lat/lon via the injected resolver (the window
        // passes VesselSpawner.TryResolveBiome; headless tests pass null). When no biome
        // resolves, fall back to the surface coordinates. KSC keeps "KSC" in the biome slot.
        internal static string EndpointLocation(
            RouteEndpoint ep, bool isKsc, Func<string, double, double, string> biomeResolver = null)
        {
            if (isKsc)
                return BodyBiome(string.IsNullOrEmpty(ep.BodyName) ? "Kerbin" : ep.BodyName, "KSC");
            if (string.IsNullOrEmpty(ep.BodyName))
                return "-";
            if (ep.IsSurface)
            {
                string biome = biomeResolver != null
                    ? biomeResolver(ep.BodyName, ep.Latitude, ep.Longitude)
                    : null;
                if (!string.IsNullOrEmpty(biome))
                    return BodyBiome(ep.BodyName, biome);
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} ({1:F2}, {2:F2})", ep.BodyName, ep.Latitude, ep.Longitude);
            }
            return ep.BodyName;
        }

        // The endpoint's situation for the Status column.
        internal static string EndpointStatus(RouteEndpoint ep, bool isKsc)
        {
            if (isKsc) return "Prelaunch";
            return ep.IsSurface ? "Landed" : "Orbiting";
        }
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
            // Staging part events across all member recordings. Decoupled events are
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

    internal static class RouteStructureListBuilder
    {
        internal static bool SuppressLogging;

        /// <summary>
        /// Builds a route's step list in logical/chronological order: origin, dock,
        /// delivery (per stop), undock. Pure. <paramref name="sourceLookup"/> resolves a
        /// recording id to its committed <see cref="Recording"/>, and
        /// <paramref name="biomeResolver"/> resolves (bodyName, lat, lon) to a biome name
        /// for surface endpoints (the window passes <c>VesselSpawner.TryResolveBiome</c>;
        /// null in headless tests falls back to coordinates). Both injected so the builder
        /// stays free of singletons / live KSP and is headless-testable.
        /// </summary>
        internal static List<StructureStep> Build(
            Logistics.Route route,
            Func<string, Recording> sourceLookup,
            Func<string, double, double, string> biomeResolver = null)
        {
            var steps = new List<StructureStep>();
            if (route == null)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("Route", "BuildStructureList: null route");
                return steps;
            }

            // Origin pseudo-step (no single UT; rendered first via NaN).
            steps.Add(new StructureStep
            {
                UT = double.NaN,
                Kind = StructureStepKind.Origin,
                Label = route.IsKscOrigin ? "Origin: KSC" : "Origin: depot",
                Status = StructureLocationFormatter.EndpointStatus(route.Origin, route.IsKscOrigin),
                Location = StructureLocationFormatter.EndpointLocation(route.Origin, route.IsKscOrigin, biomeResolver),
                VesselName = ""
            });

            // Connection window lives on the dock-member recording.
            RouteConnectionWindow win = null;
            Recording dockRec = sourceLookup != null && !string.IsNullOrEmpty(route.DockMemberRecordingId)
                ? sourceLookup(route.DockMemberRecordingId)
                : null;
            if (dockRec?.RouteConnectionWindows != null && dockRec.RouteConnectionWindows.Count > 0)
            {
                // Prefer the last COMPLETE window (the delivery binding; v0 dock members
                // carry exactly one), falling back to the last non-null window so a Dock
                // row still renders for a degenerate incomplete capture.
                for (int i = dockRec.RouteConnectionWindows.Count - 1; i >= 0; i--)
                {
                    RouteConnectionWindow w = dockRec.RouteConnectionWindows[i];
                    if (w != null && w.IsComplete)
                    {
                        win = w;
                        break;
                    }
                }
                if (win == null)
                {
                    for (int i = dockRec.RouteConnectionWindows.Count - 1; i >= 0; i--)
                    {
                        if (dockRec.RouteConnectionWindows[i] != null)
                        {
                            win = dockRec.RouteConnectionWindows[i];
                            break;
                        }
                    }
                }
            }

            bool hasEndpoint = win != null && win.EndpointAtDock.HasValue;
            string endpointLoc = hasEndpoint
                ? StructureLocationFormatter.EndpointLocation(win.EndpointAtDock.Value, false, biomeResolver)
                : "";
            string endpointStatus = hasEndpoint
                ? StructureLocationFormatter.EndpointStatus(win.EndpointAtDock.Value, false)
                : "";

            // Dock.
            if (win != null && !double.IsNaN(win.DockUT))
            {
                steps.Add(new StructureStep
                {
                    UT = win.DockUT,
                    Kind = StructureStepKind.Dock,
                    Label = "Dock",
                    Status = endpointStatus,
                    Location = endpointLoc,
                    VesselName = ""
                });
            }

            // Delivery: fires at the recorded dock phase each cycle (RecordedDockUT), one
            // per stop. Falls back to the connection window dock UT when RecordedDockUT is
            // unset. NOTE: the route steps are emitted in logical order (origin, dock,
            // delivery, undock) and NOT sorted; this stays chronological because v0 lifts
            // RecordedDockUT FROM the leaf's RouteConnectionWindow.DockUT (Route.cs), so
            // delivery never precedes dock. If a future capture path makes them diverge, add
            // a UT sort here (Origin pinned first via its NaN UT).
            double deliveryUT = route.RecordedDockUT >= 0
                ? route.RecordedDockUT
                : (win != null ? win.DockUT : double.NaN);
            for (int i = 0; i < route.Stops.Count; i++)
            {
                Logistics.RouteStop stop = route.Stops[i];
                if (stop == null) continue;
                string num = route.Stops.Count > 1 ? " #" + (i + 1).ToString(CultureInfo.InvariantCulture) : "";
                steps.Add(new StructureStep
                {
                    UT = deliveryUT,
                    Kind = StructureStepKind.Delivery,
                    Label = "Deliver" + num + FormatManifestSummary(stop.DeliveryManifest, stop.InventoryDeliveryManifest),
                    Status = StructureLocationFormatter.EndpointStatus(stop.Endpoint, false),
                    Location = StructureLocationFormatter.EndpointLocation(stop.Endpoint, false, biomeResolver),
                    VesselName = ""
                });
            }

            // Undock.
            if (win != null && !double.IsNaN(win.UndockUT))
            {
                steps.Add(new StructureStep
                {
                    UT = win.UndockUT,
                    Kind = StructureStepKind.Undock,
                    Label = "Undock",
                    Status = endpointStatus,
                    Location = endpointLoc,
                    VesselName = ""
                });
            }

            if (!SuppressLogging)
                ParsekLog.Verbose("Route",
                    $"BuildStructureList: route={ShortId(route.Id)} steps={steps.Count} " +
                    $"ksc={route.IsKscOrigin} stops={route.Stops.Count} " +
                    $"window={(win != null ? "yes" : "no")} dockRec={(dockRec != null ? "yes" : "no")}");
            return steps;
        }

        // Compact "(50 LiquidFuel, 20 Oxidizer, 2 parts)" suffix; empty when nothing.
        private static string FormatManifestSummary(
            Dictionary<string, double> resources, List<InventoryPayloadItem> inventory)
        {
            var parts = new List<string>();
            if (resources != null)
            {
                var keys = new List<string>(resources.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys)
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:F0} {1}", resources[k], k));
            }
            if (inventory != null && inventory.Count > 0)
                parts.Add(inventory.Count.ToString(CultureInfo.InvariantCulture)
                    + (inventory.Count == 1 ? " part" : " parts"));
            return parts.Count == 0 ? "" : " (" + string.Join(", ", parts.ToArray()) + ")";
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }
    }
}
