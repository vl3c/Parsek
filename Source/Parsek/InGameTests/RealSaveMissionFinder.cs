using System.Collections.Generic;
using System.Globalization;
using Parsek.Reaim;

namespace Parsek.InGameTests
{
    // Reusable read-only classifier over the REAL missions loaded from the CURRENT save
    // (MissionStore.Missions + RecordingStore.CommittedTrees / CommittedRecordings), for
    // in-game tests that validate the shipped re-aim / station-rendezvous contract against
    // the maintainer's actual recorded missions instead of a hand-built fixture. It classifies
    // each committed mission by SHAPE by driving the SAME production read-models a live scene
    // uses (MissionLoopUnitBuilder.Build with FlightGlobalsBodyInfo.Instance for re-aim;
    // MissionPeriodicity.ExtractConstraints for the VesselOrbital station constraint) and
    // returns the first match, or false when the loaded save has none (so a test built on it
    // is a graceful no-op / clean Skip on an unrelated save).
    //
    // WHY BUILD, NOT HAND-ROLL: the whole point of these real-save tests is to catch a
    // deviation in the LIVE builder that a synthetic fixture cannot, so the finder never
    // constructs a plan/schedule itself - it runs the mission's trimmed member set through the
    // production classifier/builder with the live body graph and asserts on the result.
    //
    // NON-MUTATING: the re-aim finder never flips a real Mission's LoopPlayback flag in the
    // store. It builds a TRANSIENT loop-enabled clone (Mission.Clone + LoopPlayback=true,
    // LoopTimeUnit=Auto) and passes a single-element list to the builder, so the finder works
    // whether or not the maintainer currently has looping enabled and the store is untouched.
    // The station finder only READS the tree/committed lists to build the constraint extraction.
    // All three *.SuppressLogging seams are toggled inside a guard so a scan does not spam the
    // log (the per-scan summary below is the only line emitted).
    //
    // REUSABLE SEAM (future feature branches): M5 (dock-boundary, #1239) and later real-save
    // milestones can extend this finder with their own shape classifiers (e.g. a
    // TryFindDockedStretchMission) and reuse TryFindReaimMission / TryFindStationRendezvousMission
    // verbatim for their own real-save assertions. Keep every method pure with respect to store
    // state (build transient clones, never flip a committed Mission's flags) so a test that calls
    // several finders in sequence sees a stable store. This file references ONLY main-resident
    // symbols; do not add references to unmerged-branch types here.
    internal static class RealSaveMissionFinder
    {
        /// <summary>
        /// A committed mission whose trimmed member set, run through the REAL
        /// <see cref="MissionLoopUnitBuilder"/> with the live body graph, yields a loop unit with
        /// re-aim ENGAGED (<see cref="GhostPlaybackLogic.LoopUnit.IsReaim"/>). Carries the built
        /// unit so a test can assert on the real schedule / plan / arrival hold.
        /// </summary>
        internal struct ReaimMissionMatch
        {
            /// <summary>The real committed mission (unmodified; the unit was built from a transient clone).</summary>
            public Mission Mission;

            /// <summary>The mission's recording tree.</summary>
            public RecordingTree Tree;

            /// <summary>The re-aim loop unit the live builder produced (<see cref="GhostPlaybackLogic.LoopUnit.IsReaim"/> true).</summary>
            public GhostPlaybackLogic.LoopUnit Unit;

            /// <summary>
            /// The full <see cref="GhostPlaybackLogic.LoopUnitSet"/> the builder produced for the
            /// transient clone (contains exactly the matched unit; member indices are committed-list
            /// indices). Carried so a test can drive the production per-member span-clock resolvers
            /// (<see cref="GhostPlaybackLogic.ResolveTrackingStationSampleFrame"/> /
            /// <c>GhostMapPresence.ResolveMapPresenceSampleUT</c>), which take the set, with the REAL
            /// built set instead of hand-assembling one.
            /// </summary>
            public GhostPlaybackLogic.LoopUnitSet Units;
        }

        /// <summary>
        /// A committed mission whose periodicity extraction emits a
        /// <see cref="ConstraintKind.VesselOrbital"/> constraint (a rendezvous with an orbiting
        /// station). Carries the whole extraction + the resolved constraint + the periodicity
        /// solution so a test can assert phase-lock (same-parent) or a finite station period.
        /// </summary>
        internal struct StationMissionMatch
        {
            /// <summary>The real committed mission.</summary>
            public Mission Mission;

