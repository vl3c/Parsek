using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ParsekFlightRelativeAnchorFailureTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ParsekFlightRelativeAnchorFailureTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void ParentAnchoredDebrisRetire_PreservesResolverFailureInLog()
        {
            var flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var trajectory = new MockTrajectory
            {
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec",
                VesselName = "Debris Vessel",
            };
            var target = new RelativeSectionPlaybackTarget(
                "debris-rec",
                2,
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    anchorRecordingId = "parent-rec",
                });
            var state = new GhostPlaybackState();
            var failure = RelativeAnchorResolveFailure.Create(
                RelativeAnchorResolveOutcome.AnchorOutOfScope,
                "anchor-cross-tree-out-of-scope",
                "parent-rec",
                "parent-rec",
                42.0,
                2);

            MethodInfo method = typeof(ParsekFlight).GetMethod(
                "TryRetireParentAnchoredDebrisOnRecordedAnchorMiss",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object[] args =
            {
                null,
                7,
                "Debris Vessel",
                trajectory,
                target,
                state,
                failure,
                null,
            };

            bool handled = (bool)method.Invoke(flight, args);

            Assert.True(handled);
            Assert.True(state.anchorRetiredThisFrame);
            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]") &&
                l.Contains("recorded-relative-retired") &&
                l.Contains("outcome=AnchorOutOfScope") &&
                l.Contains("reason=anchor-cross-tree-out-of-scope"));
        }
    }
}
