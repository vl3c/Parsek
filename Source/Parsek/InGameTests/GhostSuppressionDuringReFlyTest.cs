using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 7 of Rewind-to-Staging (design §3.3 + §11.5): assert that no
    /// ghost playback is occurring for recordings in the active session's
    /// SessionSuppressedSubtree. Runs in FLIGHT so the live
    /// <c>GhostPlaybackEngine</c> / <c>ParsekScenario</c> are available.
    ///
    /// <para>Preconditions: an active <see cref="ReFlySessionMarker"/> must
    /// exist. The test auto-skips when no session is live (running the test
    /// outside a re-fly is meaningless).</para>
    /// </summary>
    public class GhostSuppressionDuringReFlyTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "During active re-fly, suppressed recordings have no ghost state")]
        public void GhostSuppressionDuringReFly()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session — invoke a rewind before running this test.");
                return;
            }

            var suppressed = SessionSuppressionState.SuppressedSubtreeIds;
            InGameAssert.IsNotNull(suppressed, "SessionSuppressionState.SuppressedSubtreeIds is null");
            if (suppressed.Count == 0)
            {
                InGameAssert.Skip("Active session marker has empty SuppressedSubtree (no committed origin descendants).");
                return;
            }

            ParsekLog.Info("RewindTest",
                $"GhostSuppressionDuringReFly: session active sess={marker.SessionId}; " +
                $"asserting no ghosts for {suppressed.Count} suppressed recording(s)");

            // Walk the committed list once, record index -> id, so we can
            // cross-check the engine's ghostStates dictionary.
            var committed = RecordingStore.CommittedRecordings;
            var suppressedIndices = new List<int>();
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (suppressed.Contains(rec.RecordingId))
                    suppressedIndices.Add(i);
            }
            InGameAssert.IsTrue(suppressedIndices.Count > 0,
                "Expected at least one committed recording in SuppressedSubtree");

            // Find the host flight so we can peek at the live engine. The
            // field is internal and not exposed via a property, so we look it
            // up through the active GameObject.
            var host = GameObject.FindObjectOfType<ParsekFlight>();
            if (host == null)
            {
                InGameAssert.Skip("No ParsekFlight in current scene — test is FLIGHT-only.");
                return;
            }
            var engine = host.Engine;
            InGameAssert.IsNotNull(engine, "ParsekFlight.Engine is null");

            for (int i = 0; i < suppressedIndices.Count; i++)
            {
                int idx = suppressedIndices[i];
                bool hasGhostState = engine.ghostStates.ContainsKey(idx);
                InGameAssert.IsFalse(hasGhostState,
                    $"Suppressed recording #{idx} \"{committed[idx].VesselName}\" " +
                    $"should NOT have an active ghost state during re-fly session " +
                    $"sess={marker.SessionId}");

                // The engine filter also checks the SessionSuppressionState predicate.
                InGameAssert.IsTrue(SessionSuppressionState.IsSuppressedRecordingIndex(idx),
                    $"Suppressed recording #{idx} missing from SessionSuppressionState predicate");
            }

            ParsekLog.Info("RewindTest",
                $"GhostSuppressionDuringReFly: all {suppressedIndices.Count} suppressed recording(s) ghostless");
        }
    }
}
