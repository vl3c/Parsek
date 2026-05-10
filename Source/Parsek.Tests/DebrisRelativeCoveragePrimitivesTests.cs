using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    public class DebrisRelativeCoveragePrimitivesTests
    {
        [Fact]
        public void RelativeFramesCoverUT_PlaybackCompatibleSingleFrame_CoversSectionSpan()
        {
            var frames = new List<TrajectoryPoint> { new TrajectoryPoint { ut = 100.0 } };

            Assert.True(DebrisRelativeCoveragePrimitives.RelativeFramesCoverUT(
                frames,
                sectionStartUT: 100.0,
                sectionEndUT: 140.0,
                targetUT: 120.0,
                mode: DebrisRelativeCoverageMode.PlaybackCompatible));
        }

        [Fact]
        public void RelativeFramesCoverUT_RecorderPersistableSingleFrame_CoversOnlySampleUT()
        {
            var frames = new List<TrajectoryPoint> { new TrajectoryPoint { ut = 100.0 } };

            Assert.True(DebrisRelativeCoveragePrimitives.RelativeFramesCoverUT(
                frames,
                sectionStartUT: 100.0,
                sectionEndUT: 140.0,
                targetUT: 100.0,
                mode: DebrisRelativeCoverageMode.RecorderPersistable));
            Assert.False(DebrisRelativeCoveragePrimitives.RelativeFramesCoverUT(
                frames,
                sectionStartUT: 100.0,
                sectionEndUT: 140.0,
                targetUT: 120.0,
                mode: DebrisRelativeCoverageMode.RecorderPersistable));
        }

        [Fact]
        public void AbsoluteShadowFramesCoverUT_RequiresTwoPoints()
        {
            Assert.False(DebrisRelativeCoveragePrimitives.AbsoluteShadowFramesCoverUT(
                new List<TrajectoryPoint> { new TrajectoryPoint { ut = 100.0 } },
                100.0));

            Assert.True(DebrisRelativeCoveragePrimitives.AbsoluteShadowFramesCoverUT(
                new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 110.0 }
                },
                105.0));
        }

        [Fact]
        public void TryGetRenderableCoverageEndUT_ShadowTailLaterThanRelativeTail_UsesShadowTail()
        {
            bool result = DebrisRelativeCoveragePrimitives.TryGetRenderableCoverageEndUT(
                relativeFrames: new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 110.0 }
                },
                absoluteFrames: new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 130.0 }
                },
                checkpoints: null,
                sectionStartUT: 100.0,
                sectionEndUT: 140.0,
                mode: DebrisRelativeCoverageMode.RecorderPersistable,
                out double coverageEndUT,
                out string coverageReason);

            Assert.True(result);
            Assert.Equal(130.0, coverageEndUT);
            Assert.Equal("absolute-shadow", coverageReason);
        }
    }
}
