using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KerbalsModuleTests : IDisposable
    {
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public KerbalsModuleTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void RepeatedWalks_SlotCountStable()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");

            var rec = new Recording
            {
                RecordingId = "stable-slots-rec",
                VesselName = "Stable Slots",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
                VesselSnapshot = snapshot,
                GhostVisualSnapshot = snapshot,
                CrewEndStatesResolved = true,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jebediah Kerman", KerbalEndState.Recovered }
                }
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var module = new KerbalsModule();
            int? expectedSlotCount = null;
            for (int i = 0; i < 10; i++)
            {
                KerbalsTestHelper.RecalculateModule(module);
                if (!expectedSlotCount.HasValue)
                    expectedSlotCount = module.Slots.Count;

                Assert.Equal(expectedSlotCount.Value, module.Slots.Count);
            }

            Assert.Equal(1, expectedSlotCount.Value);
        }
    }
}
