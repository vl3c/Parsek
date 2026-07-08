using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    // M-MIS-10 archetype 4 (claw couples - the 2026-07-06 verification sweep's highest-risk
    // AUTO-NONE cell): the live-KSP half of the claw/asteroid coverage. The pure recorder seams
    // (Dock-equivalent BuildMergeBranchData, couple-event partner resolution, breakup-scan
    // rejection, TryExtractPartName) are locked headlessly in ClawCoupleRecordingTests; what
    // ONLY a live game can prove is the PartLoader half of the snapshot part-name path and the
    // ghost-visual build over a coupled claw+asteroid snapshot:
    //
    //  (1) "PotatoRoid" (the asteroid part KSP couples to the claw) and "GrapplingDevice" (the
    //      Advanced Grabbing Unit) resolve through PartLoader.getPartInfoByName /
    //      GhostVisualBuilder.ResolveAvailablePart, and the underscore->dot conversion leg of
    //      ResolveAvailablePart resolves a real dotted stock part from its underscored cfg form
    //      (the CLAUDE.md "part names" gotcha).
    //  (2) A synthesized post-grab merged-vessel snapshot (pod + claw + PotatoRoid, the same
    //      shape ClawedAsteroidShip generates for the headless suite) SURVIVES ghost-visual
    //      building: every part name resolves a prefab (skippedPrefab == 0), the pod+claw
    //      contribute visuals, and the build completes a real ghost root. Whether the PotatoRoid
    //      prefab itself contributes a MESH is prefab-dependent (stock asteroids generate their
    //      procedural mesh via ModuleAsteroid at runtime), so that half is REPORTED in the log,
    //      not asserted - the sweep's claim under test is "survives ghost-visual building".
    //
    // HONEST SCOPE: neither test performs a LIVE claw grab (spawning an asteroid + flying a
    // grapple intercept is not automatable reliably in the runner); the recorder-side couple
    // flow for a real grab stays operator-run via the M-MIS-10 runbook (label mmis10-claw).
    // Career-independent; FLIGHT only (PartLoader + prefab instantiation need a loaded game DB).
    public class ClawCoupleInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "ClawCouple", Scene = GameScenes.FLIGHT,
            Description = "PotatoRoid (grabbed asteroid) and GrapplingDevice (Advanced Grabbing Unit) "
                + "resolve through PartLoader.getPartInfoByName / ResolveAvailablePart, and the "
                + "underscore->dot leg of the snapshot part-name path resolves a real dotted stock part "
                + "(M-MIS-10 archetype 4; the live-grab half stays operator-run, runbook mmis10-claw)")]
        public void PotatoRoidAndClaw_ResolveThroughPartLoader()
        {
            if (PartLoader.Instance == null || PartLoader.LoadedPartsList == null
                || PartLoader.LoadedPartsList.Count == 0)
            {
                InGameAssert.Skip("PartLoader not ready (no loaded parts list)");
                return;
            }

            // The asteroid part: cfg name "PotatoRoid" has no underscore, so cfg and runtime
            // names coincide - both the direct PartLoader lookup and the ghost resolver must hit.
            AvailablePart roidDirect = PartLoader.getPartInfoByName("PotatoRoid");
            InGameAssert.IsNotNull(roidDirect,
                "PartLoader.getPartInfoByName(\"PotatoRoid\") must resolve the stock asteroid part");
            AvailablePart roid = GhostVisualBuilder.ResolveAvailablePart("PotatoRoid");
            InGameAssert.IsNotNull(roid, "ResolveAvailablePart(\"PotatoRoid\") must resolve");
            InGameAssert.AreEqual("PotatoRoid", roid.name,
                "the resolved asteroid part must be PotatoRoid itself, not a fuzzy match");
            InGameAssert.IsNotNull(roid.partPrefab, "PotatoRoid must carry a part prefab");

            // A snapshot-style trailing uid suffix must strip and still resolve.
            AvailablePart roidSuffixed = GhostVisualBuilder.ResolveAvailablePart(
                GhostVisualBuilder.TryExtractPartName("PotatoRoid_4289156007"));
            InGameAssert.IsNotNull(roidSuffixed,
                "\"PotatoRoid_<uid>\" must strip the suffix and resolve");

            // The claw (Advanced Grabbing Unit).
            AvailablePart claw = GhostVisualBuilder.ResolveAvailablePart("GrapplingDevice");
            InGameAssert.IsNotNull(claw,
                "ResolveAvailablePart(\"GrapplingDevice\") must resolve the Advanced Grabbing Unit");
            InGameAssert.IsNotNull(claw.partPrefab, "GrapplingDevice must carry a part prefab");

            // The underscore->dot conversion leg (CLAUDE.md gotcha: cfg "solidBooster_sm_v2" ->
            // runtime "solidBooster.sm.v2"). Only asserted when the dotted part exists in this
            // install (stock 1.12); skipping keeps the test honest on trimmed part packs.
            AvailablePart dotted = PartLoader.getPartInfoByName("solidBooster.sm.v2");
            if (dotted != null)
            {
                AvailablePart viaUnderscore = GhostVisualBuilder.ResolveAvailablePart("solidBooster_sm_v2");
                InGameAssert.IsNotNull(viaUnderscore,
                    "the underscored cfg form must resolve through the dotted-conversion leg");
                InGameAssert.AreEqual(dotted.name, viaUnderscore.name,
                    "underscored lookup must resolve to the same dotted runtime part");
            }

            ParsekLog.Info("TestRunner",
                $"ClawCouple part-name path: PotatoRoid ok (prefab='{roid.partPrefab.name}'), "
                + $"GrapplingDevice ok (prefab='{claw.partPrefab.name}'), "
                + $"underscore->dot leg {(dotted != null ? "verified" : "skipped (no solidBooster.sm.v2)")}");
        }

        [InGameTest(Category = "ClawCouple", Scene = GameScenes.FLIGHT,
            Description = "A synthesized post-grab merged-vessel snapshot (pod + claw + PotatoRoid) "
                + "survives ghost-visual building: all three part names resolve prefabs "
                + "(skippedPrefab == 0) and the build completes a real ghost root; the asteroid's "
                + "procedural-mesh contribution is reported, not asserted (M-MIS-10 archetype 4)")]
        public void ClawedAsteroidSnapshot_SurvivesGhostVisualBuild()
        {
            if (PartLoader.Instance == null || PartLoader.LoadedPartsList == null
                || PartLoader.LoadedPartsList.Count == 0)
            {
                InGameAssert.Skip("PartLoader not ready (no loaded parts list)");
                return;
            }

            // The same coupled shape the headless generator (ClawedAsteroidShip) produces:
            // pod root, claw below it, PotatoRoid coupled through the claw.
            ConfigNode snapshot = BuildClawedAsteroidSnapshot();
            var rec = new Recording
            {
                RecordingId = "ingame-claw-asteroid-ghost",
                VesselName = "Parsek Claw Test",
                VesselPersistentId = 987654321u,
                GhostVisualSnapshot = snapshot
            };

            PendingGhostVisualBuild build = GhostVisualBuilder.TryBeginTimelineGhostBuild(
                rec, snapshot, "ParsekTest_ClawAsteroidGhost",
                HeaviestSpawnBuildType.RecordingStartSnapshot);
            InGameAssert.IsNotNull(build, "ghost build must begin for a 3-part coupled snapshot");

            GameObject rootToDestroy = build.root;
            try
            {
                GhostVisualBuilder.AdvanceTimelineGhostBuild(build, long.MaxValue);

                InGameAssert.AreEqual(0, build.skippedName,
                    "every PART node must carry an extractable part name");
                InGameAssert.AreEqual(0, build.skippedPrefab,
                    "every coupled part - including PotatoRoid - must resolve a prefab through the "
                    + "snapshot part-name path (a skip here means the asteroid failed name resolution)");
                InGameAssert.IsGreaterThan(build.visualCount, 1,
                    "the pod and claw must contribute ghost visuals");

                GhostBuildResult result = GhostVisualBuilder.CompleteTimelineGhostBuild(build, rec);
                InGameAssert.IsNotNull(result,
                    "the coupled claw+asteroid snapshot must complete a ghost build");
                InGameAssert.IsNotNull(result.root, "the completed ghost must carry a root GameObject");
                rootToDestroy = result.root;

                // The asteroid's own mesh contribution is prefab-dependent (ModuleAsteroid builds
                // the procedural mesh at runtime, so the prefab may carry none) - report it.
                bool asteroidContributedMesh = build.visualCount >= 3;
                ParsekLog.Info("TestRunner",
                    $"ClawedAsteroidSnapshot ghost build: visuals={build.visualCount.ToString(IC)}/3 "
                    + $"skippedMesh={build.skippedMesh.ToString(IC)} "
                    + $"potatoRoidMesh={(asteroidContributedMesh ? "yes" : "no (procedural at runtime - prefab has no static mesh)")}");
            }
            finally
            {
                if (rootToDestroy != null)
                    Object.Destroy(rootToDestroy);
            }
        }

        // A minimal post-grab merged-vessel snapshot: only the keys the ghost build path reads
        // (PART name/persistentId/position/rotation + the VESSEL root index). Mirrors the
        // headless Tests/Generators/VesselSnapshotBuilder.ClawedAsteroidShip shape (the generator
        // itself lives in the test assembly and is not available in-game).
        private static ConfigNode BuildClawedAsteroidSnapshot()
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("pid", 987654321u.ToString("x8", IC).PadLeft(32, '0'));
            v.AddValue("persistentId", 987654321u.ToString(IC));
            v.AddValue("name", "Parsek Claw Test");
            v.AddValue("type", "Ship");
            v.AddValue("sit", "ORBITING");
            v.AddValue("root", "0");
            v.AddValue("CoM", "0,0,0");

            AddPart(v, 0, "mk1pod.v2", "0,0,0");
            AddPart(v, 1, "GrapplingDevice", "0,-1.0,0");
            AddPart(v, 2, "PotatoRoid", "0,-3.2,0", parentIndex: 1);
            return v;
        }

        private static void AddPart(ConfigNode vessel, int index, string partName,
            string position, int parentIndex = 0)
        {
            var part = new ConfigNode("PART");
            uint pid = (uint)(100000 + index * 1111);
            part.AddValue("name", partName);
            part.AddValue("uid", pid.ToString(IC));
            part.AddValue("persistentId", pid.ToString(IC));
            part.AddValue("parent", parentIndex.ToString(IC));
            part.AddValue("position", position);
            part.AddValue("rotation", "0,0,0,1");
            part.AddValue("rTrf", partName);
            vessel.AddNode(part);
        }
    }
}
