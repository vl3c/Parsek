using System;
using System.IO;
using Parsek.InGameTests.Helpers;
using Xunit;

namespace Parsek.Tests
{
    public class QuickloadResumeHelperReadinessTests
    {
        [Fact]
        public void IsReloadedFlightReady_NegativeUnityIdsStillRequireDifferentInstance()
        {
            const int previousFlightInstanceId = -1087176;

            Assert.False(QuickloadResumeHelpers.IsReloadedFlightReady(
                GameScenes.FLIGHT,
                flightGlobalsReady: true,
                activeVesselPresent: true,
                currentFlightInstanceId: previousFlightInstanceId,
                previousFlightInstanceId: previousFlightInstanceId));

            Assert.True(QuickloadResumeHelpers.IsReloadedFlightReady(
                GameScenes.FLIGHT,
                flightGlobalsReady: true,
                activeVesselPresent: true,
                currentFlightInstanceId: -1123806,
                previousFlightInstanceId: previousFlightInstanceId));
        }

        [Fact]
        public void IsReloadedFlightReady_RequiresActiveVessel()
        {
            Assert.False(QuickloadResumeHelpers.IsReloadedFlightReady(
                GameScenes.FLIGHT,
                flightGlobalsReady: true,
                activeVesselPresent: false,
                currentFlightInstanceId: 42,
                previousFlightInstanceId: 0));
        }

        [Fact]
        public void IsReloadedFlightReady_RequiresFlightScene()
        {
            Assert.False(QuickloadResumeHelpers.IsReloadedFlightReady(
                GameScenes.SPACECENTER,
                flightGlobalsReady: true,
                activeVesselPresent: true,
                currentFlightInstanceId: 42,
                previousFlightInstanceId: 0));
        }

        [Fact]
        public void IsReloadedFlightReady_RequiresFlightGlobalsReady()
        {
            Assert.False(QuickloadResumeHelpers.IsReloadedFlightReady(
                GameScenes.FLIGHT,
                flightGlobalsReady: false,
                activeVesselPresent: true,
                currentFlightInstanceId: 42,
                previousFlightInstanceId: 0));
        }

        [Fact]
        public void IsReloadedFlightReady_RequiresCurrentFlightInstance()
        {
            Assert.False(QuickloadResumeHelpers.IsReloadedFlightReady(
                GameScenes.FLIGHT,
                flightGlobalsReady: true,
                activeVesselPresent: true,
                currentFlightInstanceId: 0,
                previousFlightInstanceId: 0));
        }

        [Fact]
        public void PartPersistentIdStability_UsesStockQuickloadBackend()
        {
            string path = LocateParsekSourceFile(
                "InGameTests",
                "PartPersistentIdStabilityTest.cs");
            Assert.True(File.Exists(path), $"Could not locate source file at {path}");

            string src = File.ReadAllText(path);

            Assert.DoesNotContain("HighLogic.CurrentGame = loaded", src);
            Assert.DoesNotContain("HighLogic.LoadScene(GameScenes.FLIGHT)", src);
        }

        private static string LocateParsekSourceFile(params string[] segments)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string lastCandidate = null;
            while (!string.IsNullOrEmpty(dir))
            {
                string candidate = Path.Combine(dir, "Source", "Parsek");
                foreach (string segment in segments)
                    candidate = Path.Combine(candidate, segment);
                lastCandidate = candidate;
                if (File.Exists(candidate))
                    return candidate;

                dir = Directory.GetParent(dir)?.FullName;
            }

            return lastCandidate ?? string.Empty;
        }
    }
}
