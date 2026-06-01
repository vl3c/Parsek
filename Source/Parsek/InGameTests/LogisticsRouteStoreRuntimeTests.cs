using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Stock-runtime check for the <see cref="RouteStore"/> KSP save / load
    /// round-trip. Adds a synthetic route, drives a real
    /// <see cref="GamePersistence.SaveGame"/> to a disposable custom slot,
    /// then walks the resulting .sfs file back through
    /// <see cref="ConfigNode.Load"/> + <see cref="RouteStore.LoadRoutesFrom"/>
    /// to verify the route reappears.
    ///
    /// This exercises the production write path (KSP triggers
    /// <see cref="ParsekScenario.OnSave"/>, which calls <c>SaveRoutesTo</c>)
    /// end-to-end. It deliberately stops short of triggering a full
    /// <see cref="GamePersistence.LoadGame"/> + scene reload: scene reload
    /// destroys the test runner and is fragile to schedule from inside an
    /// in-game test. The save half is the production wiring xUnit cannot
    /// reach; the load half delegates to the same RouteStore.LoadRoutesFrom
    /// codec that <see cref="ParsekScenario.OnLoad"/> invokes, so a reader-side
    /// regression in either the codec or the SCENARIO node shape would surface.
    /// </summary>
    public sealed class LogisticsRouteStoreRuntimeTests
    {
        private const string TestSaveSlotPrefix = "parsek_logistics_ingame_test_";
        private const string ParsekScenarioName = "ParsekScenario";

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Mutates RouteStore + writes a disposable save slot; runs out of band so a parallel batch test cannot read a partially-mutated route list.",
            Description = "Adding a route, saving via KSP's GamePersistence, then loading the persisted SCENARIO node through RouteStore.LoadRoutesFrom reads the route back from the .sfs")]
        public IEnumerator RouteStore_AddRoute_SurvivesKspSaveLoadRoundTrip()
        {
            // PRECONDITION CHECKS -------------------------------------------------
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("HighLogic.CurrentGame is null; cannot drive GamePersistence.SaveGame");
            if (string.IsNullOrEmpty(HighLogic.SaveFolder))
                InGameAssert.Skip("HighLogic.SaveFolder is null/empty; cannot resolve save root");
            if (string.IsNullOrEmpty(KSPUtil.ApplicationRootPath))
                InGameAssert.Skip("KSPUtil.ApplicationRootPath is null/empty; cannot resolve .sfs path");

            string saveSlot = TestSaveSlotPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            string syntheticRouteId = "ingame-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Snapshot the existing route list so we can restore it on teardown
            // regardless of whether the test passes, fails, or skips midway.
            var preExistingRoutes = new List<Route>();
            IReadOnlyList<Route> committed = RouteStore.CommittedRoutes;
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i] != null)
                    preExistingRoutes.Add(committed[i]);
            }
            int preExistingCount = preExistingRoutes.Count;

            ParsekLog.Info("TestRunner",
                $"RouteStore_RoundTrip: preExistingCount={preExistingCount} " +
                $"slot='{saveSlot}' routeId={syntheticRouteId}");

            bool addedToStore = false;
            string savePath = ResolveSavePath(saveSlot);

            try
            {
                // ARRANGE: add a recognizable synthetic route to the in-memory store.
                Route synthetic = BuildSyntheticRoute(syntheticRouteId);
                RouteStore.AddRoute(synthetic);
                addedToStore = true;
                int postAddCount = RouteStore.CommittedRoutes.Count;
                InGameAssert.AreEqual(preExistingCount + 1, postAddCount,
                    $"AddRoute did not extend the committed list (pre={preExistingCount} post={postAddCount})");

                // ACT 1 — drive the real KSP save. GamePersistence.SaveGame fires
                // OnSave on every ScenarioModule (including ParsekScenario), which
                // calls RouteStore.SaveRoutesTo against the ParsekScenario node.
                string saveResult = GamePersistence.SaveGame(saveSlot, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                InGameAssert.IsTrue(!string.IsNullOrEmpty(saveResult),
                    $"GamePersistence.SaveGame returned null/empty for slot '{saveSlot}'");
                ParsekLog.Verbose("TestRunner",
                    $"RouteStore_RoundTrip: GamePersistence.SaveGame returned '{saveResult}'");

                // Yield one frame so any deferred-one-frame writes (FileIOUtils
                // SafeMove etc.) settle before we read the .sfs back.
                yield return null;

                InGameAssert.IsTrue(File.Exists(savePath),
                    $"Expected .sfs at '{savePath}' after GamePersistence.SaveGame");

                // ACT 2 — read the saved .sfs and pull the ParsekScenario node.
                ConfigNode root = ConfigNode.Load(savePath);
                InGameAssert.IsNotNull(root,
                    $"ConfigNode.Load returned null for '{savePath}'");

                ConfigNode parsekScenarioNode = FindParsekScenarioNode(root);
                InGameAssert.IsNotNull(parsekScenarioNode,
                    $"No SCENARIO node named '{ParsekScenarioName}' found in '{savePath}'");

                // ACT 3 — clear in-memory state and reload via the same codec
                // ParsekScenario.OnLoad uses. LoadRoutesFrom wholesale-replaces the
                // committed list, so we do not need to call ResetForTesting first;
                // documenting it inline keeps the post-test cleanup predictable.
                int loadedCount = RouteStore.LoadRoutesFrom(parsekScenarioNode);
                ParsekLog.Verbose("TestRunner",
                    $"RouteStore_RoundTrip: LoadRoutesFrom loaded {loadedCount} route(s) " +
                    $"(expected >= 1 for synthetic + {preExistingCount} pre-existing)");

                // ASSERT: synthetic id round-tripped, route count matches what we
                // wrote, and the loaded route carries the values we set.
                Route reloaded;
                InGameAssert.IsTrue(
                    RouteStore.TryGetRoute(syntheticRouteId, out reloaded),
                    $"Synthetic route id={syntheticRouteId} not present after reload " +
                    $"(loaded={loadedCount} preExisting={preExistingCount})");
                InGameAssert.IsNotNull(reloaded,
                    $"TryGetRoute returned true but route was null for id={syntheticRouteId}");
                InGameAssert.AreEqual(syntheticRouteId, reloaded.Id,
                    "Reloaded route id mismatched");
                InGameAssert.AreEqual("Parsek RouteStore Round-Trip Test", reloaded.Name,
                    "Reloaded route name did not survive the round-trip");
                InGameAssert.AreEqual(preExistingCount + 1, loadedCount,
                    $"LoadRoutesFrom count mismatch (expected={preExistingCount + 1} got={loadedCount})");

                ParsekLog.Info("TestRunner",
                    $"RouteStore_RoundTrip: PASS slot='{saveSlot}' synthetic={syntheticRouteId} " +
                    $"preExisting={preExistingCount} reloaded={loadedCount}");
            }
            finally
            {
                // TEARDOWN: restore in-memory route list to its pre-test state and
                // delete the disposable .sfs slot. Wrapping in finally guarantees
                // we do not leave the synthetic route in CommittedRoutes for the
                // next batch test even if an assert above threw mid-way.
                if (addedToStore)
                {
                    bool removed = RouteStore.RemoveRoute(syntheticRouteId);
                    ParsekLog.Verbose("TestRunner",
                        $"RouteStore_RoundTrip cleanup: RemoveRoute(synthetic)={removed}");

                    // The mid-test LoadRoutesFrom wholesale-replaced the in-memory
                    // list with whatever was in the .sfs SCENARIO node. To be safe,
                    // ensure the pre-existing routes are also back even if the
                    // reload dropped/changed any of them.
                    RestoreRoutes(preExistingRoutes);
                }
                TryDeleteTempSaveSlot(savePath);
            }
        }

        private static Route BuildSyntheticRoute(string id)
        {
            // Minimal valid Route. The codec rejects routes with no STOP children,
            // so we add one trivial stop. Default field values otherwise.
            return new Route
            {
                Id = id,
                Name = "Parsek RouteStore Round-Trip Test",
                RecordingIds = new List<string>(),
                SourceRefs = new List<RouteSourceRef>(),
                Stops = new List<RouteStop>
                {
                    new RouteStop(),
                },
                Status = RouteStatus.Active,
            };
        }

        private static ConfigNode FindParsekScenarioNode(ConfigNode root)
        {
            // .sfs root may or may not be wrapped in a GAME node depending on KSP
            // version + save type; mirror ValidateQuicksaveStructure's tolerant
            // walk so the test does not assume a particular layer count.
            ConfigNode gameNode = root;
            if (gameNode.GetNode("SCENARIO") == null)
            {
                ConfigNode wrapped = root.GetNode("GAME");
                if (wrapped != null)
                    gameNode = wrapped;
            }

            ConfigNode[] scenarioNodes = gameNode.GetNodes("SCENARIO");
            if (scenarioNodes == null)
                return null;

            for (int i = 0; i < scenarioNodes.Length; i++)
            {
                ConfigNode scn = scenarioNodes[i];
                if (scn == null) continue;
                string name = scn.GetValue("name");
                if (string.Equals(name, ParsekScenarioName, StringComparison.Ordinal))
                    return scn;
            }

            return null;
        }

        private static string ResolveSavePath(string slotName)
        {
            return Path.Combine(
                KSPUtil.ApplicationRootPath ?? string.Empty,
                "saves",
                HighLogic.SaveFolder ?? string.Empty,
                (slotName ?? string.Empty) + ".sfs");
        }

        private static void RestoreRoutes(List<Route> preExisting)
        {
            // Wholesale rebuild: clear whatever the mid-test reload left behind,
            // then re-add every route that was in the store when the test started.
            RouteStore.ResetForTesting();
            for (int i = 0; i < preExisting.Count; i++)
            {
                if (preExisting[i] != null)
                    RouteStore.AddRoute(preExisting[i]);
            }
            ParsekLog.Verbose("TestRunner",
                $"RouteStore_RoundTrip cleanup: restored {preExisting.Count} pre-existing route(s)");
        }

        private static void TryDeleteTempSaveSlot(string savePath)
        {
            if (string.IsNullOrEmpty(savePath))
                return;
            try
            {
                if (File.Exists(savePath))
                {
                    File.Delete(savePath);
                    ParsekLog.Verbose("TestRunner",
                        $"RouteStore_RoundTrip cleanup: deleted '{savePath}'");
                }
            }
            catch (IOException ioEx)
            {
                ParsekLog.Warn("TestRunner",
                    $"RouteStore_RoundTrip cleanup: failed to delete '{savePath}': {ioEx.Message}");
            }
            catch (UnauthorizedAccessException accessEx)
            {
                ParsekLog.Warn("TestRunner",
                    $"RouteStore_RoundTrip cleanup: failed to delete '{savePath}': {accessEx.Message}");
            }
        }
    }
}