            /// <summary>The mission's recording tree.</summary>
            public RecordingTree Tree;

            /// <summary>The full constraint extraction for the mission's trimmed config.</summary>
            public ConstraintExtraction Extraction;

            /// <summary>The FIRST emitted VesselOrbital constraint (the station rendezvous).</summary>
            public PhaseConstraint StationConstraint;

            /// <summary>The periodicity solution for the extraction (ShouldPhaseLock / P / method).</summary>
            public PeriodicitySolution Solution;
        }

        /// <summary>
        /// Scans every committed mission for one whose trimmed member set, run through the REAL
        /// <see cref="MissionLoopUnitBuilder.Build"/> with <paramref name="bodyInfo"/> (the live
        /// <see cref="FlightGlobalsBodyInfo.Instance"/>), yields a loop unit with re-aim ENGAGED
        /// (an interplanetary transfer like Kerbin-&gt;Duna). Returns the first match. Non-mutating:
        /// each candidate is a transient loop-enabled CLONE, so a mission needing looping turned on
        /// in the store is still classified and the store is untouched. Logs a batch-counted scan
        /// summary so a caller's Skip is explainable ("scanned N missions, none re-aim-shaped").
        /// </summary>
        internal static bool TryFindReaimMission(
            IBodyInfo bodyInfo,
            double autoLoopIntervalSeconds,
            TransitedBodyRotationMode transitedBodyRotationMode,
            out ReaimMissionMatch match)
        {
            match = default;
            IReadOnlyList<Mission> missions = MissionStore.Missions;
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (bodyInfo == null || missions == null || missions.Count == 0
                || committed == null || committed.Count == 0 || trees == null)
            {
                LogScanSummary("reaim", missions?.Count ?? 0, 0, 0, false);
                return false;
            }

            int scanned = 0;
            int builtUnits = 0;
            bool found = false;
            using (new FinderLogGuard())
            {
                for (int i = 0; i < missions.Count; i++)
                {
                    Mission mission = missions[i];
                    if (mission == null || string.IsNullOrEmpty(mission.TreeId))
                        continue;
                    scanned++;

                    RecordingTree tree = FindTree(trees, mission.TreeId);
                    if (tree == null)
                        continue;

                    // Transient loop-enabled clone so the builder produces a unit regardless of the
                    // real mission's live LoopPlayback flag; the store is never touched. Auto so the
                    // re-aim path chooses the synodic cadence itself (the real logistics setting).
                    Mission probe = mission.Clone(mission.Id + "-reaimprobe");
                    probe.LoopPlayback = true;
                    probe.LoopTimeUnit = LoopTimeUnit.Auto;

                    GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                        new List<Mission> { probe }, trees, committed,
                        autoLoopIntervalSeconds, bodyInfo, transitedBodyRotationMode);
                    if (set == null || set.Count == 0)
                        continue;
                    builtUnits++;

                    // The clone owns exactly one unit; find it (its owner index is the earliest
                    // member across the whole committed list, so scan the built set).
                    foreach (var kv in set.UnitsByOwner)
                    {
                        GhostPlaybackLogic.LoopUnit unit = kv.Value;
                        if (!unit.IsReaim)
                            continue;
                        match = new ReaimMissionMatch { Mission = mission, Tree = tree, Unit = unit, Units = set };
                        found = true;
                        break;
                    }
                    if (found)
                        break;
                }
            }

            LogScanSummary("reaim", missions.Count, scanned, builtUnits, found);
            return found;
        }

