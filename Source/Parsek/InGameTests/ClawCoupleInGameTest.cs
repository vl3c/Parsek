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
    //      shape ClawedAsteroidShip generates for the headless suite, with the ModuleAsteroid
    //      seed / prefabBaseURL / currentState stock persists) SURVIVES ghost-visual building
    //      AND the rock replays as itself: the ghost carries the rock stock's own procedural
    //      generator builds from the recorded seed (not the prefab's small placeholder lump),
    //      and its bounds match the rock stock builds for that seed within 1%. A lone comet
    //      snapshot (PotatoComet + ModuleComet) is held to the same bar (ROCK-GHOST-PREFAB-MESH).
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
            Description = "A synthesized post-grab merged-vessel snapshot (pod + claw + PotatoRoid "
                + "with its ModuleAsteroid seed) survives ghost-visual building and the asteroid "
                + "replays as the rock stock generates from that seed (bounds within 1% of stock's), "
                + "not the prefab placeholder; a lone PotatoComet snapshot likewise "
                + "(M-MIS-10 archetype 4, ROCK-GHOST-PREFAB-MESH)")]
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
                InGameAssert.AreEqual(3, build.visualCount,
                    "the pod, the claw and the asteroid must each contribute ghost visuals");

                GhostBuildResult result = GhostVisualBuilder.CompleteTimelineGhostBuild(build, rec);
                InGameAssert.IsNotNull(result,
                    "the coupled claw+asteroid snapshot must complete a ghost build");
                InGameAssert.IsNotNull(result.root, "the completed ghost must carry a root GameObject");
                rootToDestroy = result.root;

                AssertRockGhostMatchesStock(result.root, SpaceObjectKind.Asteroid, "PotatoRoid",
                    AsteroidPartPid, AsteroidSeed, AsteroidUrl);
            }
            finally
            {
                if (rootToDestroy != null)
                    Object.Destroy(rootToDestroy);
            }

            // A comet replays the same way (a released comet's own recording, a claimed comet's
            // hold ghost). Its tail and coma need a live CometVessel and are not part of a ghost.
            if (PartLoader.getPartInfoByName("PotatoComet") == null)
            {
                ParsekLog.Info("TestRunner",
                    "ClawCouple rock ghost: kind=comet skipped (no PotatoComet part in this install)");
                return;
            }
            ConfigNode cometSnapshot = BuildLoneCometSnapshot();
            var cometRec = new Recording
            {
                RecordingId = "ingame-comet-ghost",
                VesselName = "Parsek Comet Test",
                VesselPersistentId = 987654322u,
                GhostVisualSnapshot = cometSnapshot
            };
            GhostBuildResult cometResult = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                cometRec, "ParsekTest_CometGhost");
            try
            {
                InGameAssert.IsNotNull(cometResult, "the lone comet snapshot must complete a ghost build");
                InGameAssert.IsNotNull(cometResult.root, "the comet ghost must carry a root GameObject");
                AssertRockGhostMatchesStock(cometResult.root, SpaceObjectKind.Comet, "PotatoComet",
                    CometPartPid, CometSeed, CometUrl);
            }
            finally
            {
                if (cometResult != null && cometResult.root != null)
                    Object.Destroy(cometResult.root);
            }
        }

        private const uint AsteroidPartPid = 102222u;   // index 2 under the 100000 + idx*1111 scheme
        private const int AsteroidSeed = 27182818;
        private const string AsteroidUrl = "Procedural/PA_E";
        private const uint CometPartPid = 100000u;
        private const int CometSeed = 16180339;
        private const string CometUrl = "Procedural/PC_C";

        // The ghost rock must be the stock-generator path (a "ParsekRock" child in place of the
        // cloned "potatoroid" placeholder) and its mesh bounds, in the part's frame, must match
        // the rock stock's own generator builds for the recorded seed within 1% per axis.
        private static void AssertRockGhostMatchesStock(GameObject ghostRoot, SpaceObjectKind kind,
            string partName, uint partPid, int seed, string url)
        {
            Transform part = GhostVisualBuilder.FindGhostPartTransform(ghostRoot, partPid);
            InGameAssert.IsNotNull(part, $"the {partName} ghost part pid={partPid} must exist");

            Transform rock = part.Find(SpaceObjectGhostVisual.RockObjectName);
            Transform placeholder = GhostVisualBuilder.FindTransformRecursive(part, "potatoroid");
            string path = rock != null ? "stock-generator"
                : (placeholder != null ? "placeholder" : "none");
            InGameAssert.IsNotNull(rock,
                $"the {partName} ghost must carry the rock stock's generator builds from the recorded "
                + $"seed (path={path}); see the 'RockGhost built:' line in KSP.log for the reason");
            InGameAssert.IsNull(placeholder,
                $"the {partName} ghost must not also clone the prefab's placeholder lump");

            MeshFilter rockFilter = rock.GetComponent<MeshFilter>();
            InGameAssert.IsTrue(rockFilter != null && rockFilter.sharedMesh != null,
                "the ghost rock must carry a mesh");
            InGameAssert.IsNotNull(rock.GetComponent<MeshRenderer>(), "the ghost rock must render");
            InGameAssert.IsNull(rock.GetComponent<Collider>(), "a ghost rock must carry no collider");
            Vector3 ghostExtents = Vector3.Scale(rockFilter.sharedMesh.bounds.extents, rock.lossyScale);

            Part prefab = PartLoader.getPartInfoByName(partName)?.partPrefab;
            float radius;
            Vector3 stockExtents;
            string reason;
            bool referenced = TryBuildStockReference(kind, prefab, url, seed,
                out radius, out stockExtents, out reason);
            InGameAssert.IsTrue(referenced,
                $"stock's own generator must build the reference {partName} rock for url={url} ({reason})");

            for (int axis = 0; axis < 3; axis++)
            {
                float tolerance = 0.01f * Mathf.Max(stockExtents[axis], 0.01f);
                InGameAssert.IsTrue(Mathf.Abs(ghostExtents[axis] - stockExtents[axis]) <= tolerance,
                    $"{partName} ghost bounds axis {axis} = {ghostExtents[axis].ToString("F3", IC)} m "
                    + $"must match stock's rock {stockExtents[axis].ToString("F3", IC)} m within 1% "
                    + $"(seed={seed.ToString(IC)} radius={radius.ToString("F2", IC)})");
            }
            float meanExtent = (ghostExtents.x + ghostExtents.y + ghostExtents.z) / 3f;
            InGameAssert.IsTrue(meanExtent >= 0.5f * radius && meanExtent <= 2f * radius,
                $"{partName} ghost mean half-extent {meanExtent.ToString("F2", IC)} m must be the "
                + $"size of the recorded rock (radius {radius.ToString("F2", IC)} m)");

            ParsekLog.Info("TestRunner",
                $"ClawCouple rock ghost: kind={SpaceObjectGhostModel.KindToken(kind)} path={path} "
                + $"seed={seed.ToString(IC)} url={url} radius={radius.ToString("F2", IC)} "
                + $"ghostExtents={FormatExtents(ghostExtents)} stockExtents={FormatExtents(stockExtents)}");
        }

        // Stock's ModuleAsteroid / ModuleComet OnStart draw, run directly against stock's API
        // (Unity's Random state restored), so the ghost is measured against stock's own rock.
        private static bool TryBuildStockReference(SpaceObjectKind kind, Part prefab, string url, int seed,
            out float radius, out Vector3 extents, out string reason)
        {
            radius = 0f;
            extents = Vector3.zero;
            reason = null;
            ProceduralSpaceObject proc = kind == SpaceObjectKind.Comet
                ? (ProceduralSpaceObject)Resources.Load<ProceduralComet>(url)
                : Resources.Load<ProceduralAsteroid>(url);
            if (proc == null)
            {
                reason = "procedural prefab not found";
                return false;
            }
            float min = 0.75f, max = 1.25f;
            ModuleAsteroid asteroid = prefab != null ? prefab.FindModuleImplementing<ModuleAsteroid>() : null;
            ModuleComet comet = prefab != null ? prefab.FindModuleImplementing<ModuleComet>() : null;
            if (asteroid != null) { min = asteroid.minRadiusMultiplier; max = asteroid.maxRadiusMultiplier; }
            if (comet != null) { min = comet.minRadiusMultiplier; max = comet.maxRadiusMultiplier; }

            Random.State saved = Random.state;
            PSpaceObject generated = null;
            try
            {
                Random.InitState(seed);
                radius = proc.radius * Random.Range(min, max);
                System.Func<Transform, float> rangefinder = t => float.MaxValue;
                Callback done = () => { };
                generated = kind == SpaceObjectKind.Comet
                    ? (PSpaceObject)((ProceduralComet)proc).Generate(seed, radius, null, rangefinder, done, false, false)
                    : ((ProceduralAsteroid)proc).Generate(seed, radius, null, rangefinder, done, false);
                MeshFilter mf = generated != null && generated.VisualRenderer != null
                    ? generated.VisualRenderer.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null)
                {
                    reason = "generator built no visual mesh";
                    return false;
                }
                extents = mf.sharedMesh.bounds.extents;
                return true;
            }
            finally
            {
                Random.state = saved;
                if (generated != null)
                {
                    generated.gameObject.SetActive(false);
                    Object.Destroy(generated.gameObject);
                }
            }
        }

        private static string FormatExtents(Vector3 v)
        {
            return v.x.ToString("F2", IC) + "," + v.y.ToString("F2", IC) + "," + v.z.ToString("F2", IC);
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
            ConfigNode roid = AddPart(v, 2, "PotatoRoid", "0,-3.2,0", parentIndex: 1);
            AddStockModule(roid, "ModuleAsteroid",
                "seed", AsteroidSeed.ToString(IC),
                "AsteroidName", "Ast. HSJ-227",
                "prefabBaseURL", AsteroidUrl,
                "currentState", "1");
            return v;
        }

        // A lone comet vessel; mirrors the headless VesselSnapshotBuilder.LoneComet shape.
        private static ConfigNode BuildLoneCometSnapshot()
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("pid", 987654322u.ToString("x8", IC).PadLeft(32, '0'));
            v.AddValue("persistentId", 987654322u.ToString(IC));
            v.AddValue("name", "Parsek Comet Test");
            v.AddValue("type", "SpaceObject");
            v.AddValue("sit", "ORBITING");
            v.AddValue("root", "0");
            v.AddValue("CoM", "0,0,0");
            ConfigNode comet = AddPart(v, 0, "PotatoComet", "0,0,0");
            AddStockModule(comet, "ModuleComet",
                "seed", CometSeed.ToString(IC),
                "CometName", "Parsek Comet Test",
                "CometType", "intermediate",
                "prefabBaseURL", CometUrl,
                "currentState", "1",
                "utFragmentSpawned", "0");
            return v;
        }

        // A PartModule MODULE node as PartModule.Save writes it (name, isEnabled, the
        // isPersistant KSPFields, stagingEnabled, then the EVENTS / ACTIONS / UPGRADESAPPLIED nodes).
        private static void AddStockModule(ConfigNode part, string moduleName, params string[] keyValues)
        {
            ConfigNode module = part.AddNode("MODULE");
            module.AddValue("name", moduleName);
            module.AddValue("isEnabled", "True");
            for (int i = 0; i + 1 < keyValues.Length; i += 2)
                module.AddValue(keyValues[i], keyValues[i + 1]);
            module.AddValue("stagingEnabled", "True");
            module.AddNode("EVENTS");
            module.AddNode("ACTIONS");
            module.AddNode("UPGRADESAPPLIED");
        }

        private static ConfigNode AddPart(ConfigNode vessel, int index, string partName,
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
            return part;
        }
    }
}
