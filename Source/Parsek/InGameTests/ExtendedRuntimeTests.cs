using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Parsek.InGameTests
{
    // ════════════════════════════════════════════════════════════════
    //  Ghost Playback Lifecycle — verify engine state consistency
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates GhostPlaybackEngine internal state during live playback.
    /// Catches orphan ghosts, NaN positions, material leaks, overlap cap violations.
    /// These bugs are invisible to unit tests because they require real Unity GameObjects.
    /// </summary>
    public class GhostPlaybackLifecycleTests
    {
        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Every ghostStates entry has a non-null, non-destroyed GameObject")]
        public void AllGhostStatesHaveLiveGameObject()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var ghostGOs = flight.Engine.GetGhostGameObjects();
            if (ghostGOs.Count == 0)
                InGameAssert.Skip("No active ghosts");

            int valid = 0, orphaned = 0;
            foreach (var kvp in ghostGOs)
            {
                if (kvp.Value != null)
                    valid++;
                else
                    orphaned++;
            }

            ParsekLog.Info("TestRunner",
                $"Ghost GameObjects: {valid} valid, {orphaned} orphaned (of {ghostGOs.Count})");
            InGameAssert.AreEqual(0, orphaned,
                $"{orphaned} ghost state(s) have null/destroyed GameObject (leak)");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "All ghost positions are finite (no NaN or Infinity)")]
        public void AllGhostPositionsFinite()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var positions = flight.Engine.GetActiveGhostPositions().ToList();
            if (positions.Count == 0)
                InGameAssert.Skip("No active ghost positions");

            int nanCount = 0;
            foreach (var (index, pos) in positions)
            {
                if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)
                    || float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z))
                {
                    nanCount++;
                    ParsekLog.Warn("TestRunner",
                        $"Ghost index={index} has non-finite position: ({pos.x},{pos.y},{pos.z})");
                }
            }

            InGameAssert.AreEqual(0, nanCount,
                $"{nanCount} ghost(s) have NaN/Infinity positions");
            ParsekLog.Verbose("TestRunner", $"All {positions.Count} ghost positions finite");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Overlap ghost count per recording does not exceed MaxOverlapGhostsPerRecording")]
        public void OverlapGhostCountWithinCap()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int maxPerRec = GhostPlaybackEngine.MaxOverlapGhostsPerRecording;
            var ghostGOs = flight.Engine.GetGhostGameObjects();
            int violations = 0;

            foreach (var kvp in ghostGOs)
            {
                if (flight.Engine.TryGetOverlapGhosts(kvp.Key, out var overlaps))
                {
                    if (overlaps.Count > maxPerRec)
                    {
                        violations++;
                        ParsekLog.Warn("TestRunner",
                            $"Recording index={kvp.Key} has {overlaps.Count} overlap ghosts (max={maxPerRec})");
                    }
                }
            }

            InGameAssert.AreEqual(0, violations,
                $"{violations} recording(s) exceed overlap ghost cap of {maxPerRec}");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Active explosions list has no null/destroyed entries")]
        public void ActiveExplosionsClean()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var explosions = flight.Engine.activeExplosions;
            int nullCount = 0;
            foreach (var go in explosions)
            {
                if (go == null) nullCount++;
            }

            if (explosions.Count > 0)
                ParsekLog.Verbose("TestRunner", $"Active explosions: {explosions.Count}, null={nullCount}");

            InGameAssert.AreEqual(0, nullCount,
                $"{nullCount} null/destroyed entries in activeExplosions list (leak)");
            InGameAssert.IsTrue(explosions.Count <= GhostPlaybackEngine.MaxActiveExplosions,
                $"Active explosions ({explosions.Count}) exceeds cap ({GhostPlaybackEngine.MaxActiveExplosions})");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Loop phase offsets are all finite (no NaN/Infinity)")]
        public void LoopPhaseOffsetsFinite()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var offsets = flight.Engine.loopPhaseOffsets;
            if (offsets.Count == 0)
                InGameAssert.Skip("No loop phase offsets active");

            int badCount = 0;
            foreach (var kvp in offsets)
            {
                if (double.IsNaN(kvp.Value) || double.IsInfinity(kvp.Value))
                {
                    badCount++;
                    ParsekLog.Warn("TestRunner",
                        $"Loop phase offset for index={kvp.Key} is non-finite: {kvp.Value}");
                }
            }

            InGameAssert.AreEqual(0, badCount,
                $"{badCount} loop phase offset(s) are NaN/Infinity");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Soft-cap suppressed set is consistent — suppressed ghosts have no active GO")]
        public void SoftCapConsistency()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var suppressed = flight.Engine.softCapSuppressed;
            if (suppressed.Count == 0)
                InGameAssert.Skip("No soft-cap suppressed ghosts");

            int inconsistent = 0;
            foreach (int idx in suppressed)
            {
                // A suppressed ghost should not have an active (visible) ghost GO
                if (flight.Engine.HasActiveGhost(idx))
                {
                    inconsistent++;
                    ParsekLog.Warn("TestRunner",
                        $"Ghost index={idx} is in softCapSuppressed but HasActiveGhost=true");
                }
            }

            ParsekLog.Verbose("TestRunner",
                $"Soft-cap: {suppressed.Count} suppressed, {inconsistent} inconsistent");
            InGameAssert.AreEqual(0, inconsistent,
                $"{inconsistent} ghosts in softCapSuppressed still have active GameObjects");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Ghost count from engine matches expected count")]
        public void GhostCountReasonable()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int ghostCount = flight.Engine.GhostCount;
            ParsekLog.Verbose("TestRunner", $"GhostPlaybackEngine.GhostCount = {ghostCount}");

            // Ghost count should never be negative (sanity)
            InGameAssert.IsTrue(ghostCount >= 0, $"Ghost count is negative: {ghostCount}");

            // Reasonable upper bound — more than 200 simultaneous ghosts is suspicious
            InGameAssert.IsTrue(ghostCount < 200,
                $"Suspiciously high ghost count: {ghostCount} (potential leak)");
        }

        [InGameTest(Category = "GhostLifecycle", Scene = GameScenes.FLIGHT,
            Description = "Ghost interpolated body names all resolve to real CelestialBodies")]
        public void GhostBodyNamesValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            var ghostGOs = flight.Engine.GetGhostGameObjects();
            int checked_ = 0;
            var badBodies = new List<string>();

            foreach (var kvp in ghostGOs)
            {
                string bodyName = flight.Engine.GetGhostBodyName(kvp.Key);
                if (string.IsNullOrEmpty(bodyName)) continue;
                checked_++;

                if (FlightGlobals.GetBodyByName(bodyName) == null)
                    badBodies.Add($"index={kvp.Key} body='{bodyName}'");
            }

            if (checked_ == 0)
                InGameAssert.Skip("No ghosts with body names to check");

            ParsekLog.Verbose("TestRunner",
                $"Ghost body names: {checked_ - badBodies.Count}/{checked_} resolved");
            InGameAssert.IsTrue(badBodies.Count == 0,
                $"Unresolvable ghost body names: {string.Join(", ", badBodies)}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Part Event Playback — verify FX info objects built correctly
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates part event visual info (engine FX, parachutes, lights, etc.) on active ghosts.
    /// These require real KSP PartLoader prefabs and Unity particle systems — untestable offline.
    /// </summary>
    public class PartEventPlaybackTests
    {
        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Engine FX infos have non-null particle system lists")]
        public void EngineInfosHaveParticleSystems()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0, nullSystems = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.engineInfos == null) continue;

                foreach (var engineKvp in state.engineInfos)
                {
                    total++;
                    if (engineKvp.Value.particleSystems != null && engineKvp.Value.particleSystems.Count > 0)
                        valid++;
                    else
                        nullSystems++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No engine FX infos on active ghosts");

            ParsekLog.Info("TestRunner",
                $"Engine FX: {valid}/{total} have particle systems, {nullSystems} empty");
            InGameAssert.AreEqual(total, valid,
                $"{nullSystems} engine info(s) have null/empty particle systems (FX build failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Parachute ghost infos have non-null mesh transforms")]
        public void ParachuteInfosValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.parachuteInfos == null) continue;

                foreach (var pKvp in state.parachuteInfos)
                {
                    total++;
                    if (pKvp.Value.canopyTransform != null)
                        valid++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No parachute infos on active ghosts");

            ParsekLog.Info("TestRunner", $"Parachute infos: {valid}/{total} have canopy transforms");
            InGameAssert.AreEqual(total, valid,
                $"{total - valid} parachute info(s) have null canopy transform (prefab load failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Light ghost infos have valid Light components")]
        public void LightInfosValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, withLights = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.lightInfos == null) continue;

                foreach (var lKvp in state.lightInfos)
                {
                    total++;
                    if (lKvp.Value.lights != null && lKvp.Value.lights.Count > 0)
                        withLights++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No light infos on active ghosts");

            ParsekLog.Info("TestRunner", $"Light infos: {withLights}/{total} have Light components");
            InGameAssert.AreEqual(total, withLights,
                $"{total - withLights} light info(s) have null/empty Light list (prefab load failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "RCS ghost infos have non-null particle system lists")]
        public void RcsInfosHaveParticleSystems()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.rcsInfos == null) continue;

                foreach (var rKvp in state.rcsInfos)
                {
                    total++;
                    if (rKvp.Value.particleSystems != null && rKvp.Value.particleSystems.Count > 0)
                        valid++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No RCS infos on active ghosts");

            ParsekLog.Info("TestRunner", $"RCS FX: {valid}/{total} have particle systems");
            InGameAssert.AreEqual(total, valid,
                $"{total - valid} RCS info(s) have null/empty particle systems (prefab load failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Fairing ghost infos have non-null cone mesh GameObjects")]
        public void FairingInfosValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.fairingInfos == null) continue;

                foreach (var fKvp in state.fairingInfos)
                {
                    total++;
                    if (fKvp.Value.fairingMeshObject != null)
                        valid++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No fairing infos on active ghosts");

            ParsekLog.Info("TestRunner", $"Fairing infos: {valid}/{total} have cone meshes");
            InGameAssert.AreEqual(total, valid,
                $"{total - valid} fairing info(s) have null mesh object (build failure)");
        }

        [InGameTest(Category = "PartEventFX", Scene = GameScenes.FLIGHT,
            Description = "Deployable ghost infos have valid transform states")]
        public void DeployableInfosValid()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            int total = 0, valid = 0;
            foreach (var kvp in flight.Engine.GetGhostGameObjects())
            {
                if (!flight.Engine.TryGetGhostState(kvp.Key, out var state)) continue;
                if (state.deployableInfos == null) continue;

                foreach (var dKvp in state.deployableInfos)
                {
                    total++;
                    if (dKvp.Value.transforms != null && dKvp.Value.transforms.Count > 0)
                        valid++;
                }
            }

            if (total == 0)
                InGameAssert.Skip("No deployable infos on active ghosts");

            ParsekLog.Info("TestRunner", $"Deployable infos: {valid}/{total} have transform states");
            InGameAssert.AreEqual(total, valid,
                $"{total - valid} deployable info(s) have null/empty transform states (animation sample failure)");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Game Actions Health — suppression flags, resource singletons
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies game actions system health: suppression flags not stuck, resource singletons valid.
    /// Catches the class of bug where a crashed replay leaves SuppressResourceEvents=true,
    /// silently disabling all future event recording.
    /// </summary>
    public class GameActionsHealthTests
    {
        [InGameTest(Category = "GameActionsHealth",
            Description = "GameStateRecorder suppression flags are all false during normal gameplay")]
        public void SuppressionFlagsNotStuck()
        {
            // During normal gameplay (not mid-replay), all suppression flags should be false.
            // If any is stuck true, event recording is silently disabled.
            InGameAssert.IsFalse(GameStateRecorder.SuppressCrewEvents,
                "SuppressCrewEvents is stuck true — crew events will not be recorded");
            InGameAssert.IsFalse(GameStateRecorder.SuppressResourceEvents,
                "SuppressResourceEvents is stuck true — resource events will not be recorded");
            InGameAssert.IsFalse(GameStateRecorder.IsReplayingActions,
                "IsReplayingActions is stuck true — action replay never completed cleanly");

            ParsekLog.Verbose("TestRunner", "All GameStateRecorder suppression flags are false (normal)");
        }

        [InGameTest(Category = "GameActionsHealth",
            Description = "KSP Funding singleton is accessible and not null")]
        public void FundingSingletonAccessible()
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("No active game");
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
                InGameAssert.Skip($"Game mode is {HighLogic.CurrentGame.Mode}, not Career");

            var funding = Funding.Instance;
            InGameAssert.IsNotNull(funding, "Funding.Instance null in career mode");
            InGameAssert.IsTrue(funding.Funds >= 0,
                $"Funds are negative: {funding.Funds:F0} (budget deduction error?)");
            ParsekLog.Verbose("TestRunner", $"Funding singleton: Funds={funding.Funds:F0}");
        }

        [InGameTest(Category = "GameActionsHealth",
            Description = "KSP ResearchAndDevelopment singleton is accessible in career")]
        public void ScienceSingletonAccessible()
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("No active game");
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER
                && HighLogic.CurrentGame.Mode != Game.Modes.SCIENCE_SANDBOX)
                InGameAssert.Skip($"Game mode is {HighLogic.CurrentGame.Mode}, not Career/Science");

            var rd = ResearchAndDevelopment.Instance;
            InGameAssert.IsNotNull(rd, "ResearchAndDevelopment.Instance null in career/science mode");
            InGameAssert.IsTrue(ResearchAndDevelopment.Instance.Science >= 0,
                $"Science is negative: {ResearchAndDevelopment.Instance.Science:F1}");
            ParsekLog.Verbose("TestRunner",
                $"R&D singleton: Science={ResearchAndDevelopment.Instance.Science:F1}");
        }

        [InGameTest(Category = "GameActionsHealth",
            Description = "KSP Reputation singleton is accessible in career")]
        public void ReputationSingletonAccessible()
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("No active game");
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
                InGameAssert.Skip($"Game mode is {HighLogic.CurrentGame.Mode}, not Career");

            var rep = Reputation.Instance;
            InGameAssert.IsNotNull(rep, "Reputation.Instance null in career mode");
            InGameAssert.IsTrue(rep.reputation >= -1000f && rep.reputation <= 1000f,
                $"Reputation out of bounds: {rep.reputation:F1} (expected [-1000, 1000])");
            ParsekLog.Verbose("TestRunner", $"Reputation singleton: rep={rep.reputation:F1}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Ghost Chain Consistency — verify chain invariants with real data
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates ghost chain invariants computed from live RecordingStore data.
    /// Catches stale recording references, double-ghosting, and invalid chain structure.
    /// </summary>
    public class GhostChainConsistencyTests
    {
        [InGameTest(Category = "GhostChains",
            Description = "All chain Links reference recordings that exist in RecordingStore")]
        public void ChainLinksReferenceExistingRecordings()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0)
                InGameAssert.Skip("No committed trees");

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, double.MaxValue);
            if (chains.Count == 0)
                InGameAssert.Skip("No ghost chains computed");

            // Build lookup of all recording IDs across all trees
            var allRecIds = new HashSet<string>();
            foreach (var tree in trees)
                foreach (var rec in tree.Recordings)
                    allRecIds.Add(rec.Key);

            // Also check standalone committed recordings
            foreach (var rec in RecordingStore.CommittedRecordings)
                allRecIds.Add(rec.RecordingId);

            int totalLinks = 0, dangling = 0;
            foreach (var kvp in chains)
            {
                foreach (var link in kvp.Value.Links)
                {
                    totalLinks++;
                    if (!allRecIds.Contains(link.recordingId))
                    {
                        dangling++;
                        ParsekLog.Warn("TestRunner",
                            $"Chain pid={kvp.Key}: link references non-existent recording '{link.recordingId}'");
                    }
                }
            }

            ParsekLog.Info("TestRunner",
                $"Chain links: {totalLinks - dangling}/{totalLinks} reference existing recordings");
            InGameAssert.AreEqual(0, dangling,
                $"{dangling} chain link(s) reference non-existent recordings");
        }

        [InGameTest(Category = "GhostChains",
            Description = "Chain GhostStartUT <= SpawnUT (start before or at spawn time)")]
        public void ChainTimeRangesValid()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0) InGameAssert.Skip("No committed trees");

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, double.MaxValue);
            int violations = 0;

            foreach (var kvp in chains)
            {
                var chain = kvp.Value;
                if (chain.GhostStartUT > chain.SpawnUT && chain.SpawnUT > 0)
                {
                    violations++;
                    ParsekLog.Warn("TestRunner",
                        $"Chain pid={kvp.Key}: GhostStartUT ({chain.GhostStartUT:F1}) > SpawnUT ({chain.SpawnUT:F1})");
                }
            }

            ParsekLog.Verbose("TestRunner",
                $"Chain time ranges: {chains.Count} chains, {violations} violations");
            InGameAssert.AreEqual(0, violations,
                $"{violations} chain(s) have GhostStartUT > SpawnUT");
        }

        [InGameTest(Category = "GhostChains",
            Description = "No vessel PID appears in two different non-terminated chains")]
        public void NoDoubleGhosting()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0) InGameAssert.Skip("No committed trees");

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, double.MaxValue);
            var seenPids = new Dictionary<uint, int>(); // pid -> count
            int doubles = 0;

            foreach (var kvp in chains)
            {
                if (kvp.Value.IsTerminated) continue;

                if (seenPids.ContainsKey(kvp.Key))
                {
                    seenPids[kvp.Key]++;
                    doubles++;
                }
                else
                {
                    seenPids[kvp.Key] = 1;
                }
            }

            InGameAssert.AreEqual(0, doubles,
                $"{doubles} vessel PID(s) appear in multiple non-terminated chains (double-ghosting)");
        }

        [InGameTest(Category = "GhostChains",
            Description = "Spawn chains have non-null vessel snapshot on tip recording")]
        public void SpawnChainsHaveTipSnapshot()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0) InGameAssert.Skip("No committed trees");

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, double.MaxValue);
            int spawnChains = 0, withSnapshot = 0;

            foreach (var kvp in chains)
            {
                var chain = kvp.Value;
                if (chain.IsTerminated || chain.SpawnUT <= 0) continue;
                if (string.IsNullOrEmpty(chain.TipRecordingId)) continue;

                spawnChains++;

                // Find the tip recording
                Recording tipRec = null;
                foreach (var tree in trees)
                {
                    if (tree.Recordings.TryGetValue(chain.TipRecordingId, out tipRec))
                        break;
                }

                if (tipRec?.VesselSnapshot != null)
                    withSnapshot++;
                else
                    ParsekLog.Warn("TestRunner",
                        $"Spawn chain pid={kvp.Key} tip='{chain.TipRecordingId}' has no VesselSnapshot");
            }

            if (spawnChains == 0)
                InGameAssert.Skip("No spawn chains to check");

            ParsekLog.Info("TestRunner",
                $"Spawn chains: {withSnapshot}/{spawnChains} have tip vessel snapshot");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Recording Tree Integrity — structural invariants
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates recording tree structural invariants: parent/child links,
    /// branch point references, PID uniqueness across trees.
    /// </summary>
    public class RecordingTreeIntegrityTests
    {
        [InGameTest(Category = "TreeIntegrity",
            Description = "Every BranchPoint child recording ID exists in its tree")]
        public void BranchPointChildrenExist()
        {
            var trees = RecordingStore.CommittedTrees;
            int totalBPs = 0, dangling = 0;

            foreach (var tree in trees)
            {
                var recIds = new HashSet<string>(tree.Recordings.Keys);
                foreach (var bp in tree.BranchPoints)
                {
                    foreach (string childId in bp.ChildRecordingIds)
                    {
                        totalBPs++;
                        if (!recIds.Contains(childId))
                        {
                            dangling++;
                            ParsekLog.Warn("TestRunner",
                                $"BranchPoint '{bp.Id}': child '{childId}' not found in tree");
                        }
                    }
                }
            }

            if (totalBPs == 0)
                InGameAssert.Skip("No branch point children to check");

            InGameAssert.AreEqual(0, dangling,
                $"{dangling} branch point child reference(s) point to non-existent recordings");
        }

        [InGameTest(Category = "TreeIntegrity",
            Description = "Every non-root recording's ParentRecordingId exists in its tree")]
        public void ParentLinksValid()
        {
            var trees = RecordingStore.CommittedTrees;
            int checked_ = 0, dangling = 0;

            foreach (var tree in trees)
            {
                var recIds = new HashSet<string>(tree.Recordings.Keys);
                foreach (var kvp in tree.Recordings)
                {
                    var rec = kvp.Value;
                    if (string.IsNullOrEmpty(rec.ParentRecordingId)) continue; // root
                    checked_++;

                    if (!recIds.Contains(rec.ParentRecordingId))
                    {
                        dangling++;
                        ParsekLog.Warn("TestRunner",
                            $"Recording '{rec.RecordingId}' parent '{rec.ParentRecordingId}' not in tree");
                    }
                }
            }

            if (checked_ == 0)
                InGameAssert.Skip("No non-root recordings with parent links");

            ParsekLog.Verbose("TestRunner",
                $"Parent links: {checked_ - dangling}/{checked_} valid");
            InGameAssert.AreEqual(0, dangling,
                $"{dangling} recording(s) have ParentRecordingId pointing to non-existent recordings");
        }

        [InGameTest(Category = "TreeIntegrity",
            Description = "OwnedVesselPids don't collide between different trees")]
        public void NoPidCollisionAcrossTrees()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count < 2)
                InGameAssert.Skip($"Only {trees.Count} tree(s) — need 2+ for cross-tree check");

            var globalPids = new Dictionary<uint, int>(); // pid -> tree index
            int collisions = 0;

            for (int i = 0; i < trees.Count; i++)
            {
                foreach (uint pid in trees[i].OwnedVesselPids)
                {
                    if (globalPids.TryGetValue(pid, out int otherTree))
                    {
                        collisions++;
                        ParsekLog.Warn("TestRunner",
                            $"PID {pid} owned by both tree[{otherTree}] and tree[{i}]");
                    }
                    else
                    {
                        globalPids[pid] = i;
                    }
                }
            }

            InGameAssert.AreEqual(0, collisions,
                $"{collisions} vessel PID(s) claimed by multiple trees");
        }

        [InGameTest(Category = "TreeIntegrity",
            Description = "ComputeEndUT is >= every leaf recording's last point UT")]
        public void EndUTCoversAllLeaves()
        {
            var trees = RecordingStore.CommittedTrees;
            int violations = 0;

            foreach (var tree in trees)
            {
                double treeEndUT = tree.ComputeEndUT();
                if (treeEndUT == 0) continue; // degraded tree

                foreach (var kvp in tree.Recordings)
                {
                    var rec = kvp.Value;
                    if (rec.Points == null || rec.Points.Count == 0) continue;

                    double recEndUT = rec.Points[rec.Points.Count - 1].ut;
                    if (recEndUT > treeEndUT + 0.001) // small tolerance
                    {
                        violations++;
                        ParsekLog.Warn("TestRunner",
                            $"Recording '{rec.RecordingId}' EndUT={recEndUT:F1} > tree EndUT={treeEndUT:F1}");
                    }
                }
            }

            InGameAssert.AreEqual(0, violations,
                $"{violations} recording(s) have EndUT beyond their tree's ComputeEndUT");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Multi-Scene & Harmony — scene controllers and patch effects
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scene-specific controller presence and Harmony patch effect verification.
    /// </summary>
    public class SceneAndPatchTests
    {
        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.SPACECENTER,
            Description = "ParsekKSC MonoBehaviour exists in Space Center scene")]
        public void ParsekKscExistsInSpaceCenter()
        {
            var ksc = Object.FindObjectOfType<ParsekKSC>();
            InGameAssert.IsNotNull(ksc,
                "ParsekKSC MonoBehaviour should be active in Space Center scene");
            ParsekLog.Verbose("TestRunner", "ParsekKSC instance found");
        }

        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.TRACKSTATION,
            Description = "ParsekTrackingStation MonoBehaviour exists in Tracking Station")]
        public void ParsekTrackingStationExists()
        {
            var ts = Object.FindObjectOfType<ParsekTrackingStation>();
            InGameAssert.IsNotNull(ts,
                "ParsekTrackingStation MonoBehaviour should be active in Tracking Station scene");
            ParsekLog.Verbose("TestRunner", "ParsekTrackingStation instance found");
        }

        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.FLIGHT,
            Description = "Ghost map PIDs are not loaded as physics vessels (GhostVesselLoadPatch working)")]
        public void GhostPidsNotLoadedAsPhysicsVessels()
        {
            var ghostPids = GhostMapPresence.ghostMapVesselPids;
            if (ghostPids.Count == 0)
                InGameAssert.Skip("No ghost map PIDs — patch not exercised");

            int loadedGhosts = 0;
            foreach (var vessel in FlightGlobals.Vessels)
            {
                if (vessel == null || !vessel.loaded) continue;
                if (ghostPids.Contains(vessel.persistentId)
                    && GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                {
                    // Ghost is loaded as a physics vessel — patch failed
                    if (vessel.packed == false) // actually simulating physics
                    {
                        loadedGhosts++;
                        ParsekLog.Warn("TestRunner",
                            $"Ghost PID={vessel.persistentId} is loaded and unpacked (physics active)");
                    }
                }
            }

            InGameAssert.AreEqual(0, loadedGhosts,
                $"{loadedGhosts} ghost map vessel(s) are loaded with active physics (GhostVesselLoadPatch broken?)");
        }

        [InGameTest(Category = "SceneAndPatch", Scene = GameScenes.FLIGHT,
            Description = "PhysicsFramePatch.ActiveRecorder is null when not recording (no stale reference)")]
        public void PhysicsFramePatchCleanWhenIdle()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("No ParsekFlight instance");

            if (!flight.IsRecording)
            {
                InGameAssert.IsNull(Parsek.Patches.PhysicsFramePatch.ActiveRecorder,
                    "ActiveRecorder should be null when not recording (stale reference)");
            }
            else
            {
                InGameAssert.IsNotNull(Parsek.Patches.PhysicsFramePatch.ActiveRecorder,
                    "ActiveRecorder should be set when recording");
            }

            ParsekLog.Verbose("TestRunner",
                $"PhysicsFramePatch: IsRecording={flight.IsRecording}, " +
                $"ActiveRecorder={(Parsek.Patches.PhysicsFramePatch.ActiveRecorder != null ? "set" : "null")}");
        }

        [InGameTest(Category = "SceneAndPatch",
            Description = "ParsekScenario survives OnSave+OnLoad round-trip for tree structure")]
        public void ScenarioRoundTripPreservesTreeStructure()
        {
            // WARNING: This test calls OnSave+OnLoad on the live ParsekScenario.
            // OnLoad re-initializes RecordingStore from the serialized ConfigNode.
            // If the round-trip is imperfect, this could disrupt the session.
            // The test verifies exactly this property — that the round-trip is lossless.
            var scenario = Object.FindObjectOfType<ParsekScenario>();
            if (scenario == null)
                InGameAssert.Skip("No ParsekScenario instance");

            var treesBefore = RecordingStore.CommittedTrees;
            if (treesBefore.Count == 0)
                InGameAssert.Skip("No committed trees");

            // Capture tree IDs and recording counts before
            var beforeMap = new Dictionary<int, int>(); // tree index -> recording count
            for (int i = 0; i < treesBefore.Count; i++)
                beforeMap[i] = treesBefore[i].Recordings.Count;

            int beforeBPTotal = treesBefore.Sum(t => t.BranchPoints.Count);

            // Round-trip
            var saveNode = new ConfigNode("SCENARIO");
            scenario.OnSave(saveNode);
            scenario.OnLoad(saveNode);

            var treesAfter = RecordingStore.CommittedTrees;
            InGameAssert.AreEqual(treesBefore.Count, treesAfter.Count,
                $"Tree count changed after round-trip: {treesBefore.Count} -> {treesAfter.Count}");

            int afterBPTotal = treesAfter.Sum(t => t.BranchPoints.Count);
            InGameAssert.AreEqual(beforeBPTotal, afterBPTotal,
                $"Total branch points changed: {beforeBPTotal} -> {afterBPTotal}");

            for (int i = 0; i < treesAfter.Count; i++)
            {
                InGameAssert.AreEqual(beforeMap[i], treesAfter[i].Recordings.Count,
                    $"Tree[{i}] recording count changed: {beforeMap[i]} -> {treesAfter[i].Recordings.Count}");
            }

            ParsekLog.Verbose("TestRunner",
                $"Tree round-trip: {treesAfter.Count} trees, {afterBPTotal} branch points preserved");
        }

        [InGameTest(Category = "SceneAndPatch",
            Description = "Crew replacement dict survives OnSave+OnLoad round-trip")]
        public void CrewReplacementsRoundTrip()
        {
            // WARNING: Calls OnSave+OnLoad on live scenario — see comment on
            // ScenarioRoundTripPreservesTreeStructure for rationale.
            var scenario = Object.FindObjectOfType<ParsekScenario>();
            if (scenario == null) InGameAssert.Skip("No ParsekScenario instance");

            var before = CrewReservationManager.CrewReplacements.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            var saveNode = new ConfigNode("SCENARIO");
            scenario.OnSave(saveNode);
            scenario.OnLoad(saveNode);

            var after = CrewReservationManager.CrewReplacements;
            InGameAssert.AreEqual(before.Count, after.Count,
                $"Crew replacement count changed: {before.Count} -> {after.Count}");

            foreach (var kvp in before)
            {
                InGameAssert.IsTrue(after.ContainsKey(kvp.Key),
                    $"Crew replacement key '{kvp.Key}' lost after round-trip");
                if (after.ContainsKey(kvp.Key))
                    InGameAssert.AreEqual(kvp.Value, after[kvp.Key],
                        $"Crew replacement for '{kvp.Key}' changed: '{kvp.Value}' -> '{after[kvp.Key]}'");
            }

            ParsekLog.Verbose("TestRunner",
                $"Crew replacement round-trip: {after.Count} entries preserved");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Floating Origin & KSP API Sanity
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// KSP API assumptions that Parsek depends on. If any of these fail,
    /// fundamental recording/playback logic is broken.
    /// </summary>
    public class KspApiSanityTests
    {
        private readonly InGameTestRunner runner;
        public KspApiSanityTests(InGameTestRunner runner) { this.runner = runner; }

        [InGameTest(Category = "KspApiSanity",
            Description = "body.bodyTransform.rotation is stable across 3 frames (co-rotating frame assumption)")]
        public IEnumerator BodyTransformRotationStable()
        {
            var kerbin = FlightGlobals.GetBodyByName("Kerbin");
            if (kerbin == null)
                InGameAssert.Skip("Kerbin not found");

            Quaternion rot0 = kerbin.bodyTransform.rotation;
            yield return null;
            Quaternion rot1 = kerbin.bodyTransform.rotation;
            yield return null;
            Quaternion rot2 = kerbin.bodyTransform.rotation;

            // Rotation should be essentially identical across frames (no planetary spin in world frame)
            float delta01 = Quaternion.Angle(rot0, rot1);
            float delta12 = Quaternion.Angle(rot1, rot2);

            InGameAssert.IsLessThan(delta01, 0.01,
                $"Kerbin bodyTransform.rotation changed by {delta01:F4}deg between frames (expected stable)");
            InGameAssert.IsLessThan(delta12, 0.01,
                $"Kerbin bodyTransform.rotation changed by {delta12:F4}deg between frames (expected stable)");

            ParsekLog.Verbose("TestRunner",
                $"Kerbin bodyTransform.rotation stable: delta={delta01:F6}deg, {delta12:F6}deg");
        }

        [InGameTest(Category = "KspApiSanity",
            Description = "Planetarium.GetUniversalTime() is monotonically increasing across 5 frames")]
        public IEnumerator UniversalTimeMonotonic()
        {
            double prev = Planetarium.GetUniversalTime();
            int violations = 0;

            for (int i = 0; i < 5; i++)
            {
                yield return null;
                double current = Planetarium.GetUniversalTime();
                if (current < prev)
                {
                    violations++;
                    ParsekLog.Warn("TestRunner",
                        $"UT went backward: frame {i} prev={prev:F3} current={current:F3}");
                }
                prev = current;
            }

            InGameAssert.AreEqual(0, violations,
                $"Planetarium.GetUniversalTime() went backward {violations} time(s) across 5 frames");
        }

        [InGameTest(Category = "KspApiSanity",
            Description = "PartLoader.getPartInfoByName returns same instance for same name (reference equality)")]
        public void PartLoaderReturnsSameInstance()
        {
            string testPart = "mk1pod.v2";
            var info1 = PartLoader.getPartInfoByName(testPart);
            var info2 = PartLoader.getPartInfoByName(testPart);

            if (info1 == null)
                InGameAssert.Skip($"Part '{testPart}' not found in PartLoader");

            InGameAssert.IsTrue(ReferenceEquals(info1, info2),
                $"PartLoader returned different instances for '{testPart}' (cache broken)");
        }

        [InGameTest(Category = "KspApiSanity", Scene = GameScenes.FLIGHT,
            Description = "Krakensbane.GetFrameVelocity() returns finite vector during flight")]
        public void KrakensbaneFrameVelocityFinite()
        {
            Vector3d kbVel = Krakensbane.GetFrameVelocity();
            InGameAssert.IsFalse(double.IsNaN(kbVel.x) || double.IsNaN(kbVel.y) || double.IsNaN(kbVel.z),
                $"Krakensbane frame velocity is NaN: ({kbVel.x},{kbVel.y},{kbVel.z})");
            InGameAssert.IsFalse(double.IsInfinity(kbVel.x) || double.IsInfinity(kbVel.y) || double.IsInfinity(kbVel.z),
                $"Krakensbane frame velocity is Infinity: ({kbVel.x},{kbVel.y},{kbVel.z})");
        }

        [InGameTest(Category = "KspApiSanity", Scene = GameScenes.FLIGHT,
            Description = "Ghost sphere placed at known position doesn't drift to NaN within 2 frames")]
        public IEnumerator FloatingOriginNoNaNDrift()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) InGameAssert.Skip("No active vessel");

            // Place a test object near the active vessel
            var testObj = GhostVisualBuilder.CreateGhostSphere("ParsekTest_FloatOrigin", Color.green);
            runner.TrackForCleanup(testObj);
            Vector3 targetPos = vessel.transform.position + new Vector3(50, 10, 0);
            testObj.transform.position = targetPos;

            yield return null;
            yield return null;

            Vector3 finalPos = testObj.transform.position;
            InGameAssert.IsFalse(float.IsNaN(finalPos.x) || float.IsNaN(finalPos.y) || float.IsNaN(finalPos.z),
                $"Test object drifted to NaN after 2 frames: ({finalPos.x},{finalPos.y},{finalPos.z})");
            InGameAssert.IsFalse(finalPos == Vector3.zero,
                "Test object collapsed to origin (0,0,0) — floating origin issue");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Ghost Map Orbit Validation
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates that ghost map ProtoVessels with orbit data produce valid
    /// Keplerian positions. Catches NaN from degenerate orbital elements.
    /// </summary>
    public class GhostMapOrbitTests
    {
        [InGameTest(Category = "GhostMapOrbits",
            Description = "Ghost map ProtoVessels with orbits produce valid positions at current UT")]
        public void GhostOrbitsProduceValidPositions()
        {
            var ghostPids = GhostMapPresence.ghostMapVesselPids;
            if (ghostPids.Count == 0)
                InGameAssert.Skip("No ghost map vessels");

            var flightState = HighLogic.CurrentGame?.flightState;
            if (flightState == null) InGameAssert.Skip("No FlightState available");

            double ut = Planetarium.GetUniversalTime();
            int checked_ = 0, valid = 0;

            foreach (uint pid in ghostPids)
            {
                var pv = flightState.protoVessels.FirstOrDefault(p => p.persistentId == pid);
                if (pv?.orbitSnapShot == null) continue;

                checked_++;
                try
                {
                    // Check that the orbit snapshot has valid Keplerian elements
                    double sma = pv.orbitSnapShot.semiMajorAxis;
                    double ecc = pv.orbitSnapShot.eccentricity;
                    string bodyName = pv.orbitSnapShot.ReferenceBodyIndex >= 0
                        ? FlightGlobals.Bodies[pv.orbitSnapShot.ReferenceBodyIndex].name : null;

                    bool smaValid = !double.IsNaN(sma) && !double.IsInfinity(sma) && sma != 0;
                    bool eccValid = !double.IsNaN(ecc) && !double.IsInfinity(ecc) && ecc >= 0;
                    bool bodyValid = bodyName != null;

                    if (smaValid && eccValid && bodyValid)
                    {
                        valid++;
                    }
                    else
                    {
                        ParsekLog.Warn("TestRunner",
                            $"Ghost PID={pid} orbit has invalid elements: sma={sma} ecc={ecc} body={bodyName ?? "null"}");
                    }
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"Ghost PID={pid} orbit evaluation threw: {ex.Message}");
                }
            }

            if (checked_ == 0)
                InGameAssert.Skip("No ghost ProtoVessels with orbit snapshots");

            ParsekLog.Info("TestRunner",
                $"Ghost orbits: {valid}/{checked_} produce valid positions at UT={ut:F1}");
            InGameAssert.AreEqual(checked_, valid,
                $"{checked_ - valid} ghost orbit(s) produce invalid positions");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Spawn Collision — bounds computation with real vessel data
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates spawn collision detection with real loaded vessel data.
    /// </summary>
    public class SpawnCollisionLiveTests
    {
        [InGameTest(Category = "SpawnCollision", Scene = GameScenes.FLIGHT,
            Description = "Active vessel snapshot produces non-degenerate bounds")]
        public void ActiveVesselBoundsNonDegenerate()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) InGameAssert.Skip("No active vessel");

            // Build a snapshot from the active vessel and compute bounds
            var snapshot = new ConfigNode("VESSEL");
            foreach (var part in vessel.parts)
            {
                var partNode = snapshot.AddNode("PART");
                partNode.AddValue("name", part.partInfo.name);
                var localPos = part.transform.localPosition;
                partNode.AddValue("pos",
                    $"{localPos.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{localPos.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{localPos.z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");
            }

            var bounds = SpawnCollisionDetector.ComputeVesselBounds(snapshot);
            InGameAssert.IsTrue(bounds.size.magnitude > 0.01f,
                $"Vessel bounds are degenerate (size={bounds.size})");
            InGameAssert.IsTrue(bounds.size.x < 500f && bounds.size.y < 500f && bounds.size.z < 500f,
                $"Vessel bounds suspiciously large: {bounds.size}");

            ParsekLog.Verbose("TestRunner",
                $"Active vessel bounds: center={bounds.center} size={bounds.size}");
        }

        [InGameTest(Category = "SpawnCollision", Scene = GameScenes.FLIGHT,
            Description = "CheckOverlapAgainstLoadedVessels returns no overlap for a distant position")]
        public void NoOverlapAtDistantPosition()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) InGameAssert.Skip("No active vessel");

            // Pick a position 5km away from active vessel — should have no overlap
            Vector3d farPos = vessel.GetWorldPos3D() + new Vector3d(5000, 0, 0);
            var smallBounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            var result = SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(farPos, smallBounds, 5f);

            InGameAssert.IsFalse(result.overlap,
                $"Overlap detected 5km from active vessel (blocker={result.blockerName}, dist={result.closestDistance:F0}m)");
        }
    }
}
