using System;
using System.Collections.Generic;
using System.IO;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Save/load round-trip of the vessel / ghost snapshot sidecars
    /// (<c>&lt;id&gt;_vessel.craft</c>, <c>&lt;id&gt;_ghost.craft</c>). The payload is a whole
    /// ConfigNode, so "every field" is every value (name, text and order, duplicates kept)
    /// and every child node at every depth, plus the root node's name; the header fields
    /// the probe reports must be the ones the writer stamped.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(SnapshotSidecarCodec))]
    public class SnapshotSidecarSaveRoundTripTests : IDisposable
    {
        private readonly string tempDir;

        public SnapshotSidecarSaveRoundTripTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-rt-snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException) { }
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        /// <summary>
        /// A production-shaped VESSEL node (parts, modules, crew, orbit) plus values a
        /// player or a mod can put into a snapshot: spaces, '=' inside a value, non-ASCII
        /// text, an empty value, a repeated key and a deeper nesting than stock writes.
        /// </summary>
        private static ConfigNode BuildSnapshot()
        {
            ConfigNode vessel = VesselSnapshotBuilder.FleaRocket("Flea, Mk. 2", "Jebediah Kerman", 4242).Build();
            vessel.AddValue("note", "a value with spaces and = sign");
            vessel.AddValue("unicode", "Kerbal \u00e9toile \u4e2d\u6587");
            vessel.AddValue("empty", string.Empty);
            vessel.AddValue("decimal", "1.5");
            vessel.AddValue("repeat", "first");
            vessel.AddValue("repeat", "second");
            ConfigNode deep = vessel.AddNode("MODDATA").AddNode("LEVEL2").AddNode("LEVEL3");
            deep.AddValue("leaf", "-0.000123");
            deep.AddNode("EMPTY_CHILD");
            return vessel;
        }

        private ConfigNode RoundTrip(ConfigNode written, string fileName, out SnapshotSidecarProbe probe)
        {
            string path = Path.Combine(tempDir, fileName);
            SnapshotSidecarCodec.Write(path, written);
            Assert.True(SnapshotSidecarCodec.TryLoad(path, out ConfigNode read, out probe), probe.FailureReason);
            return read;
        }

        [Fact]
        public void VesselSnapshot_EveryValueAndNodeRoundTrips()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                ConfigNode written = BuildSnapshot();
                ConfigNode read = RoundTrip(written, "rt_vessel.craft", out SnapshotSidecarProbe probe);

                List<string> problems = DiffNodes(written, read, written.name);
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("Snapshot sidecar", problems));
                Assert.Equal(written.ToString(), read.ToString());

                Assert.True(probe.Supported);
                Assert.Equal(SnapshotSidecarEncoding.DeflateV1, probe.Encoding);
                Assert.Equal(SnapshotSidecarCodec.CurrentVersion, probe.FormatVersion);
                Assert.Equal(RecordingStore.CurrentRecordingSchemaGeneration, probe.SchemaGeneration);
                Assert.Equal(written.name, probe.NodeName);
            });
        }

        [Fact]
        public void GhostSnapshot_UnnamedRootComesBackUnderTheFallbackName()
        {
            // A root with no name is stored under SNAPSHOT; everything below it is kept.
            ConfigNode written = BuildSnapshot();
            written.name = string.Empty;
            ConfigNode read = RoundTrip(written, "rt_ghost.craft", out SnapshotSidecarProbe probe);

            Assert.Equal("SNAPSHOT", read.name);
            Assert.Equal("SNAPSHOT", probe.NodeName);
            List<string> problems = DiffNodes(written, read, "SNAPSHOT", compareRootName: false);
            Assert.True(problems.Count == 0, FieldRoundTrip.Report("Ghost snapshot sidecar", problems));
        }

        [Fact]
        public void Harness_DetectsAFieldTheCodecDrops()
        {
            ConfigNode written = BuildSnapshot();
            ConfigNode read = RoundTrip(written, "rt_negative.craft", out _);
            read.GetNode("MODDATA").GetNode("LEVEL2").GetNode("LEVEL3").RemoveValue("leaf");
            while (read.HasValue("repeat"))
                read.RemoveValue("repeat");
            List<string> problems = DiffNodes(written, read, written.name);
            Assert.Contains(problems, p => p.Contains("LEVEL3") && p.Contains("leaf"));
            Assert.Contains(problems, p => p.Contains("repeat"));
        }

        /// <summary>Every value (by position: name and text) and every child node, recursively.</summary>
        internal static List<string> DiffNodes(ConfigNode a, ConfigNode b, string path, bool compareRootName = true)
        {
            var problems = new List<string>();
            DiffNodesInto(a, b, path, compareRootName, problems);
            return problems;
        }

        private static void DiffNodesInto(ConfigNode a, ConfigNode b, string path, bool compareName, List<string> problems)
        {
            if (compareName && a.name != b.name)
                problems.Add(path + ": node name '" + a.name + "' read as '" + b.name + "'");

            int values = Math.Max(a.values.Count, b.values.Count);
            for (int i = 0; i < values; i++)
            {
                ConfigNode.Value va = i < a.values.Count ? a.values[i] : null;
                ConfigNode.Value vb = i < b.values.Count ? b.values[i] : null;
                if (va == null || vb == null || va.name != vb.name || va.value != vb.value)
                {
                    problems.Add(path + ".value[" + i + "]: wrote "
                        + (va == null ? "nothing" : va.name + " = '" + va.value + "'")
                        + ", read " + (vb == null ? "nothing" : vb.name + " = '" + vb.value + "'"));
                }
            }

            if (a.nodes.Count != b.nodes.Count)
                problems.Add(path + ": wrote " + a.nodes.Count + " child nodes, read " + b.nodes.Count);
            int nodes = Math.Min(a.nodes.Count, b.nodes.Count);
            for (int i = 0; i < nodes; i++)
                DiffNodesInto(a.nodes[i], b.nodes[i], path + "/" + a.nodes[i].name + "[" + i + "]", true, problems);
        }
    }
}
