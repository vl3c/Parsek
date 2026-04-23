using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §J/#395 of career-earnings-bundle plan: ScienceSubjectPatch must read
    /// committed science values from the ScienceModule (same authoritative source as
    /// the R&amp;D pool patcher) rather than from GameStateStore. Before the fix, a
    /// broken ledger could leave the module with zero credited while the store still
    /// held a stale value — the Archive display would look correct while the pool
    /// patcher applied zero, masking regressions.
    ///
    /// Tests exercise <see cref="ScienceSubjectPatch.TryResolveCommittedScience"/>
    /// directly since the Harmony postfix takes a ScienceSubject KSP type.
    /// </summary>
    [Collection("Sequential")]
    public class ScienceSubjectPatchHardeningTests : IDisposable
    {
        public ScienceSubjectPatchHardeningTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void TryResolveCommittedScience_Uninitialized_FallsBackToStore()
        {
            // With LedgerOrchestrator NOT initialized, the patch should read from the
            // store. This covers sandbox/pre-init loads.
            Assert.False(LedgerOrchestrator.IsInitialized);

            // Seed the store via the commit path.
            var pending = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "crewReport@KerbinSrfLanded",
                    science = 2.5f,
                    subjectMaxValue = 10f
                }
            };
            ScienceTestHelpers.CommitScienceSubjects(pending);

            bool found = ScienceSubjectPatch.TryResolveCommittedScience(
                "crewReport@KerbinSrfLanded", out float value);

            Assert.True(found);
            Assert.Equal(2.5f, value);
        }

        [Fact]
        public void TryResolveCommittedScience_LedgerInitialized_ReadsFromModule()
        {
            // With LedgerOrchestrator initialized and the ScienceModule holding a
            // non-zero credited total, TryResolve must read from the module.
            LedgerOrchestrator.Initialize();
            Assert.True(LedgerOrchestrator.IsInitialized);

            // Also seed the STORE with a DIFFERENT value. If TryResolve returned the
            // store value we'd know it wasn't reading from the module.
            var pending = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "temperatureScan@MunFlyingHigh",
                    science = 99f,      // store says 99
                    subjectMaxValue = 100f
                }
            };
            ScienceTestHelpers.CommitScienceSubjects(pending);

            // Seed the module with 16.44 credited.
            var scienceModule = LedgerOrchestrator.Science;
            scienceModule.ProcessEarning(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                SubjectId = "temperatureScan@MunFlyingHigh",
                ScienceAwarded = 16.44f,
                SubjectMaxValue = 100f
            });

            bool found = ScienceSubjectPatch.TryResolveCommittedScience(
                "temperatureScan@MunFlyingHigh", out float value);

            Assert.True(found);
            // Must match the MODULE's credited total, not the store's stale 99.
            Assert.Equal(16.44f, value, precision: 2);
        }

        [Fact]
        public void TryResolveCommittedScience_ModuleHasZero_ReturnsFalseIgnoringStore()
        {
            // The key regression guard: even if the store has a value, the Archive
            // must NOT show it if the module has zero credited for the subject. This
            // prevents a broken ledger from masquerading as a correct Archive display.
            LedgerOrchestrator.Initialize();

            var pending = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "masking-subject",
                    science = 50f,      // store has a stale value
                    subjectMaxValue = 100f
                }
            };
            ScienceTestHelpers.CommitScienceSubjects(pending);

            // Module has NOT processed any earning for this subject, so it has zero
            // credited.
            Assert.Equal(0.0, LedgerOrchestrator.Science.GetSubjectCredited("masking-subject"));

            bool found = ScienceSubjectPatch.TryResolveCommittedScience(
                "masking-subject", out float value);

            Assert.False(found);
            Assert.Equal(0f, value);
        }

        [Fact]
        public void TryResolveCommittedScience_UnknownSubject_ReturnsFalse()
        {
            LedgerOrchestrator.Initialize();

            bool found = ScienceSubjectPatch.TryResolveCommittedScience(
                "does-not-exist", out float value);

            Assert.False(found);
            Assert.Equal(0f, value);
        }

        [Fact]
        public void TryResolveCommittedScience_NullOrEmpty_ReturnsFalse()
        {
            Assert.False(ScienceSubjectPatch.TryResolveCommittedScience(null, out _));
            Assert.False(ScienceSubjectPatch.TryResolveCommittedScience("", out _));
        }
    }
}
