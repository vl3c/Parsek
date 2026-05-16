using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Stock-runtime checks for the logistics route-origin-proof PRODUCER
    /// (<see cref="RouteProofCapture.BuildStartRouteOriginProof"/>) that xUnit
    /// cannot prove without live KSP <see cref="Vessel"/> / <see cref="Part"/>
    /// graphs.
    ///
    /// Both tests are read-side: they walk recordings already committed by
    /// a prior in-flight recording session, instead of starting / stopping a
    /// fresh recording from inside the test runner. Starting and stopping
    /// recordings programmatically is destructive (tree creation, chain
    /// orchestration, ledger commits) and can poison the session for the next
    /// batch test; the player can drive the recording through normal gameplay
    /// before invoking the runner, which keeps the runner read-only.
    /// </summary>
    public sealed class LogisticsRouteProducerRuntimeTests
    {
        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            Description = "Producer captured a RouteOriginProof for a recording started while docked to a non-PRELAUNCH partner; proof carries the partner pid and start-transport manifests")]
        public void RouteOriginProof_StartedDockedToNonKsc_ProducerLandsProof()
        {
            // PRECONDITION: a committed recording exists whose RouteOriginProof is
            // non-null and carries a non-zero start-docked origin partner pid. This
            // is what the producer emits when StartRecording fires with the active
            // vessel docked to a non-PRELAUNCH partner. The test reads back the
            // pre-existing recording rather than driving StartRecording itself,
            // because programmatic recording start/stop tangles with the chain /
            // tree orchestration and is not safe to run inside the test runner.
            string treeId;
            Recording recording = TryFindRecordingWithOriginProof(out treeId);
            if (recording == null)
            {
                ParsekLog.Verbose("TestRunner",
                    "RouteOriginProof_StartedDockedToNonKsc: skipping — no recording with a non-null " +
                    "RouteOriginProof was found across active / pending / committed trees");
                InGameAssert.Skip(
                    "No recording with a captured RouteOriginProof. Set up: record a mission that " +
                    "started with the active vessel docked to a non-PRELAUNCH partner, commit the " +
                    "tree, then run this test.");
            }

            RouteOriginProof proof = recording.RouteOriginProof;
            InGameAssert.IsNotNull(proof,
                $"RouteOriginProof must be non-null (tree={treeId} rec={recording.RecordingId})");
            InGameAssert.AreNotEqual(0u, proof.StartDockedOriginVesselPid,
                $"RouteOriginProof.StartDockedOriginVesselPid must be a real partner pid " +
                $"(tree={treeId} rec={recording.RecordingId})");

            int startResCount = proof.StartTransportResources != null
                ? proof.StartTransportResources.Count
                : 0;
            int startInvCount = proof.StartTransportInventory != null
                ? proof.StartTransportInventory.Count
                : 0;
            int endResCount = proof.EndTransportResources != null
                ? proof.EndTransportResources.Count
                : 0;
            int endInvCount = proof.EndTransportInventory != null
                ? proof.EndTransportInventory.Count
                : 0;

            // The producer always populates StartTransportResources alongside the
            // partner pid (see RouteProofCapture.BuildStartRouteOriginProof
            // Captured branch). Empty manifests would indicate the snapshot/PID
            // extraction silently dropped data and is the regression this test
            // is designed to catch.
            InGameAssert.IsNotNull(proof.StartTransportResources,
                $"RouteOriginProof.StartTransportResources must be populated " +
                $"(tree={treeId} rec={recording.RecordingId})");

            // The recording is committed, so BuildCaptureRecording's end-manifest
            // forwarding path should have run too. Assert non-null to confirm
            // AttachEndManifestsAndForwardToCapture fired with the same part-pid
            // set captured at start.
            InGameAssert.IsNotNull(proof.EndTransportResources,
                $"RouteOriginProof.EndTransportResources must be populated after recording committed " +
                $"(tree={treeId} rec={recording.RecordingId})");

            ParsekLog.Info("TestRunner",
                $"RouteOriginProof_NonKsc: tree={treeId} rec={recording.RecordingId} " +
                $"partnerPid={proof.StartDockedOriginVesselPid} " +
                $"startRes={startResCount} startInv={startInvCount} " +
                $"endRes={endResCount} endInv={endInvCount}");
        }

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            Description = "Producer skipped RouteOriginProof for a recording started on the runway / pad (PRELAUNCH); recording has no RouteOriginProof and the ActiveVesselPrelaunch path was taken")]
        public void RouteOriginProof_StartedOnRunway_ProducerSkipsProof()
        {
            // PRECONDITION: a committed recording exists whose StartSituation
            // (captured at record_start by FlightRecorder via VesselSpawner.HumanizeSituation)
            // equals "Prelaunch". Same read-side pattern as the docked-origin
            // sibling test: the runner inspects what gameplay already produced
            // rather than driving StartRecording itself.
            string treeId;
            Recording recording = TryFindRecordingStartedPrelaunch(out treeId);
            if (recording == null)
            {
                ParsekLog.Verbose("TestRunner",
                    "RouteOriginProof_StartedOnRunway: skipping — no recording with " +
                    "StartSituation == \"Prelaunch\" was found across active / pending / committed trees");
                InGameAssert.Skip(
                    "No committed recording started in PRELAUNCH. Set up: launch a vessel from the " +
                    "pad / runway, record + commit a short flight, then run this test.");
            }

            // The PRELAUNCH branch of RouteProofCapture.TryResolveStartDockedOriginPartner
            // returns ActiveVesselPrelaunch -> no proof is captured -> recording.RouteOriginProof
            // stays null. Asserting null distinguishes "producer regressed and
            // captured a stray proof from a PRELAUNCH start" from a passing test.
            InGameAssert.IsNull(recording.RouteOriginProof,
                $"RouteOriginProof must be null for a recording started in PRELAUNCH " +
                $"(tree={treeId} rec={recording.RecordingId} startSituation='{recording.StartSituation}')");

            // We deliberately do NOT assert on the Verbose log line that the
            // producer emits ("RouteOriginProof skipped: active vessel PRELAUNCH ..."):
            // the in-game runtime has no equivalent of ParsekLog.TestSinkForTesting,
            // and grepping the live KSP.log in-process is fragile. The proof-null
            // assertion alone suffices to catch the regression target — a producer
            // that silently captured a RouteOriginProof for a PRELAUNCH start.
            ParsekLog.Info("TestRunner",
                $"RouteOriginProof_Prelaunch: tree={treeId} rec={recording.RecordingId} " +
                $"startSituation='{recording.StartSituation}' proof=null (expected)");
        }

        private static Recording TryFindRecordingWithOriginProof(out string treeId)
        {
            foreach (RecordingTreeRuntimeView view in EnumerateTrees())
            {
                if (view.Tree?.Recordings == null)
                    continue;

                foreach (Recording recording in view.Tree.Recordings.Values)
                {
                    if (recording?.RouteOriginProof == null)
                        continue;
                    if (recording.RouteOriginProof.StartDockedOriginVesselPid == 0)
                        continue;

                    treeId = view.Tree.Id;
                    return recording;
                }
            }

            treeId = null;
            return null;
        }

        private static Recording TryFindRecordingStartedPrelaunch(out string treeId)
        {
            foreach (RecordingTreeRuntimeView view in EnumerateTrees())
            {
                if (view.Tree?.Recordings == null)
                    continue;

                foreach (Recording recording in view.Tree.Recordings.Values)
                {
                    if (recording == null)
                        continue;
                    // FlightRecorder writes the humanized vessel.situation string at
                    // record_start; VesselSpawner.HumanizeSituation(Vessel.Situations.PRELAUNCH)
                    // returns "Prelaunch". This matches Recording.IsPlayerLaunched's
                    // own PRELAUNCH probe (Recording.cs:1059).
                    if (!string.Equals(recording.StartSituation, "Prelaunch", System.StringComparison.Ordinal))
                        continue;

                    treeId = view.Tree.Id;
                    return recording;
                }
            }

            treeId = null;
            return null;
        }

        private static IEnumerable<RecordingTreeRuntimeView> EnumerateTrees()
        {
            // Mirrors the enumeration pattern in LogisticsRouteProofRuntimeTests:
            // start with the active in-flight tree, then the pending tree (between
            // record-stop and commit), then committed trees. Deduplicates by
            // reference so a tree that was just promoted into Committed is only
            // walked once.
            var seen = new HashSet<RecordingTree>();

            RecordingTree activeTree = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (activeTree != null && seen.Add(activeTree))
                yield return new RecordingTreeRuntimeView { Tree = activeTree, Source = "active" };

            RecordingTree pendingTree = RecordingStore.PendingTree;
            if (pendingTree != null && seen.Add(pendingTree))
                yield return new RecordingTreeRuntimeView { Tree = pendingTree, Source = "pending" };

            List<RecordingTree> committed = RecordingStore.CommittedTrees;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    RecordingTree tree = committed[i];
                    if (tree == null) continue;
                    if (!seen.Add(tree)) continue;
                    yield return new RecordingTreeRuntimeView { Tree = tree, Source = "committed" };
                }
            }
        }

        private struct RecordingTreeRuntimeView
        {
            public RecordingTree Tree;
            public string Source;
        }
    }
}
