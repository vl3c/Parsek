using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GC11-SEAT-DISMOUNT-RECORDED-AS-UNDOCK. Owner ruling 2026-10-24: "a kerbal leaving an
    /// external seat is an EVA, as we get a vehicle and a kerbal; undock is between vehicles,
    /// as we get two vehicles at the end". Stock reaches Parsek through Part.Undock for a seat
    /// dismount (KerbalEVA.EjectFromSeat) and for a claw releasing a kerbal
    /// (ModuleGrappleNode.Release), with no onCrewOnEva, so the undock handler must route a
    /// vehicle + kerbal undock to the EVA branch.
    /// </summary>
    public class SeatDismountEvaTests
    {
        [Theory]
        [InlineData(false, true, true)]    // seat dismount, claw release of a kerbal
        [InlineData(true, false, true)]    // kerbal kept the old pid
        [InlineData(false, false, false)]  // docking-port undock: two vehicles
        [InlineData(true, true, false)]    // unreachable: no change
        public void ClassifyUndockShape_ExactlyOneEvaSideIsAnEva(
            bool oldIsEva, bool newIsEva, bool kerbalLeftVehicle)
        {
            Assert.Equal(
                kerbalLeftVehicle ? UndockShape.KerbalLeftVehicle : UndockShape.VehicleSplit,
                SegmentBoundaryLogic.ClassifyUndockShape(oldIsEva, newIsEva));
        }

        [Fact]
        public void Wiring_UndockHandlerRoutesKerbalLeftVehicleBeforeTheUndockBranch()
        {
            string src = SourceScanText.StripCSharpComments(ReadParsekFlight());
            string handler = MethodBody(src, "void OnVesselsUndocking(Vessel oldVessel, Vessel newVessel)");

            int shape = handler.IndexOf(
                "SegmentBoundaryLogic.ClassifyUndockShape(oldVessel.isEVA, newVessel.isEVA)",
                StringComparison.Ordinal);
            int route = handler.IndexOf("HandleKerbalLeftVehicleUndock(oldVessel, newVessel);", StringComparison.Ordinal);
            int notRecording = handler.IndexOf("recorder == null || !recorder.IsRecording", StringComparison.Ordinal);
            int undockBranch = handler.IndexOf("DeferredUndockBranch(", StringComparison.Ordinal);
            Assert.True(shape >= 0, "OnVesselsUndocking no longer classifies the undock shape");
            Assert.True(route > shape, "the KerbalLeftVehicle shape is not routed to the EVA handler");
            // Before the not-recording return, so a seat dismount auto-records like a hatch EVA,
            // and before the Undock branch, so it never authors an Undock split.
            Assert.True(route < notRecording, "the EVA route must run before the not-recording return");
            Assert.True(route < undockBranch, "the EVA route must run before the Undock branch");
        }

        [Fact]
        public void Wiring_KerbalLeftVehicleTakesTheEvaBranchNeverTheUndockOne()
        {
            string src = SourceScanText.StripCSharpComments(ReadParsekFlight());
            string body = MethodBody(src, "private void HandleKerbalLeftVehicleUndock(Vessel oldVessel, Vessel newVessel)");

            Assert.Contains("StartCoroutine(DeferredEvaBranch(kerbalName, evaData));", body);
            Assert.Contains("StartCoroutine(DeferredKerbalLeftVehicleNotRecording(evaData, kerbalVessel));", body);
            Assert.Contains("recorder.ConsumePendingJointBreakCheck(", body);
            Assert.Contains("new GameEvents.FromToAction<Part, Part>(", body);
            Assert.DoesNotContain("BranchPointType.Undock", body);
            Assert.DoesNotContain("DeferredUndockBranch", body);
            Assert.DoesNotContain("ResumeSplitRecorder", body);
        }

        [Theory]
        [InlineData(false, true, true, true)]    // seat dismount: focus moved to the kerbal
        [InlineData(false, true, false, false)]  // claw release: the claw's vessel kept focus
        [InlineData(false, false, false, false)] // kerbal gone within the frame
        [InlineData(true, true, true, false)]    // a recording started meanwhile
        public void NotRecordingRoute_OnlyWhenTheKerbalHoldsFocus(
            bool isRecording, bool alive, bool active, bool expected)
        {
            Assert.Equal(expected,
                ParsekFlight.ShouldRouteKerbalLeftVehicleNotRecording(isRecording, alive, active));
        }

        [Fact]
        public void Wiring_NotRecordingRouteRunsTheHatchEvaHandlerBehindTheFocusGate()
        {
            string src = SourceScanText.StripCSharpComments(ReadParsekFlight());
            string body = MethodBody(src, "private IEnumerator DeferredKerbalLeftVehicleNotRecording(");
            int gate = body.IndexOf("ShouldRouteKerbalLeftVehicleNotRecording(", StringComparison.Ordinal);
            int route = body.IndexOf("OnCrewOnEva(evaData);", StringComparison.Ordinal);
            Assert.True(gate >= 0 && route > gate, "OnCrewOnEva must run only behind the focus gate");
        }

        [Fact]
        public void EvaBranchPoint_AuthorsNoRewindPoint()
        {
            // The EVA branch the dismount now takes never gets a Re-Fly slot.
            Assert.False(RewindPointAuthor.IsReFlySplitType(BranchPointType.EVA));
            Assert.True(RewindPointAuthor.IsReFlySplitType(BranchPointType.Undock));
        }

        private static string ReadParsekFlight()
        {
            return File.ReadAllText(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "ParsekFlight.cs"));
        }

        private static string MethodBody(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " not found - renamed? Update this gate.");
            int open = src.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return src.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException(signature + " has no closing brace");
        }
    }
}
