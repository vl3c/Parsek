using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Parsek;
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

        // catches: re-introduction of the bricking UndockUT field (must-fix, plan
        // Phase 1 task 1). RouteStore.BuildLiveSourceRefForComparison rebuilds the
        // live ref from a bare Recording (no single undock UT), so a captured
        // UndockUT != 0 would permanently flag every route SourceChanged with no
        // recovery. The field MUST NOT exist on RouteSourceRef; undock drift is
        // covered by RouteProofHash instead (see proof-hash test below).
        [Fact]
        public void RouteSourceRef_HasNoUndockUtField()
        {
            FieldInfo[] fields = typeof(RouteSourceRef)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.DoesNotContain(fields, f =>
                f.Name.IndexOf("undock", System.StringComparison.OrdinalIgnoreCase) >= 0);
            // Belt-and-suspenders: also assert no property/member named UndockUT.
            Assert.Null(typeof(RouteSourceRef).GetMember(
                "UndockUT", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault());
        }

        // catches: undock drift NOT being folded into RouteProofHash. Without a
        // dedicated UndockUT field, undock-window drift must change the proof hash
        // (the existing route-proof-hash FirstDifferingField case) so the route
        // still trips SourceChanged on revalidation. Proves coverage without the
        // bricking field.
        [Fact]
        public void UndockDrift_ChangesRouteProofHash()
        {
            Recording rec = BuildRecordingWithUndockWindow(undockUT: 450.0);
            string before = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);

            // Rewrite the undock UT under the route (the optimizer / a re-fly could).
            rec.RouteConnectionWindows[0].UndockUT = 999.0;
            string after = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);

            Assert.NotEqual(before, after);
        }

        private static Recording BuildRecordingWithUndockWindow(double undockUT)
        {
            return new Recording
            {
                RecordingId = "rec-undock-proof",
                TreeId = "tree-undock-proof",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "win-1",
                        DockUT = 150.0,
                        UndockUT = undockUT,
                        TransferTargetVesselPid = 9999u,
                        TransferKind = RouteConnectionKind.DockingPort
                    }
                }
            };
        }
    }
}
