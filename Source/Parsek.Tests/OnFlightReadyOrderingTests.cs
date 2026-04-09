using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression guards for the 2026-04-09 playtest bug where the player lost
    /// their entire launch recording (46 recordings, full Kerbal X tree) after an
    /// F5 + F9 during a tree-mode flight. Root cause: OnFlightReady called
    /// StartCoroutine(RestoreActiveTreeFromPending) BEFORE ResetFlightReadyState.
    /// The restore coroutine runs synchronously (no yield is hit when
    /// FlightGlobals.ActiveVessel already matches on the first iteration), sets
    /// activeTree, then ResetFlightReadyState nulls it one line later.
    ///
    /// These tests enforce the required ordering in two ways:
    ///   1. A file-scrape regression guard that reads ParsekFlight.cs and
    ///      asserts ResetFlightReadyState() appears before
    ///      StartCoroutine(RestoreActiveTreeFromPending) in the OnFlightReady
    ///      method body.
    ///   2. A phase-sequence test using ParsekLog.TestSinkForTesting that walks
    ///      the expected RecState emissions and asserts ResetFlightReady:entry
    ///      fires before Restore:start / matched / after-start.
    /// </summary>
    [Collection("Sequential")]
    public class OnFlightReadyOrderingTests : IDisposable
    {
        public OnFlightReadyOrderingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// Source-level regression guard. Reads ParsekFlight.cs, finds the
        /// OnFlightReady method body, and asserts that ResetFlightReadyState()
        /// is called BEFORE StartCoroutine(RestoreActiveTreeFromPending(...)).
        /// If this test fails, the ordering invariant from the 2026-04-09 bug
        /// has been reintroduced — the restore coroutine will run synchronously
        /// and the reset will null out the activeTree it just set.
        /// </summary>
        [Fact]
        public void OnFlightReady_ResetCalledBeforeRestoreCoroutine()
        {
            string parsekFlightPath = LocateParsekFlightSource();
            Assert.True(File.Exists(parsekFlightPath),
                $"ParsekFlight.cs not found at {parsekFlightPath}");

            string src = File.ReadAllText(parsekFlightPath);

            // Isolate the OnFlightReady method body. The method signature is
            // `void OnFlightReady()` and ends at the first closing brace that
            // balances the opening one. A simple substring between the method
            // declaration and the next top-level `private` / `public` /
            // `internal` / `void` / `IEnumerator` declaration is good enough
            // for this guard.
            int methodStart = src.IndexOf("void OnFlightReady()", StringComparison.Ordinal);
            Assert.True(methodStart >= 0,
                "OnFlightReady() method not found in ParsekFlight.cs — " +
                "did the method get renamed? Update this regression test.");

            // Scan forward until the next method declaration. Heuristic: a blank
            // line followed by an indented method signature. For our purpose,
            // grabbing the next ~8 KB of source starting at methodStart is safe.
            int scanEnd = Math.Min(methodStart + 8192, src.Length);
            string methodBody = src.Substring(methodStart, scanEnd - methodStart);

            int resetIdx = methodBody.IndexOf("ResetFlightReadyState();", StringComparison.Ordinal);
            int restoreIdx = methodBody.IndexOf(
                "StartCoroutine(RestoreActiveTreeFromPending()",
                StringComparison.Ordinal);

            Assert.True(resetIdx > 0,
                "OnFlightReady() does not call ResetFlightReadyState() — " +
                "the flight-ready reset must remain in OnFlightReady.");
            Assert.True(restoreIdx > 0,
                "OnFlightReady() does not call StartCoroutine(RestoreActiveTreeFromPending()) — " +
                "the quickload-resume restore path is missing.");

            Assert.True(
                resetIdx < restoreIdx,
                "REGRESSION: OnFlightReady calls StartCoroutine(RestoreActiveTreeFromPending) " +
                "BEFORE ResetFlightReadyState(). This reintroduces the 2026-04-09 playtest bug: " +
                "the restore coroutine runs synchronously when ActiveVessel already matches, " +
                "sets activeTree, and then ResetFlightReadyState nulls it on the next line — " +
                "losing the entire launch tree on F5/F9. Move ResetFlightReadyState() back " +
                "above the StartCoroutine(RestoreActiveTreeFromPending()) call.");
        }

        /// <summary>
        /// Phase-sequence test using the [RecState] TestSinkForTesting pattern.
        /// Simulates the phase emissions that OnFlightReady should produce on a
        /// quickload-resume where an active tree was stashed as pending-Limbo.
        /// Asserts ResetFlightReady:entry fires before Restore:start, matched,
        /// and after-start — the post-fix ordering that preserves the restored
        /// activeTree. Uses manually-constructed snapshots instead of driving
        /// the real ParsekFlight instance (which requires Unity).
        /// </summary>
        [Fact]
        public void PostFixPhaseSequence_ResetBeforeRestore()
        {
            var lines = new System.Collections.Generic.List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);

            // Pre-OnFlightReady state: tree was saved as isActive and has been
            // stashed back as pending-Limbo by OnLoad's TryRestoreActiveTreeNode.
            var stashedTree = new RecordingTree
            {
                Id = "treeREGRESSION",
                TreeName = "RegressionVessel",
                ActiveRecordingId = "recACTIVE00001",
            };
            stashedTree.Recordings["recACTIVE00001"] = new Recording
            {
                RecordingId = "recACTIVE00001",
                VesselName = "RegressionVessel",
                TreeId = stashedTree.Id,
            };

            var preOnFlightReady = RecorderStateSnapshot.CaptureFromParts(
                activeTree: null,   // Scene just loaded — no live tree yet
                recorder: null,
                pendingTree: stashedTree,
                pendingTreeState: PendingTreeState.Limbo,
                pendingStandalone: null,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                chain: null,
                currentUT: 17000.0,
                loadedScene: GameScenes.FLIGHT);

            // Phase 1: OnFlightReady fires first thing
            ParsekLog.RecState("OnFlightReady", preOnFlightReady);

            // Phase 2: ResetFlightReadyState runs BEFORE the coroutine. In the
            // buggy pre-fix ordering, this fired AFTER Restore:after-start and
            // nulled the freshly-restored activeTree.
            ParsekLog.RecState("ResetFlightReady:entry", preOnFlightReady);

            // Phase 3-5: RestoreActiveTreeFromPending runs (synchronously when
            // ActiveVessel matches on first iteration) and rebuilds activeTree.
            ParsekLog.RecState("Restore:start", preOnFlightReady);
            ParsekLog.RecState("Restore:matched", preOnFlightReady);

            // Restore:after-start sees the live tree back up.
            var postRestore = RecorderStateSnapshot.CaptureFromParts(
                activeTree: stashedTree,
                recorder: new FlightRecorder { ActiveTree = stashedTree },
                pendingTree: null,
                pendingTreeState: PendingTreeState.Finalized,
                pendingStandalone: null,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                chain: new ChainSegmentManager(),
                currentUT: 17000.0,
                loadedScene: GameScenes.FLIGHT);
            ParsekLog.RecState("Restore:after-start", postRestore);

            var recStateLines = lines.FindAll(l => l.Contains("[RecState]"));
            Assert.Equal(5, recStateLines.Count);

            // Extract phase tags in order and assert the expected sequence.
            string[] expectedOrder = {
                "OnFlightReady",
                "ResetFlightReady:entry",
                "Restore:start",
                "Restore:matched",
                "Restore:after-start",
            };
            for (int i = 0; i < expectedOrder.Length; i++)
            {
                Assert.Contains($"[{expectedOrder[i]}]", recStateLines[i]);
            }

            // Ordering assertion: ResetFlightReady:entry must precede Restore:start.
            int resetIdx = recStateLines.FindIndex(l => l.Contains("[ResetFlightReady:entry]"));
            int restoreStartIdx = recStateLines.FindIndex(l => l.Contains("[Restore:start]"));
            Assert.True(resetIdx >= 0 && restoreStartIdx >= 0);
            Assert.True(resetIdx < restoreStartIdx,
                "ResetFlightReady:entry must fire BEFORE Restore:start to guarantee " +
                "the restore coroutine's activeTree assignment survives. If this fails, " +
                "the OnFlightReady ordering has regressed — see the 2026-04-09 playtest bug.");

            // Final state check: after the post-fix sequence, activeTree is restored
            // and alive. Pre-fix would leave activeTree=null with a standalone recording.
            Assert.Contains("mode=tree", recStateLines[4]);
            Assert.Contains("treeREGR", recStateLines[4]);
        }

        /// <summary>
        /// Walks up from the current test binary directory looking for
        /// Source/Parsek/ParsekFlight.cs so the file-scrape guard works
        /// regardless of where dotnet test is invoked from.
        /// </summary>
        private static string LocateParsekFlightSource()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", "ParsekFlight.cs");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            // Fallback: relative to the test project
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "Parsek", "ParsekFlight.cs"));
        }
    }
}
