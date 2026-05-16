using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    public class RouteSourceRefTests
    {
        private static RouteSourceRef Baseline()
        {
            return new RouteSourceRef
            {
                RecordingId = "rec-1",
                TreeId = "tree-1",
                TreeOrder = 3,
                RecordingFormatVersion = 13,
                RecordingSchemaGeneration = 2,
                SidecarEpoch = 5,
                StartUT = 42654.0,
                EndUT = 47000.0,
                RouteProofHash = "93C2"
            };
        }

        // catches: an Equals/GetHashCode implementation that drops a field.
        // Each row toggles one and only one field and asserts both Equals
        // and GetHashCode disagree from the baseline.
        [Theory]
        [InlineData("RecordingId")]
        [InlineData("TreeId")]
        [InlineData("TreeOrder")]
        [InlineData("RecordingFormatVersion")]
        [InlineData("RecordingSchemaGeneration")]
        [InlineData("SidecarEpoch")]
        [InlineData("StartUT")]
        [InlineData("EndUT")]
        [InlineData("RouteProofHash")]
        public void Equality_AllFieldsContribute(string fieldName)
        {
            RouteSourceRef a = Baseline();
            RouteSourceRef b = Baseline();

            switch (fieldName)
            {
                case "RecordingId": b.RecordingId = "rec-1-different"; break;
                case "TreeId": b.TreeId = "tree-1-different"; break;
                case "TreeOrder": b.TreeOrder = 4; break;
                case "RecordingFormatVersion": b.RecordingFormatVersion = 14; break;
                case "RecordingSchemaGeneration": b.RecordingSchemaGeneration = 3; break;
                case "SidecarEpoch": b.SidecarEpoch = 6; break;
                case "StartUT": b.StartUT = 42655.0; break;
                case "EndUT": b.EndUT = 47001.0; break;
                case "RouteProofHash": b.RouteProofHash = "DEAD"; break;
                default:
                    Assert.True(false, "Unknown field: " + fieldName);
                    break;
            }

            // Sanity: baseline == baseline.
            Assert.Equal(Baseline(), Baseline());
            Assert.Equal(Baseline().GetHashCode(), Baseline().GetHashCode());

            // Toggled field breaks equality and the hash.
            Assert.NotEqual(a, b);
            Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
        }
    }
}
