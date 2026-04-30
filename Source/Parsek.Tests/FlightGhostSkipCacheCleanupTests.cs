using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FlightGhostSkipCacheCleanupTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FlightGhostSkipCacheCleanupTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ClearGhostSkipReasonLogStateForTesting_AllowsPerRecordingSkipReasonsToReEmit()
        {
            ParsekFlight host = CreateFlightHostWithGhostSkipState(
                new HashSet<string> { "rec-cache-a", "rec-cache-b" });

            LogNoRenderableData("rec-cache-a", 0);
            LogNoRenderableData("rec-cache-b", 1);
            LogNoRenderableData("rec-cache-a", 0);
            LogNoRenderableData("rec-cache-b", 1);

            Assert.Equal(1, CountGhostSkipLines("rec-cache-a"));
            Assert.Equal(1, CountGhostSkipLines("rec-cache-b"));

            host.ClearGhostSkipReasonLogStateForTesting();

            Assert.Empty(GetActiveGhostSkipReasonIdentities(host));

            LogNoRenderableData("rec-cache-a", 0);
            LogNoRenderableData("rec-cache-b", 1);

            Assert.Equal(2, CountGhostSkipLines("rec-cache-a"));
            Assert.Equal(2, CountGhostSkipLines("rec-cache-b"));
        }

        [Fact]
        public void DestroyAllTimelineGhosts_AllowsPerRecordingSkipReasonsToReEmit()
        {
            ParsekFlight host = CreateFlightHostForDestroyAllTimelineGhosts(
                new HashSet<string> { "rec-cache-a" });

            LogNoRenderableData("rec-cache-a", 0);
            LogNoRenderableData("rec-cache-a", 0);

            Assert.Equal(1, CountGhostSkipLines("rec-cache-a"));

            host.DestroyAllTimelineGhosts();

            Assert.Empty(GetActiveGhostSkipReasonIdentities(host));

            LogNoRenderableData("rec-cache-a", 0);

            Assert.Equal(2, CountGhostSkipLines("rec-cache-a"));
        }

        private void LogNoRenderableData(string recordingId, int index)
        {
            ParsekFlight.LogGhostSkipReasonChangeForTesting(
                index,
                recordingId,
                "Skip Vessel",
                GhostPlaybackSkipReason.NoRenderableData,
                hasRenderableData: false,
                playbackEnabled: true,
                externalVesselSuppressed: false);
        }

        private int CountGhostSkipLines(string recordingId)
        {
            return logLines.Count(line =>
                line.Contains("[Parsek][VERBOSE][Flight]")
                && line.Contains("Ghost playback skip state")
                && line.Contains("id=" + recordingId));
        }

        private static ParsekFlight CreateFlightHostWithGhostSkipState(HashSet<string> identities)
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            SetActiveGhostSkipReasonIdentities(host, identities);
            return host;
        }

        private static ParsekFlight CreateFlightHostForDestroyAllTimelineGhosts(
            HashSet<string> identities)
        {
            ParsekFlight host = CreateFlightHostWithGhostSkipState(identities);
            SetPrivateField(host, "engine", new GhostPlaybackEngine(null));
            SetPrivateFieldFromDefaultConstructor(host, "orbitCache");
            SetPrivateFieldFromDefaultConstructor(host, "loggedOrbitSegments");
            SetPrivateFieldFromDefaultConstructor(host, "loggedOrbitRotationSegments");
            SetPrivateFieldFromDefaultConstructor(host, "nearbySpawnCandidates");
            SetPrivateFieldFromDefaultConstructor(host, "proximityVelocitySamples");
            SetPrivateFieldFromDefaultConstructor(host, "notifiedSpawnRecordingIds");
            SetPrivateFieldFromDefaultConstructor(host, "loggedRelativeStart");
            SetPrivateFieldFromDefaultConstructor(host, "loggedAnchorNotFound");
            SetPrivateFieldFromDefaultConstructor(host, "unknownFrameTagWarned");
            return host;
        }

        private static HashSet<string> GetActiveGhostSkipReasonIdentities(ParsekFlight host)
        {
            FieldInfo field = GetActiveGhostSkipReasonIdentitiesField();
            return (HashSet<string>)field.GetValue(host);
        }

        private static void SetActiveGhostSkipReasonIdentities(
            ParsekFlight host,
            HashSet<string> identities)
        {
            FieldInfo field = GetActiveGhostSkipReasonIdentitiesField();
            field.SetValue(host, identities);
        }

        private static FieldInfo GetActiveGhostSkipReasonIdentitiesField()
        {
            FieldInfo field = typeof(ParsekFlight).GetField(
                "activeGhostSkipReasonLogIdentities",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return field;
        }

        private static void SetPrivateField(ParsekFlight host, string fieldName, object value)
        {
            FieldInfo field = typeof(ParsekFlight).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(host, value);
        }

        private static void SetPrivateFieldFromDefaultConstructor(
            ParsekFlight host,
            string fieldName)
        {
            FieldInfo field = typeof(ParsekFlight).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(host, Activator.CreateInstance(field.FieldType));
        }
    }
}
