using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class AntennaSpecTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AntennaSpecTests()
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

        #region AntennaSpec struct

        /// <summary>
        /// AntennaSpec.ToString includes all fields for diagnostic logging.
        /// Guards: log output contains part name, power, combinable, exponent, type.
        /// </summary>
        [Fact]
        public void AntennaSpec_ToString_ContainsAllFields()
        {
            var spec = new AntennaSpec
            {
                partName = "longAntenna",
                antennaPower = 500000,
                antennaCombinable = true,
                antennaCombinableExponent = 0.75,
                antennaType = "RELAY"
            };

            string result = spec.ToString();

            Assert.Contains("longAntenna", result);
            Assert.Contains("500000", result);
            Assert.Contains("True", result);
            Assert.Contains("0.75", result);
            Assert.Contains("RELAY", result);
        }

        /// <summary>
        /// AntennaSpec.ToString handles null partName gracefully.
        /// Guards: no NullReferenceException on null part name.
        /// </summary>
        [Fact]
        public void AntennaSpec_ToString_NullPartName_ShowsNull()
        {
            var spec = new AntennaSpec { partName = null, antennaPower = 100 };

            string result = spec.ToString();

            Assert.Contains("(null)", result);
            Assert.Contains("100", result);
        }

        #endregion

        #region ExtractFromSnapshot

        /// <summary>
        /// Extracting from a snapshot with one ModuleDataTransmitter returns one AntennaSpec.
        /// Guards: basic extraction pipeline works end-to-end.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_WithDataTransmitter_ExtractsSpecs()
        {
            var vessel = new ConfigNode("VESSEL");
            var part = vessel.AddNode("PART");
            part.AddValue("name", "longAntenna");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleDataTransmitter");
            module.AddValue("antennaPower", "500000");
            module.AddValue("antennaCombinable", "True");
            module.AddValue("antennaCombinableExponent", "0.75");

            var specs = AntennaSpecExtractor.ExtractFromSnapshot(vessel);

            Assert.Single(specs);
            Assert.Equal("longAntenna", specs[0].partName);
            Assert.Equal(500000.0, specs[0].antennaPower);
            Assert.True(specs[0].antennaCombinable);
            Assert.Equal(0.75, specs[0].antennaCombinableExponent);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("longAntenna") && l.Contains("500000"));
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("extracted 1 antenna spec"));
        }

        /// <summary>
        /// Extracting from a snapshot with no ModuleDataTransmitter returns empty list.
        /// Guards: parts without antennas are skipped cleanly.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_NoDataTransmitter_ReturnsEmpty()
        {
            var vessel = new ConfigNode("VESSEL");
            var part = vessel.AddNode("PART");
            part.AddValue("name", "fuelTank.v2");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleResourceConverter");

            var specs = AntennaSpecExtractor.ExtractFromSnapshot(vessel);

            Assert.Empty(specs);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("extracted 0 antenna spec"));
        }

        /// <summary>
        /// Extracting from a snapshot with multiple antenna parts extracts all.
        /// Guards: iteration over multiple parts and modules works correctly.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_MultipleAntennas_ExtractsAll()
        {
            var vessel = new ConfigNode("VESSEL");

            // First antenna part
            var part1 = vessel.AddNode("PART");
            part1.AddValue("name", "longAntenna");
            var mod1 = part1.AddNode("MODULE");
            mod1.AddValue("name", "ModuleDataTransmitter");
            mod1.AddValue("antennaPower", "500000");
            mod1.AddValue("antennaCombinable", "True");
            mod1.AddValue("antennaCombinableExponent", "0.75");

            // Non-antenna part (should be skipped)
            var part2 = vessel.AddNode("PART");
            part2.AddValue("name", "fuelTank.v2");
            var mod2 = part2.AddNode("MODULE");
            mod2.AddValue("name", "ModuleResourceConverter");

            // Second antenna part (relay dish)
            var part3 = vessel.AddNode("PART");
            part3.AddValue("name", "RelayAntenna100");
            var mod3 = part3.AddNode("MODULE");
            mod3.AddValue("name", "ModuleDataTransmitter");
            mod3.AddValue("antennaPower", "100000000000");
            mod3.AddValue("antennaCombinable", "True");
            mod3.AddValue("antennaCombinableExponent", "0.75");

            var specs = AntennaSpecExtractor.ExtractFromSnapshot(vessel);

            Assert.Equal(2, specs.Count);
            Assert.Equal("longAntenna", specs[0].partName);
            Assert.Equal(500000.0, specs[0].antennaPower);
            Assert.Equal("RelayAntenna100", specs[1].partName);
            Assert.Equal(100000000000.0, specs[1].antennaPower);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("extracted 2 antenna spec"));
        }

        /// <summary>
        /// Extracting from a null snapshot returns empty list without throwing.
        /// Guards: null safety at entry point.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_NullSnapshot_ReturnsEmpty()
        {
            var specs = AntennaSpecExtractor.ExtractFromSnapshot(null);

            Assert.Empty(specs);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("null snapshot"));
        }

        /// <summary>
        /// Parts with no MODULE nodes are skipped without error.
        /// Guards: null MODULE array doesn't crash.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_PartWithNoModules_Skipped()
        {
            var vessel = new ConfigNode("VESSEL");
            var part = vessel.AddNode("PART");
            part.AddValue("name", "structuralPanel");
            // No MODULE nodes

            var specs = AntennaSpecExtractor.ExtractFromSnapshot(vessel);

            Assert.Empty(specs);
        }

        /// <summary>
        /// Missing numeric fields default to 0 / false.
        /// Guards: partial module data doesn't crash; missing fields get defaults.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_MissingFields_DefaultsToZero()
        {
            var vessel = new ConfigNode("VESSEL");
            var part = vessel.AddNode("PART");
            part.AddValue("name", "antenna");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleDataTransmitter");
            // No antennaPower, antennaCombinable, antennaCombinableExponent

            var specs = AntennaSpecExtractor.ExtractFromSnapshot(vessel);

            Assert.Single(specs);
            Assert.Equal("antenna", specs[0].partName);
            Assert.Equal(0.0, specs[0].antennaPower);
            Assert.False(specs[0].antennaCombinable);
            Assert.Equal(0.0, specs[0].antennaCombinableExponent);
        }

        /// <summary>
        /// Vessel snapshot with no PART nodes returns empty list.
        /// Guards: empty vessel snapshots handled gracefully.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_NoParts_ReturnsEmpty()
        {
            var vessel = new ConfigNode("VESSEL");
            // No PART nodes

            var specs = AntennaSpecExtractor.ExtractFromSnapshot(vessel);

            Assert.Empty(specs);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("no PART nodes"));
        }

        /// <summary>
        /// A part with multiple MODULE nodes, one of which is ModuleDataTransmitter,
        /// correctly extracts only the antenna module.
        /// Guards: module name filtering within a single part.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_PartWithMixedModules_ExtractsOnlyTransmitter()
        {
            var vessel = new ConfigNode("VESSEL");
            var part = vessel.AddNode("PART");
            part.AddValue("name", "probeCoreOcto");

            var mod1 = part.AddNode("MODULE");
            mod1.AddValue("name", "ModuleCommand");

            var mod2 = part.AddNode("MODULE");
            mod2.AddValue("name", "ModuleDataTransmitter");
            mod2.AddValue("antennaPower", "5000");

            var mod3 = part.AddNode("MODULE");
            mod3.AddValue("name", "ModuleSAS");

            var specs = AntennaSpecExtractor.ExtractFromSnapshot(vessel);

            Assert.Single(specs);
            Assert.Equal("probeCoreOcto", specs[0].partName);
            Assert.Equal(5000.0, specs[0].antennaPower);
        }

        /// <summary>
        /// Extracting from a snapshot with antennaType present extracts the type field.
        /// Guards: antennaType is read from the ModuleDataTransmitter node.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_WithAntennaType_ExtractsType()
        {
            var vessel = new ConfigNode("VESSEL");
            var part = vessel.AddNode("PART");
            part.AddValue("name", "RelayAntenna100");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleDataTransmitter");
            module.AddValue("antennaPower", "100000000000");
            module.AddValue("antennaCombinable", "True");
            module.AddValue("antennaCombinableExponent", "0.75");
            module.AddValue("antennaType", "RELAY");

            var specs = AntennaSpecExtractor.ExtractFromSnapshot(vessel);

            Assert.Single(specs);
            Assert.Equal("RELAY", specs[0].antennaType);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("type=RELAY"));
        }

        /// <summary>
        /// Extracting from a snapshot without antennaType defaults to empty string.
        /// Guards: legacy snapshots without type field get backward-compatible default.
        /// </summary>
        [Fact]
        public void ExtractFromSnapshot_MissingAntennaType_DefaultsEmpty()
        {
            var vessel = new ConfigNode("VESSEL");
            var part = vessel.AddNode("PART");
            part.AddValue("name", "longAntenna");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleDataTransmitter");
            module.AddValue("antennaPower", "500000");
            // No antennaType field

            var specs = AntennaSpecExtractor.ExtractFromSnapshot(vessel);

            Assert.Single(specs);
            Assert.Equal("", specs[0].antennaType);
        }

        #endregion
    }
}
