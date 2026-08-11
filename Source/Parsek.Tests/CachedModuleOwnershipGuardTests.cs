using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P4a / audit M5 — the vessel-identity guard on the recorder's cached engine / RCS / robotic
    /// module lists.
    ///
    /// The caches hold direct <c>Part</c> / <c>PartModule</c> references captured at
    /// <c>StartRecording</c>. A staged-away booster's <c>Part</c> is NOT destroyed: KSP moves it to
    /// a brand-new debris <c>Vessel</c> and it keeps burning. The pre-fix poll only tested
    /// <c>part == null</c>, so that booster kept writing EngineIgnited / EngineThrottle /
    /// EngineShutdown into the PARENT recording under the booster's own part pid.
    ///
    /// These cells cover the pure decision core. The live wrapper feeds it
    /// <c>ReferenceEquals(part.vessel, recordedVessel)</c> — a live in-memory object comparison
    /// within one session, not a persistentId or guid comparison, so the craft-baked-pid identity
    /// rule does not apply.
    /// </summary>
    public class CachedModuleOwnershipGuardTests
    {
        [Fact]
        public void APartStillOnTheRecordedVesselIsPolled()
        {
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.Poll,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: true, hasModule: true,
                    hasPartVessel: true, partVesselIsRecordedVessel: true));
        }

        [Fact]
        public void AStagedAwayBoosterStillHoldingALiveEngineIsNotPolled()
        {
            // The exact M5 shape: part alive, module alive, vessel alive — but it is the debris
            // vessel, not ours.
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipForeignVessel,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: true, hasModule: true,
                    hasPartVessel: true, partVesselIsRecordedVessel: false));
        }

        [Fact]
        public void APartWhoseVesselIsNullMidSplitIsNotPolled()
        {
            // One-frame window between the decoupler firing and KSP reparenting the part.
            // "Probably still ours" is precisely the guess that produced the bug.
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipForeignVessel,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: true, hasModule: true,
                    hasPartVessel: false, partVesselIsRecordedVessel: false));
        }

        [Fact]
        public void ADestroyedPartIsReportedAsANullEntryNotAsAForeignVessel()
        {
            // The two skip reasons are distinct because only the foreign-vessel one is logged:
            // a destroyed part is routine, a foreign part means events were being misattributed.
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipNullEntry,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: false, hasModule: true,
                    hasPartVessel: false, partVesselIsRecordedVessel: false));
        }

        [Fact]
        public void ADestroyedModuleOnASurvivingPartIsANullEntry()
        {
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipNullEntry,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: true, hasModule: false,
                    hasPartVessel: true, partVesselIsRecordedVessel: true));
        }

        [Fact]
        public void TheNullEntryVerdictWinsEvenWhenTheVesselStillMatches()
        {
            // Ordering matters: a null module with a matching vessel must not read as Poll.
            Assert.Equal(
                FlightRecorder.CachedModulePollDecision.SkipNullEntry,
                FlightRecorder.DecideCachedModulePoll(
                    hasPart: false, hasModule: false,
                    hasPartVessel: true, partVesselIsRecordedVessel: true));
        }

        [Fact]
        public void OnlyTheAllFourTrueCombinationPolls()
        {
            // Exhaustive truth table — 16 combinations, exactly one Poll.
            int pollCount = 0;
            for (int mask = 0; mask < 16; mask++)
            {
                var decision = FlightRecorder.DecideCachedModulePoll(
                    (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);
                if (decision == FlightRecorder.CachedModulePollDecision.Poll)
                    pollCount++;
            }
            Assert.Equal(1, pollCount);
        }
    }
}
