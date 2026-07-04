using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gates for the early spawn-pending map-presence notification
    /// (<c>GhostPlaybackEngine.OnGhostSpawnPending</c>). The flight-scene ghost map ProtoVessel
    /// (and with it the ghost's orbit line) used to be created only from <c>OnGhostCreated</c>,
    /// which fires when the TIME-SLICED mesh build finalizes (4ms/frame, slowest distance tier),
    /// so a far-SOI multi-part ghost's map line went missing for ~24s after a TS Fly vessel
    /// switch (2026-07-04 Duna playtest: Kerbal X, 75 snapshot parts). The fix notifies the
    /// idempotent map-presence enqueue the moment the primary pending-spawn state REGISTERS.
    ///
    /// <para>The engine spawn paths cannot be driven headless (Unity/KSP types throughout), so
    /// this locks the wiring with the house source-text gate pattern (mirrors
    /// <see cref="PolylineDriverWalkDeleteGateTests"/>): the four primary registration sites
    /// notify, the demoted boundary-overlap secondary does NOT, every notify sits after its
    /// Failed-status check, the policy subscribes AND unsubscribes, the handler routes to the
    /// map-presence body, and the frame tail fires pending before created.</para>
    /// </summary>
    public class GhostSpawnPendingNotifyTests
    {
        private const string EnginePath = "GhostPlaybackEngine.cs";
        private const string PolicyPath = "ParsekPlaybackPolicy.cs";

        [Fact]
        public void Engine_DeclaresSpawnPendingEvent_AndDeferredList()
        {
            string src = StripComments(ReadParsekSource(EnginePath));
            Assert.Contains("internal event Action<GhostLifecycleEvent> OnGhostSpawnPending;", src);
            Assert.Contains("deferredSpawnPendingEvents", src);
        }

        [Fact]
        public void Engine_AllFourPrimaryRegistrationSites_Notify_SecondaryDoesNot()
        {
            // 4 call sites + 1 method definition = 5 occurrences. A 5th call site appearing
            // means a new registration path was added: decide deliberately whether it must
            // notify (primary) or must not (demoted secondary) and update this count.
            string src = StripComments(ReadParsekSource(EnginePath));
            int occurrences = Regex.Matches(src, @"QueueOrEmitGhostSpawnPending\(").Count;
            Assert.Equal(5, occurrences);

            // The demoted boundary-overlap secondary must stay side-effect-free: its
            // CreatePendingSpawnState call is immediately demoted and must never notify.
            // Lock via its unique log token's enclosing method body.
            int secondaryIdx = src.IndexOf(
                "boundary-overlap secondary first spawn", StringComparison.Ordinal);
            Assert.True(secondaryIdx >= 0,
                "Expected the boundary-overlap secondary spawn site (unique reason string) to exist.");
            int windowStart = Math.Max(0, secondaryIdx - 1500);
            int windowEnd = Math.Min(src.Length, secondaryIdx + 1500);
            string secondaryWindow = src.Substring(windowStart, windowEnd - windowStart);
            Assert.DoesNotContain("QueueOrEmitGhostSpawnPending", secondaryWindow);
        }

        [Fact]
        public void Engine_EveryNotify_SitsAfterItsFailedStatusCheck()
        {
            // A Failed build removes the just-registered state; notifying before the check would
            // enqueue map presence for a ghost that never existed (old behavior: no GhostCreated,
            // no map presence). Each call site must therefore appear AFTER a Failed-status
            // comparison within its site block. Heuristic: within the 900 chars BEFORE each call,
            // a GhostVisualLoadStatus.Failed comparison must occur.
            string src = StripComments(ReadParsekSource(EnginePath));
            var calls = Regex.Matches(src, @"QueueOrEmitGhostSpawnPending\(");
            int checkedSites = 0;
            foreach (Match m in calls)
            {
                // Skip the method definition itself.
                string before = src.Substring(Math.Max(0, m.Index - 60), Math.Min(60, m.Index));
                if (before.Contains("private void"))
                    continue;
                string window = src.Substring(Math.Max(0, m.Index - 900), Math.Min(900, m.Index));
                Assert.Contains("GhostVisualLoadStatus.Failed", window);
                checkedSites++;
            }
            Assert.Equal(4, checkedSites);
        }

        [Fact]
        public void Engine_FrameTail_FiresPendingBeforeCreated()
        {
            // Pending precedes finalize semantically, even for a same-frame immediate build.
            string src = StripComments(ReadParsekSource(EnginePath));
            int pendingFire = src.IndexOf(
                "OnGhostSpawnPending?.Invoke(deferredSpawnPendingEvents[i])", StringComparison.Ordinal);
            int createdFire = src.IndexOf(
                "OnGhostCreated?.Invoke(deferredCreatedEvents[i])", StringComparison.Ordinal);
            Assert.True(pendingFire >= 0, "Deferred spawn-pending flush loop not found.");
            Assert.True(createdFire >= 0, "Deferred created flush loop not found.");
            Assert.True(pendingFire < createdFire,
                "Frame tail must fire OnGhostSpawnPending before OnGhostCreated.");
        }

        [Fact]
        public void Policy_SubscribesUnsubscribes_AndRoutesToMapPresence()
        {
            string src = StripComments(ReadParsekSource(PolicyPath));
            Assert.Contains("engine.OnGhostSpawnPending += HandleGhostSpawnPending;", src);
            Assert.Contains("engine.OnGhostSpawnPending -= HandleGhostSpawnPending;", src);

            // The handler must run ONLY the mesh-independent map-presence enqueue: the camera
            // auto-follow (TryAutoFollowChainSeamSpawn) needs a mesh and stays with GhostCreated.
            int handlerIdx = src.IndexOf(
                "private void HandleGhostSpawnPending(GhostLifecycleEvent evt)", StringComparison.Ordinal);
            Assert.True(handlerIdx >= 0, "HandleGhostSpawnPending handler not found.");
            // Slice ONLY the pending handler's body: the window must stop before the next member
            // (HandleGhostCreated legitimately calls TryAutoFollowChainSeamSpawn right below).
            int handlerEnd = src.IndexOf("private void HandleGhostCreated", handlerIdx, StringComparison.Ordinal);
            Assert.True(handlerEnd > handlerIdx,
                "Expected HandleGhostCreated to follow HandleGhostSpawnPending (window bound).");
            string body = src.Substring(handlerIdx, handlerEnd - handlerIdx);
            Assert.Contains("GhostMapPresence.HandleFlightGhostCreatedMapPresence(evt, engine.CurrentLoopUnits);", body);
            Assert.DoesNotContain("TryAutoFollowChainSeamSpawn", body);
        }

        // ---- helpers (mirror PolylineDriverWalkDeleteGateTests) ----

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
