using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    public class TimeRangeFilter_IsUTInRange_Tests
    {
        [Fact]
        public void NullBounds_AlwaysReturnsTrue()
        {
            Assert.True(TimeRangeFilterLogic.IsUTInRange(0, null, null));
            Assert.True(TimeRangeFilterLogic.IsUTInRange(999999, null, null));
            Assert.True(TimeRangeFilterLogic.IsUTInRange(-1, null, null));
        }

        [Fact]
        public void MinOnly_PassesAboveMin()
        {
            Assert.True(TimeRangeFilterLogic.IsUTInRange(100, 50.0, null));
            Assert.True(TimeRangeFilterLogic.IsUTInRange(50, 50.0, null));
        }

        [Fact]
        public void MinOnly_FailsBelowMin()
        {
            Assert.False(TimeRangeFilterLogic.IsUTInRange(49.9, 50.0, null));
        }

        [Fact]
        public void MaxOnly_PassesBelowMax()
        {
            Assert.True(TimeRangeFilterLogic.IsUTInRange(100, null, 200.0));
            Assert.True(TimeRangeFilterLogic.IsUTInRange(200, null, 200.0));
        }

        [Fact]
        public void MaxOnly_FailsAboveMax()
        {
            Assert.False(TimeRangeFilterLogic.IsUTInRange(200.1, null, 200.0));
        }

        [Fact]
        public void BothBounds_PassesInsideRange()
        {
            Assert.True(TimeRangeFilterLogic.IsUTInRange(150, 100.0, 200.0));
            Assert.True(TimeRangeFilterLogic.IsUTInRange(100, 100.0, 200.0));
            Assert.True(TimeRangeFilterLogic.IsUTInRange(200, 100.0, 200.0));
        }

        [Fact]
        public void BothBounds_FailsOutsideRange()
        {
            Assert.False(TimeRangeFilterLogic.IsUTInRange(99.9, 100.0, 200.0));
            Assert.False(TimeRangeFilterLogic.IsUTInRange(200.1, 100.0, 200.0));
        }
    }

    public class TimeRangeFilter_DoesRecordingOverlapRange_Tests
    {
        [Fact]
        public void NoBounds_AlwaysOverlaps()
        {
            Assert.True(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                100, 200, null, null));
        }

        [Fact]
        public void FullyInsideRange_Overlaps()
        {
            Assert.True(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                120, 180, 100.0, 200.0));
        }

        [Fact]
        public void PartialOverlap_StartBeforeRange_Overlaps()
        {
            Assert.True(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                50, 150, 100.0, 200.0));
        }

        [Fact]
        public void PartialOverlap_EndAfterRange_Overlaps()
        {
            Assert.True(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                150, 250, 100.0, 200.0));
        }

        [Fact]
        public void RecordingSpansRange_Overlaps()
        {
            Assert.True(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                50, 250, 100.0, 200.0));
        }

        [Fact]
        public void FullyBeforeRange_DoesNotOverlap()
        {
            Assert.False(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                10, 50, 100.0, 200.0));
        }

        [Fact]
        public void FullyAfterRange_DoesNotOverlap()
        {
            Assert.False(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                250, 300, 100.0, 200.0));
        }

        [Fact]
        public void ExactBoundaryTouch_Start_Overlaps()
        {
            // Recording ends exactly at filter start
            Assert.True(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                50, 100, 100.0, 200.0));
        }

        [Fact]
        public void ExactBoundaryTouch_End_Overlaps()
        {
            // Recording starts exactly at filter end
            Assert.True(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                200, 250, 100.0, 200.0));
        }

        [Fact]
        public void InProgressRecording_EndBeforeOrEqualStart_OverlapsAnything()
        {
            // In-progress: EndUT == 0 < StartUT — treated as unbounded end
            Assert.True(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                150, 0, 100.0, 200.0));
        }

        [Fact]
        public void InProgressRecording_StartAfterRange_DoesNotOverlap()
        {
            // In-progress but starts after filter max
            Assert.False(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                250, 0, 100.0, 200.0));
        }

        [Fact]
        public void MinOnly_RecordingEndsBefore_DoesNotOverlap()
        {
            Assert.False(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                10, 50, 100.0, null));
        }

        [Fact]
        public void MaxOnly_RecordingStartsAfter_DoesNotOverlap()
        {
            Assert.False(TimeRangeFilterLogic.DoesRecordingOverlapRange(
                250, 300, null, 200.0));
        }
    }

    public class TimeRangeFilter_ComputeSliderBounds_Tests
    {
        [Fact]
        public void EmptyList_ReturnsZero()
        {
            TimeRangeFilterLogic.ComputeSliderBounds(
                new List<Recording>(), 0, out double min, out double max);
            Assert.Equal(0, min);
            Assert.Equal(0, max);
        }

        [Fact]
        public void NullList_ReturnsZero()
        {
            TimeRangeFilterLogic.ComputeSliderBounds(
                null, 100, out double min, out double max);
            Assert.Equal(0, min);
            Assert.Equal(0, max);
        }

        [Fact]
        public void SingleRecording_BoundsMatchRecording()
        {
            var rec = new Recording { StartUT = 100, EndUT = 500 };
            var list = new List<Recording> { rec };
            TimeRangeFilterLogic.ComputeSliderBounds(
                list, 300, out double min, out double max);
            Assert.Equal(100, min);
            Assert.Equal(500, max);
        }

        [Fact]
        public void CurrentUT_ExtendsMaxBound()
        {
            var rec = new Recording { StartUT = 100, EndUT = 500 };
            var list = new List<Recording> { rec };
            TimeRangeFilterLogic.ComputeSliderBounds(
                list, 1000, out double min, out double max);
            Assert.Equal(100, min);
            Assert.Equal(1000, max);
        }

        [Fact]
        public void MultipleRecordings_SpansAll()
        {
            var list = new List<Recording>
            {
                new Recording { StartUT = 200, EndUT = 400 },
                new Recording { StartUT = 50, EndUT = 150 },
                new Recording { StartUT = 300, EndUT = 800 },
            };
            TimeRangeFilterLogic.ComputeSliderBounds(
                list, 600, out double min, out double max);
            Assert.Equal(50, min);
            Assert.Equal(800, max);
        }
    }

    public class TimeRangeFilterState_Tests
    {
        [Fact]
        public void Default_IsNotActive()
        {
            var state = new TimeRangeFilterState();
            Assert.False(state.IsActive);
            Assert.Null(state.MinUT);
            Assert.Null(state.MaxUT);
            Assert.Null(state.ActivePresetName);
        }

        [Fact]
        public void SetRange_BecomesActive()
        {
            var state = new TimeRangeFilterState();
            state.SetRange(100, 200, "Test");
            Assert.True(state.IsActive);
            Assert.Equal(100.0, state.MinUT);
            Assert.Equal(200.0, state.MaxUT);
            Assert.Equal("Test", state.ActivePresetName);
        }

        [Fact]
        public void Clear_BecomesInactive()
        {
            var state = new TimeRangeFilterState();
            state.SetRange(100, 200, "Test");
            state.Clear();
            Assert.False(state.IsActive);
            Assert.Null(state.MinUT);
            Assert.Null(state.MaxUT);
            Assert.Null(state.ActivePresetName);
        }

        [Fact]
        public void SetRange_NullPreset_CustomRange()
        {
            var state = new TimeRangeFilterState();
            state.SetRange(100, 200);
            Assert.True(state.IsActive);
            Assert.Null(state.ActivePresetName);
        }
    }
}
