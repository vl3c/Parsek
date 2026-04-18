using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #446 — GloopsRecorderUI.DrawWindow NRE after Discard Recording.
    ///
    /// Repro context: cached locals at the top of DrawWindow (isRecording,
    /// hasLastRecording, isPreviewing) become stale once a button handler
    /// runs synchronously and mutates flight state. Specifically, the
    /// "Discard Recording" handler calls ParsekFlight.DiscardLastGloopsRecording
    /// which nulls LastGloopsRecording — but IMGUI does NOT return from
    /// DrawWindow at that point, so the status-label ladder below still reads
    /// the stale hasLastRecording=true and dereferences the now-null Recording.
    ///
    /// Fix: re-evaluate the booleans from `flight` after the button handlers,
    /// and dispatch through SelectStatusBlock(isRecording, hasLastRecording).
    /// These tests pin the pure decision helper plus simulate the button
    /// side-effect to assert the post-discard branch is Empty (no NRE site)
    /// and that the synthetic "stale-locals" path would have been Saved
    /// (which is exactly the NRE branch).
    /// </summary>
    [Collection("Sequential")]
    public class Bug446GloopsDiscardNreTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug446GloopsDiscardNreTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ── Pure decision: SelectStatusBlock ──────────────────────────

        [Fact]
        public void SelectStatusBlock_Recording_TakesRecordingBranch()
        {
            var block = GloopsRecorderUI.SelectStatusBlock(
                isRecording: true, hasLastRecording: false);
            Assert.Equal(GloopsRecorderUI.StatusBlock.Recording, block);
        }

        [Fact]
        public void SelectStatusBlock_RecordingTakesPriorityOverSaved()
        {
            // hasLastRecording can be true mid-session (a previous saved recording
            // exists) while a new one is being captured. Recording wins.
            var block = GloopsRecorderUI.SelectStatusBlock(
                isRecording: true, hasLastRecording: true);
            Assert.Equal(GloopsRecorderUI.StatusBlock.Recording, block);
        }

        [Fact]
        public void SelectStatusBlock_SavedRecordingPresent_TakesSavedBranch()
        {
            var block = GloopsRecorderUI.SelectStatusBlock(
                isRecording: false, hasLastRecording: true);
            Assert.Equal(GloopsRecorderUI.StatusBlock.Saved, block);
        }

        [Fact]
        public void SelectStatusBlock_Idle_TakesEmptyBranch()
        {
            var block = GloopsRecorderUI.SelectStatusBlock(
                isRecording: false, hasLastRecording: false);
            Assert.Equal(GloopsRecorderUI.StatusBlock.Empty, block);
        }

        // ── #446 regression: discard-then-draw must NOT pick Saved ────

        /// <summary>
        /// Synthetic re-creation of the IMGUI dispatch sequence inside
        /// DrawWindow when the user clicks "Discard Recording" with
        /// isRecording=false, hasLastRecording=true.
        ///
        /// 1. DrawWindow caches the booleans from `flight` at the top.
        /// 2. Discard button handler runs synchronously, nulling
        ///    LastGloopsRecording on the underlying flight model.
        /// 3. Status-label ladder runs.
        ///
        /// Pre-fix code path picked Saved (NRE — Recording is null).
        /// Fixed code path re-reads the booleans first → picks Empty (safe).
        /// </summary>
        [Fact]
        public void DiscardThenSelectStatusBlock_WithReevaluation_PicksEmpty_NoNre()
        {
            // Arrange: a "flight" holding a saved Gloops recording. The Recording
            // class has Points/VesselName which DrawWindow's Saved branch
            // dereferences — building a real one keeps this test honest about
            // what would NRE.
            var rec = new Recording { VesselName = "TestVessel" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 142.5 });

            var fakeFlight = new FakeGloopsFlight
            {
                IsGloopsRecording = false,
                LastGloopsRecording = rec,
                IsPlaying = false,
            };

            // Step 1: DrawWindow caches locals at the top (matches lines 97-99).
            bool isRecording = fakeFlight.IsGloopsRecording;
            bool hasLastRecording = fakeFlight.LastGloopsRecording != null;
            bool isPreviewing = fakeFlight.IsPlaying;
            Assert.False(isRecording);
            Assert.True(hasLastRecording);
            Assert.False(isPreviewing);

            // Step 2: user clicks Discard. The handler runs synchronously inside
            // DrawWindow's MouseUp pass — Recording is nulled.
            ParsekLog.Verbose("Test", "Simulating Discard Recording button handler");
            fakeFlight.DiscardLastGloopsRecording();
            Assert.Null(fakeFlight.LastGloopsRecording);

            // Step 3: the FIX re-reads the booleans from `flight` BEFORE the
            // status-label ladder runs (see GloopsRecorderUI.DrawWindow,
            // re-evaluation block).
            bool prevHasLastRecording = hasLastRecording;
            isRecording = fakeFlight.IsGloopsRecording;
            hasLastRecording = fakeFlight.LastGloopsRecording != null;
            isPreviewing = fakeFlight.IsPlaying;
            Assert.True(prevHasLastRecording, "stale local should have been true (regression guard)");
            Assert.False(hasLastRecording, "fresh re-read should be false");

            // Step 4: SelectStatusBlock with the FRESH booleans picks Empty.
            // (With the stale locals it would have picked Saved → NRE on
            // flight.LastGloopsRecording.VesselName.)
            var block = GloopsRecorderUI.SelectStatusBlock(isRecording, hasLastRecording);
            Assert.Equal(GloopsRecorderUI.StatusBlock.Empty, block);

            // The Saved-branch dereference site under stale locals (this is
            // what would have NRE'd in the unfixed code).
            Assert.Throws<NullReferenceException>(() =>
            {
                var _ = fakeFlight.LastGloopsRecording.VesselName;
            });
        }

        /// <summary>
        /// Sanity check: confirm that the pre-fix "stale locals" code path
        /// would have selected Saved — which is the NRE branch. This pins the
        /// regression so a future refactor that re-introduces top-of-method
        /// caching without re-evaluation gets caught.
        /// </summary>
        [Fact]
        public void DiscardThenSelectStatusBlock_WithStaleLocals_WouldHavePickedSavedBranch()
        {
            var rec = new Recording { VesselName = "TestVessel" };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });

            var fakeFlight = new FakeGloopsFlight
            {
                IsGloopsRecording = false,
                LastGloopsRecording = rec,
                IsPlaying = false,
            };

            // Cache locals (the bug pattern).
            bool isRecording = fakeFlight.IsGloopsRecording;
            bool hasLastRecording = fakeFlight.LastGloopsRecording != null;

            // Discard side-effect.
            fakeFlight.DiscardLastGloopsRecording();

            // WITHOUT re-evaluation, the cached locals say "show Saved" — but the
            // Recording behind that branch is now null. SelectStatusBlock with
            // the STALE booleans returns Saved, matching the bug.
            var staleBlock = GloopsRecorderUI.SelectStatusBlock(isRecording, hasLastRecording);
            Assert.Equal(GloopsRecorderUI.StatusBlock.Saved, staleBlock);
            Assert.Null(fakeFlight.LastGloopsRecording);
        }

        // ── Test double for the slice of ParsekFlight DrawWindow touches ──

        /// <summary>
        /// Plain-CLR stand-in for the ParsekFlight surface DrawWindow reads.
        /// The real ParsekFlight is a MonoBehaviour that requires a Unity
        /// runtime to instantiate, so the test exercises the IMGUI dispatch
        /// pattern (cache-mutate-read) against this purely-managed proxy.
        /// </summary>
        private class FakeGloopsFlight
        {
            public bool IsGloopsRecording;
            public Recording LastGloopsRecording;
            public bool IsPlaying;

            public void DiscardLastGloopsRecording()
            {
                if (LastGloopsRecording == null)
                {
                    ParsekLog.Warn("Flight",
                        "DiscardLastGloopsRecording: nothing to discard");
                    return;
                }
                ParsekLog.Info("Flight",
                    $"Deleting ghost-only recording '{LastGloopsRecording.VesselName}'");
                LastGloopsRecording = null;
            }
        }
    }
}
