using System;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate for the WATCH-LOOPED-PARK-TARGET-LOSS-NRE-STORM fix.
    ///
    /// <para>Every behavioural cell in <see cref="WatchModeTargetLossTests"/> drives the pure
    /// classifiers directly, and every PRODUCTION site that consumes them lives inside
    /// per-frame / Unity-event code no headless cell can run. So deleting any one of the three
    /// wiring lines - the destroy-time bridge call, the camera-infra loss routing through the
    /// classifier, the fallback consuming the retarget's return value - restores the defect
    /// with the whole xUnit suite still green. This gate is what closes that silent-revert
    /// hole: it pins the WIRING, not behaviour the other file already covers.</para>
    ///
    /// <para>Mirrors <c>SoiSeamProducerWiringGateTests</c> / <c>PolylineDriverWalkDeleteGateTests</c>
    /// in mechanism (read the source, strip line comments, assert over one brace-matched
    /// method body). Comment stripping matters here: the fix's own comments name the pre-fix
    /// shapes on purpose, and a raw scan would read those pointers as code.</para>
    /// </summary>
    public class WatchModeTargetLossWiringGateTests
    {
        private const string ControllerPath = "WatchModeController.cs";
        private const string PolicyPath = "ParsekPlaybackPolicy.cs";

        // ---- (a) destroy-time bridge: the policy actually calls the controller ----

        [Fact]
        public void HandleGhostDestroyed_NotifiesTheWatchControllerBeforeTheEngineTearsDown()
        {
            string body = MethodBody(PolicyPath, "private void HandleGhostDestroyed(");

            Assert.Contains("NotifyWatchedGhostDestroying(", body);
            Assert.Contains("host.WatchMode", body);

            // Ordering is the whole point: the engine fires OnGhostDestroyed BEFORE it tears
            // the hierarchy down, so the bridge must be taken before this handler does its own
            // bookkeeping - and, more importantly, must exist at all in this handler rather
            // than in some later-running subscriber.
            Assert.True(
                body.IndexOf("NotifyWatchedGhostDestroying(", StringComparison.Ordinal)
                    < body.IndexOf("heldGhosts.Remove(", StringComparison.Ordinal),
                "watch-target-loss gate: NotifyWatchedGhostDestroying must run at the top of "
                + "HandleGhostDestroyed, before any other teardown bookkeeping.");
        }

        [Fact]
        public void NotifyWatchedGhostDestroying_CannotAbortTheEnginesDestroy()
        {
            // GhostPlaybackEngine.DestroyGhost invokes OnGhostDestroyed with no try/catch and
            // BEFORE ghostStates.Remove, so an unguarded throw here leaves a zombie ghost the
            // loop can never rebuild - strictly worse than the storm this fixes.
            string body = MethodBody(ControllerPath, "internal void NotifyWatchedGhostDestroying(");

            Assert.Contains("catch (Exception", body);
            Assert.Contains("ParsekLog.WarnRateLimited(\"CameraFollow\"", body);

            // The Unity touches must sit inside the guarded helper, never inline in the
            // entry point (which would put them outside the try on a careless edit).
            Assert.False(body.Contains("new GameObject("),
                "watch-target-loss gate: the bridge's Unity work must live in "
                + "BridgeWatchCameraOffDestroyingGhost, inside the try, not in the entry point.");
            Assert.Contains("BridgeWatchCameraOffDestroyingGhost(", body);
        }

        [Fact]
        public void BridgeHelper_RoutesTheDecisionThroughThePurePredicate()
        {
            string body = MethodBody(ControllerPath, "private void BridgeWatchCameraOffDestroyingGhost(");

            Assert.Contains("ShouldBridgeWatchCameraOffDestroyedGhost(", body);
            Assert.Contains("SetTargetTransform(", body);
        }

        // ---- (b) camera-infra loss is COUNTED, not an uncounted early return ----

        [Fact]
        public void UpdateWatchCamera_RoutesBothLossCausesThroughTheClassifier()
        {
            string body = MethodBody(ControllerPath, "internal void UpdateWatchCamera(");

            Assert.Contains("ClassifyWatchTargetLoss(", body);
            Assert.Contains("WatchNoTargetExitFrames", body);
            Assert.Contains("WatchTargetLossAction.ExitWatch", body);
            Assert.Contains("BuildWatchTargetLossExitMessage(", body);
        }

        [Fact]
        public void UpdateWatchCamera_CountsTheCameraInfrastructureLossInsteadOfReturningUncounted()
        {
            // THE REVERT THIS CELL EXISTS FOR. Pre-fix, the infra branch warned and returned
            // from ABOVE the safety net, so the counter never saw it and watch stayed armed to
            // scene end. Both halves of that shape are pinned by ORDER: the classifier must be
            // consulted before the infra warn is built, and the counter must be incremented
            // before it too. Restoring the early return moves the warn above both.
            string body = MethodBody(ControllerPath, "internal void UpdateWatchCamera(");

            int classify = body.IndexOf("ClassifyWatchTargetLoss(", StringComparison.Ordinal);
            int increment = body.IndexOf("watchNoTargetFrames++", StringComparison.Ordinal);
            int infraWarn = body.IndexOf("BuildWatchCameraInfrastructureMessage(", StringComparison.Ordinal);

            Assert.True(classify >= 0, "watch-target-loss gate: UpdateWatchCamera must consult ClassifyWatchTargetLoss.");
            Assert.True(increment >= 0, "watch-target-loss gate: UpdateWatchCamera must count lost frames.");
            Assert.True(infraWarn >= 0,
                "watch-target-loss gate: the camera-infrastructure Warn must still be emitted. "
                + "IF YOU JUST EXTRACTED IT INTO A HELPER, the Warn is not missing - this gate "
                + "pins its ORDER relative to ClassifyWatchTargetLoss and the frame counter by "
                + "looking for BuildWatchCameraInfrastructureMessage inside UpdateWatchCamera's "
                + "own body, so a behaviour-preserving extraction reds it. Re-point the three "
                + "IndexOf probes at whatever now marks the infra branch rather than hunting a "
                + "deleted Warn.");

            Assert.True(classify < infraWarn,
                "watch-target-loss gate: the camera-infrastructure Warn must be reached THROUGH "
                + "ClassifyWatchTargetLoss, not from an early return above it - that uncounted "
                + "return is the defect (WATCH-LOOPED-PARK-TARGET-LOSS-NRE-STORM).");
            Assert.True(increment < infraWarn,
                "watch-target-loss gate: the lost-frame counter must be incremented before the "
                + "camera-infrastructure Warn, so a camera that never resolves converges on an exit.");
        }

        [Fact]
        public void UpdateWatchCamera_ResolvesTheCycleBridgeOnTheContinuePath()
        {
            // Without this the bridge anchor is only ever cleared by the cycle fallback, which
            // a destroy+respawn at an unchanged cycle index never enters - leaving the camera
            // frozen on a static GameObject for the rest of the session.
            string body = MethodBody(ControllerPath, "internal void UpdateWatchCamera(");

            Assert.Contains("ResolveWatchCycleBridgeAnchor(", body);
        }

        [Fact]
        public void ResolveWatchCycleBridgeAnchor_RebindsRatherThanOnlyReleasing()
        {
            string body = MethodBody(ControllerPath, "private void ResolveWatchCycleBridgeAnchor(");

            Assert.Contains("ClassifyWatchCycleBridgeDisposition(", body);
            Assert.Contains("WatchCycleBridgeDisposition.RebindThenRelease", body);
            Assert.Contains("TryRetargetWatchCameraPreservingState(", body);
            Assert.Contains("DestroyWatchCycleBridgeAnchor(", body);
        }

        // ---- (c) the cycle fallback consumes the retarget's return value ----

        [Fact]
        public void FindWatchedGhostState_CommitsTheFallbackOnlyOnARebindThatTook()
        {
            string body = MethodBody(ControllerPath, "private GhostPlaybackState FindWatchedGhostState(");

            Assert.Contains("ClassifyWatchCycleFallback(", body);
            Assert.Contains("WatchCycleFallbackDecision.Commit", body);
            Assert.Contains("WatchCycleFallbackDecision.ReleaseTarget", body);

            // The retarget's result must be CAPTURED. Pre-fix it was a bare statement behind
            // `if (FlightCamera.fetch != null)`, so a camera-less frame skipped the rebind and
            // the branch committed anyway.
            int retarget = body.IndexOf("TryRetargetWatchCameraPreservingState(primary)", StringComparison.Ordinal);
            Assert.True(retarget >= 0,
                "watch-target-loss gate: the cycle fallback must still attempt the rebind.");
            Assert.Contains("retargeted", body);
            Assert.True(
                body.IndexOf("bool retargeted", StringComparison.Ordinal) >= 0,
                "watch-target-loss gate: the cycle fallback must capture the retarget's return "
                + "value (bool retargeted) rather than firing it and forgetting it.");
            Assert.Contains("ClassifyWatchCycleFallback(primaryUsable, retargeted)", body);
        }

        // ---- helpers (mirror SoiSeamProducerWiringGateTests) ----

        private static string MethodBody(string relPath, string signatureFragment)
        {
            string src = StripComments(ReadParsekSource(relPath));

            int sigIdx = src.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0,
                "watch-target-loss gate: signature not found in " + relPath + ": " + signatureFragment);
            Assert.True(
                src.IndexOf(signatureFragment, sigIdx + 1, StringComparison.Ordinal) < 0,
                "watch-target-loss gate: signature is ambiguous in " + relPath + ": " + signatureFragment);

            int open = src.IndexOf('{', sigIdx);
            Assert.True(open >= 0, "watch-target-loss gate: no method body found for " + signatureFragment);

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{')
                    depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return src.Substring(open, i - open + 1);
                }
            }

            Assert.True(false, "watch-target-loss gate: unbalanced braces after " + signatureFragment);
            return null;
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }

        // Strip line comments (XML doc comments included - they start with //) so the fix's
        // own notes, which name the pre-fix shapes deliberately, do not satisfy or trip a scan.
        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
