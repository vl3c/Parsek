using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CrewManifestTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrewManifestTests()
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

        #region Helper — build vessel ConfigNodes with crew

        private static ConfigNode MakePartWithCrew(params string[] crewNames)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "testPart");
            foreach (var name in crewNames)
                part.AddValue("crew", name);
            return part;
        }

        private static ConfigNode MakeVessel(params ConfigNode[] parts)
        {
            var vessel = new ConfigNode("VESSEL");
            foreach (var p in parts)
                vessel.AddNode(p);
            return vessel;
        }

        #endregion

        #region T11-CREW — ExtractCrewManifest

        [Fact]
        public void ExtractCrewManifest_NullInput_ReturnsNull()
        {
            var result = VesselSpawner.ExtractCrewManifest(null);

            Assert.Null(result);
        }

        [Fact]
        public void ExtractCrewManifest_NoCrew_ReturnsNull()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "fuelTank");
            var vessel = MakeVessel(part);

            var result = VesselSpawner.ExtractCrewManifest(vessel);

            Assert.Null(result);
        }

        [Fact]
        public void ExtractCrewManifest_SingleCrew_WithResolver()
        {
            var vessel = MakeVessel(MakePartWithCrew("Jeb Kerman"));

            var result = VesselSpawner.ExtractCrewManifest(vessel, name => "Pilot");

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal(1, result["Pilot"]);
            Assert.Contains(logLines, l => l.Contains("[Spawner]") && l.Contains("1 crew"));
        }

        [Fact]
        public void ExtractCrewManifest_MultipleCrew_SameTraitSummed()
        {
            var vessel = MakeVessel(
                MakePartWithCrew("Jeb Kerman", "Val Kerman", "Bob Kerman"));

            var result = VesselSpawner.ExtractCrewManifest(vessel, name => "Pilot");

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal(3, result["Pilot"]);
        }

        [Fact]
        public void ExtractCrewManifest_MultipleCrew_DifferentTraits()
        {
            var vessel = MakeVessel(
                MakePartWithCrew("Jeb Kerman", "Bob Kerman", "Bill Kerman"));

            var resolver = new Func<string, string>(name =>
            {
                if (name == "Jeb Kerman") return "Pilot";
                if (name == "Bob Kerman") return "Scientist";
                if (name == "Bill Kerman") return "Engineer";
                return "Pilot";
            });

            var result = VesselSpawner.ExtractCrewManifest(vessel, resolver);

            Assert.NotNull(result);
            Assert.Equal(3, result.Count);
            Assert.Equal(1, result["Pilot"]);
            Assert.Equal(1, result["Scientist"]);
            Assert.Equal(1, result["Engineer"]);
        }

        [Fact]
        public void ExtractCrewManifest_DefaultResolver_FallbackToPilot()
        {
            // Outside KSP runtime, FindTraitForKerbal falls back to "Pilot"
            var vessel = MakeVessel(
                MakePartWithCrew("Jeb Kerman", "Bob Kerman"));

            var result = VesselSpawner.ExtractCrewManifest(vessel);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal(2, result["Pilot"]);
        }

        [Fact]
        public void ExtractCrewManifest_EmptyCrewValue_Skipped()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "testPart");
            part.AddValue("crew", "");
            part.AddValue("crew", "Jeb Kerman");
            var vessel = MakeVessel(part);

            var result = VesselSpawner.ExtractCrewManifest(vessel, name => "Pilot");

            Assert.NotNull(result);
            Assert.Equal(1, result["Pilot"]);
        }

        [Fact]
        public void ExtractCrewManifest_MultiplePartsWithCrew()
        {
            var vessel = MakeVessel(
                MakePartWithCrew("Jeb Kerman"),
                MakePartWithCrew("Bob Kerman"));

            var resolver = new Func<string, string>(name =>
                name == "Jeb Kerman" ? "Pilot" : "Scientist");

            var result = VesselSpawner.ExtractCrewManifest(vessel, resolver);

            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Equal(1, result["Pilot"]);
            Assert.Equal(1, result["Scientist"]);
            Assert.Contains(logLines, l => l.Contains("2 crew") && l.Contains("2 trait(s)"));
        }

        [Fact]
        public void ExtractCrewManifest_NoParts_ReturnsNull()
        {
            var vessel = new ConfigNode("VESSEL");

            var result = VesselSpawner.ExtractCrewManifest(vessel);

            Assert.Null(result);
        }

        #endregion

        #region T11-CREW — ComputeCrewDelta

        [Fact]
        public void ComputeCrewDelta_BothNull_ReturnsNull()
        {
            var delta = CrewManifest.ComputeCrewDelta(null, null);

            Assert.Null(delta);
        }

        [Fact]
        public void ComputeCrewDelta_NormalTransfer()
        {
            var start = new Dictionary<string, int>
            {
                ["Pilot"] = 2,
                ["Engineer"] = 1
            };
            var end = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Engineer"] = 0
            };

            var delta = CrewManifest.ComputeCrewDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(-1, delta["Pilot"]);
            Assert.Equal(-1, delta["Engineer"]);
        }

        [Fact]
        public void ComputeCrewDelta_StartNull()
        {
            var end = new Dictionary<string, int>
            {
                ["Pilot"] = 2,
                ["Scientist"] = 1
            };

            var delta = CrewManifest.ComputeCrewDelta(null, end);

            Assert.NotNull(delta);
            Assert.Equal(2, delta["Pilot"]);
            Assert.Equal(1, delta["Scientist"]);
        }

        [Fact]
        public void ComputeCrewDelta_EndNull()
        {
            var start = new Dictionary<string, int>
            {
                ["Pilot"] = 2,
                ["Scientist"] = 1
            };

            var delta = CrewManifest.ComputeCrewDelta(start, null);

            Assert.NotNull(delta);
            Assert.Equal(-2, delta["Pilot"]);
            Assert.Equal(-1, delta["Scientist"]);
        }

        [Fact]
        public void ComputeCrewDelta_Unchanged()
        {
            var start = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Scientist"] = 1
            };
            var end = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Scientist"] = 1
            };

            var delta = CrewManifest.ComputeCrewDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(0, delta["Pilot"]);
            Assert.Equal(0, delta["Scientist"]);
        }

        #endregion
    }
}
