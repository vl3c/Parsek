using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards against RecordingSupersedeRelation save/load drift (design doc
    /// section 5.3).
    /// </summary>
    public class RecordingSupersedeRelationRoundTripTests
    {
        [Fact]
        public void RecordingSupersedeRelation_AllFields_RoundTrips()
        {
            var rel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_abcd",
                OldRecordingId = "rec_old",
                NewRecordingId = "rec_new",
                UT = 12345.678,
                CreatedRealTime = "2026-04-17T22:10:00Z"
            };

            var parent = new ConfigNode("RECORDING_SUPERSEDES");
            rel.SaveInto(parent);
            var entry = parent.GetNode("ENTRY");
            Assert.NotNull(entry);

            var restored = RecordingSupersedeRelation.LoadFrom(entry);
            Assert.Equal("rsr_abcd", restored.RelationId);
            Assert.Equal("rec_old", restored.OldRecordingId);
            Assert.Equal("rec_new", restored.NewRecordingId);
            Assert.Equal(12345.678, restored.UT);
            Assert.Equal("2026-04-17T22:10:00Z", restored.CreatedRealTime);
        }

        [Fact]
        public void RecordingSupersedeRelation_MissingCreatedRealTime_DefaultsNull()
        {
            var rel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_1",
                OldRecordingId = "a",
                NewRecordingId = "b",
                UT = 0.0
            };

            var parent = new ConfigNode("RECORDING_SUPERSEDES");
            rel.SaveInto(parent);
            var restored = RecordingSupersedeRelation.LoadFrom(parent.GetNode("ENTRY"));

            Assert.Null(restored.CreatedRealTime);
        }
    }
}
