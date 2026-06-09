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

    /// <summary>One row of a structure list: a single event with its time and location.</summary>
    internal struct StructureStep
    {
        public double UT;            // event time; NaN for the route Origin pseudo-step (rendered first)
        public StructureStepKind Kind;
        public string Label;         // "Launch", "Decoupled booster", "Dock", "Deliver (50 LiquidFuel)", "Landed"
        public string Location;      // "LaunchPad, Kerbin", "Mun", "Orbiting Kerbin", "Mun surface"
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
        // Coarse body label for a mid-flight event (dock / undock / decouple / staging).
        // Per-step coordinate / situation resolution is deferred (plan decision #3); the
        // recording's body is the honest coarse value and keeps the builder pure.
        internal static string DescribeMid(Recording rec)
        {
            if (rec == null) return "";
            return !string.IsNullOrEmpty(rec.StartBodyName) ? rec.StartBodyName : "-";
        }

        // A route endpoint (origin / dock / delivery / undock). RouteEndpoint is a struct
        // with no backing Recording, so this is purpose-built location text.
        internal static string DescribeEndpoint(RouteEndpoint ep, bool isKsc)
        {
            if (isKsc)
                return string.IsNullOrEmpty(ep.BodyName) ? "KSC" : "KSC, " + ep.BodyName;

            if (string.IsNullOrEmpty(ep.BodyName))
                return "-";

            if (ep.IsSurface)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} surface ({1:F2}, {2:F2})", ep.BodyName, ep.Latitude, ep.Longitude);

            return "Orbiting " + ep.BodyName;
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
            foreach (var rootId in structure.RootLegIds)
            {
                if (!structure.LegsById.TryGetValue(rootId, out MissionLeg leg))
                    continue;
                Recording rec = Rec(rootId);
                steps.Add(new StructureStep
                {
                    UT = leg.StartUT,
                    Kind = StructureStepKind.Launch,
                    Label = !string.IsNullOrEmpty(leg.EvaCrewName) ? "EVA " + leg.EvaCrewName : "Launch",
                    Location = rec != null ? RecordingsTableFormatters.FormatStartPosition(rec) : "",
                    VesselName = LegLabel(leg)
                });
            }

            // 2. Branch-point events. Collect decoupler PIDs handled here so the staging
            //    pass can dedup the Decoupled PartEvent that mirrors a controlled split.
            var handledDecouplerPids = new HashSet<uint>();
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

                    string repId = FirstControlled(bp.ParentRecordingIds, structure)
                        ?? FirstControlled(bp.ChildRecordingIds, structure);
                    MissionLeg repLeg = repId != null && structure.LegsById.TryGetValue(repId, out MissionLeg l) ? l : null;
                    Recording repRec = Rec(repId);
                    string cause = bp.SplitCause ?? bp.BreakupCause;

                    steps.Add(new StructureStep
                    {
                        UT = bp.UT,
                        Kind = ClassifyBranch(bp.Type),
                        Label = MissionCompositionBuilder.BranchEventName(bp.Type, cause),
                        Location = StructureLocationFormatter.DescribeMid(repRec),
                        VesselName = repLeg != null ? LegLabel(repLeg) : ""
                    });

                    if (bp.DecouplerPartId != 0)
                        handledDecouplerPids.Add(bp.DecouplerPartId);
                }
            }

            // 3. Staging part events across all member recordings. Decoupled events are
            //    dropped when a controlled Separation branch point already covers the same
            //    decoupler PID; fairing / shroud have no branch-point counterpart and pass
            //    through. Self-dedup guards the same physical event recorded twice.
            var seenStaging = new HashSet<string>();
            if (tree.Recordings != null)
            {
                foreach (Recording rec in tree.Recordings.Values)
                {
                    if (rec?.PartEvents == null) continue;
                    foreach (PartEvent pe in rec.PartEvents)
                    {
                        if (!IsStagingEvent(pe.eventType)) continue;
                        if (pe.eventType == PartEventType.Decoupled
                            && handledDecouplerPids.Contains(pe.partPersistentId))
                            continue;

                        string key = (int)pe.eventType + "|" + pe.partPersistentId.ToString(CultureInfo.InvariantCulture)
                            + "|" + pe.ut.ToString("F1", CultureInfo.InvariantCulture);
                        if (!seenStaging.Add(key)) continue;

                        steps.Add(new StructureStep
                        {
                            UT = pe.ut,
                            Kind = StructureStepKind.Staging,
                            Label = StagingLabel(pe),
                            Location = StructureLocationFormatter.DescribeMid(rec),
                            VesselName = "",
                            SortPid = pe.partPersistentId
                        });
                    }
                }
            }

            // 4. Terminal: one per controlled leg that ends in a terminal state.
            foreach (MissionLeg leg in structure.LegsById.Values)
            {
                if (!leg.TerminalStateValue.HasValue) continue;
                Recording rec = Rec(leg.RecordingId);
                steps.Add(new StructureStep
                {
                    UT = leg.EndUT,
                    Kind = StructureStepKind.Terminal,
                    Label = MissionCompositionBuilder.TerminalName(leg.TerminalStateValue),
                    Location = rec != null ? RecordingsTableFormatters.FormatEndPosition(rec) : "",
                    VesselName = LegLabel(leg)
                });
            }

            // 5. Deterministic chronological sort.
            steps.Sort(CompareStep);

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
        /// recording id to its committed <see cref="Recording"/> (injected so the builder
        /// stays free of the <c>RecordingStore</c> singleton and is headless-testable).
        /// </summary>
        internal static List<StructureStep> Build(Logistics.Route route, Func<string, Recording> sourceLookup)
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
                Location = StructureLocationFormatter.DescribeEndpoint(route.Origin, route.IsKscOrigin),
                VesselName = ""
            });

            // Connection window lives on the dock-member recording.
            RouteConnectionWindow win = null;
            Recording dockRec = sourceLookup != null && !string.IsNullOrEmpty(route.DockMemberRecordingId)
                ? sourceLookup(route.DockMemberRecordingId)
                : null;
            if (dockRec?.RouteConnectionWindows != null && dockRec.RouteConnectionWindows.Count > 0)
            {
                // Last completed window is the delivery binding.
                for (int i = dockRec.RouteConnectionWindows.Count - 1; i >= 0; i--)
                {
                    if (dockRec.RouteConnectionWindows[i] != null)
                    {
                        win = dockRec.RouteConnectionWindows[i];
                        break;
                    }
                }
            }

            string endpointLoc = win != null && win.EndpointAtDock.HasValue
                ? StructureLocationFormatter.DescribeEndpoint(win.EndpointAtDock.Value, false)
                : "";

            // Dock.
            if (win != null && !double.IsNaN(win.DockUT))
            {
                steps.Add(new StructureStep
                {
                    UT = win.DockUT,
                    Kind = StructureStepKind.Dock,
                    Label = "Dock",
                    Location = endpointLoc,
                    VesselName = ""
                });
            }

            // Delivery: fires at the recorded dock phase each cycle (RecordedDockUT), one
            // per stop. Falls back to the connection window dock UT when RecordedDockUT is
            // unset.
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
                    Location = StructureLocationFormatter.DescribeEndpoint(stop.Endpoint, false),
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
