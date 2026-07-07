using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Stock-runtime checks for the claw/grapple connection producer
    /// (docs/dev/design-logistics-claw-producer.md section 6) that xUnit cannot
    /// prove without live KSP: classifier behavior on REAL part prefabs
    /// (module lists come from loaded parts), PotatoRoid PartLoader
    /// resolution, and the live-recorded grapple window shape. The physics
    /// half (a real claw contact capture at 0.06 m and the release) is
    /// operator territory: those tests Skip with a runbook line when the save
    /// carries no claw evidence yet.
    /// </summary>
    public sealed class LogisticsGrappleRuntimeTests
    {
        private const string ClawPartName = "GrapplingDevice";
        private const string AsteroidPartName = "PotatoRoid";
        private const string DockPortPartName = "dockingPort2";
        private const string PodPartName = "mk1pod.v2";

        [InGameTest(Category = "LogisticsGrapple", Scene = GameScenes.FLIGHT,
            Description = "ConnectionProducerClassifier classifies real loaded part prefabs: claw couples are Grapple from either side, dock pairs are DockingPort, moduleless pairs are Unknown")]
        public void Classifier_LivePrefabs_ClassifyByProducerModule()
        {
            Part claw = PrefabPart(ClawPartName);
            Part dock = PrefabPart(DockPortPartName);
            Part pod = PrefabPart(PodPartName);
            Part asteroid = PrefabPart(AsteroidPartName);

            InGameAssert.IsNotNull(claw, $"claw prefab '{ClawPartName}' must resolve");
            InGameAssert.IsNotNull(dock, $"dock prefab '{DockPortPartName}' must resolve");
            InGameAssert.IsNotNull(pod, $"pod prefab '{PodPartName}' must resolve");
            InGameAssert.IsNotNull(asteroid, $"asteroid prefab '{AsteroidPartName}' must resolve");

            // Claw on either onPartCouple end -> Grapple; the grabbed side is
            // an arbitrary part (findings 1.3: from/to order depends on
            // vessel dominance).
            InGameAssert.AreEqual(RouteConnectionKind.Grapple,
                ConnectionProducerClassifier.Classify(asteroid, claw),
                "asteroid->claw must classify Grapple");
            InGameAssert.AreEqual(RouteConnectionKind.Grapple,
                ConnectionProducerClassifier.Classify(claw, pod),
                "claw->pod must classify Grapple");
            InGameAssert.AreEqual(RouteConnectionKind.DockingPort,
                ConnectionProducerClassifier.Classify(dock, dock),
                "dock->dock must classify DockingPort");
            InGameAssert.AreEqual(RouteConnectionKind.Unknown,
                ConnectionProducerClassifier.Classify(pod, asteroid),
                "moduleless couple must classify Unknown (fail closed)");

            ParsekLog.Info("InGameTest",
                "[LogisticsGrapple] classifier prefab truth table verified " +
                $"(claw={ClawPartName}, dock={DockPortPartName}, pod={PodPartName}, asteroid={AsteroidPartName})");
        }

        [InGameTest(Category = "LogisticsGrapple", Scene = GameScenes.FLIGHT,
            Description = "PotatoRoid resolves through PartLoader and the snapshot part-name path (suffix strip + no-op dot conversion)")]
        public void PotatoRoid_PartLoaderAndSnapshotNameResolve()
        {
            AvailablePart direct = PartLoader.getPartInfoByName(AsteroidPartName);
            InGameAssert.IsNotNull(direct,
                $"PartLoader.getPartInfoByName(\"{AsteroidPartName}\") must resolve");

            // The snapshot path: a stored name like "PotatoRoid_4294590964"
            // suffix-strips to the exact loader name; the underscore-dot
            // conversion is a no-op for it.
            string extracted = GhostVisualBuilder.TryExtractPartName(
                AsteroidPartName + "_4294590964");
            InGameAssert.AreEqual(AsteroidPartName, extracted,
                "snapshot suffix strip must yield the loader name");
            AvailablePart resolved = GhostVisualBuilder.ResolveAvailablePart(extracted);
            InGameAssert.IsNotNull(resolved,
                "ResolveAvailablePart must resolve the stripped asteroid name");
            InGameAssert.AreEqual(direct.name, resolved.name,
                "resolved asteroid part must be the PartLoader entry");

            ParsekLog.Info("InGameTest",
                $"[LogisticsGrapple] PotatoRoid part-name path verified (name={resolved.name})");
        }

        [InGameTest(Category = "LogisticsGrapple", Scene = GameScenes.FLIGHT,
            Description = "A live-recorded claw couple produced a route connection window stamped Grapple (operator: run the mmis10-claw playtest first)")]
        public void GrappleWindow_LiveRecordedClawCouple_StampedGrapple()
        {
            RouteConnectionWindow found = FindWindowOfKind(RouteConnectionKind.Grapple);
            if (found == null)
            {
                InGameAssert.Skip(
                    "No recorded Grapple window in this save. Operator runbook (label mmis10-claw): " +
                    "start recording, grab an asteroid or derelict with the Advanced Grabbing Unit, " +
                    "then re-run. Grep: 'OnPartCouple producer classified' and " +
                    "'Route proof dock window captured' with kind=Grapple.");
            }

            InGameAssert.AreEqual(RouteConnectionKind.Grapple, found.TransferKind,
                "live claw window must be stamped Grapple");
            InGameAssert.IsTrue(found.TransferTargetVesselPid != 0,
                "live claw window must carry the grabbed vessel's pid as endpoint");
            ParsekLog.Info("InGameTest",
                $"[LogisticsGrapple] live grapple window verified window={found.WindowId} " +
                $"targetPid={found.TransferTargetVesselPid} complete={found.IsComplete}");
        }

        private static Part PrefabPart(string name)
        {
            AvailablePart info = PartLoader.getPartInfoByName(name);
            return info != null ? info.partPrefab : null;
        }

        private static RouteConnectionWindow FindWindowOfKind(RouteConnectionKind kind)
        {
            int scanned = 0;
            RouteConnectionWindow found = null;
            IReadOnlyList<Recording> committed = EffectiveState.ComputeERS();
            if (committed != null)
            {
                for (int i = 0; i < committed.Count && found == null; i++)
                {
                    found = WindowOfKind(committed[i], kind, ref scanned);
                }
            }
            RecordingTree tree = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (found == null && tree?.Recordings != null)
            {
                foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
                {
                    found = WindowOfKind(kvp.Value, kind, ref scanned);
                    if (found != null)
                        break;
                }
            }
            ParsekLog.Verbose("InGameTest",
                $"[LogisticsGrapple] window scan kind={kind} scanned={scanned} found={(found != null)}");
            return found;
        }

        private static RouteConnectionWindow WindowOfKind(
            Recording rec, RouteConnectionKind kind, ref int scanned)
        {
            if (rec?.RouteConnectionWindows == null)
                return null;
            for (int i = 0; i < rec.RouteConnectionWindows.Count; i++)
            {
                RouteConnectionWindow w = rec.RouteConnectionWindows[i];
                if (w == null)
                    continue;
                scanned++;
                if (w.TransferKind == kind)
                    return w;
            }
            return null;
        }
    }
}
