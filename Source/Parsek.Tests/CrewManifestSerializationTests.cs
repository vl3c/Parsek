using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CrewManifestSerializationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrewManifestSerializationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        [Fact]
        public void RoundTrip_BothStartAndEnd()
        {
            var rec = new Recording();
            rec.RecordingId = "test-crew-both";
            rec.StartCrew = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Engineer"] = 2
            };
            rec.EndCrew = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Engineer"] = 0
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeCrewManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-crew-both";
            RecordingStore.DeserializeCrewManifest(node, loaded);

            Assert.NotNull(loaded.StartCrew);
            Assert.NotNull(loaded.EndCrew);
            Assert.Equal(2, loaded.StartCrew.Count);
            Assert.Equal(2, loaded.EndCrew.Count);

            Assert.Equal(1, loaded.StartCrew["Pilot"]);
            Assert.Equal(2, loaded.StartCrew["Engineer"]);
            Assert.Equal(1, loaded.EndCrew["Pilot"]);
            Assert.Equal(0, loaded.EndCrew["Engineer"]);

            Assert.Contains(logLines, l => l.Contains("[RecordingStore]") && l.Contains("wrote 2 trait(s)"));
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]") && l.Contains("loaded=2") && l.Contains("skipped=0"));
        }

        [Fact]
        public void RoundTrip_StartOnly()
        {
            var rec = new Recording();
            rec.RecordingId = "test-crew-start";
            rec.StartCrew = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Scientist"] = 1
            };
            rec.EndCrew = null;

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeCrewManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-crew-start";
            RecordingStore.DeserializeCrewManifest(node, loaded);

            Assert.NotNull(loaded.StartCrew);
            Assert.Null(loaded.EndCrew);
            Assert.Equal(1, loaded.StartCrew["Pilot"]);
            Assert.Equal(1, loaded.StartCrew["Scientist"]);
        }

        [Fact]
        public void RoundTrip_NullBoth_NoNodeWritten()
        {
            var rec = new Recording();
            rec.RecordingId = "test-crew-null";
            rec.StartCrew = null;
            rec.EndCrew = null;

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeCrewManifest(node, rec);

            Assert.Null(node.GetNode("CREW_MANIFEST"));
        }

        [Fact]
        public void RoundTrip_ViaTree()
        {
            var rec = new Recording();
            rec.RecordingId = "test-crew-tree";
            rec.StartCrew = new Dictionary<string, int>
            {
                ["Pilot"] = 2
            };
            rec.EndCrew = new Dictionary<string, int>
            {
                ["Pilot"] = 1
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-crew-tree";
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.NotNull(loaded.StartCrew);
            Assert.NotNull(loaded.EndCrew);
            Assert.Equal(2, loaded.StartCrew["Pilot"]);
            Assert.Equal(1, loaded.EndCrew["Pilot"]);
        }

        [Fact]
        public void LegacyRecording_NoNode_NullFields()
        {
            var node = new ConfigNode("RECORDING");
            // No CREW_MANIFEST node — simulates legacy recording

            var loaded = new Recording();
            loaded.RecordingId = "test-crew-legacy";
            RecordingStore.DeserializeCrewManifest(node, loaded);

            Assert.Null(loaded.StartCrew);
            Assert.Null(loaded.EndCrew);
        }

        [Fact]
        public void RoundTrip_EndOnly()
        {
            var rec = new Recording();
            rec.RecordingId = "test-crew-end";
            rec.StartCrew = null;
            rec.EndCrew = new Dictionary<string, int>
            {
                ["Pilot"] = 1
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeCrewManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-crew-end";
            RecordingStore.DeserializeCrewManifest(node, loaded);

            Assert.Null(loaded.StartCrew);
            Assert.NotNull(loaded.EndCrew);
            Assert.Equal(1, loaded.EndCrew["Pilot"]);
        }

        [Fact]
        public void RoundTrip_EmptyDicts_NoNodeWritten()
        {
            var rec = new Recording();
            rec.RecordingId = "test-crew-empty";
            rec.StartCrew = new Dictionary<string, int>();
            rec.EndCrew = new Dictionary<string, int>();

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeCrewManifest(node, rec);

            Assert.Null(node.GetNode("CREW_MANIFEST"));
        }

        [Fact]
        public void RoundTrip_AsymmetricKeys()
        {
            var rec = new Recording();
            rec.RecordingId = "test-crew-asym";
            rec.StartCrew = new Dictionary<string, int>
            {
                ["Pilot"] = 2,
                ["Engineer"] = 1
            };
            rec.EndCrew = new Dictionary<string, int>
            {
                ["Pilot"] = 1,
                ["Tourist"] = 1
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeCrewManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-crew-asym";
            RecordingStore.DeserializeCrewManifest(node, loaded);

            Assert.Equal(2, loaded.StartCrew.Count);
            Assert.Equal(2, loaded.EndCrew.Count);
            Assert.True(loaded.StartCrew.ContainsKey("Pilot"));
            Assert.True(loaded.StartCrew.ContainsKey("Engineer"));
            Assert.True(loaded.EndCrew.ContainsKey("Pilot"));
            Assert.True(loaded.EndCrew.ContainsKey("Tourist"));
            Assert.False(loaded.StartCrew.ContainsKey("Tourist"));
            Assert.False(loaded.EndCrew.ContainsKey("Engineer"));
        }

        [Fact]
        public void MalformedTrait_Skipped()
        {
            var node = new ConfigNode("RECORDING");
            var manifest = node.AddNode("CREW_MANIFEST");
            var good = manifest.AddNode("TRAIT");
            good.AddValue("name", "Pilot");
            good.AddValue("startCount", "2");
            var bad = manifest.AddNode("TRAIT");
            bad.AddValue("name", "");
            bad.AddValue("startCount", "1");

            var loaded = new Recording();
            loaded.RecordingId = "test-crew-malformed";
            RecordingStore.DeserializeCrewManifest(node, loaded);

            Assert.NotNull(loaded.StartCrew);
            Assert.Single(loaded.StartCrew);
            Assert.Equal(2, loaded.StartCrew["Pilot"]);
            Assert.Contains(logLines, l => l.Contains("loaded=1") && l.Contains("skipped=1"));
        }
    }
}