        /// <summary>
        /// Scans every committed mission for one whose periodicity extraction (the REAL
        /// <see cref="MissionPeriodicity.ExtractConstraints"/> with <paramref name="bodyInfo"/>)
        /// emits a <see cref="ConstraintKind.VesselOrbital"/> constraint (a rendezvous with an
        /// orbiting station). Returns the first match, carrying the extraction, the resolved
        /// station constraint, and the periodicity solution. Read-only; logs a batch-counted scan
        /// summary so a caller's Skip is explainable.
        /// </summary>
        internal static bool TryFindStationRendezvousMission(
            IBodyInfo bodyInfo, out StationMissionMatch match)
        {
            match = default;
            IReadOnlyList<Mission> missions = MissionStore.Missions;
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (bodyInfo == null || missions == null || missions.Count == 0
                || committed == null || committed.Count == 0 || trees == null)
            {
                LogScanSummary("station", missions?.Count ?? 0, 0, 0, false);
                return false;
            }

            int scanned = 0;
            int extracted = 0;
            bool found = false;
            using (new FinderLogGuard())
            {
                for (int i = 0; i < missions.Count; i++)
                {
                    Mission mission = missions[i];
                    if (mission == null || string.IsNullOrEmpty(mission.TreeId))
                        continue;
                    scanned++;

                    RecordingTree tree = FindTree(trees, mission.TreeId);
                    if (tree == null)
                        continue;

                    // Build the same read-models the builder + extractor use, then run the REAL
                    // constraint extraction with the live body graph.
                    MissionStructure structure = MissionStructureBuilder.Build(tree);
                    MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
                    List<MissionCompositionNode> compRoots = MissionCompositionBuilder.Build(structure);
                    ConstraintExtraction extraction = MissionPeriodicity.ExtractConstraints(
                        view, compRoots, committed, mission.ExcludedIntervalKeys, bodyInfo);
                    if (extraction.Constraints == null)
                        continue;
                    extracted++;

                    if (!TryFindVesselOrbitalConstraint(extraction.Constraints, out PhaseConstraint station))
                        continue;

                    // Solve for the same reference-UT contract the builder uses at cycle 0 (UT0),
                    // so the test can assert phase-lock (same-parent) with a finite period.
                    PeriodicitySolution solution = MissionPeriodicity.Solve(
                        extraction.Constraints, extraction.Support, extraction.UT0, extraction.UT0,
                        bodyInfo);

                    match = new StationMissionMatch
                    {
                        Mission = mission,
                        Tree = tree,
                        Extraction = extraction,
                        StationConstraint = station,
                        Solution = solution,
                    };
                    found = true;
                    break;
                }
            }

            LogScanSummary("station", missions.Count, scanned, extracted, found);
            return found;
        }

        // The first VesselOrbital constraint in the list (the station rendezvous), or false when none.
        private static bool TryFindVesselOrbitalConstraint(
            IReadOnlyList<PhaseConstraint> constraints, out PhaseConstraint station)
        {
            station = default;
            if (constraints == null)
                return false;
            for (int i = 0; i < constraints.Count; i++)
            {
                if (constraints[i].Kind == ConstraintKind.VesselOrbital)
                {
                    station = constraints[i];
                    return true;
                }
            }
            return false;
        }

        private static RecordingTree FindTree(List<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null
                    && string.Equals(trees[i].Id, treeId, System.StringComparison.Ordinal))
                    return trees[i];
            return null;
        }

        private static void LogScanSummary(
            string shape, int missionCount, int scanned, int read, bool found)
        {
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("RealSaveMissionFinder",
                $"scan {shape}: missions={missionCount.ToString(ic)} scanned={scanned.ToString(ic)} " +
                $"{(shape == "reaim" ? "builtUnits" : "extracted")}={read.ToString(ic)} " +
                $"match={(found ? "yes" : "no")}");
        }

        // Silences the per-mission Verbose chatter from the three mission builders while the finder
        // sweeps the whole store (dozens of extract/build calls); the single per-scan summary above
        // is the only line the finder emits. Restores prior flags on dispose. A class (not a struct)
        // so a parameterless `new FinderLogGuard()` runs the constructor that captures + sets the
        // flags rather than the struct default-ctor that would zero the fields and no-op.
        private sealed class FinderLogGuard : System.IDisposable
        {
            private readonly bool priorMissionStore;
            private readonly bool priorLoopBuilder;
            private readonly bool priorPeriodicity;

            public FinderLogGuard()
            {
                priorMissionStore = MissionStore.SuppressLogging;
                priorLoopBuilder = MissionLoopUnitBuilder.SuppressLogging;
                priorPeriodicity = MissionPeriodicity.SuppressLogging;
                MissionStore.SuppressLogging = true;
                MissionLoopUnitBuilder.SuppressLogging = true;
                MissionPeriodicity.SuppressLogging = true;
            }

            public void Dispose()
            {
                MissionStore.SuppressLogging = priorMissionStore;
                MissionLoopUnitBuilder.SuppressLogging = priorLoopBuilder;
                MissionPeriodicity.SuppressLogging = priorPeriodicity;
            }
        }
    }
}
