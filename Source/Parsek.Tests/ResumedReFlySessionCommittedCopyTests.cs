using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A load that restores a live Re-Fly session's tree from its save keeps the committed copy
    /// of that tree committed beside it, the shape the session had before the save (the Re-Fly
    /// start's bundle restore puts the committed original back). Detaching it dropped the
    /// committed original's ghost chains on the resumed session (CI-3).
    /// </summary>
    public class ResumedReFlySessionCommittedCopyTests
    {
        private static RecordingTree Tree(string id, params string[] recordingIds)
        {
            var tree = new RecordingTree { Id = id, TreeName = "T " + id };
            foreach (string r in recordingIds)
                tree.AddOrReplaceRecording(new Recording { RecordingId = r, TreeId = id });
            return tree;
        }

        private static ReFlySessionMarker Marker(string treeId, string activeId)
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_t",
                TreeId = treeId,
                ActiveReFlyRecordingId = activeId,
            };
        }

        [Fact]
        public void SessionTreeWithItsProvisional_IsResumedSession()
        {
            Assert.True(ParsekScenario.IsResumedReFlySessionTree(
                Marker("tree_a", "rec_prov"), Tree("tree_a", "rec_origin", "rec_prov")));
        }

        [Fact]
        public void OtherTreeMissingProvisionalOrNoMarker_IsNotResumedSession()
        {
            var tree = Tree("tree_a", "rec_origin", "rec_prov");
            Assert.False(ParsekScenario.IsResumedReFlySessionTree(null, tree));
            Assert.False(ParsekScenario.IsResumedReFlySessionTree(Marker("tree_b", "rec_prov"), tree));
            Assert.False(ParsekScenario.IsResumedReFlySessionTree(Marker("tree_a", "rec_gone"), tree));
            Assert.False(ParsekScenario.IsResumedReFlySessionTree(Marker("tree_a", null), tree));
            Assert.False(ParsekScenario.IsResumedReFlySessionTree(Marker("tree_a", "rec_prov"), null));
        }

        // catches: the detach running for a resumed session (the guard dropped or moved after
        // the RemoveCommittedTreeById call).
        [Fact]
        public void TryRestoreActiveTreeNode_SkipsTheDetachForAResumedSession()
        {
            string src = Regex.Replace(
                SourceScanText.StripCSharpComments(File.ReadAllText(LocateSource("ParsekScenario.cs"))),
                @"\s+", " ");
            int method = src.IndexOf("internal static bool TryRestoreActiveTreeNode(", StringComparison.Ordinal);
            Assert.True(method >= 0);
            int guard = src.IndexOf("IsResumedReFlySessionTree(", method, StringComparison.Ordinal);
            int skip = src.IndexOf("&& !reFlySessionResumed", method, StringComparison.Ordinal);
            int detach = src.IndexOf("RecordingStore.RemoveCommittedTreeById(", method, StringComparison.Ordinal);
            Assert.True(guard > method && skip > guard && detach > skip,
                $"guard={guard} skip={skip} detach={detach}");
        }

        private static string LocateSource(string fileName)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            throw new FileNotFoundException(fileName);
        }
    }
}
