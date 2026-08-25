using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek.MapRender;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure cells for the M-A7 <c>ExportRenderManifest</c> seam verb: the OK payload shape a
    /// spec pins, the reason normalization, and the two constants that must stay pointed at
    /// the recorder rather than re-spelled. The Unity applier is one thin call into
    /// <c>RenderCompositionRecorder.TryExportNow</c>, whose own behaviour is covered by
    /// RenderCompositionManifestTests.
    /// </summary>
    public class TestCommandExportRenderManifestTests
    {
        private static Dictionary<string, string> Map(List<KeyValuePair<string, string>> payload)
        {
            var d = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in payload)
                d[kv.Key] = kv.Value;
            return d;
        }

        [Fact]
        public void Payload_carries_path_manifest_name_and_the_four_counts()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandExportRenderManifest.BuildResultPayload(
                    @"C:\ksp\parsek-render-manifest.txt",
                    dwells: 7, transitions: 3, planUnits: 2, clockEvents: 11);

            Dictionary<string, string> m = Map(payload);
            Assert.Equal(@"C:\ksp\parsek-render-manifest.txt", m["path"]);
            Assert.Equal(TestCommandExportRenderManifest.ManifestFileName, m["manifest"]);
            Assert.Equal("2", m["planUnits"]);
            Assert.Equal("7", m["dwells"]);
            Assert.Equal("3", m["transitions"]);
            Assert.Equal("11", m["clockEvents"]);
            Assert.Equal(6, payload.Count);
        }

        [Fact]
        public void Payload_never_carries_a_null_path()
        {
            // A null path would serialize into the response as the literal "null" (or throw
            // at the codec); an empty value is the honest "the writer reported none".
            Dictionary<string, string> m = Map(
                TestCommandExportRenderManifest.BuildResultPayload(null, 0, 0, 0, 0));
            Assert.Equal(string.Empty, m["path"]);
        }

        [Fact]
        public void Counts_are_invariant_culture()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A comma-decimal / digit-grouping locale must not reach the wire.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Dictionary<string, string> m = Map(
                    TestCommandExportRenderManifest.BuildResultPayload(
                        "p", dwells: 1234567, transitions: 0, planUnits: 0, clockEvents: 0));
                Assert.Equal("1234567", m["dwells"]);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Fact]
        public void Error_reason_falls_back_when_the_recorder_reported_none()
        {
            Assert.Equal("write-failed:IOException",
                TestCommandExportRenderManifest.ErrorReason("write-failed:IOException"));
            Assert.Equal(TestCommandExportRenderManifest.ExportFailedReason,
                TestCommandExportRenderManifest.ErrorReason(null));
            Assert.Equal(TestCommandExportRenderManifest.ExportFailedReason,
                TestCommandExportRenderManifest.ErrorReason(string.Empty));
        }

        [Fact]
        public void Constants_point_at_the_recorder_not_a_second_copy()
        {
            // Both are compile-time aliases of the recorder's own constants. If either is
            // ever re-spelled as a literal, this cell keeps the two from drifting apart.
            Assert.Equal(RenderCompositionRecorder.ManifestFileName,
                TestCommandExportRenderManifest.ManifestFileName);
            Assert.Equal(RenderCompositionRecorder.ErrorNotArmed,
                TestCommandExportRenderManifest.NotArmedReason);
        }

        [Fact]
        public void Verb_is_implemented_and_any_scene()
        {
            Assert.Equal(TestCommandVerbClass.Implemented,
                TestCommandVerbs.Classify("ExportRenderManifest"));
            Assert.Equal("AnyScene",
                TestCommandDispatcher.RequirementFor("ExportRenderManifest").ToString());
        }
    }
}
